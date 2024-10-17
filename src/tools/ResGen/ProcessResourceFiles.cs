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
    private readonly Logger Log;

    // Indicates whether the resource reader should use the source file's
    // directory to resolve relative file paths.
    private bool _useSourcePath = false;

    // List of those output resources that were not actually created, due to an error
    private ArrayList _unsuccessfullyCreatedOutFiles = new ArrayList();

    // Storage for names of *all files* written to disk.
    private ArrayList _filesWritten = new ArrayList();

    // When true, a separate AppDomain is always created.
    private bool _neverLockTypeAssemblies = false;

    // Newest uncorrelated input,
    // or null if not yet determined
    private string _newestUncorrelatedInput;

    // Write time of newest uncorrelated input
    // DateTime.MinValue indicates "missing" iff _newestUncorrelatedInput != null
    private DateTime _newestUncorrelatedInputWriteTime;

    // The targets may pass in the path to the SDKToolsPath. If so this should be used to generate the commandline
    // for logging purposes.  Also, when ExecuteAsTool is true, it determines where the system goes looking for resgen.exe
    private string _sdkToolsPath;

    // True if the resource generation should be sent out-of-proc to resgen.exe; false otherwise.  Defaults to true
    // because we want to execute out-of-proc when ToolsVersion is < 4.0, and the earlier targets files don't know
    // about this property.
    private bool _executeAsTool = true;

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
    private List<string> _satelliteInputs;

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
        set { _useSourcePath = value; }
        get { return _useSourcePath; }
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
    /// (default = false)
    /// When true, a new AppDomain is always created to evaluate the .resx files.
    /// When false, a new AppDomain is created only when it looks like a user's
    ///  assembly is referenced by the .resx.
    /// </summary>
    public bool NeverLockTypeAssemblies
    {
        set { _neverLockTypeAssemblies = value; }
        get { return _neverLockTypeAssemblies; }
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
    /// Property to allow multitargeting of ResolveComReferences:  If true, tlbimp.exe and
    /// aximp.exe from the appropriate target framework will be run out-of-proc to generate
    /// the necessary wrapper assemblies.
    /// </summary>
    public bool ExecuteAsTool
    {
        set { _executeAsTool = value; }
        get { return _executeAsTool; }
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
            Log.LogMessageFromResources(MessageImportance.Low, "GenerateResource.NoSources");
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
            if (!Log.HasLoggedErrors)
            {
                if (cachedOutputFiles.Count > 0)
                {
                    OutputResources = cachedOutputFiles.ToArray();
                }

                Log.LogMessageFromResources("GenerateResource.NothingOutOfDate");
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
                Logger.LogErrorFromResources("GenerateResource.ProcessingFile", inputsToProcess[index], outputsToProcess[index]);
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

            // Check for the mark of the web on all possibly-exploitable files
            // to be processed.
            bool dangerousResourceFound = false;

            foreach (string source in Sources)
            {
                if (IsDangerous(source))
                {
                    Log.LogErrorWithCodeFromResources("GenerateResource.MOTW", source);
                    dangerousResourceFound = true;
                }
            }

            if (dangerousResourceFound)
            {
                // Do no further processing
                return false;
            }

            ProcessResourceFiles process = new ProcessResourceFiles();

            process.Run(new Logger(),
                        References,
                        inputsToProcess,
                        _satelliteInputs,
                        outputsToProcess,
                        UseSourcePath,
                        UsePreserializedResources,
                        OutputDirectory,
                        WarnOnBinaryFormatterUse);

            if (process.UnsuccessfullyCreatedOutFiles != null)
            {
                foreach (string item in process.UnsuccessfullyCreatedOutFiles)
                {
                    _unsuccessfullyCreatedOutFiles.Add(item);
                }
            }
        }

        RemoveUnsuccessfullyCreatedResourcesFromOutputResources();

        RecordFilesWritten();

        //MSBuildEventSource.Log.GenerateResourceOverallStop();

        return !Log.HasLoggedErrors && outOfProcExecutionSucceeded;
    }

#if FEATURE_RESXREADER_LIVEDESERIALIZATION
    private static readonly bool AllowMOTW = !NativeMethodsShared.IsWindows || (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\.NETFramework\SDK", "AllowProcessOfUntrustedResourceFiles", null) is string allowUntrustedFiles && allowUntrustedFiles.Equals("true", StringComparison.OrdinalIgnoreCase));

    private const string CLSID_InternetSecurityManager = "7b8a2d94-0ac9-11d1-896c-00c04fb6bfc4";
    private const uint ZoneInternet = 3;
    private static IInternetSecurityManager internetSecurityManager = null;
#endif

    // Resources can have arbitrarily serialized objects in them which can execute arbitrary code
    // so check to see if we should trust them before analyzing them
    private static bool IsDangerous(string filename)
    {
#if !FEATURE_RESXREADER_LIVEDESERIALIZATION
        return false;
    }
#else
        // If they are opted out, there's no work to do
        if (AllowMOTW)
        {
            return false;
        }

        // First check the zone, if they are not an untrusted zone, they aren't dangerous
        if (internetSecurityManager == null)
        {
            Type iismType = Type.GetTypeFromCLSID(new Guid(CLSID_InternetSecurityManager));
            internetSecurityManager = (IInternetSecurityManager)Activator.CreateInstance(iismType);
        }

        int zone;
        internetSecurityManager.MapUrlToZone(Path.GetFullPath(filename), out zone, 0);
        if (zone < ZoneInternet)
        {
            return false;
        }

        // By default all file types that get here are considered dangerous
        bool dangerous = true;

        string extension = Path.GetExtension(filename);
        if (String.Equals(extension, ".resx", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(extension, ".resw", StringComparison.OrdinalIgnoreCase))
        {
            // XML files are only dangerous if there are unrecognized objects in them
            dangerous = false;

            using FileStream stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
            using XmlTextReader reader = new XmlTextReader(stream);
            reader.DtdProcessing = DtdProcessing.Ignore;
            reader.XmlResolver = null;
            try
            {
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        string s = reader.LocalName;

                        // We only want to parse data nodes,
                        // the mimetype attribute gives the serializer
                        // that's requested.
                        if (reader.LocalName.Equals("data"))
                        {
                            if (reader["mimetype"] != null)
                            {
                                dangerous = true;
                            }
                        }
                        else if (reader.LocalName.Equals("metadata"))
                        {
                            if (reader["mimetype"] != null)
                            {
                                dangerous = true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // If we hit an error while parsing assume there's a dangerous type in this file.
                dangerous = true;
            }
            stream.Close();
        }

        return dangerous;
    }

    /// <summary>
    /// For setting OutputResources and ensuring it can be read after the second AppDomain has been unloaded.
    /// </summary>
    /// <param name="remoteValues">ITaskItems in another AppDomain</param>
    /// <returns></returns>
    private static ITaskItem[] CloneValuesInThisAppDomain(IList<ITaskItem> remoteValues)
    {
        ITaskItem[] clonedOutput = new ITaskItem[remoteValues.Count];
        for (int i = 0; i < remoteValues.Count; i++)
        {
            clonedOutput[i] = new TaskItem(remoteValues[i]);
        }

        return clonedOutput;
    }

    /// <summary>
    /// Remember this TaskItem so that we can disconnect it when this Task has finished executing
    /// Only if we're passing TaskItems to another AppDomain is this necessary. This call
    /// Will make that determination for you.
    /// </summary>
    private void RecordItemsForDisconnectIfNecessary(IEnumerable<ITaskItem> items)
    {
        if (_remotedTaskItems != null && items != null)
        {
            // remember that we need to disconnect these items
            _remotedTaskItems.AddRange(items);
        }
    }
#endif // FEATURE_APPDOMAIN

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
            Log.LogErrorWithCodeFromResources("General.TwoVectorsMustHaveSameLength", Sources.Length, OutputResources.Length, "Sources", "OutputResources");
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
                Log.LogErrorWithCodeFromResources("GenerateResource.ResourceNotFound", Sources[i]);
                _unsuccessfullyCreatedOutFiles.Add(OutputResources[i]);
            }
            else
            {
                // check to see if the output resources file (and, if it is a .resx, any linked files)
                // is up to date compared to the input file
                if (ShouldRebuildResgenOutputFile(Sources[i], OutputResources[i]))
                {
                    inputsToProcess.Add(Sources[i]);
                    outputsToProcess.Add(OutputResources[i]);
                }
            }
        }
    }

    /// <summary>
    /// Given a cached portable library that is up to date, create ITaskItems to represent the output of the task, as if we did real work.
    /// </summary>
    /// <param name="library">The portable library cache entry to extract output files and metadata from.</param>
    /// <param name="cachedOutputFiles">List of output files produced from the cache.</param>
    private void AppendCachedOutputTaskItems(ResGenDependencies.PortableLibraryFile library, List<ITaskItem> cachedOutputFiles)
    {
        foreach (string outputFileName in library.OutputFiles)
        {
            ITaskItem item = new TaskItem(outputFileName);
            item.SetMetadata("ResourceIndexName", library.AssemblySimpleName);
            if (library.NeutralResourceLanguage != null)
            {
                item.SetMetadata("NeutralResourceLanguage", library.NeutralResourceLanguage);
            }

            cachedOutputFiles.Add(item);
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
                    Log.LogErrorWithCodeFromResources("GenerateResource.DuplicateOutputFilenames", item);
                    return true;
                }
            }
            catch (InvalidOperationException e)
            {
                Log.LogErrorWithCodeFromResources("GenerateResource.InvalidFilename", item, e.Message);
                // Returning true causes us to not continue executing.
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines if the given output file is up to date with respect to the
    /// the given input file by comparing timestamps of the two files as well as
    /// (if the source is a .resx) the linked files inside the .resx file itself
    /// </summary>
    /// <param name="sourceFilePath"></param>
    /// <param name="outputFilePath"></param>
    /// <returns></returns>
    private bool ShouldRebuildResgenOutputFile(string sourceFilePath, string outputFilePath)
    {
        // See if any uncorrelated inputs are missing before checking source and output file timestamps.
        // Only do this if we already checked the uncorrelated inputs, since
        // typically, it's the .resx's that are out of date so we want to check those first.
        if (_newestUncorrelatedInput != null && _newestUncorrelatedInputWriteTime == DateTime.MinValue)
        {
            // An uncorrelated input is missing; need to build
            // We logged this once already, when we found it
            return true;
        }

        DateTime outputTime = NativeMethodsShared.GetLastWriteFileUtcTime(outputFilePath);

        // Quick check to see if any uncorrelated input is newer in which case we can avoid checking source file timestamp
        if (_newestUncorrelatedInput != null && _newestUncorrelatedInputWriteTime > outputTime)
        {
            // An uncorrelated input is newer, need to build
            Log.LogMessageFromResources(MessageImportance.Low, "GenerateResource.InputNewer", _newestUncorrelatedInput, outputFilePath);
            return true;
        }

        DateTime sourceTime = NativeMethodsShared.GetLastWriteFileUtcTime(sourceFilePath);

        string extension = Path.GetExtension(sourceFilePath);
        if (!string.Equals(extension, ".resx", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".resw", StringComparison.OrdinalIgnoreCase))
        {
            // If source file is NOT a .resx, for example a .restext file,
            // timestamp checking is simple, because there's no linked files to examine, and no references.
            return NeedToRebuildSourceFile(sourceFilePath, sourceTime, outputFilePath, outputTime);
        }

        // OK, we have a .resx file

        // PERF: Regardless of whether the outputFile exists, if the source file is a .resx
        // go ahead and retrieve it from the cache. This is because we want the cache
        // to be populated so that incremental builds can be fast.
        // Note that this is a trade-off: clean builds will be slightly slower. However,
        // for clean builds we're about to read in this very same .resx file so reading
        // it now will page it in. The second read should be cheap.
        ResGenDependencies.ResXFile resxFileInfo;
        try
        {
            resxFileInfo = _cache.GetResXFileInfo(sourceFilePath, UsePreserializedResources, Log, WarnOnBinaryFormatterUse);
        }
        catch (Exception e) when (!ExceptionHandling.NotExpectedIoOrXmlException(e) || e is MSBuildResXException)
        {
            // Return true, so that resource processing will display the error
            // No point logging a duplicate error here as well
            return true;
        }

        // if the .resources file is out of date even just with respect to the .resx or
        // the additional inputs, we don't need to go to the point of checking the linked files.
        if (NeedToRebuildSourceFile(sourceFilePath, sourceTime, outputFilePath, outputTime))
        {
            // This already logged the reason
            return true;
        }

        // The .resources is up to date with respect to the .resx file -
        // we need to compare timestamps for each linked file inside
        // the .resx file itself
        if (resxFileInfo.LinkedFiles != null)
        {
            foreach (string linkedFilePath in resxFileInfo.LinkedFiles)
            {
                DateTime linkedFileTime = NativeMethodsShared.GetLastWriteFileUtcTime(linkedFilePath);

                if (linkedFileTime == DateTime.MinValue)
                {
                    // Linked file is missing - force a build, so that resource generation
                    // will produce a nice error message
                    Log.LogMessageFromResources(MessageImportance.Low, "GenerateResource.LinkedInputDoesntExist", linkedFilePath);
                    return true;
                }

                if (linkedFileTime > outputTime)
                {
                    // Linked file is newer, need to build
                    Log.LogMessageFromResources(MessageImportance.Low, "GenerateResource.LinkedInputNewer", linkedFilePath, outputFilePath);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the output does not exist, if the provided source is newer than the output,
    /// or if any of the set of additional inputs is newer than the output.  Otherwise, returns false.
    /// </summary>
    private bool NeedToRebuildSourceFile(string sourceFilePath, DateTime sourceTime, string outputFilePath, DateTime outputTime)
    {
        if (outputTime == DateTime.MinValue)
        {
            // Output file is missing, need to build
            Log.LogMessageFromResources(MessageImportance.Low, "GenerateResource.OutputDoesntExist", outputFilePath);
            return true;
        }

        if (sourceTime > outputTime)
        {
            // Source file is newer, need to build
            Log.LogMessageFromResources(MessageImportance.Low, "GenerateResource.InputNewer", sourceFilePath, outputFilePath);
            return true;
        }

        UpdateNewestUncorrelatedInputWriteTime();
        if (_newestUncorrelatedInput != null && _newestUncorrelatedInputWriteTime > outputTime)
        {
            // An uncorrelated input is newer, need to build
            Log.LogMessageFromResources(MessageImportance.Low, "GenerateResource.InputNewer", _newestUncorrelatedInput, outputFilePath);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the newest last write time among the references and additional inputs.
    /// If any do not exist, returns DateTime.MaxValue so that resource generation produces a nice error.
    /// </summary>
    private void UpdateNewestUncorrelatedInputWriteTime()
    {
        if (_newestUncorrelatedInput != null)
        {
            // We already did this
            return;
        }

        // Check the timestamp of each of the passed-in references to find the newest;
        // and then the additional inputs
        ITaskItem[] inputs = this.References ?? [..(this.AdditionalInputs ?? [])];

        foreach (ITaskItem input in inputs)
        {
            DateTime time = NativeMethodsShared.GetLastWriteFileUtcTime(input.ItemSpec);

            if (time == DateTime.MinValue)
            {
                // File does not exist: force a build to produce an error message
                _newestUncorrelatedInput = input.ItemSpec;
                _newestUncorrelatedInputWriteTime = time;
                // Log it here so it's logged only once for all inputs
                Log.LogMessageFromResources(MessageImportance.Low, "GenerateResource.InputDoesntExist", _newestUncorrelatedInput);
                return;
            }

            if (time > _newestUncorrelatedInputWriteTime)
            {
                _newestUncorrelatedInput = input.ItemSpec;
                _newestUncorrelatedInputWriteTime = time;
            }
        }
    }

#if FEATURE_APPDOMAIN
    /// <summary>
    /// Make the decision about whether a separate AppDomain is needed.
    /// If this algorithm is unsure about whether a separate AppDomain is
    /// needed, it should always err on the side of returning 'true'. This
    /// is because a separate AppDomain, while slow to create, is always safe.
    /// </summary>
    /// <returns></returns>
    private bool NeedSeparateAppDomain()
    {
        if (NeverLockTypeAssemblies)
        {
            Log.LogMessageFromResources(MessageImportance.Low, "GenerateResource.SeparateAppDomainBecauseNeverLockTypeAssembliesTrue");
            return true;
        }

        foreach (ITaskItem source in Sources)
        {
            string extension = Path.GetExtension(source.ItemSpec);

            if (String.Equals(extension, ".resources.dll", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (String.Equals(extension, ".resx", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(extension, ".resw", StringComparison.OrdinalIgnoreCase))
            {
                XmlReader reader = null;
                string name = null;

                try
                {
                    XmlReaderSettings readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, CloseInput = true };

                    FileStream fs = File.OpenRead(source.ItemSpec);
                    reader = XmlReader.Create(fs, readerSettings);

                    while (reader.Read())
                    {
                        // Look for the <data> section
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (String.Equals(reader.Name, "data", StringComparison.OrdinalIgnoreCase))
                            {
                                // Is there an attribute called type?
                                string typeName = reader.GetAttribute("type");
                                name = reader.GetAttribute("name");

                                if (typeName != null)
                                {
                                    Type type;

                                    // It is likely that we've seen this type before
                                    // we'll try our table of previously seen types
                                    // since it is *much* faster to do that than
                                    // call Type.GetType needlessly!
                                    if (!_typeTable.TryGetValue(typeName, out type))
                                    {
                                        string resolvedTypeName = typeName;

                                        // This type name might be an alias, so first resolve that if any.
                                        int indexOfSeperator = typeName.IndexOf(",", StringComparison.Ordinal);

                                        if (indexOfSeperator != -1)
                                        {
                                            string typeFromTypeName = typeName.Substring(0, indexOfSeperator);
                                            string maybeAliasFromTypeName = typeName.Substring(indexOfSeperator + 1);

                                            if (!String.IsNullOrWhiteSpace(maybeAliasFromTypeName))
                                            {
                                                maybeAliasFromTypeName = maybeAliasFromTypeName.Trim();

                                                string fullName = null;
                                                if (_aliases.TryGetValue(maybeAliasFromTypeName, out fullName))
                                                {
                                                    resolvedTypeName = typeFromTypeName + ", " + fullName;
                                                }
                                            }
                                        }

                                        // Can this type be found in the GAC?
                                        type = Type.GetType(resolvedTypeName, throwOnError: false, ignoreCase: false);

                                        // Remember our resolved type
                                        _typeTable[typeName] = type;
                                    }

                                    if (type == null)
                                    {
                                        // If the type could not be found in the GAC, then we're going to need
                                        // to load the referenced assemblies (those passed in through the
                                        // "References" parameter during the building of this .RESX.  Therefore,
                                        // we should create a separate app-domain, so that those assemblies
                                        // can be unlocked when the task is finished.
                                        // The type didn't start with "System." so return true.
                                        Log.LogMessageFromResources(
                                            MessageImportance.Low,
                                            "GenerateResource.SeparateAppDomainBecauseOfType",
                                            name ?? String.Empty,
                                            typeName,
                                            source.ItemSpec,
                                            ((IXmlLineInfo)reader).LineNumber);

                                        return true;
                                    }

                                    // If there's a type, we don't need to look at any mimetype
                                    continue;
                                }

                                // DDB #9825.
                                // There's no type attribute on this <data> -- if there's a MimeType, it's a serialized
                                // object of unknown type, and we have to assume it will need a new app domain.
                                // The possible mimetypes ResXResourceReader understands are:
                                //
                                // application/x-microsoft.net.object.binary.base64
                                // application/x-microsoft.net.object.bytearray.base64
                                // application/x-microsoft.net.object.binary.base64
                                // application/x-microsoft.net.object.soap.base64
                                // text/microsoft-urt/binary-serialized/base64
                                // text/microsoft-urt/psuedoml-serialized/base64
                                // text/microsoft-urt/soap-serialized/base64
                                //
                                // Of these, application/x-microsoft.net.object.bytearray.base64 usually has a type attribute
                                // as well; ResxResourceReader will use that Type, which may not need a new app domain. So
                                // if there's a type attribute, we don't look at mimetype.
                                //
                                // If there is a mimetype and no type, we can't tell the type without deserializing and loading it,
                                // so we assume a new appdomain is needed.
                                //
                                // Actually, if application/x-microsoft.net.object.bytearray.base64 doesn't have a Type attribute,
                                // ResxResourceReader assumes System.String, but for safety we don't assume that here.

                                string mimeType = reader.GetAttribute("mimetype");

                                if (mimeType != null)
                                {
                                    if (NeedSeparateAppDomainBasedOnSerializedType(reader))
                                    {
                                        Log.LogMessageFromResources(
                                            MessageImportance.Low,
                                            "GenerateResource.SeparateAppDomainBecauseOfMimeType",
                                            name ?? String.Empty,
                                            mimeType,
                                            source.ItemSpec,
                                            ((IXmlLineInfo)reader).LineNumber);

                                        return true;
                                    }
                                }
                            }
                            else if (String.Equals(reader.Name, "assembly", StringComparison.OrdinalIgnoreCase))
                            {
                                string alias = reader.GetAttribute("alias");
                                string fullName = reader.GetAttribute("name");

                                if (!String.IsNullOrWhiteSpace(alias) && !String.IsNullOrWhiteSpace(fullName))
                                {
                                    alias = alias.Trim();
                                    fullName = fullName.Trim();

                                    _aliases[alias] = fullName;
                                }
                            }
                        }
                    }
                }
                catch (XmlException e)
                {
                    Log.LogMessageFromResources(
                                    MessageImportance.Low,
                                    "GenerateResource.SeparateAppDomainBecauseOfExceptionLineNumber",
                                    source.ItemSpec,
                                    ((IXmlLineInfo)reader).LineNumber,
                                    e.Message);

                    return true;
                }
                catch (SerializationException e)
                {
                    Log.LogMessageFromResources(
                                    MessageImportance.Low,
                                    "GenerateResource.SeparateAppDomainBecauseOfErrorDeserializingLineNumber",
                                    source.ItemSpec,
                                    name ?? String.Empty,
                                    ((IXmlLineInfo)reader).LineNumber,
                                    e.Message);

                    return true;
                }
                catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
                {
                    // DDB#9819
                    // Customers have reported the following exceptions coming out of this method's call to GetType():
                    //      System.Runtime.InteropServices.COMException (0x8000000A): The data necessary to complete this operation is not yet available. (Exception from HRESULT: 0x8000000A)
                    //      System.NullReferenceException: Object reference not set to an instance of an object.
                    //      System.InvalidOperationException: Collection was modified; enumeration operation may not execute.
                    // We don't have reproes, but probably the right thing to do is to assume a new app domain is needed on almost any exception.
                    // Any problem loading the type will get logged later when the resource reader tries it.
                    //
                    // XmlException or an IO exception is also possible from an invalid input file.

                    // If there was any problem parsing the .resx then log a message and
                    // fall back to using a separate AppDomain.
                    Log.LogMessageFromResources(
                                    MessageImportance.Low,
                                    "GenerateResource.SeparateAppDomainBecauseOfException",
                                    source.ItemSpec,
                                    e.Message);

                    // In case we need more information from the customer (given this has been heavily reported
                    // and we don't understand it properly) let the usual debug switch dump the stack.
                    if (Environment.GetEnvironmentVariable("MSBUILDDEBUG") == "1")
                    {
                        Log.LogErrorFromException(e, /* stack */ true, /* inner exceptions */ true, null);
                    }

                    return true;
                }
                finally
                {
                    reader?.Close();
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the "value" element expected to be the next element read from the supplied reader.
    /// Deserializes the data content in order to figure out whether it implies a new app domain
    /// should be used to process resources.
    /// </summary>
    private bool NeedSeparateAppDomainBasedOnSerializedType(XmlReader reader)
    {
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (!String.Equals(reader.Name, "value", StringComparison.OrdinalIgnoreCase))
                {
                    // <data> claimed it was serialized, but didn't have a <value>;
                    // return true to err on side of caution
                    return true;
                }

                // Found "value" element
                string data = reader.ReadElementContentAsString();

                bool isSerializedObjectLoadable = DetermineWhetherSerializedObjectLoads(data);

                // If it's not loadable, it's presumably a user type, so create a new app domain
                return !isSerializedObjectLoadable;
            }
        }

        // We didn't find any element at all -- the .resx is malformed.
        // Return true to err on the side of caution. Error will appear later.
        return true;
    }

    /// <summary>
    /// Deserializes a base64 block from a resx in order to figure out if its type is in the GAC.
    /// Because we're not providing any assembly resolution callback, deserialization
    /// will attempt to load the object's type using fusion rules, which essentially means
    /// the GAC. So, if the object is null, it's definitely not in the GAC.
    /// </summary>
    private bool DetermineWhetherSerializedObjectLoads(string data)
    {
        byte[] serializedData = ByteArrayFromBase64WrappedString(data);

        BinaryFormatter binaryFormatter = new();

        using (MemoryStream memoryStream = new MemoryStream(serializedData))
        {
            // CodeQL [SM03722] required trust of BinaryFormatter-serialized resources documented at https://learn.microsoft.com/visualstudio/msbuild/generateresource-task
            // CodeQL [SM04191] required trust of BinaryFormatter-serialized resources documented at https://learn.microsoft.com/visualstudio/msbuild/generateresource-task
            object result = binaryFormatter.Deserialize(memoryStream);

            return result != null;
        }
    }
#endif

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
            OutputResources = new ITaskItem[Sources.Length];
            int i = 0;
            try
            {
                for (i = 0; i < Sources.Length; ++i)
                {
                    OutputResources[i] = new TaskItem(Path.ChangeExtension(Sources[i].ItemSpec, ".resources"));
                }
            }
            catch (ArgumentException e)
            {
                Log.LogErrorWithCodeFromResources("GenerateResource.InvalidFilename", Sources[i].ItemSpec, e.Message);
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

        ErrorUtilities.VerifyThrow(OutputResources != null && OutputResources.Length != 0, "Should be at least one output resource");

        // We only get here if there was at least one resource generation error.
        ITaskItem[] temp = new ITaskItem[OutputResources.Length - _unsuccessfullyCreatedOutFiles.Count];
        int copied = 0;
        int removed = 0;
        for (int i = 0; i < Sources.Length; i++)
        {
            // Check whether this one is in the bad list.
            if (removed < _unsuccessfullyCreatedOutFiles.Count &&
                _unsuccessfullyCreatedOutFiles.Contains(OutputResources[i].ItemSpec))
            {
                removed++;
                Sources[i].SetMetadata("OutputResource", string.Empty);
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

    /// <summary>
    /// Record the list of file that will be written to disk.
    /// </summary>
    private void RecordFilesWritten()
    {
        // Add any output resources that were successfully created,
        // or would have been if they weren't already up to date (important for Clean)
        if (this.OutputResources != null)
        {
            foreach (ITaskItem item in this.OutputResources)
            {
                _filesWritten.Add(item);
            }
        }

        // Add any state file
        if (StateFile?.ItemSpec.Length > 0)
        {
            // It's possible the file wasn't actually written (eg the path was invalid)
            // We can't easily tell whether that happened here, and I think it's fine to add it anyway.
            _filesWritten.Add(StateFile);
        }

        // Only add the STR class file if the CodeDOM succeeded, or if it exists and is up to date.
        // Otherwise, we probably didn't write it successfully.
        if (_stronglyTypedResourceSuccessfullyCreated)
        {
            if (StronglyTypedFileName == null)
            {
                CodeDomProvider provider = null;
                try
                {
                    if (ProcessResourceFiles.TryCreateCodeDomProvider(Log, StronglyTypedLanguage, out provider))
                    {
                        StronglyTypedFileName = ProcessResourceFiles.GenerateDefaultStronglyTypedFilename(
                            provider, OutputResources[0].ItemSpec);
                    }
                }
                finally
                {
                    provider?.Dispose();
                }
            }

            _filesWritten.Add(new TaskItem(this.StronglyTypedFileName));
        }
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

#if !FEATURE_ASSEMBLYLOADCONTEXT
    /// <summary>
    /// Class that gets called by the ResxResourceReader to resolve references
    /// to assemblies within the .RESX.
    /// </summary>
    private AssemblyNamesTypeResolutionService _typeResolver = null;

    /// <summary>
    /// Handles assembly resolution events.
    /// </summary>
    private ResolveEventHandler _eventHandler;
#endif

    /// <summary>
    /// The referenced assemblies
    /// </summary>
    private readonly string[] _assemblyFiles;

    /// <summary>
    /// The AssemblyNameExtensions for each of the referenced assemblies in "assemblyFiles".
    /// This is populated lazily.
    /// </summary>
    private AssemblyNameExtension[] _assemblyNames;

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
    /// Record all the information about outputs here to avoid future incremental builds.
    /// </summary>
    internal List<ResGenDependencies.PortableLibraryFile> PortableLibraryCacheInfo
    {
        get { return _portableLibraryCacheInfo; }
    }
    private List<ResGenDependencies.PortableLibraryFile> _portableLibraryCacheInfo;

    /// <summary>
    /// List of output files that we failed to create due to an error.
    /// See note in RemoveUnsuccessfullyCreatedResourcesFromOutputResources()
    /// </summary>
    internal ArrayList UnsuccessfullyCreatedOutFiles
    {
        get
        {
            if (_unsuccessfullyCreatedOutFiles == null)
            {
                _unsuccessfullyCreatedOutFiles = new ArrayList();
            }
            return _unsuccessfullyCreatedOutFiles;
        }
    }
    private ArrayList _unsuccessfullyCreatedOutFiles;

    /// <summary>
    /// Whether we successfully created the STR class
    /// </summary>
    internal bool StronglyTypedResourceSuccessfullyCreated
    {
        get
        {
            return _stronglyTypedResourceSuccessfullyCreated;
        }
    }
    private bool _stronglyTypedResourceSuccessfullyCreated = false;

    /// <summary>
    /// Indicates whether the resource reader should use the source file's
    /// directory to resolve relative file paths.
    /// </summary>
    private bool _useSourcePath = false;

    private bool _logWarningForBinaryFormatter = false;

    /// <summary>
    /// Process all files.
    /// </summary>
    internal void Run(
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
        _portableLibraryCacheInfo = new List<ResGenDependencies.PortableLibraryFile>();
        _usePreserializedResources = usePreserializedResources;
        _logWarningForBinaryFormatter = logWarningForBinaryFormatter;

#if !FEATURE_ASSEMBLYLOADCONTEXT
        // If references were passed in, we will have to give the ResxResourceReader an object
        // by which it can resolve types that are referenced from within the .RESX.
        if ((_assemblyFiles?.Length > 0))
        {
            _typeResolver = new AssemblyNamesTypeResolutionService(_assemblyFiles);
        }
#endif

        try
        {
#if !FEATURE_ASSEMBLYLOADCONTEXT
            // Install assembly resolution event handler.
            _eventHandler = new ResolveEventHandler(ResolveAssembly);
            AppDomain.CurrentDomain.AssemblyResolve += _eventHandler;
#endif

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
        finally
        {
#if !FEATURE_ASSEMBLYLOADCONTEXT
            // Remove the event handler.
            AppDomain.CurrentDomain.AssemblyResolve -= _eventHandler;
            _eventHandler = null;
#endif
        }
    }

#if !FEATURE_ASSEMBLYLOADCONTEXT
    /// <summary>
    /// Callback to resolve assembly names to assemblies.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    internal Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        AssemblyNameExtension requestedAssemblyName = new AssemblyNameExtension(args.Name);

        if (_assemblyFiles != null)
        {
            PopulateAssemblyNames();

            // Loop through all the references passed in, and see if any of them have an assembly
            // name that exactly matches the requested one.
            for (int i = 0; i < _assemblyNames.Length; i++)
            {
                AssemblyNameExtension candidateAssemblyName = _assemblyNames[i];

                if (candidateAssemblyName != null)
                {
                    if (candidateAssemblyName.CompareTo(requestedAssemblyName) == 0)
                    {
                        return Assembly.UnsafeLoadFrom(_assemblyFiles[i].ItemSpec);
                    }
                }
            }

            // If none of the referenced assembly names matches exactly, try to find one that
            // has the same base name.  This is here to fix bug where the
            // serialized data inside the .RESX referred to the assembly just by the base name,
            // omitting the version, culture, publickeytoken information.
            for (int i = 0; i < _assemblyNames.Length; i++)
            {
                AssemblyNameExtension candidateAssemblyName = _assemblyNames[i];

                if (candidateAssemblyName != null)
                {
                    if (string.Equals(requestedAssemblyName.Name, candidateAssemblyName.Name, StringComparison.CurrentCultureIgnoreCase))
                    {
                        return Assembly.UnsafeLoadFrom(_assemblyFiles[i].ItemSpec);
                    }
                }
            }
        }

        return null;
    }
#endif

    private void PopulateAssemblyNames()
    {
        // Populate the list of assembly names for all passed-in references if it hasn't
        // been populated already.
        if (_assemblyNames == null)
        {
            _assemblyNames = new AssemblyNameExtension[_assemblyFiles.Length];
            for (int i = 0; i < _assemblyFiles.Length; i++)
            {
                ITaskItem assemblyFile = _assemblyFiles[i];
                _assemblyNames[i] = null;

                if (assemblyFile.ItemSpec != null && FileSystems.Default.FileExists(assemblyFile.ItemSpec))
                {
                    string fusionName = assemblyFile.GetMetadata(ItemMetadataNames.fusionName);
                    if (!string.IsNullOrEmpty(fusionName))
                    {
                        _assemblyNames[i] = new AssemblyNameExtension(fusionName);
                    }
                    else
                    {
                        // whoever passed us this reference wasn't polite enough to also
                        // give us a metadata with the fusion name.  Trying to load up every
                        // assembly here would take a lot of time, so just stick the assembly
                        // file name (which we assume generally maps to the simple name) into
                        // the list instead. If there's a fusion name that matches, we'll get
                        // that first; otherwise there's a good chance that if the simple name
                        // matches the file name, it's a good match.
                        _assemblyNames[i] = new AssemblyNameExtension(Path.GetFileNameWithoutExtension(assemblyFile.ItemSpec));
                    }
                }
            }
        }
    }

    #region Code from ResGen.EXE

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
        if (inFileFormat != Format.Assembly) // outFileOrDir is a directory when the input file is an assembly
        {
            Format outFileFormat = GetFormat(outFileOrDir);
            if (outFileFormat == Format.Assembly)
            {
                Logger.LogErrorFromResources("GenerateResource.CannotWriteAssembly", outFileOrDir);
                return false;
            }
            else if (outFileFormat == Format.Error)
            {
                return false;
            }
        }

        Logger.LogMessageFromResources("GenerateResource.ProcessingFile", inFile, outFileOrDir);

        // Reset state
        _readers = new List<ReaderInfo>();

        try
        {
            ReadResources(inFile, _useSourcePath, outFileOrDir);
        }
        catch (InputFormatNotSupportedException)
        {
            Logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                                                    "GenerateResource.CoreSupportsLimitedScenarios");
            return false;
        }
        catch (MSBuildResXException msbuildResXException)
        {
            Logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                "General.InvalidResxFile", msbuildResXException.InnerException.ToString());
            return false;
        }
        catch (ArgumentException ae)
        {
            if (ae.InnerException is XmlException xe)
            {
                Logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), xe.LineNumber,
                    xe.LinePosition, 0, 0, "General.InvalidResxFile", xe.Message);
            }
            else
            {
                Logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                    "General.InvalidResxFile", ae.Message);
            }
            return false;
        }
        catch (TextFileException tfe)
        {
            // Used to pass back error context from ReadTextResources to here.
            Logger.LogErrorWithCodeFromResources(null, tfe.FileName, tfe.LineNumber, tfe.LinePosition, 1, 1,
                "GenerateResource.MessageTunnel", tfe.Message);
            return false;
        }
        catch (XmlException xe)
        {
            Logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), xe.LineNumber,
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
            Logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                "General.InvalidResxFile", e.Message);

            // Log the stack, so the problem with the type in the .resx is diagnosable by the customer
            Logger.LogErrorFromException(e, /* stack */ true, /* inner exceptions */ true,
                FileUtilities.GetFullPathNoThrow(inFile));
            return false;
        }
        catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
        {
            // Regular IO error
            Logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                "General.InvalidResxFile", e.Message);
            return false;
        }

        string currentOutputFile = null;
        string currentOutputDirectory = null;
        bool currentOutputDirectoryAlreadyExisted = true;

        try
        {
            if (GetFormat(inFile) == Format.Assembly)
            {
                // Prepare cache data
                ResGenDependencies.PortableLibraryFile library = new ResGenDependencies.PortableLibraryFile(inFile);
                List<string> resWFilesForThisAssembly = new List<string>();

                foreach (ReaderInfo reader in _readers)
                {
                    string currentOutputFileNoPath = reader.outputFileName + ".resw";
                    currentOutputFile = null;
                    currentOutputDirectoryAlreadyExisted = true;
                    string priDirectory = Path.Combine(outFileOrDir ?? string.Empty,
                        reader.assemblySimpleName);
                    currentOutputDirectory = Path.Combine(priDirectory,
                        reader.cultureName ?? string.Empty);

                    if (!FileSystems.Default.DirectoryExists(currentOutputDirectory))
                    {
                        currentOutputDirectoryAlreadyExisted = false;
                        Directory.CreateDirectory(currentOutputDirectory);
                    }
                    currentOutputFile = Path.Combine(currentOutputDirectory, currentOutputFileNoPath);

                    // For very long resource names, this directory structure may be too deep.
                    // If so, assume that the name is so long it will already uniquely distinguish itself.
                    // However for shorter names we'd still prefer to use the assembly simple name
                    // in the path to avoid conflicts.
                    currentOutputFile = EnsurePathIsShortEnough(currentOutputFile, currentOutputFileNoPath,
                        outFileOrDir, reader.cultureName);

                    if (currentOutputFile == null)
                    {
                        // We couldn't generate a file name short enough to handle this.  Fail but continue.
                        continue;
                    }

                    // Always write the output file here - other logic prevents us from processing this
                    // file for incremental builds if everything was up to date.
                    WriteResources(reader, currentOutputFile);

                    string escapedOutputFile = EscapingUtilities.Escape(currentOutputFile);
                    ITaskItem newOutputFile = new TaskItem(escapedOutputFile);
                    resWFilesForThisAssembly.Add(escapedOutputFile);
                    newOutputFile.SetMetadata("ResourceIndexName", reader.assemblySimpleName);
                    library.AssemblySimpleName = reader.assemblySimpleName;
                    if (reader.fromNeutralResources)
                    {
                        newOutputFile.SetMetadata("NeutralResourceLanguage", reader.cultureName);
                        library.NeutralResourceLanguage = reader.cultureName;
                    }
                }

                library.OutputFiles = resWFilesForThisAssembly.ToArray();
                _portableLibraryCacheInfo.Add(library);
            }
            else
            {
                currentOutputFile = outFileOrDir;
                ErrorUtilities.VerifyThrow(_readers.Count == 1,
                    "We have no readers, or we have multiple readers & are ignoring subsequent ones.  Num readers: {0}",
                    _readers.Count);
                WriteResources(_readers[0], outFileOrDir);
            }
        }
        catch (IOException io)
        {
            if (currentOutputFile != null)
            {
                Logger.LogErrorWithCodeFromResources("GenerateResource.CannotWriteOutput",
                    FileUtilities.GetFullPathNoThrow(currentOutputFile), io.Message);
                if (File.Exists(currentOutputFile))
                {
                    if (GetFormat(currentOutputFile) != Format.Assembly)
                    // Never delete an assembly since we don't ever actually write to assemblies.
                    {
                        RemoveCorruptedFile(currentOutputFile);
                    }
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
                catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
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
            Logger.LogErrorWithCodeFromResources("GenerateResource.CannotWriteOutput",
                FileUtilities.GetFullPathNoThrow(inFile), e.Message); // Input file is more useful to log

            // Log the stack, so the problem with the type in the .resx is diagnosable by the customer
            Logger.LogErrorFromException(e, /* stack */ true, /* inner exceptions */ true,
                FileUtilities.GetFullPathNoThrow(inFile));
            return false;
        }
        catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
        {
            // Regular IO error
            Logger.LogErrorWithCodeFromResources("GenerateResource.CannotWriteOutput",
                FileUtilities.GetFullPathNoThrow(currentOutputFile), e.Message);
            return false;
        }

        return true;
    }

    /// <summary>
    /// For very long resource names, the directory structure we generate may be too deep.
    /// If so, assume that the name is so long it will already uniquely distinguish itself.
    /// However for shorter names we'd still prefer to use the assembly simple name
    /// in the path to avoid conflicts.
    /// </summary>
    /// <param name="currentOutputFile">The current path name</param>
    /// <param name="currentOutputFileNoPath">The current file name without a path.</param>
    /// <param name="outputDirectory">Output directory path</param>
    /// <param name="cultureName">culture for this resource</param>
    /// <returns>The current path or a shorter one.</returns>
    private string EnsurePathIsShortEnough(string currentOutputFile, string currentOutputFileNoPath, string outputDirectory, string cultureName)
    {
        if (!NativeMethodsShared.HasMaxPath)
        {
            return Path.GetFullPath(currentOutputFile);
        }

        // File names >= 260 characters won't work.  File names of exactly 259 characters are odd though.
        // They seem to work with Notepad and Windows Explorer, but not with MakePri.  They don't work
        // reliably with cmd's dir command either (depending on whether you use absolute or relative paths
        // and whether there are quotes around the name).
        const int EffectiveMaxPath = 258;   // Everything <= EffectiveMaxPath should work well.
        bool success;
        try
        {
            currentOutputFile = Path.GetFullPath(currentOutputFile);
            success = currentOutputFile.Length <= EffectiveMaxPath;
        }
        catch (PathTooLongException)
        {
            success = false;
        }

        if (!success)
        {
            string shorterPath = Path.Combine(outputDirectory ?? string.Empty, cultureName ?? string.Empty);
            Directory.CreateDirectory(shorterPath);
            currentOutputFile = Path.Combine(shorterPath, currentOutputFileNoPath);

            // Try again
            try
            {
                currentOutputFile = Path.GetFullPath(currentOutputFile);
                success = currentOutputFile.Length <= EffectiveMaxPath;
            }
            catch (PathTooLongException)
            {
                success = false;
            }

            // Can't do anything more without violating correctness.
            if (!success)
            {
                Logger.LogErrorWithCodeFromResources("GenerateResource.PathTooLong", currentOutputFile);
                currentOutputFile = null;
                // We've logged an error message.  This MSBuild task will fail, but can continue processing other input (to find other errors).
            }
        }
        return currentOutputFile;
    }

    /// <summary>
    /// Remove a corrupted file, with error handling and a warning if we fail.
    /// </summary>
    /// <param name="filename">Full path to file to delete</param>
    private void RemoveCorruptedFile(string filename)
    {
        Logger.LogMessageFromResources("GenerateResource.CorruptOutput", FileUtilities.GetFullPathNoThrow(filename));
        try
        {
            File.Delete(filename);
        }
        catch (Exception deleteException)
        {
            Logger.LogWarningWithCodeFromResources("GenerateResource.DeleteCorruptOutputFailed", FileUtilities.GetFullPathNoThrow(filename), deleteException.Message);

            if (ExceptionHandling.NotExpectedException(deleteException))
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Figure out the format of an input resources file from the extension
    /// </summary>
    /// <param name="filename">Input resources file</param>
    /// <returns>Resources format</returns>
    private static Format GetFormat(string filename)
    {
        string extension;
        try
        {
            extension = Path.GetExtension(filename);
        }
        catch (ArgumentException ex)
        {
            Logger.LogErrorWithCodeFromResources("GenerateResource.InvalidFilename", filename, ex.Message);
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
        else if (string.Equals(extension, ".resources.dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Format.Assembly;
        }
        else if (string.Equals(extension, ".resources", StringComparison.OrdinalIgnoreCase))
        {
            return Format.Binary;
        }
        else
        {
            Logger.LogErrorWithCodeFromResources("GenerateResource.UnknownFileExtension", Path.GetExtension(filename), filename);
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
        Assembly, // .dll, .exe or .resources.dll
        Error, // anything else
    }

    /// <summary>
    /// Reads the resources out of the specified file and populates the
    /// resources hashtable.
    /// </summary>
    /// <param name="filename">Filename to load</param>
    /// <param name="shouldUseSourcePath">Whether to resolve paths in the
    /// resources file relative to the resources file location</param>
    /// <param name="outFileOrDir"> Output file or directory. </param>
    private void ReadResources(string filename, bool shouldUseSourcePath, string outFileOrDir)
    {
        Format format = GetFormat(filename);

        if (format == Format.Assembly) // Multiple input .resources files within one assembly
        {
#if !FEATURE_ASSEMBLYLOADCONTEXT
            ReadAssemblyResources(filename, outFileOrDir);
#else
            throw new InputFormatNotSupportedException("Reading resources from Assembly not supported on .NET Core MSBuild");
#endif
        }
        else
        {
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
#if FEATURE_RESXREADER_LIVEDESERIALIZATION
                    if (!_usePreserializedResources)
                    {
                        ResXResourceReader resXReader = null;
                        if (_typeResolver != null)
                        {
                            resXReader = new ResXResourceReader(filename, _typeResolver);
                        }
                        else
                        {
                            resXReader = new ResXResourceReader(filename);
                        }

                        if (shouldUseSourcePath)
                        {
                            String fullPath = Path.GetFullPath(filename);
                            resXReader.BasePath = Path.GetDirectoryName(fullPath);
                        }
                        // ReadResources closes the reader for us
                        ReadResources(reader, resXReader, filename);
                    }
                    else
#endif
                    {
                        if (Traits.Instance.EscapeHatches.UseMinimalResxParsingInCoreScenarios)
                        {
                            AddResourcesUsingMinimalCoreResxParsing(filename, reader);
                        }
                        else
                        {
                            foreach (IResource resource in MSBuildResXReader.GetResourcesFromFile(filename, shouldUseSourcePath, Logger, _logWarningForBinaryFormatter))
                            {
                                AddResource(reader, resource, filename, 0, 0);
                            }
                        }
                    }
                    break;

                case Format.Binary:
#if FEATURE_RESXREADER_LIVEDESERIALIZATION
                    ReadResources(reader, new ResourceReader(filename), filename); // closes reader for us
                    break;
#else
                    throw new InputFormatNotSupportedException("Reading resources from binary .resources not supported on .NET Core MSBuild");
#endif

                default:
                    // We should never get here, we've already checked the format
                    Debug.Fail("Unknown format " + format.ToString());
                    return;
            }
            Logger.LogMessageFromResources(MessageImportance.Low, "GenerateResource.ReadResourceMessage", reader.resources.Count, filename);
        }
    }

    /// <summary>
    /// Legacy Core implementation of string-only ResX handling
    /// </summary>
    private void AddResourcesUsingMinimalCoreResxParsing(string filename, ReaderInfo reader)
    {
        using (var xmlReader = new XmlTextReader(filename))
        {
            xmlReader.WhitespaceHandling = WhitespaceHandling.None;
            XDocument doc = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
            foreach (XElement dataElem in doc.Element("root").Elements("data"))
            {
                string name = dataElem.Attribute("name").Value;
                string value = dataElem.Element("value").Value;
                AddResource(reader, name, value, filename);
            }
        }
    }

#if !FEATURE_ASSEMBLYLOADCONTEXT
    /// <summary>
    /// Reads resources from an assembly.
    /// </summary>
    /// <param name="name"></param>
    /// <param name="outFileOrDir"> Output file or directory. </param>
    /// <remarks> This should not run for Framework assemblies. </remarks>
    internal void ReadAssemblyResources(string name, string outFileOrDir)
    {
        // If something else in the solution failed to build...
        if (!FileSystems.Default.FileExists(name))
        {
            Logger.LogErrorWithCodeFromResources("GenerateResource.MissingFile", name);
            return;
        }

        Assembly a = null;
        bool mainAssembly = false;
        bool failedLoadingCultureInfo = false;
        NeutralResourcesLanguageAttribute neutralResourcesLanguageAttribute = null;
        AssemblyName assemblyName = null;

        try
        {
            a = Assembly.UnsafeLoadFrom(name);
            assemblyName = a.GetName();

            CultureInfo ci = null;
            try
            {
                ci = assemblyName.CultureInfo;
            }
            catch (ArgumentException e)
            {
                Logger.LogWarningWithCodeFromResources(null, name, 0, 0, 0, 0, "GenerateResource.CreatingCultureInfoFailed", e.GetType().Name, e.Message, assemblyName.ToString());
                failedLoadingCultureInfo = true;
            }

            if (!failedLoadingCultureInfo)
            {
                mainAssembly = ci.Equals(CultureInfo.InvariantCulture);
                neutralResourcesLanguageAttribute = CheckAssemblyCultureInfo(name, assemblyName, ci, a, mainAssembly);
            }  // if (!failedLoadingCultureInfo)
        }
        catch (BadImageFormatException)
        {
            // If we're extracting ResW files, this task is being run on all DLL's referred to by the project.
            // That may potentially include C++ libraries & immersive (non-portable) class libraries, which don't have resources.
            // We can't easily filter those.  We can simply skip them.
            return;
        }
        catch (ArgumentException e) when (e.InnerException is BadImageFormatException)
        {
            // BadImageFormatExceptions can be wrapped in ArgumentExceptions, so catch those, too. See https://referencesource.microsoft.com/#mscorlib/system/reflection/module.cs,857
            return;
        }
        catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
        {
            Logger.LogErrorWithCodeFromResources("GenerateResource.CannotLoadAssemblyLoadFromFailed", name, e);
        }

        if (a != null)
        {
            string[] resources = a.GetManifestResourceNames();
            CultureInfo satCulture = null;
            string expectedExt = null;
            if (!failedLoadingCultureInfo)
            {
                satCulture = assemblyName.CultureInfo;
                if (!satCulture.Equals(CultureInfo.InvariantCulture))
                {
                    expectedExt = '.' + satCulture.Name + ".resources";
                }
            }

            foreach (string resName in resources)
            {
                if (!resName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) // Skip non-.resources assembly blobs
                {
                    continue;
                }

                if (mainAssembly)
                {
                    if (CultureInfo.InvariantCulture.CompareInfo.IsSuffix(resName, ".en-US.resources", CompareOptions.IgnoreCase))
                    {
                        Logger.LogErrorFromResources("GenerateResource.ImproperlyBuiltMainAssembly", resName, name);
                        continue;
                    }

                    if (neutralResourcesLanguageAttribute == null)
                    {
                        Logger.LogWarningWithCodeFromResources(null, name, 0, 0, 0, 0, "GenerateResource.MainAssemblyMissingNeutralResourcesLanguage", name);
                        break;
                    }
                }
                else if (!failedLoadingCultureInfo && !CultureInfo.InvariantCulture.CompareInfo.IsSuffix(resName, expectedExt, CompareOptions.IgnoreCase))
                {
                    Logger.LogErrorFromResources("GenerateResource.ImproperlyBuiltSatelliteAssembly", resName, expectedExt, name);
                    continue;
                }

                try
                {
                    Stream s = a.GetManifestResourceStream(resName);
                    using (IResourceReader rr = new ResourceReader(s))
                    {
                        ReaderInfo reader = new ReaderInfo();
                        if (mainAssembly)
                        {
                            reader.fromNeutralResources = true;
                            reader.assemblySimpleName = assemblyName.Name;
                        }
                        else
                        {
                            Debug.Assert(assemblyName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase));
                            reader.assemblySimpleName = assemblyName.Name.Remove(assemblyName.Name.Length - 10);  // Remove .resources from satellite assembly name
                        }
                        reader.outputFileName = resName.Remove(resName.Length - 10); // Remove the .resources extension
                        if (satCulture != null && !string.IsNullOrEmpty(satCulture.Name))
                        {
                            reader.cultureName = satCulture.Name;
                        }
                        else if (neutralResourcesLanguageAttribute != null && !string.IsNullOrEmpty(neutralResourcesLanguageAttribute.CultureName))
                        {
                            reader.cultureName = neutralResourcesLanguageAttribute.CultureName;
                        }

                        if (reader.cultureName != null)
                        {
                            // Remove the culture from the filename
                            if (reader.outputFileName.EndsWith("." + reader.cultureName, StringComparison.OrdinalIgnoreCase))
                            {
                                reader.outputFileName = reader.outputFileName.Remove(reader.outputFileName.Length - (reader.cultureName.Length + 1));
                            }
                        }
                        _readers.Add(reader);

                        foreach (DictionaryEntry pair in rr)
                        {
                            AddResource(reader, (string)pair.Key, pair.Value, resName);
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    Logger.LogErrorWithCodeFromResources(null, name, 0, 0, 0, 0, "GenerateResource.NoResourcesFileInAssembly", resName);
                }
            }
        }

        var satelliteAssemblies = _satelliteInFiles.Where(ti => ti.GetMetadata("OriginalItemSpec").Equals(name, StringComparison.OrdinalIgnoreCase));

        foreach (var satelliteAssembly in satelliteAssemblies)
        {
            ReadAssemblyResources(satelliteAssembly.ItemSpec, outFileOrDir);
        }
    }

    /// <summary>
    /// Checks the consistency of the CultureInfo and NeutralResourcesLanguageAttribute settings.
    /// </summary>
    /// <param name="name">Assembly's file name</param>
    /// <param name="assemblyName">AssemblyName of this assembly</param>
    /// <param name="culture">Assembly's CultureInfo</param>
    /// <param name="a">The actual Assembly</param>
    /// <param name="mainAssembly">Whether this is the main assembly</param>
    private NeutralResourcesLanguageAttribute CheckAssemblyCultureInfo(string name, AssemblyName assemblyName, CultureInfo culture, Assembly a, bool mainAssembly)
    {
        NeutralResourcesLanguageAttribute neutralResourcesLanguageAttribute = null;
        if (mainAssembly)
        {
            object[] attrs = a.GetCustomAttributes(typeof(NeutralResourcesLanguageAttribute), false);
            if (attrs.Length != 0)
            {
                neutralResourcesLanguageAttribute = (NeutralResourcesLanguageAttribute)attrs[0];
                bool fallbackToSatellite = neutralResourcesLanguageAttribute.Location == UltimateResourceFallbackLocation.Satellite;
                if (!fallbackToSatellite && neutralResourcesLanguageAttribute.Location != UltimateResourceFallbackLocation.MainAssembly)
                {
                    Logger.LogWarningWithCodeFromResources(null, name, 0, 0, 0, 0, "GenerateResource.UnrecognizedUltimateResourceFallbackLocation", neutralResourcesLanguageAttribute.Location, name);
                }
                // This MSBuild task needs to not report an error for main assemblies that don't have managed resources.
            }
        }
        else
        {  // Satellite assembly, or a mal-formed main assembly
            // Additional error checking from ResView.
            if (!assemblyName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarningWithCodeFromResources(null, name, 0, 0, 0, 0, "GenerateResource.SatelliteOrMalformedAssembly", name, culture.Name, assemblyName.Name);
                return null;
            }
            Type[] types = a.GetTypes();
            if (types.Length > 0)
            {
                Logger.LogWarningWithCodeFromResources("GenerateResource.SatelliteAssemblyContainsCode", name);
            }

            if (!ContainsProperlyNamedResourcesFiles(a, false))
            {
                Logger.LogWarningWithCodeFromResources("GenerateResource.SatelliteAssemblyContainsNoResourcesFile", assemblyName.CultureInfo.Name);
            }
        }
        return neutralResourcesLanguageAttribute;
    }

    private static bool ContainsProperlyNamedResourcesFiles(Assembly a, bool mainAssembly)
    {
        string postfix = mainAssembly ? ".resources" : a.GetName().CultureInfo.Name + ".resources";
        foreach (string manifestResourceName in a.GetManifestResourceNames())
        {
            if (manifestResourceName.EndsWith(postfix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
#endif

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
#if FEATURE_RESXREADER_LIVEDESERIALIZATION
                WriteResources(reader, new ResXResourceWriter(filename)); // closes writer for us
#else
                Logger.LogError(format.ToString() + " not supported on .NET Core MSBuild");
#endif
                break;

            case Format.Assembly:
                Logger.LogErrorFromResources("GenerateResource.CannotWriteAssembly", filename);
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
        if (_usePreserializedResources && HaveSystemResourcesExtensionsReference)
        {
            WriteResources(reader, new PreserializedResourceWriter(new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None))); // closes writer for us
            return;
        }

        try
        {
            WriteResources(reader, new ResourceWriter(new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None))); // closes writer for us
        }
        catch (PreserializedResourceWriterRequiredException)
        {
            if (!_usePreserializedResources)
            {
                Logger.LogErrorWithCodeFromResources("GenerateResource.PreserializedResourcesRequiresProperty");
            }

            if (!HaveSystemResourcesExtensionsReference)
            {
                Logger.LogErrorWithCodeFromResources("GenerateResource.PreserializedResourcesRequiresExtensions");
            }

            // one of the above should have been logged as we would have used preserialized writer otherwise.
            Debug.Assert(Logger.HasLoggedErrors);

            // We may have partially written some string resources to a file, then bailed out
            // because we encountered a non-string resource but don't meet the prereqs.
            RemoveCorruptedFile(filename);
        }
    }

    private bool? _haveSystemResourcesExtensionsReference;

    private bool HaveSystemResourcesExtensionsReference
    {
        get
        {
            if (_haveSystemResourcesExtensionsReference.HasValue)
            {
                return _haveSystemResourcesExtensionsReference.Value;
            }

            if (_assemblyFiles == null)
            {
                _haveSystemResourcesExtensionsReference = false;
                return false;
            }

            PopulateAssemblyNames();

            foreach (AssemblyNameExtension assemblyName in _assemblyNames)
            {
                if (assemblyName == null)
                {
                    continue;
                }

                if (string.Equals(assemblyName.Name, "System.Resources.Extensions", StringComparison.OrdinalIgnoreCase))
                {
                    _haveSystemResourcesExtensionsReference = true;
                    return true;
                }
            }

            _haveSystemResourcesExtensionsReference = false;
            return false;
        }
    }

#if FEATURE_RESXREADER_LIVEDESERIALIZATION
    /// <summary>
    /// Read resources from an XML or binary format file
    /// </summary>
    /// <param name="readerInfo">Reader info</param>
    /// <param name="reader">Appropriate IResourceReader</param>
    /// <param name="fileName">Filename, for error messages</param>
    private void ReadResources(ReaderInfo readerInfo, IResourceReader reader, String fileName)
    {
        using (reader)
        {
            IDictionaryEnumerator resEnum = reader.GetEnumerator();
            while (resEnum.MoveNext())
            {
                string name = (string)resEnum.Key;
                object value = resEnum.Value;
                AddResource(readerInfo, name, value, fileName);
            }
        }
    }
#endif // FEATURE_RESXREADER_LIVEDESERIALIZATION

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
                        Logger.LogWarningWithCodeFromResources(null, fileName, sr.LineNumber - 1, 1, 0, 0, "GenerateResource.ObsoleteStringsTag");
                    }
                    else
                    {
                        throw new TextFileException(Logger.FormatResourceString("GenerateResource.UnexpectedInfBracket", "[" + skip), fileName, sr.LineNumber - 1, 1);
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
                        throw new TextFileException(Logger.FormatResourceString("GenerateResource.NoEqualsInLine", name), fileName, sr.LineNumber, sr.LinePosition);
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
                    throw new TextFileException(Logger.FormatResourceString("GenerateResource.NoNameInLine"), fileName, sr.LineNumber, sr.LinePosition);
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
                                        throw new TextFileException(Logger.FormatResourceString("GenerateResource.InvalidEscape", name.ToString(), (char)ch), fileName, sr.LineNumber, sr.LinePosition);
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
                                    throw new TextFileException(Logger.FormatResourceString("GenerateResource.InvalidHexEscapeValue", name.ToString(), new string(hex)), fileName, sr.LineNumber, sr.LinePosition);
                                }
                                catch (OverflowException)
                                {
                                    // We know about this one, too...
                                    throw new TextFileException(Logger.FormatResourceString("GenerateResource.InvalidHexEscapeValue", name.ToString(), new string(hex)), fileName, sr.LineNumber, sr.LinePosition);
                                }
                                quotedNewLine = (ch == '\n' || ch == '\r');
                                break;

                            default:
                                throw new TextFileException(Logger.FormatResourceString("GenerateResource.InvalidEscape", name.ToString(), (char)ch), fileName, sr.LineNumber, sr.LinePosition);
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
    private void WriteResources(ReaderInfo reader,
        IResourceWriter writer)
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
                    Logger.LogErrorWithCodeFromResources(null, fileName, 0, 0, 0, 0, "GenerateResource.OnlyStringsSupported", key, v?.GetType().FullName);
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
            Logger.LogWarningWithCodeFromResources(null, inputFileName, lineNumber, linePosition, 0, 0, "GenerateResource.DuplicateResourceName", entry.Name);
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
        public string outputFileName;
        public string cultureName;
        // We use a list to preserve the resource ordering (primarily for easier testing),
        // but also use a hash table to check for duplicate names.
        public List<IResource> resources;
        public Dictionary<string, IResource> resourcesHashTable;
        public string assemblySimpleName;  // The main assembly's simple name (ie, no .resources)
        public bool fromNeutralResources;  // Was this from the main assembly (or if the NRLA specified fallback to satellite, that satellite?)

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
    [Serializable]
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
    #endregion // Code from ResGen.EXE
}

#if !FEATURE_ASSEMBLYLOADCONTEXT
/// <summary>
/// This implemention of ITypeResolutionService is passed into the ResxResourceReader
/// class, which calls back into the methods on this class in order to resolve types
/// and assemblies that are referenced inside of the .RESX.
/// </summary>
internal class AssemblyNamesTypeResolutionService : ITypeResolutionService
{
    private Hashtable _cachedAssemblies;
    private string[] _referencePaths;
    private Hashtable _cachedTypes = new Hashtable();

    /// <summary>
    /// Constructor, initialized with the set of resolved reference paths passed
    /// into the GenerateResource task.
    /// </summary>
    /// <param name="referencePaths"></param>
    internal AssemblyNamesTypeResolutionService(string[] referencePaths)
    {
        _referencePaths = referencePaths;
    }

    /// <summary>
    /// Not implemented.  Not called by the ResxResourceReader.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public Assembly GetAssembly(AssemblyName name)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// Not implemented.  Not called by the ResxResourceReader.
    /// </summary>
    /// <param name="name"></param>
    /// <param name="throwOnError"></param>
    /// <returns></returns>
    public Assembly GetAssembly(AssemblyName name, bool throwOnError)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// Given a path to an assembly, load the assembly if it's not already loaded.
    /// </summary>
    /// <param name="pathToAssembly"></param>
    /// <param name="throwOnError"></param>
    /// <returns></returns>
    private Assembly GetAssemblyByPath(string pathToAssembly, bool throwOnError)
    {
        if (_cachedAssemblies == null)
        {
            _cachedAssemblies = new Hashtable();
        }

        if (!_cachedAssemblies.Contains(pathToAssembly))
        {
            try
            {
                _cachedAssemblies[pathToAssembly] = Assembly.UnsafeLoadFrom(pathToAssembly);
            }
            catch when (!throwOnError)
            {
            }
        }

        return (Assembly)_cachedAssemblies[pathToAssembly];
    }

    /// <summary>
    /// Not implemented.  Not called by the ResxResourceReader.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public string GetPathOfAssembly(AssemblyName name)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// Returns the type with the specified name.  Searches for the type in all
    /// of the assemblies passed into the References parameter of the GenerateResource
    /// task.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public Type GetType(string name)
    {
        return GetType(name, true);
    }

    /// <summary>
    /// Returns the type with the specified name.  Searches for the type in all
    /// of the assemblies passed into the References parameter of the GenerateResource
    /// task.
    /// </summary>
    /// <param name="name"></param>
    /// <param name="throwOnError"></param>
    /// <returns></returns>
    public Type GetType(string name, bool throwOnError)
    {
        return GetType(name, throwOnError, false);
    }

    /// <summary>
    /// Returns the type with the specified name.  Searches for the type in all
    /// of the assemblies passed into the References parameter of the GenerateResource
    /// task.
    /// </summary>
    /// <param name="name"></param>
    /// <param name="throwOnError"></param>
    /// <param name="ignoreCase"></param>
    /// <returns></returns>
    public Type GetType(string name, bool throwOnError, bool ignoreCase)
    {
        Type resultFromCache = (Type)_cachedTypes[name];

        if (!_cachedTypes.Contains(name))
        {
            // first try to resolve in the GAC
            Type result = Type.GetType(name, false, ignoreCase);

            // did not find it in the GAC, check each assembly
            if ((result == null) && (_referencePaths != null))
            {
                foreach (ITaskItem referencePath in _referencePaths)
                {
                    Assembly a = this.GetAssemblyByPath(referencePath.ItemSpec, throwOnError);
                    if (a != null)
                    {
                        result = a.GetType(name, false, ignoreCase);
                        if (result == null)
                        {
                            int indexOfComma = name.IndexOf(",", StringComparison.Ordinal);
                            if (indexOfComma != -1)
                            {
                                string shortName = name.Substring(0, indexOfComma);
                                result = a.GetType(shortName, false, ignoreCase);
                            }
                        }

                        if (result != null)
                        {
                            break;
                        }
                    }
                }
            }

            if (result == null && throwOnError)
            {
                ErrorUtilities.ThrowArgument("GenerateResource.CouldNotLoadType", name);
            }

            _cachedTypes[name] = result;
            resultFromCache = result;
        }

        return resultFromCache;
    }

    /// <summary>
    /// Not implemented.  Not called by the ResxResourceReader.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public void ReferenceAssembly(AssemblyName name)
    {
        throw new NotSupportedException();
    }
}
#endif