// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The Win32 entry points that the Windows virtual memory port of GCToOSInterface calls,
// declared as <windows.h> declares them, except that every BOOL is spelled as int: a Win32 BOOL
// is four bytes wide and a managed bool is one, and there is no marshalling here to convert
// between them.
//
// They are [RuntimeImport]s rather than [DllImport]s: a runtime import is a direct call to a
// linked symbol with no marshalling, no argument copying, no lazy binding step and no GC mode
// transition, which is what code that runs with the world suspended requires. kernel32.lib and
// advapi32.lib are on the default link line of every NativeAOT application
// (Microsoft.NETCore.Native.Windows.targets), so each of these resolves at link time.
//
// This file is compiled into the shipping library only. The xUnit tests compile
// tests/GCToOSInterface.Imports.Windows.TestHost.cs in its place, which declares the same
// methods as ordinary P/Invokes so that the ported logic above them can be exercised, and
// records their arguments so that the flag translation can be asserted.

using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        [RuntimeImport(RuntimeLibrary, "VirtualAlloc")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

        [RuntimeImport(RuntimeLibrary, "VirtualAllocExNuma")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* VirtualAllocExNuma(void* hProcess, void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect, uint nndPreferred);

        [RuntimeImport(RuntimeLibrary, "VirtualFree")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

        [RuntimeImport(RuntimeLibrary, "VirtualUnlock")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int VirtualUnlock(void* lpAddress, nuint dwSize);

        [RuntimeImport(RuntimeLibrary, "GetLargePageMinimum")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern nuint GetLargePageMinimum();

        [RuntimeImport(RuntimeLibrary, "GlobalMemoryStatusEx")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int GlobalMemoryStatusEx(MEMORYSTATUSEX* lpBuffer);

        [RuntimeImport(RuntimeLibrary, "GetCurrentProcess")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* GetCurrentProcess();

        [RuntimeImport(RuntimeLibrary, "GetLastError")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint GetLastError();

        [RuntimeImport(RuntimeLibrary, "CloseHandle")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int CloseHandle(void* hObject);

        [RuntimeImport(RuntimeLibrary, "OpenProcessToken")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int OpenProcessToken(void* ProcessHandle, uint DesiredAccess, void** TokenHandle);

        [RuntimeImport(RuntimeLibrary, "LookupPrivilegeValueW")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int LookupPrivilegeValueW(char* lpSystemName, char* lpName, LUID* lpLuid);

        [RuntimeImport(RuntimeLibrary, "AdjustTokenPrivileges")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int AdjustTokenPrivileges(void* TokenHandle, int DisableAllPrivileges, TOKEN_PRIVILEGES* NewState, uint BufferLength, TOKEN_PRIVILEGES* PreviousState, uint* ReturnLength);
    }
}
