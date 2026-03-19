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
using static ILCompiler.ObjectWriter.ElfNative;

namespace ILCompiler.Compiler.Tests
{
    /// <summary>
    /// Integration test: compile a real .o with gcc, link it with our managed
    /// linker, and verify the output ELF with readelf.
    /// </summary>
    public class LinkerIntegrationTests
    {
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

        private static string RunProcess(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi);
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);

            if (process.ExitCode != 0)
                throw new Exception($"{fileName} {arguments} failed (exit {process.ExitCode}):\n{stderr}");

            return stdout + stderr;
        }
    }
}
