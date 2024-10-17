// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Tasks;

Console.WriteLine("Hello, World!");

ProcessResourceFiles process = new ProcessResourceFiles();

int _references = 0;
int inputsToProcess = 0;
int _satelliteInputs = 0;
int outputsToProcess = 0;
bool outOfProcExecutionSucceeded = false;
bool _stronglyTypedResourceSuccessfullyCreated = false;
bool UseSourcePath = false;
bool UsePreserializedResources = false;
string _stronglyTypedNamespace = "";
string StronglyTypedLanguage = "";
string _stronglyTypedManifestPrefix = "";
string StronglyTypedFileName = "";
string StronglyTypedClassName = "";
bool PublicClass = false;
bool ExtractResWFiles = false;
string OutputDirectory = "";
bool WarnOnBinaryFormatterUse = false;

process.Run(
    _references,
    inputsToProcess,
    _satelliteInputs,
    outputsToProcess,
    UseSourcePath,
    UsePreserializedResources,
    StronglyTypedLanguage,
    _stronglyTypedNamespace,
    _stronglyTypedManifestPrefix,
    StronglyTypedFileName,
    StronglyTypedClassName,
    PublicClass,
    ExtractResWFiles,
    OutputDirectory,
    WarnOnBinaryFormatterUse);

this.StronglyTypedClassName = process.StronglyTypedClassName; // in case a default was chosen
this.StronglyTypedFileName = process.StronglyTypedFilename;   // in case a default was chosen
_stronglyTypedResourceSuccessfullyCreated = process.StronglyTypedResourceSuccessfullyCreated;
if (process.UnsuccessfullyCreatedOutFiles != null)
{
    foreach (string item in process.UnsuccessfullyCreatedOutFiles)
    {
        _unsuccessfullyCreatedOutFiles.Add(item);
    }
}

if (ExtractResWFiles)
{
    ITaskItem[] outputResources = process.ExtractedResWFiles.ToArray();

    if (cachedOutputFiles.Count > 0)
    {
        OutputResources = new ITaskItem[outputResources.Length + cachedOutputFiles.Count];
        outputResources.CopyTo(OutputResources, 0);
        cachedOutputFiles.CopyTo(OutputResources, outputResources.Length);
    }
    else
    {
        OutputResources = outputResources;
    }

    // Get portable library cache info (and if needed, marshal it to this AD).
    List<ResGenDependencies.PortableLibraryFile> portableLibraryCacheInfo = process.PortableLibraryCacheInfo;
    for (int i = 0; i < portableLibraryCacheInfo.Count; i++)
    {
        _cache.UpdatePortableLibrary(portableLibraryCacheInfo[i]);
    }
}

// And now we serialize the cache to save our resgen linked file resolution for later use.
WriteStateFile();

RemoveUnsuccessfullyCreatedResourcesFromOutputResources();

RecordFilesWritten();

//MSBuildEventSource.Log.GenerateResourceOverallStop();

return !Log.HasLoggedErrors;