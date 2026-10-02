// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "createdump.h"
#include "minipal/time.h"

uint64_t g_ticksPerMS = 0;
uint64_t g_startTime = 0;

FILE* g_logfile = nullptr;
FILE* g_stdout = stdout;
bool g_diagnostics = false;
bool g_diagnosticsVerbose = false;

extern "C" int createdump_run(const CreateDumpOptions* options)
{
#ifdef HOST_UNIX
    CLRConfigNoCache waitForAttach = CLRConfigNoCache::Get("CreateDumpWaitForAttach", /*noprefix*/ false, &getenv);
    DWORD value = 0;
    if (waitForAttach.IsSet() && waitForAttach.TryAsInteger(10, value) && value == 1)
    {
        fprintf(stderr, "[createdump] waiting for attach %u: ", getpid());
        fgetc(stdin);
    }
#endif
    if (options == nullptr)
    {
        printf_error("Missing dump options\n");
        return -1;
    }

    CreateDumpOptions dumpOptions = *options;
    g_ticksPerMS = minipal_hires_tick_frequency() / 1000UL;
    g_startTime = minipal_hires_ticks();
    TRACE("TickFrequency: %" PRIu64 " ticks per ms\n", g_ticksPerMS);

    AStringHolder tmpPath = new char[MAX_LONGPATH];
    if (dumpOptions.DumpPathTemplate == nullptr)
    {
        if (GetTempPathWrapper(MAX_LONGPATH, tmpPath) == 0)
        {
            printf_error("GetTempPath failed\n");
            return -1;
        }
        int exitCode = strcat_s(tmpPath, MAX_LONGPATH, DEFAULT_DUMP_TEMPLATE);
        if (exitCode != 0)
        {
            printf_error("strcat_s failed (%d)", exitCode);
            return exitCode;
        }
        dumpOptions.DumpPathTemplate = tmpPath;
    }

    if (CreateDump(dumpOptions))
    {
        printf_status("Dump successfully written in %" PRIu64 "ms\n", (minipal_hires_ticks() - g_startTime) / g_ticksPerMS);
        return 0;
    }
    printf_error("Failure took %" PRIu64 "ms\n", (minipal_hires_ticks() - g_startTime) / g_ticksPerMS);
    return -1;
}

const char*
GetDumpTypeString(DumpType dumpType)
{
    switch (dumpType)
    {
        case DumpType::Mini:
            return "minidump";
        case DumpType::Heap:
            return "minidump with heap";
        case DumpType::Triage:
            return "triage minidump";
        case DumpType::Full:
            return "full dump";
        default:
            return "unknown";
    }
}

void
printf_status(const char* format, ...)
{
    va_list args;
    va_start(args, format);
    if (g_logfile == nullptr)
    {
        fprintf(g_stdout, "[createdump] ");
    }
    vfprintf(g_stdout, format, args);
    fflush(g_stdout);
    va_end(args);
}

void
printf_error(const char* format, ...)
{
    va_list args;
    va_start(args, format);

    // Log error message to file
    if (g_logfile != nullptr)
    {
        va_list args2;
        va_copy(args2, args);
        vfprintf(g_logfile, format, args2);
        fflush(g_logfile);
    }
    // Always print errors on stderr
    fprintf(stderr, "[createdump] ");
    vfprintf(stderr, format, args);
    fflush(stderr);
    va_end(args);
}

MINIDUMP_TYPE
GetMiniDumpType(DumpType dumpType)
{
    switch (dumpType)
    {
        case DumpType::Mini:
            return (MINIDUMP_TYPE)(MiniDumpNormal |
                                   MiniDumpWithDataSegs |
                                   MiniDumpWithHandleData |
                                   MiniDumpWithThreadInfo);
        case DumpType::Heap:
            return (MINIDUMP_TYPE)(MiniDumpWithPrivateReadWriteMemory |
                                   MiniDumpWithDataSegs |
                                   MiniDumpWithHandleData |
                                   MiniDumpWithUnloadedModules |
                                   MiniDumpWithFullMemoryInfo |
                                   MiniDumpWithThreadInfo |
                                   MiniDumpWithTokenInformation);
        case DumpType::Triage:
            return (MINIDUMP_TYPE)(MiniDumpFilterTriage |
                                   MiniDumpIgnoreInaccessibleMemory |
                                   MiniDumpWithoutOptionalData |
                                   MiniDumpWithProcessThreadData |
                                   MiniDumpFilterModulePaths |
                                   MiniDumpWithUnloadedModules |
                                   MiniDumpFilterMemory |
                                   MiniDumpWithHandleData);
        case DumpType::Full:
        default:
            return (MINIDUMP_TYPE)(MiniDumpWithFullMemory |
                                   MiniDumpWithDataSegs |
                                   MiniDumpWithHandleData |
                                   MiniDumpWithUnloadedModules |
                                   MiniDumpWithFullMemoryInfo |
                                   MiniDumpWithThreadInfo |
                                   MiniDumpWithTokenInformation);
    }
}

#ifdef HOST_UNIX

static void
trace_prefix(const char* format, va_list args)
{
    // Only add this prefix if logging to the console
    if (g_logfile == nullptr)
    {
        fprintf(g_stdout, "[createdump] ");
    }
    fprintf(g_stdout, "%08" PRIx64 " ", minipal_hires_ticks() / g_ticksPerMS);
    vfprintf(g_stdout, format, args);
    fflush(g_stdout);
}

void
trace_printf(const char* format, ...)
{
    if (g_diagnostics)
    {
        va_list args;
        va_start(args, format);
        trace_prefix(format, args);
        va_end(args);
    }
}

void
trace_verbose_printf(const char* format, ...)
{
    if (g_diagnosticsVerbose)
    {
        va_list args;
        va_start(args, format);
        trace_prefix(format, args);
        va_end(args);
    }
}

void
CrashInfo::Trace(const char* format, ...)
{
    if (g_diagnostics)
    {
        va_list args;
        va_start(args, format);
        trace_prefix(format, args);
        va_end(args);
    }
}

void
CrashInfo::TraceVerbose(const char* format, ...)
{
    if (g_diagnosticsVerbose)
    {
        va_list args;
        va_start(args, format);
        trace_prefix(format, args);
        va_end(args);
    }
}

#endif // HOST_UNIX
