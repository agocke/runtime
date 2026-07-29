// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Runtime
{
    // Recognized by ILC by namespace and name rather than by assembly identity, so every
    // assembly that needs it declares its own copy (System.Private.CoreLib and Runtime.Base
    // both do the same).
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false)]
    internal sealed class RuntimeImportAttribute : Attribute
    {
        public string DllName { get; }
        public string EntryPoint { get; }

        public RuntimeImportAttribute(string entry)
        {
            EntryPoint = entry;
        }

        public RuntimeImportAttribute(string dllName, string entry)
        {
            EntryPoint = entry;
            DllName = dllName;
        }
    }
}
