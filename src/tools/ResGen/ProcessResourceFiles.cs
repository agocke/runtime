// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
#if FEATURE_RESXREADER_LIVEDESERIALIZATION
using System.ComponentModel.Design;
#endif
#if FEATURE_SYSTEM_CONFIGURATION
using System.Configuration;
#endif
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Resources;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
#if !FEATURE_ASSEMBLYLOADCONTEXT
using System.Runtime.Versioning;
using System.Security;
#endif
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.ComponentModel.Design;
using System.Resources.Extensions;

#if FEATURE_RESXREADER_LIVEDESERIALIZATION
using Microsoft.Win32;
#endif

#nullable disable

namespace Microsoft.Build.Tasks;

/// <summary>
/// This class defines the "GenerateResource" MSBuild task, which enables using resource APIs
/// to transform resource files.
/// </summary>
public sealed partial class GenerateResource
{
    private readonly Logger _log = new Logger();

    // Indicates whether the resource reader should use the source file's
    // directory to resolve relative file paths.
    private bool _useSourcePath = false;

    // List of those output resources that were not actually created, due to an error
    private ArrayList _unsuccessfullyCreatedOutFiles = new ArrayList();

    // The targets may pass in the path to the SDKToolsPath. If so this should be used to generate the commandline
    // for logging purposes.  Also, when ExecuteAsTool is true, it determines where the system goes looking for resgen.exe
    private string _sdkToolsPath;

    // Path to resgen.exe
    private string _resgenPath;


#if FEATURE_RESGEN
    // Our calculation is not quite correct. Using a number substantially less than 32768 in order to
    // be sure we don't exceed it.
    private static int s_maximumCommandLength = 28000;
#endif // FEATURE_RESGEN

    /// <summary>
    /// Satellite input assemblies.
    /// </summary>
    private List<string> _satelliteInputs = new();

    #region Properties

    /// <summary>
    /// The names of the items to be converted. The extension must be one of the
    /// following: .txt, .resx or .resources.
    /// </summary>
    public string[] Sources { get; set; }

    /// <summary>
    /// Indicates whether the resource reader should use the source file's directory to
    /// resolve relative file paths.
    /// </summary>
    public bool UseSourcePath
    {
        get { return _useSourcePath; }
        set { _useSourcePath = value; }
    }

    /// <summary>
    /// Resolves types in ResX files (XML resources) for Strongly Typed Resources
    /// </summary>
    public string[] References { get; set; }

    /// <summary>
    /// Indicates whether resources should be passed through in their current serialization
    /// format. .NET Core-targeted assemblies should use this; it's the only way to support
    /// non-string resources with MSBuild running on .NET Core.
    /// </summary>
    public bool UsePreserializedResources { get; set; } = false;

    /// <summary>
    /// Additional inputs to the dependency checking done by this task. For example,
    /// the project and targets files typically should be inputs, so that if they are updated,
    /// all resources are regenerated.
    /// </summary>
    public string[] AdditionalInputs { get; set; }

    /// <summary>
    /// The name(s) of the resource file to create. If the user does not specify this
    /// attribute, the task will append a .resources extension to each input filename
    /// argument and write the file to the directory that contains the input file.
    /// Includes any output files that were already up to date, but not any output files
    /// that failed to be written due to an error.
    /// </summary>
    public string[] OutputResources { get; set; }

    // Indicates whether any BinaryFormatter use should lead to a warning.
    public bool WarnOnBinaryFormatterUse
    {
        get; set;
    }

    /// <summary>
    /// Even though the generate resource task will do the processing in process, a logging message is still generated. This logging message
    /// will include the path to the windows SDK. Since the targets now will pass in the Windows SDK path we should use this for logging.
    /// </summary>
    public string SdkToolsPath
    {
        get { return _sdkToolsPath; }
        set { _sdkToolsPath = value; }
    }

    /// <summary>
    /// Array of equals-separated pairs of environment
    /// variables that should be passed to the spawned resgen.exe,
    /// in addition to (or selectively overriding) the regular environment block.
    /// These aren't currently used when resgen is run in-process.
    /// </summary>
    public string[] EnvironmentVariables
    {
        get;
        set;
    }

    /// <summary>
    /// Microsoft.Build.Utilities.ExecutableType of ResGen.exe.  Used to determine whether or not
    /// Tracker.exe needs to be used to spawn ResGen.exe.  If empty, uses a heuristic to determine
    /// a default architecture.
    /// </summary>
    public string ToolArchitecture
    {
        get
        {
            return string.Empty;
        }

        set
        {
            // do nothing
        }
    }

    /// <summary>
    /// Path to the appropriate .NET Framework location that contains FileTracker.dll.  If set, the user
    /// takes responsibility for making sure that the bitness of the FileTracker.dll that they pass matches
    /// the bitness of the ResGen.exe that they intend to use. If not set, the task decides the appropriate
    /// location based on the current .NET Framework version.
    /// </summary>
    /// <comments>
    /// Should only need to be used in partial or full checked in toolset scenarios.
    /// </comments>
    public string TrackerFrameworkPath
    {
        get
        {
            return string.Empty;
        }

        set
        {
            // do nothing
        }
    }

    /// <summary>
    /// Path to the appropriate Windows SDK location that contains Tracker.exe.  If set, the user takes
    /// responsibility for making sure that the bitness of the Tracker.exe that they pass matches the
    /// bitness of the ResGen.exe that they intend to use. If not set, the task decides the appropriate
    /// location based on the current Windows SDK.
    /// </summary>
    /// <comments>
    /// Should only need to be used in partial or full checked in toolset scenarios.
    /// </comments>
    public string TrackerSdkPath
    {
        get
        {
            return string.Empty;
        }

        set
        {
            // do nothing
        }
    }

    /// <summary>
    /// Where to extract ResW files.  (Could be the intermediate directory.)
    /// </summary>
    public string OutputDirectory
    {
        get;
        set;
    }

    #endregion // properties

    /// <summary>
    /// Simple public constructor.
    /// </summary>
    public GenerateResource()
    {
        // do nothing
    }

    public bool FailIfNotIncremental { get; set; }

