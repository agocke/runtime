// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Internal.Runtime.GC
{
    internal enum HeapVerifyFlags : int
    {
        HEAPVERIFY_NONE = 0,
        HEAPVERIFY_GC = 1,
        HEAPVERIFY_BARRIERCHECK = 2,
        HEAPVERIFY_SYNCBLK = 4,
        HEAPVERIFY_NO_RANGE_CHECKS = 0x10,
        HEAPVERIFY_NO_MEM_FILL = 0x20,
        HEAPVERIFY_POST_GC_ONLY = 0x40,
        HEAPVERIFY_DEEP_ON_COMPACT = 0x80,
    }

    internal enum WriteBarrierFlavor : int
    {
        WRITE_BARRIER_DEFAULT = 0,
        WRITE_BARRIER_REGION_BIT = 1,
        WRITE_BARRIER_REGION_BYTE = 2,
        WRITE_BARRIER_SERVER = 3,
    }

    internal unsafe struct GCConfigStringHolder
    {
        private byte* m_str;
        public GCConfigStringHolder(byte* str) => m_str = str;
        public byte* Get() => m_str;
    }

    internal unsafe struct GCConfig
    {
        private static bool s_serverGC, s_serverGCProvided, s_UpdatedServerGC;
        private static bool s_concurrentGC, s_concurrentGCProvided, s_UpdatedConcurrentGC;
        private static bool s_conservativeGC, s_conservativeGCProvided, s_UpdatedConservativeGC;
        private static bool s_forceCompact, s_forceCompactProvided, s_UpdatedForceCompact;
        private static bool s_retainVM, s_retainVMProvided, s_UpdatedRetainVM;
        private static bool s_breakOnOOM, s_breakOnOOMProvided, s_UpdatedBreakOnOOM;
        private static bool s_noAffinitize, s_noAffinitizeProvided, s_UpdatedNoAffinitize;
        private static bool s_logEnabled, s_logEnabledProvided, s_UpdatedLogEnabled;
        private static bool s_configLogEnabled, s_configLogEnabledProvided, s_UpdatedConfigLogEnabled;
        private static bool s_gcNumaAware, s_gcNumaAwareProvided, s_UpdatedGCNumaAware;
        private static bool s_gcCpuGroup, s_gcCpuGroupProvided, s_UpdatedGCCpuGroup;
        private static bool s_gcCacheSizeFromSysConf, s_gcCacheSizeFromSysConfProvided, s_UpdatedGCCacheSizeFromSysConf;

        private static long s_gcLargePages, s_UpdatedGCLargePages; private static bool s_gcLargePagesProvided;
        private static long s_heapVerifyLevel, s_UpdatedHeapVerifyLevel; private static bool s_heapVerifyLevelProvided;
        private static long s_lohCompactionMode, s_UpdatedLOHCompactionMode; private static bool s_lohCompactionModeProvided;
        private static long s_lohThreshold, s_UpdatedLOHThreshold; private static bool s_lohThresholdProvided;
        private static long s_bgcSpinCount, s_UpdatedBGCSpinCount; private static bool s_bgcSpinCountProvided;
        private static long s_bgcSpin, s_UpdatedBGCSpin; private static bool s_bgcSpinProvided;
        private static long s_heapCount, s_UpdatedHeapCount; private static bool s_heapCountProvided;
        private static long s_maxHeapCount, s_UpdatedMaxHeapCount; private static bool s_maxHeapCountProvided;
        private static long s_gen0Size, s_UpdatedGen0Size; private static bool s_gen0SizeProvided;
        private static long s_segmentSize, s_UpdatedSegmentSize; private static bool s_segmentSizeProvided;
        private static long s_latencyMode, s_UpdatedLatencyMode; private static bool s_latencyModeProvided;
        private static long s_latencyLevel, s_UpdatedLatencyLevel; private static bool s_latencyLevelProvided;
        private static long s_logFileSize, s_UpdatedLogFileSize; private static bool s_logFileSizeProvided;
        private static long s_gcHeapAffinitizeMask, s_UpdatedGCHeapAffinitizeMask; private static bool s_gcHeapAffinitizeMaskProvided;
        private static long s_gcTrimYoungestKeepPercent, s_UpdatedGCTrimYoungestKeepPercent; private static bool s_gcTrimYoungestKeepPercentProvided;
        private static long s_gcHighMemPercent, s_UpdatedGCHighMemPercent; private static bool s_gcHighMemPercentProvided;
        private static long s_gcGen0MaxBudget, s_UpdatedGCGen0MaxBudget; private static bool s_gcGen0MaxBudgetProvided;
        private static long s_gcGen1MaxBudget, s_UpdatedGCGen1MaxBudget; private static bool s_gcGen1MaxBudgetProvided;
        private static long s_gcHeapHardLimit, s_UpdatedGCHeapHardLimit; private static bool s_gcHeapHardLimitProvided;
        private static long s_gcHeapHardLimitPercent, s_UpdatedGCHeapHardLimitPercent; private static bool s_gcHeapHardLimitPercentProvided;
        private static long s_gcTotalPhysicalMemory, s_UpdatedGCTotalPhysicalMemory; private static bool s_gcTotalPhysicalMemoryProvided;
        private static long s_gcRegionRange, s_UpdatedGCRegionRange; private static bool s_gcRegionRangeProvided;
        private static long s_gcRegionSize, s_UpdatedGCRegionSize; private static bool s_gcRegionSizeProvided;
        private static long s_gcEnableSpecialRegions, s_UpdatedGCEnableSpecialRegions; private static bool s_gcEnableSpecialRegionsProvided;
        private static long s_gcConserveMem, s_UpdatedGCConserveMem; private static bool s_gcConserveMemProvided;
        private static long s_gcWriteBarrier, s_UpdatedGCWriteBarrier; private static bool s_gcWriteBarrierProvided;
        private static long s_gcSpinCountUnit, s_UpdatedGCSpinCountUnit; private static bool s_gcSpinCountUnitProvided;
        private static long s_gcDynamicAdaptationMode, s_UpdatedGCDynamicAdaptationMode; private static bool s_gcDynamicAdaptationModeProvided;

        private static bool Get(bool value, bool provided, bool defaultValue) => provided ? value : defaultValue;
        private static long Get(long value, bool provided, long defaultValue) => provided ? value : defaultValue;

        public static void Initialize()
        {
            Initialize(ref s_serverGC, ref s_serverGCProvided, ref s_UpdatedServerGC, false);
            Initialize(ref s_concurrentGC, ref s_concurrentGCProvided, ref s_UpdatedConcurrentGC, true);
            Initialize(ref s_conservativeGC, ref s_conservativeGCProvided, ref s_UpdatedConservativeGC, false);
            Initialize(ref s_forceCompact, ref s_forceCompactProvided, ref s_UpdatedForceCompact, false);
            Initialize(ref s_retainVM, ref s_retainVMProvided, ref s_UpdatedRetainVM, false);
            Initialize(ref s_breakOnOOM, ref s_breakOnOOMProvided, ref s_UpdatedBreakOnOOM, false);
            Initialize(ref s_noAffinitize, ref s_noAffinitizeProvided, ref s_UpdatedNoAffinitize, false);
            Initialize(ref s_logEnabled, ref s_logEnabledProvided, ref s_UpdatedLogEnabled, false);
            Initialize(ref s_configLogEnabled, ref s_configLogEnabledProvided, ref s_UpdatedConfigLogEnabled, false);
            Initialize(ref s_gcNumaAware, ref s_gcNumaAwareProvided, ref s_UpdatedGCNumaAware, true);
            Initialize(ref s_gcCpuGroup, ref s_gcCpuGroupProvided, ref s_UpdatedGCCpuGroup, false);
            Initialize(ref s_gcCacheSizeFromSysConf, ref s_gcCacheSizeFromSysConfProvided, ref s_UpdatedGCCacheSizeFromSysConf, false);

            Initialize(ref s_gcLargePages, ref s_gcLargePagesProvided, ref s_UpdatedGCLargePages, 0);
            Initialize(ref s_heapVerifyLevel, ref s_heapVerifyLevelProvided, ref s_UpdatedHeapVerifyLevel, 0);
            Initialize(ref s_lohCompactionMode, ref s_lohCompactionModeProvided, ref s_UpdatedLOHCompactionMode, 0);
            Initialize(ref s_lohThreshold, ref s_lohThresholdProvided, ref s_UpdatedLOHThreshold, 85000);
            Initialize(ref s_bgcSpinCount, ref s_bgcSpinCountProvided, ref s_UpdatedBGCSpinCount, 140);
            Initialize(ref s_bgcSpin, ref s_bgcSpinProvided, ref s_UpdatedBGCSpin, 2);
            Initialize(ref s_heapCount, ref s_heapCountProvided, ref s_UpdatedHeapCount, 0);
            Initialize(ref s_maxHeapCount, ref s_maxHeapCountProvided, ref s_UpdatedMaxHeapCount, 0);
            Initialize(ref s_gen0Size, ref s_gen0SizeProvided, ref s_UpdatedGen0Size, 0);
            Initialize(ref s_segmentSize, ref s_segmentSizeProvided, ref s_UpdatedSegmentSize, 0);
            Initialize(ref s_latencyMode, ref s_latencyModeProvided, ref s_UpdatedLatencyMode, -1);
            Initialize(ref s_latencyLevel, ref s_latencyLevelProvided, ref s_UpdatedLatencyLevel, 1);
            Initialize(ref s_logFileSize, ref s_logFileSizeProvided, ref s_UpdatedLogFileSize, 0);
            Initialize(ref s_gcHeapAffinitizeMask, ref s_gcHeapAffinitizeMaskProvided, ref s_UpdatedGCHeapAffinitizeMask, 0);
            Initialize(ref s_gcTrimYoungestKeepPercent, ref s_gcTrimYoungestKeepPercentProvided, ref s_UpdatedGCTrimYoungestKeepPercent, 10);
            Initialize(ref s_gcHighMemPercent, ref s_gcHighMemPercentProvided, ref s_UpdatedGCHighMemPercent, 0);
            Initialize(ref s_gcGen0MaxBudget, ref s_gcGen0MaxBudgetProvided, ref s_UpdatedGCGen0MaxBudget, 0);
            Initialize(ref s_gcGen1MaxBudget, ref s_gcGen1MaxBudgetProvided, ref s_UpdatedGCGen1MaxBudget, 0);
            Initialize(ref s_gcHeapHardLimit, ref s_gcHeapHardLimitProvided, ref s_UpdatedGCHeapHardLimit, 0);
            Initialize(ref s_gcHeapHardLimitPercent, ref s_gcHeapHardLimitPercentProvided, ref s_UpdatedGCHeapHardLimitPercent, 0);
            Initialize(ref s_gcTotalPhysicalMemory, ref s_gcTotalPhysicalMemoryProvided, ref s_UpdatedGCTotalPhysicalMemory, 0);
            Initialize(ref s_gcRegionRange, ref s_gcRegionRangeProvided, ref s_UpdatedGCRegionRange, 0);
            Initialize(ref s_gcRegionSize, ref s_gcRegionSizeProvided, ref s_UpdatedGCRegionSize, 0);
            Initialize(ref s_gcEnableSpecialRegions, ref s_gcEnableSpecialRegionsProvided, ref s_UpdatedGCEnableSpecialRegions, 0);
            Initialize(ref s_gcConserveMem, ref s_gcConserveMemProvided, ref s_UpdatedGCConserveMem, 0);
            Initialize(ref s_gcWriteBarrier, ref s_gcWriteBarrierProvided, ref s_UpdatedGCWriteBarrier, 0);
            Initialize(ref s_gcSpinCountUnit, ref s_gcSpinCountUnitProvided, ref s_UpdatedGCSpinCountUnit, 0);
            Initialize(ref s_gcDynamicAdaptationMode, ref s_gcDynamicAdaptationModeProvided, ref s_UpdatedGCDynamicAdaptationMode, 1);
        }

        private static void Initialize(ref bool value, ref bool provided, ref bool updated, bool defaultValue)
        {
            value = defaultValue;
            provided = false;
            updated = defaultValue;
        }

        private static void Initialize(ref long value, ref bool provided, ref long updated, long defaultValue)
        {
            value = defaultValue;
            provided = false;
            updated = defaultValue;
        }

        public static bool GetServerGC() => s_serverGC;
        public static bool GetServerGC(bool value) => Get(s_serverGC, s_serverGCProvided, value);
        public static void SetServerGC(bool value) => s_UpdatedServerGC = value;
        public static bool GetConcurrentGC() => s_concurrentGC;
        public static bool GetConcurrentGC(bool value) => Get(s_concurrentGC, s_concurrentGCProvided, value);
        public static void SetConcurrentGC(bool value) => s_UpdatedConcurrentGC = value;
        public static bool GetConservativeGC() => s_conservativeGC;
        public static bool GetConservativeGC(bool value) => Get(s_conservativeGC, s_conservativeGCProvided, value);
        public static void SetConservativeGC(bool value) => s_UpdatedConservativeGC = value;
        public static bool GetForceCompact() => s_forceCompact;
        public static bool GetForceCompact(bool value) => Get(s_forceCompact, s_forceCompactProvided, value);
        public static void SetForceCompact(bool value) => s_UpdatedForceCompact = value;
        public static bool GetRetainVM() => s_retainVM;
        public static bool GetRetainVM(bool value) => Get(s_retainVM, s_retainVMProvided, value);
        public static void SetRetainVM(bool value) => s_UpdatedRetainVM = value;
        public static bool GetBreakOnOOM() => s_breakOnOOM;
        public static bool GetBreakOnOOM(bool value) => Get(s_breakOnOOM, s_breakOnOOMProvided, value);
        public static void SetBreakOnOOM(bool value) => s_UpdatedBreakOnOOM = value;
        public static bool GetNoAffinitize() => s_noAffinitize;
        public static bool GetNoAffinitize(bool value) => Get(s_noAffinitize, s_noAffinitizeProvided, value);
        public static void SetNoAffinitize(bool value) => s_UpdatedNoAffinitize = value;
        public static bool GetLogEnabled() => s_logEnabled;
        public static bool GetLogEnabled(bool value) => Get(s_logEnabled, s_logEnabledProvided, value);
        public static void SetLogEnabled(bool value) => s_UpdatedLogEnabled = value;
        public static bool GetConfigLogEnabled() => s_configLogEnabled;
        public static bool GetConfigLogEnabled(bool value) => Get(s_configLogEnabled, s_configLogEnabledProvided, value);
        public static void SetConfigLogEnabled(bool value) => s_UpdatedConfigLogEnabled = value;
        public static bool GetGCNumaAware() => s_gcNumaAware;
        public static bool GetGCNumaAware(bool value) => Get(s_gcNumaAware, s_gcNumaAwareProvided, value);
        public static void SetGCNumaAware(bool value) => s_UpdatedGCNumaAware = value;
        public static bool GetGCCpuGroup() => s_gcCpuGroup;
        public static bool GetGCCpuGroup(bool value) => Get(s_gcCpuGroup, s_gcCpuGroupProvided, value);
        public static void SetGCCpuGroup(bool value) => s_UpdatedGCCpuGroup = value;
        public static bool GetGCCacheSizeFromSysConf() => s_gcCacheSizeFromSysConf;
        public static bool GetGCCacheSizeFromSysConf(bool value) => Get(s_gcCacheSizeFromSysConf, s_gcCacheSizeFromSysConfProvided, value);
        public static void SetGCCacheSizeFromSysConf(bool value) => s_UpdatedGCCacheSizeFromSysConf = value;

        public static long GetGCLargePages() => s_gcLargePages;
        public static long GetGCLargePages(long value) => Get(s_gcLargePages, s_gcLargePagesProvided, value);
        public static void SetGCLargePages(long value) => s_UpdatedGCLargePages = value;
        public static long GetHeapVerifyLevel() => s_heapVerifyLevel;
        public static long GetHeapVerifyLevel(long value) => Get(s_heapVerifyLevel, s_heapVerifyLevelProvided, value);
        public static void SetHeapVerifyLevel(long value) => s_UpdatedHeapVerifyLevel = value;
        public static long GetLOHCompactionMode() => s_lohCompactionMode;
        public static long GetLOHCompactionMode(long value) => Get(s_lohCompactionMode, s_lohCompactionModeProvided, value);
        public static void SetLOHCompactionMode(long value) => s_UpdatedLOHCompactionMode = value;
        public static long GetLOHThreshold() => s_lohThreshold;
        public static long GetLOHThreshold(long value) => Get(s_lohThreshold, s_lohThresholdProvided, value);
        public static void SetLOHThreshold(long value) => s_UpdatedLOHThreshold = value;
        public static long GetBGCSpinCount() => s_bgcSpinCount;
        public static long GetBGCSpinCount(long value) => Get(s_bgcSpinCount, s_bgcSpinCountProvided, value);
        public static void SetBGCSpinCount(long value) => s_UpdatedBGCSpinCount = value;
        public static long GetBGCSpin() => s_bgcSpin;
        public static long GetBGCSpin(long value) => Get(s_bgcSpin, s_bgcSpinProvided, value);
        public static void SetBGCSpin(long value) => s_UpdatedBGCSpin = value;
        public static long GetHeapCount() => s_heapCount;
        public static long GetHeapCount(long value) => Get(s_heapCount, s_heapCountProvided, value);
        public static void SetHeapCount(long value) => s_UpdatedHeapCount = value;
        public static long GetMaxHeapCount() => s_maxHeapCount;
        public static long GetMaxHeapCount(long value) => Get(s_maxHeapCount, s_maxHeapCountProvided, value);
        public static void SetMaxHeapCount(long value) => s_UpdatedMaxHeapCount = value;
        public static long GetGen0Size() => s_gen0Size;
        public static long GetGen0Size(long value) => Get(s_gen0Size, s_gen0SizeProvided, value);
        public static void SetGen0Size(long value) => s_UpdatedGen0Size = value;
        public static long GetSegmentSize() => s_segmentSize;
        public static long GetSegmentSize(long value) => Get(s_segmentSize, s_segmentSizeProvided, value);
        public static void SetSegmentSize(long value) => s_UpdatedSegmentSize = value;
        public static long GetLatencyMode() => s_latencyMode;
        public static long GetLatencyMode(long value) => Get(s_latencyMode, s_latencyModeProvided, value);
        public static void SetLatencyMode(long value) => s_UpdatedLatencyMode = value;
        public static long GetLatencyLevel() => s_latencyLevel;
        public static long GetLatencyLevel(long value) => Get(s_latencyLevel, s_latencyLevelProvided, value);
        public static void SetLatencyLevel(long value) => s_UpdatedLatencyLevel = value;
        public static long GetLogFileSize() => s_logFileSize;
        public static long GetLogFileSize(long value) => Get(s_logFileSize, s_logFileSizeProvided, value);
        public static void SetLogFileSize(long value) => s_UpdatedLogFileSize = value;
        public static long GetGCHeapAffinitizeMask() => s_gcHeapAffinitizeMask;
        public static long GetGCHeapAffinitizeMask(long value) => Get(s_gcHeapAffinitizeMask, s_gcHeapAffinitizeMaskProvided, value);
        public static void SetGCHeapAffinitizeMask(long value) => s_UpdatedGCHeapAffinitizeMask = value;
        public static long GetGCTrimYoungestKeepPercent() => s_gcTrimYoungestKeepPercent;
        public static long GetGCTrimYoungestKeepPercent(long value) => Get(s_gcTrimYoungestKeepPercent, s_gcTrimYoungestKeepPercentProvided, value);
        public static void SetGCTrimYoungestKeepPercent(long value) => s_UpdatedGCTrimYoungestKeepPercent = value;
        public static long GetGCHighMemPercent() => s_gcHighMemPercent;
        public static long GetGCHighMemPercent(long value) => Get(s_gcHighMemPercent, s_gcHighMemPercentProvided, value);
        public static void SetGCHighMemPercent(long value) => s_UpdatedGCHighMemPercent = value;
        public static long GetGCGen0MaxBudget() => s_gcGen0MaxBudget;
        public static long GetGCGen0MaxBudget(long value) => Get(s_gcGen0MaxBudget, s_gcGen0MaxBudgetProvided, value);
        public static void SetGCGen0MaxBudget(long value) => s_UpdatedGCGen0MaxBudget = value;
        public static long GetGCGen1MaxBudget() => s_gcGen1MaxBudget;
        public static long GetGCGen1MaxBudget(long value) => Get(s_gcGen1MaxBudget, s_gcGen1MaxBudgetProvided, value);
        public static void SetGCGen1MaxBudget(long value) => s_UpdatedGCGen1MaxBudget = value;
        public static long GetGCHeapHardLimit() => s_gcHeapHardLimit;
        public static long GetGCHeapHardLimit(long value) => Get(s_gcHeapHardLimit, s_gcHeapHardLimitProvided, value);
        public static void SetGCHeapHardLimit(long value) => s_UpdatedGCHeapHardLimit = value;
        public static long GetGCHeapHardLimitPercent() => s_gcHeapHardLimitPercent;
        public static long GetGCHeapHardLimitPercent(long value) => Get(s_gcHeapHardLimitPercent, s_gcHeapHardLimitPercentProvided, value);
        public static void SetGCHeapHardLimitPercent(long value) => s_UpdatedGCHeapHardLimitPercent = value;
        public static long GetGCTotalPhysicalMemory() => s_gcTotalPhysicalMemory;
        public static long GetGCTotalPhysicalMemory(long value) => Get(s_gcTotalPhysicalMemory, s_gcTotalPhysicalMemoryProvided, value);
        public static void SetGCTotalPhysicalMemory(long value) => s_UpdatedGCTotalPhysicalMemory = value;
        public static long GetGCRegionRange() => s_gcRegionRange;
        public static long GetGCRegionRange(long value) => Get(s_gcRegionRange, s_gcRegionRangeProvided, value);
        public static void SetGCRegionRange(long value) => s_UpdatedGCRegionRange = value;
        public static long GetGCRegionSize() => s_gcRegionSize;
        public static long GetGCRegionSize(long value) => Get(s_gcRegionSize, s_gcRegionSizeProvided, value);
        public static void SetGCRegionSize(long value) => s_UpdatedGCRegionSize = value;
        public static long GetGCEnableSpecialRegions() => s_gcEnableSpecialRegions;
        public static long GetGCEnableSpecialRegions(long value) => Get(s_gcEnableSpecialRegions, s_gcEnableSpecialRegionsProvided, value);
        public static void SetGCEnableSpecialRegions(long value) => s_UpdatedGCEnableSpecialRegions = value;
        public static long GetGCConserveMem() => s_gcConserveMem;
        public static long GetGCConserveMem(long value) => Get(s_gcConserveMem, s_gcConserveMemProvided, value);
        public static void SetGCConserveMem(long value) => s_UpdatedGCConserveMem = value;
        public static long GetGCWriteBarrier() => s_gcWriteBarrier;
        public static long GetGCWriteBarrier(long value) => Get(s_gcWriteBarrier, s_gcWriteBarrierProvided, value);
        public static void SetGCWriteBarrier(long value) => s_UpdatedGCWriteBarrier = value;
        public static long GetGCSpinCountUnit() => s_gcSpinCountUnit;
        public static long GetGCSpinCountUnit(long value) => Get(s_gcSpinCountUnit, s_gcSpinCountUnitProvided, value);
        public static void SetGCSpinCountUnit(long value) => s_UpdatedGCSpinCountUnit = value;
        public static long GetGCDynamicAdaptationMode() => s_gcDynamicAdaptationMode;
        public static long GetGCDynamicAdaptationMode(long value) => Get(s_gcDynamicAdaptationMode, s_gcDynamicAdaptationModeProvided, value);
        public static void SetGCDynamicAdaptationMode(long value) => s_UpdatedGCDynamicAdaptationMode = value;

        public static void RefreshHeapHardLimitSettingsDeferred()
        {
        }

        public static bool ParseGCHeapAffinitizeRanges(byte* cpuIndexRanges, AffinitySet* configAffinitySet, nuint* configAffinityMask)
        {
            if (cpuIndexRanges is null)
            {
                return *configAffinityMask == 0 || !GCToOSInterface.CanEnableGCCPUGroups();
            }

            if (*configAffinityMask != 0)
            {
                return true;
            }

            byte* current = cpuIndexRanges;
            do
            {
                nuint startIndex;
                nuint endIndex;
                if (!GCToOSInterface.ParseGCHeapAffinitizeRangesEntry(&current, &startIndex, &endIndex))
                {
                    return false;
                }

                nuint maxCpuCount = GCToOSInterface.GetMaxProcessorCount();
                if (startIndex >= maxCpuCount || endIndex >= maxCpuCount || endIndex < startIndex)
                {
                    return false;
                }

                for (nuint i = startIndex; i <= endIndex; i++)
                {
                    configAffinitySet->Add(i);
                    *configAffinityMask |= (nuint)1 << (int)(i & (nuint)((sizeof(nuint) * 8) - 1));
                }

                if (*current == (byte)',')
                {
                    current++;
                }
                else
                {
                    return *current == 0;
                }
            }
            while (true);
        }
    }
}
