// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "common.h"
#include "CommonTypes.h"
#include "PalLimitedContext.h"
#include "CommonMacros.h"
#include "config.h"

#include "UnixSignals.h"

// Add handler for hardware exception signal
bool AddSignalHandler(int signal, SignalHandler handler, struct sigaction* previousAction)
{
    struct sigaction newAction;

    newAction.sa_flags = SA_RESTART;
    newAction.sa_handler = NULL;
    newAction.sa_sigaction = handler;
    newAction.sa_flags |= SA_SIGINFO;

    // Run our signal handlers on the alternate stack so we coexist correctly with
    // hosts (Go c-shared, JVM, etc.) that require all installed signal handlers honor
    // SA_ONSTACK. The runtime arranges for an alternate stack to be installed on every
    // managed thread via EnsureSignalAlternateStack.
#if !defined(TARGET_WASM)
    newAction.sa_flags |= SA_ONSTACK;
#endif

    sigemptyset(&newAction.sa_mask);

    if (sigaction(signal, NULL, previousAction) == -1)
    {
        ASSERT_UNCONDITIONALLY("Failed to get previous signal handler");
        return false;
    }

    if (previousAction->sa_flags & SA_ONSTACK)
    {
        // If the previous signal handler had additional signals in its mask we honor
        // that, so when we chain-call the previous handler those signals remain blocked
        // for the duration of the handler.
        newAction.sa_mask = previousAction->sa_mask;
    }

    if (sigaction(signal, &newAction, previousAction) == -1)
    {
        ASSERT_UNCONDITIONALLY("Failed to install signal handler");
        return false;
    }

    return true;
}

// Restore original handler for hardware exception signal
void RestoreSignalHandler(int signal_id, struct sigaction *previousAction)
{
    if (-1 == sigaction(signal_id, previousAction, NULL))
    {
        ASSERT_UNCONDITIONALLY("RestoreSignalHandler: sigaction() call failed");
    }
}
