// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma once

#include <stdint.h>
#include <stdio.h>

#ifdef HOST_WINDOWS
#define DEFAULT_DUMP_PATH "%TEMP%\\"
#define DEFAULT_DUMP_TEMPLATE "dump.%p.dmp"
#else
#define DEFAULT_DUMP_PATH "/tmp/"
#define DEFAULT_DUMP_TEMPLATE "coredump.%p"
#endif

enum class DumpType
{
    Mini,
    Heap,
    Triage,
    Full
};

enum class AppModelType
{
    Normal,
    SingleFile,
    NativeAOT
};

typedef struct
{
    const char* DumpPathTemplate;
    enum DumpType DumpType;
    enum AppModelType AppModel;
    bool CreateDump;
    bool CrashReport;
    int Pid;
    int CrashThread;
    int Signal;
    int SignalCode;
    int SignalErrno;
    uint64_t SignalAddress;
    uint64_t ExceptionRecord;
} CreateDumpOptions;

#ifdef __GNUC__
#define CREATEDUMP_FORMAT_PRINTF(formatIndex, argumentIndex) __attribute__((format(printf, formatIndex, argumentIndex)))
#else
#define CREATEDUMP_FORMAT_PRINTF(formatIndex, argumentIndex)
#endif

extern FILE* g_logfile;
extern FILE* g_stdout;
extern bool g_diagnostics;
extern bool g_diagnosticsVerbose;

// Invokes the dump library without parsing command-line arguments or owning the caller's logging streams.
extern "C" int createdump_run(const CreateDumpOptions* options);
const char* GetDumpTypeString(DumpType dumpType);
void printf_status(const char* format, ...) CREATEDUMP_FORMAT_PRINTF(1, 2);
void printf_error(const char* format, ...) CREATEDUMP_FORMAT_PRINTF(1, 2);