    /// <summary>
    /// This is the main entry point for the GenerateResource task.
    /// </summary>
    /// <returns>true, if task executes successfully</returns>
    public bool Execute()
    {
        bool outOfProcExecutionSucceeded = true;

        // If there are no sources to process, just return (with success) and report the condition.
        if ((Sources == null) || (Sources.Length == 0))
        {
            _log.LogMessageFromResources(MessageImportance.Low, "GenerateResource.NoSources");
            // Indicate we generated nothing
            OutputResources = null;
            return true;
        }

        if (!ValidateParameters())
        {
            // Indicate we generated nothing
            OutputResources = null;
            return false;
        }

        // In the case that OutputResources wasn't set, build up the outputs by transforming the Sources
        // However if we are extracting ResW files, we cannot easily tell which files we'll produce up front,
        // without loading each assembly.
        if (!CreateOutputResourcesNames())
        {
            // Indicate we generated nothing
            OutputResources = null;
            return false;
        }

        List<string> inputsToProcess;
        List<string> outputsToProcess;
        List<string> cachedOutputFiles;  // For incremental builds, this is the set of already-existing, up to date files.

        GetResourcesToProcess(out inputsToProcess, out outputsToProcess, out cachedOutputFiles);

        if (inputsToProcess.Count == 0)
        {
            if (!_log.HasLoggedErrors)
            {
                if (cachedOutputFiles.Count > 0)
                {
                    OutputResources = cachedOutputFiles.ToArray();
                }

                _log.LogMessageFromResources("GenerateResource.NothingOutOfDate");
            }
            else
            {
                // No valid sources found--failures should have been logged in GetResourcesToProcess
                return false;
            }
        }
        else if (FailIfNotIncremental)
        {
            int maxCount = Math.Min(inputsToProcess.Count, outputsToProcess.Count);
            maxCount = Math.Min(maxCount, 5);  // Limit to just 5

            for (int index = 0; index < maxCount; index++)
            {
                _log.LogErrorFromResources("GenerateResource.ProcessingFile", inputsToProcess[index], outputsToProcess[index]);
            }

            return false;
        }
        else
        {
            if (!ComputePathToResGen())
            {
                // unable to compute the path to resgen.exe and that is necessary to
                // continue forward, so return now.
                return false;
            }

            var process = new ProcessResourceFiles(
                new Logger(),
                References,
                inputsToProcess,
                _satelliteInputs,
                outputsToProcess,
                UseSourcePath,
                UsePreserializedResources,
                WarnOnBinaryFormatterUse);

            process.Run();

            if (process.UnsuccessfullyCreatedOutFiles != null)
            {
                foreach (string item in process.UnsuccessfullyCreatedOutFiles)
                {
                    _unsuccessfullyCreatedOutFiles.Add(item);
                }
            }
        }

        RemoveUnsuccessfullyCreatedResourcesFromOutputResources();

        //MSBuildEventSource.Log.GenerateResourceOverallStop();

        return !_log.HasLoggedErrors && outOfProcExecutionSucceeded;
    }

    /// <summary>
    /// Computes the path to ResGen.exe for use in logging and for passing to the
    /// nested ResGen task.
    /// </summary>
    /// <returns>True if the path is found (or it doesn't matter because we're executing in memory), false otherwise</returns>
    private bool ComputePathToResGen()
    {
#if FEATURE_RESGEN
        _resgenPath = null;

        if (String.IsNullOrEmpty(_sdkToolsPath))
        {
            // Important: the GenerateResource task is declared twice in Microsoft.Common.CurrentVersion.targets:
            // https://github.com/dotnet/msbuild/blob/369631b4b21ef485f4d6f35e16b0c839a971b0e9/src/Tasks/Microsoft.Common.CurrentVersion.targets#L3177-L3178
            // First for CLR >= 4.0, where SdkToolsPath is passed $(ResgenToolPath) which in turn is set to
            // $(TargetFrameworkSDKToolsDirectory).
            // But for CLR < 4.0 the SdkToolsPath is not passed, so we need to explicitly assume 3.5:
            var version = TargetDotNetFrameworkVersion.Version35;

            _resgenPath = ToolLocationHelper.GetPathToDotNetFrameworkSdkFile("resgen.exe", version);

            if (_resgenPath == null && ExecuteAsTool)
            {
                Log.LogErrorWithCodeFromResources("General.PlatformSDKFileNotFound", "resgen.exe",
                    ToolLocationHelper.GetDotNetFrameworkSdkInstallKeyValue(version),
                    ToolLocationHelper.GetDotNetFrameworkSdkRootRegistryKey(version));
            }
        }
        else
        {
            _resgenPath = SdkToolsPathUtility.GeneratePathToTool(
                SdkToolsPathUtility.FileInfoExists,
                Microsoft.Build.Utilities.ProcessorArchitecture.CurrentProcessArchitecture,
                SdkToolsPath,
                "resgen.exe",
                Log,
                ExecuteAsTool);
        }

        if (_resgenPath == null && !ExecuteAsTool)
        {
            // if Resgen.exe is not installed, just use the filename
            _resgenPath = String.Empty;
            return true;
        }

        // We may be passing this to the ResGen task (wrapper around resgen.exe), in which case
        // we want to pass just the path -- ResGen will attach the "resgen.exe" onto the end
        // itself.
        if (_resgenPath != null)
        {
            _resgenPath = Path.GetDirectoryName(_resgenPath);
        }

        return _resgenPath != null;
#else
        _resgenPath = string.Empty;
        return true;
#endif
    }

#if FEATURE_RESGEN
    /// <summary>
    /// Given an instance of the ResGen task with everything but the strongly typed
    /// resource-related parameters filled out, execute the task and return the result
    /// </summary>
    /// <param name="inputsToProcess">Input files to give to resgen.exe.</param>
    /// <param name="outputsToProcess">Output files to give to resgen.exe.</param>
    private bool TransformResourceFilesUsingResGen(List<ITaskItem> inputsToProcess, List<ITaskItem> outputsToProcess)
    {
        ErrorUtilities.VerifyThrow(inputsToProcess.Count != 0, "There should be resource files to process");
        ErrorUtilities.VerifyThrow(inputsToProcess.Count == outputsToProcess.Count, "The number of inputs and outputs should be equal");

        bool succeeded = true;

        // We need to do a whole lot of work to make sure that we're not overrunning the command line ... UNLESS
        // we're running ResGen 4.0 or later, which supports response files.
        if (!_resgenPath.Equals(Path.GetDirectoryName(NativeMethodsShared.GetLongFilePath(ToolLocationHelper.GetPathToDotNetFrameworkSdkFile("resgen.exe", TargetDotNetFrameworkVersion.Version35))), StringComparison.OrdinalIgnoreCase))
        {
            ResGen resGen = CreateResGenTaskWithDefaultParameters();

            resGen.InputFiles = inputsToProcess.ToArray();
            resGen.OutputFiles = outputsToProcess.ToArray();

            ITaskItem[] outputFiles = resGen.OutputFiles;

            succeeded = resGen.Execute();

            if (!succeeded)
            {
                foreach (ITaskItem outputFile in outputFiles)
                {
                    if (!FileSystems.Default.FileExists(outputFile.ItemSpec))
                    {
                        _unsuccessfullyCreatedOutFiles.Add(outputFile.ItemSpec);
                    }
                }
            }
        }
        else
        {
            int initialResourceIndex = 0;
            bool doneProcessingResources = false;
            CommandLineBuilderExtension resourcelessCommandBuilder = new CommandLineBuilderExtension();
            string resourcelessCommand = null;

            GenerateResGenCommandLineWithoutResources(resourcelessCommandBuilder);

            if (resourcelessCommandBuilder.Length > 0)
            {
                resourcelessCommand = resourcelessCommandBuilder.ToString();
            }

            while (!doneProcessingResources)
            {
                int numberOfResourcesToAdd = CalculateResourceBatchSize(inputsToProcess, outputsToProcess, resourcelessCommand, initialResourceIndex);
                ResGen resGen = CreateResGenTaskWithDefaultParameters();

                resGen.InputFiles = inputsToProcess.GetRange(initialResourceIndex, numberOfResourcesToAdd).ToArray();
                resGen.OutputFiles = outputsToProcess.GetRange(initialResourceIndex, numberOfResourcesToAdd).ToArray();

                ITaskItem[] outputFiles = resGen.OutputFiles;

                bool thisBatchSucceeded = resGen.Execute();

                // This batch failed, so add the failing resources from this batch to the list of failures
                if (!thisBatchSucceeded)
                {
                    foreach (ITaskItem outputFile in outputFiles)
                    {
                        if (!FileSystems.Default.FileExists(outputFile.ItemSpec))
                        {
                            _unsuccessfullyCreatedOutFiles.Add(outputFile.ItemSpec);
                        }
                    }
                }

                initialResourceIndex += numberOfResourcesToAdd;
                doneProcessingResources = initialResourceIndex == inputsToProcess.Count;
                succeeded = succeeded && thisBatchSucceeded;
            }
        }

        return succeeded;
    }

