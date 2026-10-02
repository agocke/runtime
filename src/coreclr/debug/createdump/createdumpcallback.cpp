// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "createdump.h"

extern int createdump_main(const int argc, const char* argv[]);

void initialize_static_createdump()
{
    PAL_SetCreateDumpCallback(createdump_main);
}
