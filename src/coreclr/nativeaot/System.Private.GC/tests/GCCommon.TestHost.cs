// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Internal.Runtime.GarbageCollection;

internal static partial class GCCommon
{
    internal static void ResetHighPrecisionTimeStamp()
    {
        g_QPFus = 0.0;
    }
}