    /// <summary>
    /// Given the list of inputs and outputs, returns the number of resources (starting at the provided initial index)
    /// that can fit onto the commandline without exceeding MaximumCommandLength.
    /// </summary>
    private int CalculateResourceBatchSize(List<ITaskItem> inputsToProcess, List<ITaskItem> outputsToProcess, string resourcelessCommand, int initialResourceIndex)
    {
        CommandLineBuilderExtension currentCommand = new CommandLineBuilderExtension();

        if (!String.IsNullOrEmpty(resourcelessCommand))
        {
            currentCommand.AppendTextUnquoted(resourcelessCommand);
        }

        int i = initialResourceIndex;
        while (currentCommand.Length < s_maximumCommandLength && i < inputsToProcess.Count)
        {
            currentCommand.AppendFileNamesIfNotNull(
                    [inputsToProcess[i], outputsToProcess[i]],
                    ",");
            i++;
        }

        int numberOfResourcesToAdd;
        if (currentCommand.Length <= s_maximumCommandLength)
        {
            // We've successfully added all the rest.
            numberOfResourcesToAdd = i - initialResourceIndex;
        }
        else
        {
            // The last one added tossed us over the edge.
            numberOfResourcesToAdd = i - initialResourceIndex - 1;
        }

        return numberOfResourcesToAdd;
    }

    /// <summary>
    /// Given an instance of the ResGen task with everything but the strongly typed
    /// resource-related parameters filled out, execute the task and return the result
    /// </summary>
    /// <param name="inputsToProcess">Input files to give to resgen.exe.</param>
    /// <param name="outputsToProcess">Output files to give to resgen.exe.</param>
    private bool GenerateStronglyTypedResourceUsingResGen(List<ITaskItem> inputsToProcess, List<ITaskItem> outputsToProcess)
    {
        ErrorUtilities.VerifyThrow(inputsToProcess.Count == 1 && outputsToProcess.Count == 1, "For STR, there should only be one input and one output.");

        ResGen resGen = CreateResGenTaskWithDefaultParameters();

        resGen.InputFiles = inputsToProcess.ToArray();
        resGen.OutputFiles = outputsToProcess.ToArray();

        resGen.StronglyTypedNamespace = StronglyTypedNamespace;
        resGen.StronglyTypedClassName = StronglyTypedClassName;
        resGen.StronglyTypedFileName = StronglyTypedFileName;

        // Save the output file name -- ResGen will delete failing files
        ITaskItem outputFile = resGen.OutputFiles[0];

        _stronglyTypedResourceSuccessfullyCreated = resGen.Execute();

        if (!_stronglyTypedResourceSuccessfullyCreated && (resGen.OutputFiles == null || resGen.OutputFiles.Length == 0))
        {
            _unsuccessfullyCreatedOutFiles.Add(outputFile.ItemSpec);
        }

        // now need to set the defaults (if defaults were chosen) so that they can be
        // consumed by outside users
        StronglyTypedClassName = resGen.StronglyTypedClassName;
        StronglyTypedFileName = resGen.StronglyTypedFileName;

        return _stronglyTypedResourceSuccessfullyCreated;
    }

