// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;

internal static unsafe class EventPipeGcSmoke
{
    public static bool CollectAndValidate(bool background)
    {
        fixed (char* providerName = "Microsoft-Windows-DotNETRuntime")
        fixed (char* privateProviderName =
                   "Microsoft-Windows-DotNETRuntimePrivate")
        {
            EventPipeProviderConfiguration* providers =
                stackalloc EventPipeProviderConfiguration[2];
            providers[0] = new()
            {
                ProviderName = providerName,
                Keywords = 1,
                LoggingLevel = 4,
            };
            providers[1] = new()
            {
                ProviderName = privateProviderName,
                Keywords = 1,
                LoggingLevel = 4,
            };
            ulong session = EventPipeInternal_Enable(
                null,
                format: 1,
                circularBufferSizeInMB: 16,
                providers,
                numProviders: background ? 2u : 1u);
            if (session == 0)
            {
                Console.WriteLine("EventPipe GC session could not be enabled.");
                return false;
            }

            try
            {
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: !background,
                    compacting: !background);
                if (background)
                {
                    GC.Collect(
                        GC.MaxGeneration,
                        GCCollectionMode.Forced,
                        blocking: true,
                        compacting: false);
                }

                int startCount = 0;
                int endCount = 0;
                int heapStatsCount = 0;
                int backgroundBeginCount = 0;
                int backgroundEndCount = 0;
                nint privateProvider = background
                    ? EventPipeInternal_GetProvider(privateProviderName)
                    : 0;
                EventPipeEventInstanceData eventData = default;
                while (EventPipeInternal_GetNextEvent(session, &eventData) != 0)
                {
                    if (eventData.ProviderId == privateProvider)
                    {
                        if (eventData.EventId == 11)
                        {
                            backgroundBeginCount++;
                        }
                        else if (eventData.EventId == 17)
                        {
                            backgroundEndCount++;
                        }

                        continue;
                    }

                    switch (eventData.EventId)
                    {
                        case 1:
                            startCount++;
                            break;
                        case 2:
                            endCount++;
                            break;
                        case 4:
                            heapStatsCount++;
                            break;
                    }
                }

                bool result =
                    startCount > 0 &&
                    endCount > 0 &&
                    heapStatsCount > 0 &&
                    (!background ||
                     (backgroundBeginCount > 0 &&
                      backgroundEndCount > 0));
                if (!result)
                {
                    Console.WriteLine(
                        $"Runtime GC events missing: start={startCount}, " +
                        $"end={endCount}, stats={heapStatsCount}, " +
                        $"bgcBegin={backgroundBeginCount}, " +
                        $"bgcEnd={backgroundEndCount}");
                }

                return result;
            }
            finally
            {
                // Ensure the finalizer thread has finished emitting events for the collections
                // above before the listener session releases its buffers.
                GC.WaitForPendingFinalizers();
                EventPipeInternal_Disable(session);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventPipeProviderConfiguration
    {
        public char* ProviderName;
        public ulong Keywords;
        public uint LoggingLevel;
        public char* FilterData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventPipeEventInstanceData
    {
        public nint ProviderId;
        public uint EventId;
        public uint ThreadId;
        public long TimeStamp;
        public Guid ActivityId;
        public Guid ChildActivityId;
        public nint Payload;
        public uint PayloadLength;
    }

    [DllImport("*", EntryPoint = "EventPipeInternal_Enable")]
    private static extern ulong EventPipeInternal_Enable(
        char* outputFile,
        uint format,
        uint circularBufferSizeInMB,
        EventPipeProviderConfiguration* providers,
        uint numProviders);

    [DllImport("*", EntryPoint = "EventPipeInternal_Disable")]
    private static extern void EventPipeInternal_Disable(ulong session);

    [DllImport("*", EntryPoint = "EventPipeInternal_GetNextEvent")]
    private static extern int EventPipeInternal_GetNextEvent(
        ulong session,
        EventPipeEventInstanceData* eventData);

    [DllImport("*", EntryPoint = "EventPipeInternal_GetProvider")]
    private static extern nint EventPipeInternal_GetProvider(
        char* providerName);
}
