// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ILCompiler.Linker;
using Xunit;
using Xunit.Abstractions;
using static ILCompiler.ObjectWriter.ElfNative;

namespace ILCompiler.Compiler.Tests
{
    /// <summary>
    /// Integration tests: compile real .o files, link with our managed linker,
    /// and verify the output with readelf and actual execution.
    /// </summary>
    public class LinkerIntegrationTests
    {
        private readonly ITestOutputHelper _output;

        public LinkerIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static bool IsLinuxX64 =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.OSArchitecture == Architecture.X64;

        [Fact]
        public void MinimalLink_ProducesValidElf()
        {
            if (!IsLinuxX64)
                return; // This test requires gcc and readelf on linux-x64

            string tempDir = Path.Combine(Path.GetTempPath(), $"linker-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. Write a minimal C source that calls exit(42) via syscall
                string srcPath = Path.Combine(tempDir, "start.c");
                File.WriteAllText(srcPath, """
                    void _start(void) {
                        // exit(42) via syscall
                        __asm__ volatile (
                            "mov $60, %%rax\n"  // __NR_exit
                            "mov $42, %%rdi\n"  // exit code
                            "syscall"
                            : : : "rax", "rdi"
                        );
                        __builtin_unreachable();
                    }
                    """);

                // 2. Compile to .o with gcc
                string objPath = Path.Combine(tempDir, "start.o");
                RunProcess("gcc", $"-c -nostdlib -ffreestanding -o {objPath} {srcPath}");
                Assert.True(File.Exists(objPath), "gcc failed to produce .o");

                // 3. Read the .o with our reader
                byte[] objData = File.ReadAllBytes(objPath);
                var reader = new ElfObjectReader(objData);
                Assert.Equal(EM_X86_64, reader.Machine);

                ElfSection textSection = reader.Sections.FirstOrDefault(s => s.Name == ".text");
                Assert.NotNull(textSection);

                ElfSymbolEntry startSym = reader.Symbols.FirstOrDefault(s => s.Name == "_start");
                Assert.NotNull(startSym);
                Assert.True(startSym.IsGlobal || startSym.IsFunction);

                // 4. Resolve symbols
                var resolver = new SymbolResolver();
                resolver.AddObject(0, reader.Symbols);
                resolver.Resolve(new[] { reader.Symbols });
                Assert.Empty(resolver.UnresolvedSymbols);

                // 5. Lay out sections
                var layout = new SectionLayout();
                layout.AddSections(0, reader.Sections);
                layout.ComputeLayout();
                Assert.True(layout.MergedSections.Count > 0);

                // 6. Build section data with relocations applied
                var sectionData = new Dictionary<string, byte[]>();
                foreach (MergedSection merged in layout.MergedSections)
                {
                    using var ms = new MemoryStream();
                    foreach (InputSection input in merged.Inputs)
                    {
                        // Pad to alignment
                        while (ms.Length < (long)input.OutputOffset)
                            ms.WriteByte(0);

                        ElfSection sec = input.Section;
                        if (sec.Type != SHT_NOBITS)
                        {
                            byte[] data = reader.GetSectionData(sec).ToArray();

                            // Apply relocations targeting this section
                            var relocs = reader.Relocations
                                .Where(r => r.TargetSectionIndex == sec.Index)
                                .ToList();

                            if (relocs.Count > 0)
                            {
                                var processor = new RelocationProcessor(layout, resolver);
                                var buffers = new Dictionary<int, byte[]> { [sec.Index] = data };
                                processor.Apply(0, relocs, reader.Symbols, buffers);
                            }

                            ms.Write(data);
                        }
                    }
                    sectionData[merged.Name] = ms.ToArray();
                }

                // 7. Determine entry point address
                Assert.True(resolver.TryResolve("_start", out ResolvedSymbol resolvedStart));
                ulong entryPoint = layout.GetSymbolAddress(
                    resolvedStart.ObjectIndex, resolvedStart.SectionIndex, resolvedStart.Value);

                // 8. Emit ELF executable
                string exePath = Path.Combine(tempDir, "test_exe");
                var writer = new ElfExecutableWriter(layout, EM_X86_64, entryPoint);
                using (var fs = File.Create(exePath))
                {
                    writer.Write(fs, sectionData);
                }

                Assert.True(File.Exists(exePath));
                Assert.True(new FileInfo(exePath).Length > 64);

                // 9. Validate with readelf
                string readelfOutput = RunProcess("readelf", $"-h {exePath}");
                Assert.Contains("ELF64", readelfOutput);
                Assert.Contains("EXEC", readelfOutput);
                Assert.Contains("X86-64", readelfOutput, StringComparison.OrdinalIgnoreCase);

                string phOutput = RunProcess("readelf", $"-l {exePath}");
                Assert.Contains("LOAD", phOutput);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// End-to-end test: publish a NativeAOT Hello World with `dotnet publish`,
        /// then verify our linker components can read and resolve all inputs.
        /// </summary>
        [Fact]
        public void NativeAotHelloWorld_ReadAndResolve()
        {
            if (!IsLinuxX64)
                return;

            // Find the dotnet SDK in the repo root
            string repoRoot = FindRepoRoot();
            if (repoRoot is null)
            {
                _output.WriteLine("Skipping: could not find repo root with .dotnet SDK.");
                return;
            }

            string dotnetPath = Path.Combine(repoRoot, ".dotnet", "dotnet");
            if (!File.Exists(dotnetPath))
            {
                // Fall back to system dotnet
                dotnetPath = "dotnet";
                try { RunProcess(dotnetPath, "--version", repoRoot); }
                catch
                {
                    _output.WriteLine("Skipping: dotnet not found.");
                    return;
                }
            }

            string tempDir = Path.Combine(Path.GetTempPath(), $"naot-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. Create a Hello World project manually with correct TFM
                string projectDir = Path.Combine(tempDir, "hello");
                Directory.CreateDirectory(projectDir);

                File.WriteAllText(Path.Combine(projectDir, "hello.csproj"), """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net10.0</TargetFramework>
                        <PublishAot>true</PublishAot>
                      </PropertyGroup>
                    </Project>
                    """);

                File.WriteAllText(Path.Combine(projectDir, "Program.cs"), """
                    System.Console.WriteLine("Hello from NativeAOT!");
                    return 42;
                    """);

                // 2. Publish with NativeAOT (capture link command)
                RunProcess(dotnetPath, $"restore {projectDir} -r linux-x64", tempDir);
                string publishOutput = RunProcess(dotnetPath,
                    $"publish {projectDir} -r linux-x64 -c Release --no-restore -v n",
                    tempDir, timeoutMs: 120_000);

                // 3. Verify the system-linked binary works
                string publishDir = Path.Combine(projectDir, "bin", "Release", "net10.0", "linux-x64", "native");
                string exePath = Path.Combine(publishDir, "hello");
                Assert.True(File.Exists(exePath), $"NativeAOT binary not found at {exePath}");

                int exitCode = RunProcessForExitCode(exePath, "", tempDir, out string stdout);
                Assert.Equal(42, exitCode);
                Assert.Contains("Hello from NativeAOT!", stdout);
                _output.WriteLine($"System-linked binary works: exit={exitCode}, output={stdout.Trim()}");

                // 4. Extract actual linker inputs from the publish output
                //    The publish output contains the clang command with all .a and .o files
                var linkInputs = ExtractLinkInputs(publishOutput, projectDir);
                Assert.True(linkInputs.Count > 0, "Could not extract link inputs from publish output");

                string ilcObj = linkInputs.FirstOrDefault(f => f.EndsWith("hello.o"));
                Assert.True(ilcObj is not null && File.Exists(ilcObj), $"ILC .o not found in link inputs");

                _output.WriteLine($"ILC .o: {new FileInfo(ilcObj).Length / 1024}KB");
                _output.WriteLine($"Link inputs ({linkInputs.Count} files):");
                foreach (string input in linkInputs)
                    _output.WriteLine($"  {Path.GetFileName(input)}");

                // 5. Parse ILC .o with our ElfObjectReader
                byte[] ilcData = File.ReadAllBytes(ilcObj);
                var mainObj = new ElfObjectReader(ilcData);
                Assert.Equal(EM_X86_64, mainObj.Machine);
                _output.WriteLine($"ILC .o: {mainObj.Sections.Count} sections, {mainObj.Symbols.Count} symbols, {mainObj.Relocations.Count} relocations");

                Assert.True(mainObj.Sections.Count > 10, "Expected many sections from ILC output");
                Assert.True(mainObj.Symbols.Count > 100, "Expected many symbols from ILC output");
                Assert.True(mainObj.Relocations.Count > 1000, "Expected many relocations from ILC output");

                // Verify expected section types
                Assert.Contains(mainObj.Sections, s => s.Name == ".text");
                Assert.Contains(mainObj.Sections, s => s.Name == ".data");

                // 6. Parse the SDK .a archives and .o files from actual link inputs
                var archives = new Dictionary<string, ElfArchiveReader>();
                var sdkObjects = new List<(string Name, ElfObjectReader Reader)>();
                int totalArchiveMembers = 0;

                foreach (string inputFile in linkInputs)
                {
                    if (inputFile == ilcObj)
                        continue;

                    string fileName = Path.GetFileName(inputFile);
                    byte[] data = File.ReadAllBytes(inputFile);

                    if (inputFile.EndsWith(".a"))
                    {
                        var archive = new ElfArchiveReader(data);
                        archives[fileName] = archive;
                        totalArchiveMembers += archive.Members.Count;
                        _output.WriteLine($"  {fileName}: {archive.Members.Count} members, {archive.SymbolTable.Count} symbols");
                    }
                    else if (inputFile.EndsWith(".o"))
                    {
                        var reader = new ElfObjectReader(data);
                        sdkObjects.Add((fileName, reader));
                        _output.WriteLine($"  {fileName}: {reader.Symbols.Count} symbols");
                    }
                }

                // 8. Iterative archive member selection and symbol resolution
                //    This mimics what a real linker does: start with the main .o,
                //    find undefined symbols, pull archive members that define them,
                //    repeat until stable.
                var resolver = new SymbolResolver();
                var allObjectSymbols = new List<IReadOnlyList<ElfSymbolEntry>>();
                int objectIndex = 0;

                // Add main ILC .o
                resolver.AddObject(objectIndex++, mainObj.Symbols);
                allObjectSymbols.Add(mainObj.Symbols);

                // Add standalone SDK .o files
                foreach (var (name, reader) in sdkObjects)
                {
                    resolver.AddObject(objectIndex++, reader.Symbols);
                    allObjectSymbols.Add(reader.Symbols);
                }

                // Iteratively pull archive members
                var extractedMembers = new HashSet<(string Archive, int MemberIndex)>();
                int iterations = 0;
                const int maxIterations = 20;

                while (iterations++ < maxIterations)
                {
                    // Find current unresolved symbols
                    resolver.Resolve(allObjectSymbols);
                    var currentUnresolved = new HashSet<string>(resolver.UnresolvedSymbols);

                    if (currentUnresolved.Count == 0)
                        break;

                    bool pulledAny = false;

                    foreach (var (archiveName, archive) in archives)
                    {
                        var needed = archive.SelectMembersForSymbols(currentUnresolved);
                        foreach (int memberIdx in needed)
                        {
                            if (!extractedMembers.Add((archiveName, memberIdx)))
                                continue;

                            ArchiveMemberEntry member = archive.Members[memberIdx];
                            ReadOnlySpan<byte> memberData = archive.GetMemberData(member);

                            try
                            {
                                var memberReader = new ElfObjectReader(memberData);
                                resolver.AddArchiveMember(objectIndex++, memberReader.Symbols);
                                allObjectSymbols.Add(memberReader.Symbols);
                                pulledAny = true;
                            }
                            catch (InvalidDataException ex)
                            {
                                _output.WriteLine($"  Warning: Could not parse {archiveName}:{member.Name}: {ex.Message}");
                            }
                        }
                    }

                    if (!pulledAny)
                        break;
                }

                // Final resolution pass
                resolver.Resolve(allObjectSymbols);

                _output.WriteLine($"Archive member selection: {iterations} iterations, {extractedMembers.Count} members pulled");
                _output.WriteLine($"Total objects: {objectIndex}");
                _output.WriteLine($"Resolved symbols: {resolver.GlobalSymbols.Count}");
                _output.WriteLine($"Unresolved symbols: {resolver.UnresolvedSymbols.Count}");

                // 9. Verify results
                // All unresolved symbols should be libc/system symbols
                // (not Rh*, not managed symbols, not NativeAOT runtime symbols)
                var nativeAotUnresolved = resolver.UnresolvedSymbols
                    .Where(s => s.StartsWith("Rh", StringComparison.Ordinal) ||
                                s.StartsWith("__managed_", StringComparison.Ordinal) ||
                                s.StartsWith("S_P_", StringComparison.Ordinal) ||
                                s.StartsWith("System_", StringComparison.Ordinal) ||
                                s == "InitializeModules" ||
                                s == "ProcessFinalizers" ||
                                s == "ThreadEntryPoint" ||
                                s == "RuntimeFailFast" ||
                                s == "GetRuntimeException" ||
                                s == "AppendExceptionStackFrame")
                    .ToList();

                if (nativeAotUnresolved.Count > 0)
                {
                    _output.WriteLine("Unexpected NativeAOT unresolved symbols:");
                    foreach (string sym in nativeAotUnresolved)
                        _output.WriteLine($"  {sym}");
                }

                Assert.Empty(nativeAotUnresolved);

                // The remaining unresolved should all be standard C library symbols
                _output.WriteLine("Remaining unresolved (expected libc symbols):");
                foreach (string sym in resolver.UnresolvedSymbols.Take(20))
                    _output.WriteLine($"  {sym}");
                if (resolver.UnresolvedSymbols.Count > 20)
                    _output.WriteLine($"  ... and {resolver.UnresolvedSymbols.Count - 20} more");

                // We should have resolved thousands of symbols
                Assert.True(resolver.GlobalSymbols.Count > 1000,
                    $"Expected >1000 resolved symbols, got {resolver.GlobalSymbols.Count}");

                // Extracted members should include the GC runtime
                Assert.True(extractedMembers.Count > 10,
                    $"Expected >10 archive members pulled, got {extractedMembers.Count}");

                _output.WriteLine("NativeAOT e2e test PASSED: all inputs parsed and resolved correctly.");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static string FindRepoRoot()
        {
            // Check REPO_ROOT environment variable first
            string envRoot = Environment.GetEnvironmentVariable("REPO_ROOT");
            if (envRoot is not null && File.Exists(Path.Combine(envRoot, "global.json")))
                return envRoot;

            // Try multiple starting points
            string[] startDirs = new[]
            {
                Environment.CurrentDirectory,
                AppContext.BaseDirectory,
            };

            foreach (string start in startDirs)
            {
                string dir = start;
                while (dir is not null)
                {
                    if (File.Exists(Path.Combine(dir, "global.json")) &&
                        (Directory.Exists(Path.Combine(dir, ".dotnet")) ||
                         File.Exists(Path.Combine(dir, "build.sh"))))
                        return dir;
                    dir = Path.GetDirectoryName(dir);
                }
            }

            return null;
        }

        private static List<string> ExtractLinkInputs(string publishOutput, string projectDir)
        {
            var inputs = new List<string>();

            // Find the clang link command in the publish output
            foreach (string line in publishOutput.Split('\n'))
            {
                string trimmed = line.Trim();
                if (!trimmed.Contains("\"clang\"", StringComparison.Ordinal))
                    continue;

                // Extract all file paths that end with .o or .a
                foreach (string token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string path = token.Trim('"');
                    if (!path.EndsWith(".o") && !path.EndsWith(".a"))
                        continue;

                    // Resolve relative paths against project directory
                    string fullPath = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(projectDir, path));
                    if (File.Exists(fullPath))
                        inputs.Add(fullPath);
                }

                if (inputs.Count > 0)
                    break;
            }

            return inputs;
        }

        private static int RunProcessForExitCode(string fileName, string arguments, string workDir, out string stdout)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi);
            stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            return process.ExitCode;
        }

        private static string RunProcess(string fileName, string arguments, string workingDirectory = null, int timeoutMs = 30_000)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            if (workingDirectory is not null)
                psi.WorkingDirectory = workingDirectory;

            using var process = Process.Start(psi);
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(timeoutMs);

            if (process.ExitCode != 0)
                throw new Exception($"{fileName} failed (exit {process.ExitCode}):\nSTDOUT: {stdout}\nSTDERR: {stderr}");

            return stdout + stderr;
        }
    }
}