    /// <summary>
    /// Factoring out the setting of the default parameters to the
    /// ResGen task.
    /// </summary>
    private ResGen CreateResGenTaskWithDefaultParameters()
    {
        ResGen resGen = new ResGen();

        resGen.BuildEngine = BuildEngine;
        resGen.SdkToolsPath = _resgenPath;
        resGen.PublicClass = PublicClass;
        resGen.References = References;
        resGen.UseSourcePath = UseSourcePath;
        resGen.EnvironmentVariables = EnvironmentVariables;

        return resGen;
    }
#endif

    /// <summary>
    /// Check for parameter errors.
    /// </summary>
    /// <returns>true if parameters are valid</returns>
    private bool ValidateParameters()
    {
        // make sure that if the output resources were set, they exactly match the number of input sources
        if ((OutputResources != null) && (OutputResources.Length != Sources.Length))
        {
            _log.LogErrorWithCodeFromResources("General.TwoVectorsMustHaveSameLength", Sources.Length, OutputResources.Length, "Sources", "OutputResources");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true if everything is up to date and we don't need to do any work.
    /// </summary>
    private void GetResourcesToProcess(out List<string> inputsToProcess, out List<string> outputsToProcess, out List<string> cachedOutputFiles)
    {
        inputsToProcess = new List<string>();
        outputsToProcess = new List<string>();
        cachedOutputFiles = new List<string>();

        // decide what sources we need to build
        for (int i = 0; i < Sources.Length; ++i)
        {
            if (!File.Exists(Sources[i]))
            {
                // Error but continue with the files that do exist
                _log.LogErrorWithCodeFromResources("GenerateResource.ResourceNotFound", Sources[i]);
                _unsuccessfullyCreatedOutFiles.Add(OutputResources[i]);
            }
            else
            {
                inputsToProcess.Add(Sources[i]);
                outputsToProcess.Add(OutputResources[i]);
            }
        }
    }

    /// <summary>
    /// Checks if this list contain any duplicates.  Do this so we don't have any races where we have two
    /// threads trying to write to the same file simultaneously.
    /// </summary>
    /// <param name="originalList">A list that may have duplicates</param>
    /// <returns>Were there duplicates?</returns>
    private bool ContainsDuplicates(IList<string> originalList)
    {
        HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in originalList)
        {
            try
            {
                // Get the fully qualified path, to ensure two file names don't end up pointing to the same directory.
                if (!set.Add(Path.GetFullPath(item)))
                {
                    _log.LogErrorWithCodeFromResources("GenerateResource.DuplicateOutputFilenames", item);
                    return true;
                }
            }
            catch (InvalidOperationException e)
            {
                _log.LogErrorWithCodeFromResources("GenerateResource.InvalidFilename", item, e.Message);
                // Returning true causes us to not continue executing.
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Chars that should be ignored in the nicely justified block of base64
    /// </summary>
    private static readonly char[] s_specialChars = [' ', '\r', '\n'];

    /// <summary>
    /// Turns the nicely justified block of base64 found in a resx into a byte array.
    /// Copied from fx\src\winforms\managed\system\winforms\control.cs
    /// </summary>
    private static byte[] ByteArrayFromBase64WrappedString(string text)
    {
        if (text.IndexOfAny(s_specialChars) != -1)
        {
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                switch (text[i])
                {
                    case ' ':
                    case '\r':
                    case '\n':
                        break;
                    default:
                        sb.Append(text[i]);
                        break;
                }
            }
            return Convert.FromBase64String(sb.ToString());
        }
        else
        {
            return Convert.FromBase64String(text);
        }
    }

    /// <summary>
    /// Make sure that OutputResources has 1 file name for each name in Sources.
    /// </summary>
    private bool CreateOutputResourcesNames()
    {
        if (OutputResources == null)
        {
            OutputResources = new string[Sources.Length];
            int i = 0;
            try
            {
                for (i = 0; i < Sources.Length; ++i)
                {
                    OutputResources[i] = Path.ChangeExtension(Sources[i], ".resources");
                }
            }
            catch (ArgumentException e)
            {
                _log.LogErrorWithCodeFromResources("GenerateResource.InvalidFilename", Sources[i], e.Message);
                return false;
            }
        }

        // Now check for duplicates.
        if (ContainsDuplicates(OutputResources))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Remove any output resources that we didn't successfully create (due to error) from the
    /// OutputResources list. Keeps the ordering of OutputResources the same.
    /// </summary>
    /// <remarks>
    /// Q: Why didn't we keep a "successfully created" list instead, like in the Copy task does, which
    /// would save us doing the removal algorithm below?
    /// A: Because we want the ordering of OutputResources to be the same as the ordering passed in.
    /// Some items (the up to date ones) would be added to the successful output list first, and the other items
    /// are added during processing, so the ordering would change. We could fix that up, but it's better to do
    /// the fix up only in the rarer error case. If there were no errors, the algorithm below skips.</remarks>
    private void RemoveUnsuccessfullyCreatedResourcesFromOutputResources()
    {
        // Normally, there aren't any unsuccessful conversions.
        if (_unsuccessfullyCreatedOutFiles == null ||
            _unsuccessfullyCreatedOutFiles.Count == 0)
        {
            return;
        }

        // We only get here if there was at least one resource generation error.
        string[] temp = new string[OutputResources.Length - _unsuccessfullyCreatedOutFiles.Count];
        int copied = 0;
        int removed = 0;
        for (int i = 0; i < Sources.Length; i++)
        {
            // Check whether this one is in the bad list.
            if (removed < _unsuccessfullyCreatedOutFiles.Count &&
                _unsuccessfullyCreatedOutFiles.Contains(OutputResources[i]))
            {
                removed++;
            }
            else
            {
                // Copy it to the okay list.
                temp[copied] = OutputResources[i];
                copied++;
            }
        }
        OutputResources = temp;
    }
}

/// <summary>
/// This class handles the processing of source resource files into compiled resource files.
/// Its designed to be called from a separate AppDomain so that any files locked by ResXResourceReader
/// can be released.
/// </summary>
internal sealed class ProcessResourceFiles
{
    private Logger _logger;

    /// <summary>
    /// List of readers used for input.
    /// </summary>
    private List<ReaderInfo> _readers = new List<ReaderInfo>();

    /// <summary>
    /// The referenced assemblies
    /// </summary>
    private string[] _assemblyFiles;

    /// <summary>
    /// List of input files to process.
    /// </summary>
    private List<string> _inFiles;

    /// <summary>
    /// List of satellite input files to process.
    /// </summary>
    private List<string> _satelliteInFiles;

    /// <summary>
    /// List of output files to process.
    /// </summary>
    private List<string> _outFiles;

    private bool _usePreserializedResources;

    /// <summary>
    /// List of output files that we failed to create due to an error.
    /// See note in RemoveUnsuccessfullyCreatedResourcesFromOutputResources()
    /// </summary>
    internal ArrayList UnsuccessfullyCreatedOutFiles
    {
        get
        {
            _unsuccessfullyCreatedOutFiles ??= new ArrayList();
            return _unsuccessfullyCreatedOutFiles;
        }
    }
    private ArrayList _unsuccessfullyCreatedOutFiles;

    /// <summary>
    /// Indicates whether the resource reader should use the source file's
    /// directory to resolve relative file paths.
    /// </summary>
    private bool _useSourcePath = false;

    private bool _logWarningForBinaryFormatter = false;

    /// <summary>
    /// Process all files.
    /// </summary>
    public ProcessResourceFiles(
        Logger logger,
        string[] assemblyFilesList,
        List<string> inputs,
        List<string> satelliteInputs,
        List<string> outputs,
        bool sourcePath,
        bool usePreserializedResources,
        bool logWarningForBinaryFormatter)
    {
        _logger = logger;
        _assemblyFiles = assemblyFilesList;
        _inFiles = inputs;
        _satelliteInFiles = satelliteInputs;
        _outFiles = outputs;
        _useSourcePath = sourcePath;
        _readers = new List<ReaderInfo>();
        _usePreserializedResources = usePreserializedResources;
        _logWarningForBinaryFormatter = logWarningForBinaryFormatter;
    }

    public void Run()
    {
        for (int i = 0; i < _inFiles.Count; ++i)
        {
            string outputSpec = _outFiles[i];
            if (!ProcessFile(_inFiles[i], outputSpec))
            {
                // Since we failed, remove items from OutputResources.
                UnsuccessfullyCreatedOutFiles.Add(outputSpec);
            }
        }
    }

    public ProcessResourceFiles(
        Logger logger,
        string srcPath,
        string outPath,
        string[] references)
    {
        _logger = logger;
        _inFiles = [ srcPath ];
        _outFiles = [ outPath ];
        _assemblyFiles = references;
    }

    /// <summary>
    /// Read all resources from a file and write to a new file in the chosen format
    /// </summary>
    /// <remarks>Uses the input and output file extensions to determine their format</remarks>
    /// <param name="inFile">Input resources file</param>
    /// <param name="outFileOrDir">Output resources file or directory</param>
    /// <returns>True if conversion was successful, otherwise false</returns>
    private bool ProcessFile(string inFile, string outFileOrDir)
    {
        Format inFileFormat = GetFormat(inFile);
        if (inFileFormat == Format.Error)
        {
            // GetFormat would have logged an error.
            return false;
        }
        Format outFileFormat = GetFormat(outFileOrDir);
        if (outFileFormat == Format.Error)
        {
            return false;
        }

        _logger.LogMessageFromResources("GenerateResource.ProcessingFile", inFile, outFileOrDir);

        // Reset state
        _readers = new List<ReaderInfo>();

        try
        {
            ReadResources(inFile, _useSourcePath);
        }
        catch (InputFormatNotSupportedException)
        {
            _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                                                    "GenerateResource.CoreSupportsLimitedScenarios");
            return false;
        }
        catch (MSBuildResXException msbuildResXException)
        {
            _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                "General.InvalidResxFile", msbuildResXException.InnerException.ToString());
            return false;
        }
        catch (ArgumentException ae)
        {
            if (ae.InnerException is XmlException xe)
            {
                _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), xe.LineNumber,
                    xe.LinePosition, 0, 0, "General.InvalidResxFile", xe.Message);
            }
            else
            {
                _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                    "General.InvalidResxFile", ae.Message);
            }
            return false;
        }
        catch (TextFileException tfe)
        {
            // Used to pass back error context from ReadTextResources to here.
            _logger.LogErrorWithCodeFromResources(null, tfe.FileName, tfe.LineNumber, tfe.LinePosition, 1, 1,
                "GenerateResource.MessageTunnel", tfe.Message);
            return false;
        }
        catch (XmlException xe)
        {
            _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), xe.LineNumber,
                xe.LinePosition, 0, 0, "General.InvalidResxFile", xe.Message);
            return false;
        }
        catch (Exception e) when (
                                    e is SerializationException ||
                                    e is TargetInvocationException)
        {
            // DDB #9819
            // SerializationException and TargetInvocationException can occur when trying to deserialize a type from a resource format (typically with other exceptions inside)
            // This is a bug in the type being serialized, so the best we can do is dump diagnostic information and move on to the next input resource file.
            _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                "General.InvalidResxFile", e.Message);

            // Log the stack, so the problem with the type in the .resx is diagnosable by the customer
            _logger.LogErrorFromException(e); //, /* stack */ true, /* inner exceptions */ true, FileUtilities.GetFullPathNoThrow(inFile));
            return false;
        }
        catch (Exception e)
        {
            // Regular IO error
            _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                "General.InvalidResxFile", e.Message);
            return false;
        }

        string currentOutputFile = null;
        string currentOutputDirectory = null;
        bool currentOutputDirectoryAlreadyExisted = true;

        try
        {
            currentOutputFile = outFileOrDir;
            WriteResources(_readers[0], outFileOrDir);
        }
        catch (IOException io)
        {
            if (currentOutputFile != null)
            {
                _logger.LogErrorWithCodeFromResources("GenerateResource.CannotWriteOutput",
                    FileUtilities.GetFullPathNoThrow(currentOutputFile), io.Message);
                if (File.Exists(currentOutputFile))
                {
                    RemoveCorruptedFile(currentOutputFile);
                }
            }

            if (currentOutputDirectory != null &&
                !currentOutputDirectoryAlreadyExisted)
            {
                // Do not annoy the user by removing an empty directory we did not create.
                try
                {
                    Directory.Delete(currentOutputDirectory); // Remove output directory if empty
                }
                catch (Exception)
                {
                    // Fail silently (we are not even checking if the call to File.Delete succeeded)
                }
            }
            return false;
        }
        catch (Exception e) when (
                                    e is SerializationException ||
                                    e is TargetInvocationException)
        {
            // DDB #9819
            // SerializationException and TargetInvocationException can occur when trying to serialize a type into a resource format (typically with other exceptions inside)
            // This is a bug in the type being serialized, so the best we can do is dump diagnostic information and move on to the next input resource file.
            _logger.LogErrorWithCodeFromResources("GenerateResource.CannotWriteOutput",
                FileUtilities.GetFullPathNoThrow(inFile), e.Message); // Input file is more useful to log

            // Log the stack, so the problem with the type in the .resx is diagnosable by the customer
            _logger.LogErrorFromException(e); // , /* stack */ true, /* inner exceptions */ true, FileUtilities.GetFullPathNoThrow(inFile));
            return false;
        }
        catch (Exception e)
        {
            // Regular IO error
            _logger.LogErrorWithCodeFromResources("GenerateResource.CannotWriteOutput",
                FileUtilities.GetFullPathNoThrow(currentOutputFile), e.Message);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the resources out of the specified file and populates the
    /// resources hashtable.
    /// </summary>
    /// <param name="filename">Filename to load</param>
    /// <param name="shouldUseSourcePath">Whether to resolve paths in the
    /// resources file relative to the resources file location</param>
    /// <param name="outFileOrDir"> Output file or directory. </param>
    private void ReadResources(string filename, bool shouldUseSourcePath)
    {
        Format format = GetFormat(filename);

        ReaderInfo reader = new ReaderInfo();
        _readers.Add(reader);
        switch (format)
        {
            case Format.Text:
                ReadTextResources(reader, filename);
                break;

            case Format.XML:
                // On full framework, the default is to use the longstanding
                // deserialize/reserialize approach. On Core, always use the new
                // preserialized approach.
                foreach (IResource resource in MSBuildResXReader.GetResourcesFromFile(filename, shouldUseSourcePath, _logger, _logWarningForBinaryFormatter))
                {
                    AddResource(reader, resource, filename, 0, 0);
                }
                break;

            case Format.Binary:
                throw new InputFormatNotSupportedException("Reading resources from binary .resources not supported on .NET Core MSBuild");

            default:
                // We should never get here, we've already checked the format
                Debug.Fail("Unknown format " + format.ToString());
                return;
        }
        _logger.LogMessageFromResources(MessageImportance.Low, "GenerateResource.ReadResourceMessage", reader.resources.Count, filename);
    }

    /// <summary>
    /// Remove a corrupted file, with error handling and a warning if we fail.
    /// </summary>
    /// <param name="filename">Full path to file to delete</param>
    private void RemoveCorruptedFile(string filename)
    {
        _logger.LogMessageFromResources("GenerateResource.CorruptOutput", FileUtilities.GetFullPathNoThrow(filename));
        try
        {
            File.Delete(filename);
        }
        catch (Exception deleteException)
        {
            _logger.LogWarningWithCodeFromResources("GenerateResource.DeleteCorruptOutputFailed", FileUtilities.GetFullPathNoThrow(filename), deleteException.Message);

            throw;
        }
    }

    /// <summary>
    /// Figure out the format of an input resources file from the extension
    /// </summary>
    /// <param name="filename">Input resources file</param>
    /// <returns>Resources format</returns>
    private Format GetFormat(string filename)
    {
        string extension;
        try
        {
            extension = Path.GetExtension(filename);
        }
        catch (ArgumentException ex)
        {
            _logger.LogErrorWithCodeFromResources("GenerateResource.InvalidFilename", filename, ex.Message);
            return Format.Error;
        }

        if (string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".restext", StringComparison.OrdinalIgnoreCase))
        {
            return Format.Text;
        }
        else if (string.Equals(extension, ".resx", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".resw", StringComparison.OrdinalIgnoreCase))
        {
            return Format.XML;
        }
        else if (string.Equals(extension, ".resources", StringComparison.OrdinalIgnoreCase))
        {
            return Format.Binary;
        }
        else
        {
            _logger.LogErrorWithCodeFromResources("GenerateResource.UnknownFileExtension", Path.GetExtension(filename), filename);
            return Format.Error;
        }
    }

    /// <summary>
    /// Text files are just name/value pairs.  ResText is the same format
    /// with a unique extension to work around some ambiguities with MSBuild
    /// ResX is our existing XML format from V1.
    /// </summary>
    private enum Format
    {
        Text, // .txt or .restext
        XML, // .resx
        Binary, // .resources
        Error, // anything else
    }


    /// <summary>
    /// Write resources from the resources ArrayList to the specified output file
    /// </summary>
    /// <param name="reader">Reader information</param>
    /// <param name="filename">Output resources file</param>
    private void WriteResources(ReaderInfo reader, string filename)
    {
        Format format = GetFormat(filename);
        switch (format)
        {
            case Format.Text:
                WriteTextResources(reader, filename);
                break;

            case Format.XML:
                _logger.LogError(format.ToString() + " not supported on .NET Core MSBuild");
                break;

            case Format.Binary:
                WriteBinaryResources(reader, filename);
                break;

            default:
                // We should never get here, we've already checked the format
                Debug.Fail("Unknown format " + format.ToString());
                break;
        }
    }

    private void WriteBinaryResources(ReaderInfo reader, string filename)
    {
        try
        {
            WriteResources(reader, new PreserializedResourceWriter(new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None))); // closes writer for us
        }
        catch
        {
            // We may have partially written some string resources to a file, then bailed out
            // because we encountered a non-string resource but don't meet the prereqs.
            RemoveCorruptedFile(filename);
        }
    }


    /// <summary>
    /// Read resources from a text format file.
    /// </summary>
    /// <param name="reader">Reader info.</param>
    /// <param name="fileName">Input resources filename.</param>
    private void ReadTextResources(ReaderInfo reader, string fileName)
    {
        // Check for byte order marks in the beginning of the input file, but
        // default to UTF-8.
        using var fs = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        using (LineNumberStreamReader sr = new LineNumberStreamReader(fs, new UTF8Encoding(true), true))
        {
            StringBuilder name = new StringBuilder(255);
            StringBuilder value = new StringBuilder(2048);

            int ch = sr.Read();
            while (ch != -1)
            {
                if (ch == '\n' || ch == '\r')
                {
                    ch = sr.Read();
                    continue;
                }

                // Skip over commented lines or ones starting with whitespace.
                // Support LocStudio INF format's comment char, ';'
                if (ch == '#' || ch == '\t' || ch == ' ' || ch == ';')
                {
                    // comment char (or blank line) - skip line.
                    sr.ReadLine();
                    ch = sr.Read();
                    continue;
                }
                // Note that in Beta of version 1 we recommended users should put a [strings]
                // section in their file.  Now it's completely unnecessary and can
                // only cause bugs.  We will not parse anything using '[' stuff now
                // and we should give a warning about seeing [strings] stuff.
                // In V1.1 or V2, we can rip this out completely, I hope.
                if (ch == '[')
                {
                    string skip = sr.ReadLine();
                    if (skip.Equals("strings]"))
                    {
                        _logger.LogWarningWithCodeFromResources(null, fileName, sr.LineNumber - 1, 1, 0, 0, "GenerateResource.ObsoleteStringsTag");
                    }
                    else
                    {
                        throw new TextFileException(_logger.FormatResourceString("GenerateResource.UnexpectedInfBracket", "[" + skip), fileName, sr.LineNumber - 1, 1);
                    }
                    ch = sr.Read();
                    continue;
                }

                // Read in name
                name.Length = 0;
                while (ch != '=')
                {
                    if (ch == '\r' || ch == '\n')
                    {
                        throw new TextFileException(_logger.FormatResourceString("GenerateResource.NoEqualsInLine", name), fileName, sr.LineNumber, sr.LinePosition);
                    }

                    name.Append((char)ch);
                    ch = sr.Read();
                    if (ch == -1)
                    {
                        break;
                    }
                }
                if (name.Length == 0)
                {
                    throw new TextFileException(_logger.FormatResourceString("GenerateResource.NoNameInLine"), fileName, sr.LineNumber, sr.LinePosition);
                }

                // For the INF file, we must allow a space on both sides of the equals
                // sign.  Deal with it.
                if (name[name.Length - 1] == ' ')
                {
                    name.Length--;
                }
                ch = sr.Read(); // move past =

                // If it exists, move past the first space after the equals sign.
                if (ch == ' ')
                {
                    ch = sr.Read();
                }

                // Read in value
                value.Length = 0;

                while (ch != -1)
                {
                    // Did we read @"\r" or @"\n"?
                    bool quotedNewLine = false;
                    if (ch == '\\')
                    {
                        ch = sr.Read();
                        switch (ch)
                        {
                            case '\\':
                                // nothing needed
                                break;
                            case 'n':
                                ch = '\n';
                                quotedNewLine = true;
                                break;
                            case 'r':
                                ch = '\r';
                                quotedNewLine = true;
                                break;
                            case 't':
                                ch = '\t';
                                break;
                            case '"':
                                ch = '\"';
                                break;
                            case 'u':
                                char[] hex = new char[4];
                                int numChars = 4;
                                int index = 0;
                                while (numChars > 0)
                                {
                                    int n = sr.Read(hex, index, numChars);
                                    if (n == 0)
                                    {
                                        throw new TextFileException(_logger.FormatResourceString("GenerateResource.InvalidEscape", name.ToString(), (char)ch), fileName, sr.LineNumber, sr.LinePosition);
                                    }

                                    index += n;
                                    numChars -= n;
                                }
                                try
                                {
                                    ch = (char)ushort.Parse(new string(hex), NumberStyles.HexNumber, CultureInfo.CurrentCulture);
                                }
                                catch (FormatException)
                                {
                                    // We know about this one...
                                    throw new TextFileException(_logger.FormatResourceString("GenerateResource.InvalidHexEscapeValue", name.ToString(), new string(hex)), fileName, sr.LineNumber, sr.LinePosition);
                                }
                                catch (OverflowException)
                                {
                                    // We know about this one, too...
                                    throw new TextFileException(_logger.FormatResourceString("GenerateResource.InvalidHexEscapeValue", name.ToString(), new string(hex)), fileName, sr.LineNumber, sr.LinePosition);
                                }
                                quotedNewLine = (ch == '\n' || ch == '\r');
                                break;

                            default:
                                throw new TextFileException(_logger.FormatResourceString("GenerateResource.InvalidEscape", name.ToString(), (char)ch), fileName, sr.LineNumber, sr.LinePosition);
                        }
                    }

                    // Consume endline...
                    //   Endline can be \r\n or \n.  But do not treat a
                    //   quoted newline (ie, @"\r" or @"\n" in text) as a
                    //   real new line.  They aren't the end of a line.
                    if (!quotedNewLine)
                    {
                        if (ch == '\r')
                        {
                            ch = sr.Read();
                            if (ch == -1)
                            {
                                break;
                            }
                            else if (ch == '\n')
                            {
                                ch = sr.Read();
                                break;
                            }
                        }
                        else if (ch == '\n')
                        {
                            ch = sr.Read();
                            break;
                        }
                    }

                    value.Append((char)ch);
                    ch = sr.Read();
                }

                // Note that value can be an empty string
                AddResource(reader, name.ToString(), value.ToString(), fileName, sr.LineNumber, sr.LinePosition);
            }
        }
    }

    /// <summary>
    /// Write resources to an XML or binary format resources file.
    /// </summary>
    /// <remarks>Closes writer automatically</remarks>
    /// <param name="reader">Reader information</param>
    /// <param name="writer">Appropriate IResourceWriter</param>
    private void WriteResources(ReaderInfo reader, PreserializedResourceWriter writer)
    {
        Exception capturedException = null;
        try
        {
            foreach (IResource entry in reader.resources)
            {
                entry.AddTo(writer);
            }
        }
        catch (Exception e)
        {
            capturedException = e; // Rethrow this after catching exceptions thrown by Close().
        }
        finally
        {
            if (capturedException == null)
            {
                writer.Dispose(); // If this throws, exceptions will be caught upstream.
            }
            else
            {
                // It doesn't hurt to call Close() twice. In the event of a full disk, we *need* to call Close() twice.
                // In that case, the first time we catch an exception indicating that the XML written to disk is malformed,
                // specifically an InvalidOperationException: "Token EndElement in state Error would result in an invalid XML document."
                try { writer.Dispose(); }
                catch (Exception) { } // We aggressively catch all exception types since we already have one we will throw.

                // The second time we catch the out of disk space exception.
                try { writer.Dispose(); }
                catch (Exception) { } // We aggressively catch all exception types since we already have one we will throw.
                throw capturedException; // In the event of a full disk, this is an out of disk space IOException.
            }
        }
    }

    /// <summary>
    /// Write resources to a text format resources file
    /// </summary>
    /// <param name="reader">Reader information</param>
    /// <param name="fileName">Output resources file</param>
    private void WriteTextResources(ReaderInfo reader, string fileName)
    {
        using (StreamWriter writer = FileUtilities.OpenWrite(fileName, false, Encoding.UTF8))
        {
            foreach (IResource resource in reader.resources)
            {
                LiveObjectResource entry = resource as LiveObjectResource;

                string key = entry?.Name;
                object v = entry?.Value;
                string value = v as string;
                if (value == null)
                {
                    _logger.LogErrorWithCodeFromResources(null, fileName, 0, 0, 0, 0, "GenerateResource.OnlyStringsSupported", key, v?.GetType().FullName);
                }
                else
                {
                    // Escape any special characters in the String.
                    value = value.Replace("\\", "\\\\");
                    value = value.Replace("\n", "\\n");
                    value = value.Replace("\r", "\\r");
                    value = value.Replace("\t", "\\t");

                    writer.WriteLine("{0}={1}", key, value);
                }
            }
        }
    }

    /// <summary>
    /// Add a resource from a text file to the internal data structures
    /// </summary>
    /// <param name="reader">Reader information</param>
    /// <param name="name">Resource name</param>
    /// <param name="value">Resource value</param>
    /// <param name="inputFileName">Input file for messages</param>
    /// <param name="lineNumber">Line number for messages</param>
    /// <param name="linePosition">Column number for messages</param>
    private void AddResource(ReaderInfo reader, string name, object value, string inputFileName, int lineNumber, int linePosition)
    {
        LiveObjectResource entry = new LiveObjectResource(name, value);

        AddResource(reader, entry, inputFileName, lineNumber, linePosition);
    }

    private void AddResource(ReaderInfo reader, IResource entry, string inputFileName, int lineNumber, int linePosition)
    {
        if (reader.resourcesHashTable.ContainsKey(entry.Name))
        {
            _logger.LogWarningWithCodeFromResources(null, inputFileName, lineNumber, linePosition, 0, 0, "GenerateResource.DuplicateResourceName", entry.Name);
            return;
        }

        reader.resources.Add(entry);
        reader.resourcesHashTable.Add(entry.Name, entry);
    }

    /// <summary>
    /// Add a resource from an XML or binary format file to the internal data structures
    /// </summary>
    /// <param name="reader">Reader information.</param>
    /// <param name="name">Resource name.</param>
    /// <param name="value">Resource value.</param>
    /// <param name="inputFileName">Input file for messages.</param>
    private void AddResource(ReaderInfo reader, string name, object value, string inputFileName)
    {
        AddResource(reader, name, value, inputFileName, 0, 0);
    }

    internal sealed class ReaderInfo
    {
        // We use a list to preserve the resource ordering (primarily for easier testing),
        // but also use a hash table to check for duplicate names.
        public List<IResource> resources;
        public Dictionary<string, IResource> resourcesHashTable;

        public ReaderInfo()
        {
            resources = new List<IResource>();
            resourcesHashTable = new Dictionary<string, IResource>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Custom StreamReader that provides detailed position information,
    /// used when reading text format resources
    /// </summary>
    internal sealed class LineNumberStreamReader : StreamReader
    {
        // Line numbers start from 1, as well as line position.
        // For better error reporting, set line number to 1 and col to 0.
        private int _lineNumber;
        private int _col;

        internal LineNumberStreamReader(Stream fileStream, Encoding encoding, bool detectEncoding)
            : base(fileStream, encoding, detectEncoding)
        {
            _lineNumber = 1;
            _col = 0;
        }

        internal LineNumberStreamReader(Stream stream)
            : base(stream)
        {
            _lineNumber = 1;
            _col = 0;
        }

        public override int Read()
        {
            int ch = base.Read();
            if (ch != -1)
            {
                _col++;
                if (ch == '\n')
                {
                    _lineNumber++;
                    _col = 0;
                }
            }
            return ch;
        }

        public override int Read([In, Out] char[] chars, int index, int count)
        {
            int r = base.Read(chars, index, count);
            for (int i = 0; i < r; i++)
            {
                if (chars[i + index] == '\n')
                {
                    _lineNumber++;
                    _col = 0;
                }
                else
                {
                    _col++;
                }
            }
            return r;
        }

        public override string ReadLine()
        {
            string s = base.ReadLine();
            if (s != null)
            {
                _lineNumber++;
                _col = 0;
            }
            return s;
        }

        public override string ReadToEnd()
        {
            throw new NotImplementedException("NYI");
        }

        internal int LineNumber
        {
            get { return _lineNumber; }
        }

        internal int LinePosition
        {
            get { return _col; }
        }
    }

    /// <summary>
    /// For flow of control and passing sufficient error context back
    /// from ReadTextResources
    /// </summary>
    internal sealed class TextFileException : Exception
    {
        private string fileName;
        private int lineNumber;
        private int column;

#if NET8_0_OR_GREATER
        [Obsolete(DiagnosticId = "SYSLIB0051")]
#endif
        private TextFileException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

        internal TextFileException(string message, string fileName, int lineNumber, int linePosition)
            : base(message)
        {
            this.fileName = fileName;
            this.lineNumber = lineNumber;
            column = linePosition;
        }

        internal string FileName
        {
            get { return fileName; }
        }

        internal int LineNumber
        {
            get { return lineNumber; }
        }

        internal int LinePosition
        {
            get { return column; }
        }
    }
}
