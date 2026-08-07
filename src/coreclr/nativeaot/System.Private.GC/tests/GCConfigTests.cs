// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the gcconfig.h / gcconfig.cpp port -- the configuration table, its storage
// and defaults, Initialize, RefreshHeapHardLimitSettings, EnumerateConfigurationValues, the
// string holder, and ParseGCHeapAffinitizeRanges.
//
// The ported bodies are the code under test. What is substituted is the EE underneath them:
// GCToEEInterface.TestHost.cs models the four config methods of nativeaot/Runtime/gcenv.ee.cpp
// and records every request, so the key sequence the port asks for is checked directly rather
// than inferred from the values it caches.
//
// The table itself is checked against gcconfig.h and gcconfig.cpp, which are embedded in this
// assembly, in the same way GCInterfaceLayoutTests checks the translated types against
// GCInterfaceOffsets.h: an entry that is missing, mistyped, out of order, or given the wrong key
// or default is reported per entry, without building or booting a runtime.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Xunit;

namespace Internal.Runtime.GarbageCollection;

internal static class GCConfigTestAssemblyInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        RuntimeHelpers.RunClassConstructor(typeof(GCConfigTests).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(ManagedGCEntryPointsTests).TypeHandle);
#if USE_REGIONS
        RuntimeHelpers.RunClassConstructor(typeof(GCWriteBarrierTests).TypeHandle);
#endif
    }
}

// GCConfig's storage is process-wide, and the affinitize-range tests inject into the same
// substituted imports as the rest, so this class joins the collection that serializes them.
[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCConfigTests : IDisposable
{
    /// <summary>
    /// The value of every backing field as the port declares it, captured before any test can
    /// call Initialize, and put back around each test so that no test observes another's writes
    /// -- or leaves a value behind for the test classes that read a config of their own.
    /// </summary>
    private static readonly KeyValuePair<FieldInfo, object>[] s_declaredValues = CaptureDeclaredValues();

    public GCConfigTests()
    {
        RestoreDeclaredValues();
        GCToEEInterface.Reset();
        GCToOSInterface.ResetProcessorRecording();
        EnumerationRecorder.Reset();
    }

    public void Dispose()
    {
        RestoreDeclaredValues();
        GCToEEInterface.Reset();
    }

    //
    // The native table, and the translation of it.
    //

    public static IEnumerable<object[]> NativeConfigs() =>
        NativeTable.Select(config => new object[] { config.Name });

    [Theory]
    [MemberData(nameof(NativeConfigs))]
    public void EveryNativeConfigIsTranslated(string name)
    {
        NativeConfig config = NativeTable.Single(entry => entry.Name == name);

        if (config.Kind == "STRING_CONFIG")
        {
            MethodInfo getter = typeof(GCConfig).GetMethod($"Get{name}", Type.EmptyTypes);
            Assert.True(getter is not null, $"GCConfig has no Get{name}().");
            Assert.Equal(typeof(GCConfigStringHolder), getter.ReturnType);
            return;
        }

        Type valueType = config.Kind == "BOOL_CONFIG" ? typeof(byte) : typeof(long);

        MethodInfo cached = typeof(GCConfig).GetMethod($"Get{name}", Type.EmptyTypes);
        Assert.True(cached is not null, $"GCConfig has no Get{name}().");
        Assert.Equal(valueType, cached.ReturnType);

        MethodInfo withDefault = typeof(GCConfig).GetMethod($"Get{name}", new[] { valueType });
        Assert.True(withDefault is not null, $"GCConfig has no Get{name}({valueType.Name}).");
        Assert.Equal(valueType, withDefault.ReturnType);

        MethodInfo setter = typeof(GCConfig).GetMethod($"Set{name}", new[] { valueType });
        Assert.True(setter is not null, $"GCConfig has no Set{name}({valueType.Name}).");
        Assert.Equal(typeof(void), setter.ReturnType);

        Assert.Equal(valueType, Field($"s_{name}").FieldType);
        Assert.Equal(typeof(byte), Field($"s_{name}Provided").FieldType);
        Assert.Equal(valueType, Field($"s_Updated{name}").FieldType);
    }

    [Theory]
    [MemberData(nameof(NativeConfigs))]
    public void EveryDefaultMatchesTheNativeTable(string name)
    {
        NativeConfig config = NativeTable.Single(entry => entry.Name == name);
        if (config.Kind == "STRING_CONFIG")
        {
            return;
        }

        Assert.Equal(config.Default, DeclaredValue($"s_{name}"));
        Assert.Equal(config.Default, DeclaredValue($"s_Updated{name}"));

        // Nothing has been provided before Initialize runs.
        Assert.Equal(0L, DeclaredValue($"s_{name}Provided"));
    }

    /// <summary>
    /// The port must not have grown a config the native table does not have: an extra one would
    /// be reported to the EE by EnumerateConfigurationValues and read from the environment by
    /// Initialize without anything in the C++ answering for it.
    /// </summary>
    [Fact]
    public void NoConfigIsTranslatedThatTheNativeTableDoesNotHave()
    {
        HashSet<string> native = NativeTable.Select(config => config.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string name in CachedConfigFieldNames())
        {
            Assert.True(native.Contains(name), $"GCConfig caches '{name}', which gcconfig.h does not declare.");
        }
    }

    /// <summary>
    /// The declaration order is part of what makes the two files diffable, and it is the order
    /// Initialize and EnumerateConfigurationValues walk the configs in.
    /// </summary>
    [Fact]
    public void FieldOrderMatchesTheNativeTable()
    {
        string[] expected = NativeTable
            .Where(config => config.Kind != "STRING_CONFIG")
            .Select(config => config.Name)
            .ToArray();

        Assert.Equal(expected, CachedConfigFieldNames().ToArray());
    }

    //
    // The cached accessors.
    //

    [Fact]
    public void ACachedGetterReturnsTheDeclaredDefault()
    {
        Assert.Equal(0, GCConfig.GetServerGC());
        Assert.Equal(1, GCConfig.GetConcurrentGC());
        Assert.Equal(1, GCConfig.GetGCNumaAware());
        Assert.Equal(85000, GCConfig.GetLOHThreshold());
        Assert.Equal(-1, GCConfig.GetLatencyMode());
        Assert.Equal(140, GCConfig.GetBGCSpinCount());
        Assert.Equal(0, GCConfig.GetGCCacheSizeFromSysConf());
    }

    [Fact]
    public void AnUnprovidedConfigTakesTheCallersDefault()
    {
        // The one-argument overload is the "unless the user said otherwise" form, so an
        // unprovided config must answer with the caller's value and not with the table's.
        Assert.Equal(1, GCConfig.GetServerGC(1));
        Assert.Equal(0, GCConfig.GetConcurrentGC(0));
        Assert.Equal(42, GCConfig.GetLOHThreshold(42));
    }

    [Fact]
    public void AProvidedConfigIgnoresTheCallersDefault()
    {
        GCToEEInterface.SetPrivateValue("gcServer", 1);
        GCToEEInterface.SetPrivateValue("GCLOHThreshold", 100000);
        GCConfig.Initialize();

        Assert.Equal(1, GCConfig.GetServerGC(0));
        Assert.Equal(100000, GCConfig.GetLOHThreshold(42));
    }

    [ConditionalFact(nameof(CallingConventionsCoincide))]
    public void SettingAConfigOnlyChangesWhatIsReported()
    {
        GCConfig.SetHeapCount(12);
        GCConfig.SetServerGC(1);

        // Set##name writes s_Updated##name, which is what EnumerateConfigurationValues reports;
        // the value the GC reads back is untouched.
        Assert.Equal(0, GCConfig.GetHeapCount());
        Assert.Equal(0, GCConfig.GetServerGC());

        EnumerateConfigurationValues();
        Assert.Equal(12, EnumerationRecorder.Single("HeapCount").Data);
        Assert.Equal(1, EnumerationRecorder.Single("ServerGC").Data);
    }

    //
    // Initialize.
    //

    [Fact]
    public void InitializeAsksForEveryCachedConfigInTableOrder()
    {
        GCConfig.Initialize();

        (string Kind, string PrivateKey, string PublicKey)[] expected = NativeTable
            .Where(config => config.Kind != "STRING_CONFIG")
            .Select(config => (config.Kind == "BOOL_CONFIG" ? "bool" : "int", config.PrivateKey, config.PublicKey))
            .ToArray();

        Assert.Equal(expected, GCToEEInterface.Requests
            .Select(request => (request.Kind, request.PrivateKey, request.PublicKey))
            .ToArray());
    }

    [Fact]
    public void InitializeDoesNotReadTheStringConfigs()
    {
        GCConfig.Initialize();

        // The C++ STRING_CONFIG expansion in Initialize is empty: string configs are read on
        // every call instead of being cached.
        Assert.DoesNotContain(GCToEEInterface.Requests, request => request.Kind == "string");
    }

    [Fact]
    public void InitializePrefersThePrivateKey()
    {
        GCToEEInterface.SetPrivateValue("GCHeapCount", 4);
        GCToEEInterface.SetPublicValue("System.GC.HeapCount", 8);

        GCConfig.Initialize();

        Assert.Equal(4, GCConfig.GetHeapCount());
    }

    [Fact]
    public void InitializeFallsBackToThePublicKey()
    {
        GCToEEInterface.SetPublicValue("System.GC.HeapCount", 8);

        GCConfig.Initialize();

        Assert.Equal(8, GCConfig.GetHeapCount());
    }

    [Fact]
    public void AConfigWithoutAPublicKeyIsAskedForWithANullOne()
    {
        // The EE only consults the public settings when it is handed a key, so a config the
        // native table gives NULL must never reach them.
        GCToEEInterface.SetPublicValue("HeapVerify", 3);
        GCToEEInterface.SetPublicValue("GCLOHCompact", 3);

        GCConfig.Initialize();

        Assert.All(
            GCToEEInterface.Requests.Where(request => request.PrivateKey is "HeapVerify" or "GCLOHCompact"),
            request => Assert.Null(request.PublicKey));
        Assert.Equal(0, GCConfig.GetHeapVerifyLevel());
        Assert.Equal(0, GCConfig.GetLOHCompactionMode());
    }

    [ConditionalFact(nameof(CallingConventionsCoincide))]
    public void InitializeCopiesEveryValueIntoTheReportedCopy()
    {
        GCToEEInterface.SetPrivateValue("gcServer", 1);
        GCToEEInterface.SetPrivateValue("GCgen0size", 4096);

        GCConfig.Initialize();
        EnumerateConfigurationValues();

        Assert.Equal(1, EnumerationRecorder.Single("ServerGC").Data);
        Assert.Equal(4096, EnumerationRecorder.Single("Gen0Size").Data);
    }

    [Theory]
    // The EE reads an unsigned 64 bit value and hands it over as an int64_t, so every bit
    // pattern has to arrive unchanged -- including the ones that read back as negative.
    [InlineData(0ul, 0L)]
    [InlineData(1ul, 1L)]
    [InlineData((ulong)long.MaxValue, long.MaxValue)]
    [InlineData(0x8000000000000000ul, long.MinValue)]
    [InlineData(0xFFFFFFFFFFFFFFFFul, -1L)]
    public void AnIntegerConfigKeepsEveryBitTheEEProvided(ulong provided, long expected)
    {
        GCToEEInterface.SetPrivateValue("GCgen0size", provided);

        GCConfig.Initialize();

        Assert.Equal(expected, GCConfig.GetGen0Size());
    }

    [Theory]
    // The EE writes a bool, which is the environment value compared against zero, so the GC only
    // ever sees 0 or 1 however large the number in the environment was.
    [InlineData(0ul, 0)]
    [InlineData(1ul, 1)]
    [InlineData(2ul, 1)]
    [InlineData(0xFFFFFFFFFFFFFFFFul, 1)]
    public void ABooleanConfigIsNarrowedByTheEE(ulong provided, int expected)
    {
        GCToEEInterface.SetPrivateValue("gcServer", provided);

        GCConfig.Initialize();

        Assert.Equal((byte)expected, GCConfig.GetServerGC());
    }

    [Fact]
    public void AValueTheEEWritesWithoutProvidingIsStillCached()
    {
        // The C++ hands the EE the address of the cached value itself, so anything the EE writes
        // stays behind whatever it returns. The port copies through a local and writes it back
        // unconditionally, which is the same observable behavior; a port that only wrote back on
        // success would fail this.
        GCToEEInterface.WriteWithoutProviding = 7;

        GCConfig.Initialize();

        Assert.Equal(7, GCConfig.GetGen0Size());
        Assert.Equal(1, GCConfig.GetServerGC());

        // Not provided, so the caller's default still wins.
        Assert.Equal(11, GCConfig.GetGen0Size(11));
    }

    [Fact]
    public void InitializeIsRepeatable()
    {
        GCToEEInterface.SetPrivateValue("GCHeapCount", 4);
        GCConfig.Initialize();
        Assert.Equal(4, GCConfig.GetHeapCount());

        // Initialize reads every config again from scratch; a config that has gone away goes back
        // to reporting the value it last read, and stops being "provided".
        GCToEEInterface.Reset();
        GCConfig.Initialize();

        Assert.Equal(4, GCConfig.GetHeapCount());
        Assert.Equal(9, GCConfig.GetHeapCount(9));
    }

    //
    // RefreshHeapHardLimitSettings.
    //

    [Fact]
    public void RefreshAsksForTheHeapHardLimitConfigsInOrder()
    {
        GCConfig.RefreshHeapHardLimitSettings();

        (string Kind, string PrivateKey, string PublicKey)[] expected = NativeRefreshedConfigs
            .Select(config => ("int", config.PrivateKey, config.PublicKey))
            .ToArray();

        Assert.Equal(expected, GCToEEInterface.Requests
            .Select(request => (request.Kind, request.PrivateKey, request.PublicKey))
            .ToArray());
    }

    [ConditionalFact(nameof(CallingConventionsCoincide))]
    public void RefreshUpdatesTheHeapHardLimitValues()
    {
        foreach (NativeConfig config in NativeRefreshedConfigs)
        {
            GCToEEInterface.SetPrivateValue(config.PrivateKey, 4096);
        }

        GCConfig.RefreshHeapHardLimitSettings();
        EnumerateConfigurationValues();

        foreach (NativeConfig config in NativeRefreshedConfigs)
        {
            Assert.Equal(4096L, Value($"s_{config.Name}"));
            Assert.Equal(4096L, Value($"s_Updated{config.Name}"));
            Assert.Equal(4096L, EnumerationRecorder.Single(config.Name).Data);
        }
    }

    [Fact]
    public void RefreshDoesNotMakeAConfigProvided()
    {
        foreach (NativeConfig config in NativeRefreshedConfigs)
        {
            GCToEEInterface.SetPrivateValue(config.PrivateKey, 4096);
        }

        GCConfig.RefreshHeapHardLimitSettings();

        // The C++ discards the bool GetIntConfigValue returns here, so the "was it provided"
        // state is exactly what Initialize left behind.
        foreach (NativeConfig config in NativeRefreshedConfigs)
        {
            Assert.Equal(0L, Value($"s_{config.Name}Provided"));
        }
    }

    //
    // EnumerateConfigurationValues.
    //

    [ConditionalFact(nameof(CallingConventionsCoincide))]
    public void EnumerateReportsEveryConfigInTableOrder()
    {
        EnumerateConfigurationValues();

        (string Name, string PublicKey, GCConfigurationType Type)[] expected = NativeTable
            .Select(config => (config.Name, config.PublicKey, config.Kind switch
            {
                "BOOL_CONFIG" => GCConfigurationType.Boolean,
                "INT_CONFIG" => GCConfigurationType.Int64,
                _ => GCConfigurationType.StringUtf8,
            }))
            .ToArray();

        Assert.Equal(expected, EnumerationRecorder.Values
            .Select(value => (value.Name, value.PublicKey, value.Type))
            .ToArray());
    }

    [ConditionalFact(nameof(CallingConventionsCoincide))]
    public void EnumerateReportsTheDeclaredDefaults()
    {
        EnumerateConfigurationValues();

        foreach (NativeConfig config in NativeTable.Where(entry => entry.Kind != "STRING_CONFIG"))
        {
            Assert.Equal(config.Default, EnumerationRecorder.Single(config.Name).Data);
        }
    }

    [ConditionalFact(nameof(CallingConventionsCoincide))]
    public void EnumerateForwardsTheContextUnchanged()
    {
        int context = 0;
        IntPtr expected = (IntPtr)(&context);
        EnumerateConfigurationValues(&context);

        Assert.All(EnumerationRecorder.Values, value => Assert.Equal(expected, value.Context));
    }

    [ConditionalFact(nameof(CallingConventionsCoincide))]
    public void EnumerateReadsTheStringConfigsFromTheEE()
    {
        GCToEEInterface.SetPrivateString("GCLogFile", "/gc.log");
        GCToEEInterface.SetPublicString("System.GC.Name", "libclrgc.so");

        EnumerateConfigurationValues();

        // The value reported for a string config is the pointer the EE handed over, which the
        // EE's own callback reads before the holder frees it.
        Assert.Equal("/gc.log", EnumerationRecorder.Single("LogFile").Text);
        Assert.Equal("libclrgc.so", EnumerationRecorder.Single("GCName").Text);
        Assert.Equal(0, EnumerationRecorder.Single("GCPath").Data);
        Assert.Null(EnumerationRecorder.Single("GCPath").Text);
    }

    [ConditionalFact(nameof(CallingConventionsCoincide))]
    public void EnumerateFreesEveryStringItRead()
    {
        foreach (NativeConfig config in NativeTable.Where(entry => entry.Kind == "STRING_CONFIG"))
        {
            GCToEEInterface.SetPrivateString(config.PrivateKey, config.Name);
        }

        EnumerateConfigurationValues();

        // The C++ holder lives to the end of the block the callback is made in, so every string
        // is still the EE's while the callback reads it and none of them survives the loop.
        Assert.All(
            EnumerationRecorder.Values.Where(value => value.Type == GCConfigurationType.StringUtf8),
            value => Assert.True(value.WasOutstanding));

        Assert.Equal(5, GCToEEInterface.FreedStrings.Count);
        Assert.Empty(GCToEEInterface.OutstandingStrings);
    }

    [ConditionalFact(nameof(CallingConventionsCoincide))]
    public void EnumerateDoesNotFreeAStringTheEEDidNotProvide()
    {
        EnumerateConfigurationValues();

        // GCConfigStringHolder's destructor only frees a non-null string, and an unset config
        // leaves the pointer null.
        Assert.Empty(GCToEEInterface.FreedStrings);
    }

    [ConditionalFact(nameof(CallingConventionsCoincide))]
    public void EnumerateAsksForTheStringConfigsWithBothKeys()
    {
        EnumerateConfigurationValues();

        (string PrivateKey, string PublicKey)[] expected = NativeTable
            .Where(config => config.Kind == "STRING_CONFIG")
            .Select(config => (config.PrivateKey, config.PublicKey))
            .ToArray();

        Assert.Equal(expected, GCToEEInterface.Requests
            .Where(request => request.Kind == "string")
            .Select(request => (request.PrivateKey, request.PublicKey))
            .ToArray());
    }

    //
    // The string configs and their holder.
    //

    [Fact]
    public void AStringConfigIsReadFromTheEEOnEveryCall()
    {
        GCToEEInterface.SetPrivateString("GCLogFile", "first");

        using (GCConfigStringHolder first = GCConfig.GetLogFile())
        {
            Assert.Equal("first", GCToEEInterface.ReadString(first.Get()));
        }

        GCToEEInterface.SetPrivateString("GCLogFile", "second");
        using (GCConfigStringHolder second = GCConfig.GetLogFile())
        {
            Assert.Equal("second", GCToEEInterface.ReadString(second.Get()));
        }

        Assert.Equal(2, GCToEEInterface.Requests.Count(request => request.Kind == "string"));
    }

    [Fact]
    public void AStringConfigIsNullWhenTheEEHasNone()
    {
        using GCConfigStringHolder holder = GCConfig.GetGCPath();

        Assert.True(holder.Get() is null);
    }

    [Fact]
    public void TheHolderFreesTheStringExactlyOnce()
    {
        GCToEEInterface.SetPrivateString("GCName", "libclrgc.so");

        GCConfigStringHolder holder = GCConfig.GetGCName();
        byte* value = holder.Get();
        Assert.Single(GCToEEInterface.OutstandingStrings);

        holder.Dispose();
        Assert.Equal(new[] { (IntPtr)value }, GCToEEInterface.FreedStrings);
        Assert.Empty(GCToEEInterface.OutstandingStrings);

        // The C++ destructor nulls the pointer after freeing it, so a second release is a no-op
        // rather than a double free.
        holder.Dispose();
        Assert.Single(GCToEEInterface.FreedStrings);
        Assert.True(holder.Get() is null);
    }

    [Fact]
    public void TheHolderDoesNotFreeANullString()
    {
        GCConfigStringHolder holder = GCConfig.GetConfigLogFile();

        holder.Dispose();

        Assert.Empty(GCToEEInterface.FreedStrings);
    }

    [Theory]
    [InlineData("GCHeapAffinitizeRanges", "System.GC.HeapAffinitizeRanges")]
    [InlineData("GCName", "System.GC.Name")]
    [InlineData("GCPath", "System.GC.Path")]
    public void AStringConfigFallsBackToItsPublicKey(string privateKey, string publicKey)
    {
        GCToEEInterface.SetPublicString(publicKey, "value");

        using GCConfigStringHolder holder = privateKey switch
        {
            "GCHeapAffinitizeRanges" => GCConfig.GetGCHeapAffinitizeRanges(),
            "GCName" => GCConfig.GetGCName(),
            _ => GCConfig.GetGCPath(),
        };

        Assert.Equal("value", GCToEEInterface.ReadString(holder.Get()));
        Assert.Equal(privateKey, GCToEEInterface.Requests[0].PrivateKey);
        Assert.Equal(publicKey, GCToEEInterface.Requests[0].PublicKey);
    }

    [Theory]
    [InlineData("GCLogFile")]
    [InlineData("GCConfigLogFile")]
    public void AStringConfigWithoutAPublicKeyIsAskedForWithANullOne(string privateKey)
    {
        using (GCConfigStringHolder holder = privateKey == "GCLogFile" ? GCConfig.GetLogFile() : GCConfig.GetConfigLogFile())
        {
            Assert.True(holder.Get() is null);
        }

        Assert.Equal(privateKey, GCToEEInterface.Requests[0].PrivateKey);
        Assert.Null(GCToEEInterface.Requests[0].PublicKey);
    }

    //
    // ParseGCHeapAffinitizeRanges.
    //

    [Fact]
    public void NoRangesAndNoMaskIsNotAnError()
    {
        AffinitySetScope scope = new AffinitySetScope(64);
        nuint mask = 0;

        Assert.True(GCConfig.ParseGCHeapAffinitizeRanges(null, scope.Set, ref mask));
        Assert.Equal((nuint)0, mask);
        Assert.True(scope.Set->IsEmpty());

        scope.Dispose();
    }

    [Fact]
    public void AMaskWithoutRangesIsUsedAsIs()
    {
        AffinitySetScope scope = new AffinitySetScope(64);
        nuint mask = 0b1010;

#if TARGET_WINDOWS
        // Case 2.5: a mask cannot express a CPU group, so a mask with groups enabled is an error.
        GCToOSInterface.CanEnableGCCPUGroupsValue = 0;
#endif

        Assert.True(GCConfig.ParseGCHeapAffinitizeRanges(null, scope.Set, ref mask));
        Assert.Equal((nuint)0b1010, mask);
        Assert.True(scope.Set->IsEmpty());

        scope.Dispose();
    }

#if TARGET_WINDOWS
    [Fact]
    public void AMaskWithoutRangesIsRejectedWhenCpuGroupsAreEnabled()
    {
        AffinitySetScope scope = new AffinitySetScope(64);
        nuint mask = 0b1010;
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;

        Assert.False(GCConfig.ParseGCHeapAffinitizeRanges(null, scope.Set, ref mask));

        scope.Dispose();
    }
#endif

    [Fact]
    public void RangesAreIgnoredWhenAMaskWasAlsoGiven()
    {
        AffinitySetScope scope = new AffinitySetScope(64);
        nuint mask = 0b0100;

        // Case 2: the mask decides, and the ranges are not even parsed.
        Assert.True(ParseRanges(Ranges("1,3"), scope.Set, ref mask));
        Assert.Equal((nuint)0b0100, mask);
        Assert.True(scope.Set->IsEmpty());

        scope.Dispose();
    }

    [Fact]
    public void RangesFillTheSetAndTheMask()
    {
        SetMaxProcessorCount(64);
        AffinitySetScope scope = new AffinitySetScope(64);
        nuint mask = 0;

        Assert.True(ParseRanges(Ranges("1,3,5,7-9,12"), scope.Set, ref mask));

        nuint[] expected = { 1, 3, 5, 7, 8, 9, 12 };
        for (nuint i = 0; i < 64; i++)
        {
            Assert.Equal(Array.IndexOf(expected, i) >= 0, scope.Set->Contains(i));
        }

        nuint expectedMask = 0;
        foreach (nuint cpu in expected)
        {
            expectedMask |= (nuint)1 << (int)cpu;
        }

        Assert.Equal(expectedMask, mask);
        scope.Dispose();
    }

    [Fact]
    public void ASingleIndexIsARangeOfOne()
    {
        SetMaxProcessorCount(64);
        AffinitySetScope scope = new AffinitySetScope(64);
        nuint mask = 0;

        Assert.True(ParseRanges(Ranges("5"), scope.Set, ref mask));

        Assert.True(scope.Set->Contains(5));
        Assert.Equal((nuint)1 << 5, mask);
        scope.Dispose();
    }

    [Fact]
    public void AnIndexAboveTheBitsetEntryWrapsInTheMaskOnly()
    {
        // The mask is one native word, so the C++ folds the index into it with
        // `1 << (i & (BitsPerBitsetEntry - 1))` while the set keeps the real index.
        nuint bitsPerEntry = (nuint)sizeof(nuint) * 8;
        nuint cpu = bitsPerEntry + 1;

        SetMaxProcessorCount(bitsPerEntry * 2);
        AffinitySetScope scope = new AffinitySetScope(bitsPerEntry * 2);
        nuint mask = 0;

        Assert.True(ParseRanges(Ranges(cpu.ToString(CultureInfo.InvariantCulture)), scope.Set, ref mask));

        Assert.True(scope.Set->Contains(cpu));
        Assert.Equal((nuint)1 << 1, mask);
        scope.Dispose();
    }

    [Fact]
    public void AnEmptyStringIsAccepted()
    {
        // The C++ breaks out of the loop before it ever moves number_end, so the terminator it
        // then looks at is the one the string started with. Preserved rather than corrected.
        SetMaxProcessorCount(64);
        AffinitySetScope scope = new AffinitySetScope(64);
        nuint mask = 0;

        Assert.True(ParseRanges(Ranges(string.Empty), scope.Set, ref mask));
        Assert.Equal((nuint)0, mask);

        scope.Dispose();
    }

    [Theory]
    [InlineData("x")]
    [InlineData("1,")]
    [InlineData("1,x")]
    [InlineData("1 2")]
    [InlineData("64")]
    [InlineData("1,64")]
    [InlineData("60-64")]
    [InlineData("9-7")]
    public void AMalformedOrOutOfRangeListIsRejected(string ranges)
    {
        SetMaxProcessorCount(64);
        AffinitySetScope scope = new AffinitySetScope(64);
        nuint mask = 0;

        Assert.False(ParseRanges(Ranges(ranges), scope.Set, ref mask));

        scope.Dispose();
    }

    [Fact]
    public void TheHighestLegalIndexIsAccepted()
    {
        // One below the maximum processor count is in range; the entry that follows it is not.
        // Both are the boundary the C++ checks with >=, and neither may reach the assert in
        // AffinitySet::Add, which is what a Debug run of this test proves.
        SetMaxProcessorCount(64);
        AffinitySetScope scope = new AffinitySetScope(64);
        nuint mask = 0;

        Assert.True(ParseRanges(Ranges("63"), scope.Set, ref mask));
        Assert.True(scope.Set->Contains(63));

        scope.Dispose();
    }

#if !TARGET_WINDOWS
    [Fact]
    public void RangesUseTheMaximumProcessorCountAtTheTimeOfTheCall()
    {
        nuint bitsPerEntry = (nuint)sizeof(nuint) * 8;
        AffinitySetScope scope = new AffinitySetScope(bitsPerEntry * 2);
        nuint mask = 0;

        SetMaxProcessorCount(bitsPerEntry);
        Assert.False(ParseRanges(Ranges(bitsPerEntry.ToString(CultureInfo.InvariantCulture)), scope.Set, ref mask));

        SetMaxProcessorCount(bitsPerEntry * 2);
        mask = 0;
        Assert.True(ParseRanges(Ranges(bitsPerEntry.ToString(CultureInfo.InvariantCulture)), scope.Set, ref mask));

        scope.Dispose();
    }
#endif

#if TARGET_WINDOWS
    [Fact]
    public void WindowsRangesAreGroupRelative()
    {
        SetMaxProcessorCount(64);
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.CpuGroupCountValue = 2;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[0] = 4;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[1] = 3;
        GCToOSInterface.CpuGroupBeginValues[0] = 0;
        GCToOSInterface.CpuGroupBeginValues[1] = 4;

        AffinitySetScope scope = new AffinitySetScope(64);
        nuint mask = 0;

        Assert.True(ParseRanges(Ranges("0:1,1:0-2"), scope.Set, ref mask));

        nuint[] expected = { 1, 4, 5, 6 };
        for (nuint i = 0; i < 8; i++)
        {
            Assert.Equal(Array.IndexOf(expected, i) >= 0, scope.Set->Contains(i));
        }

        scope.Dispose();
    }

    [Fact]
    public void WindowsRejectsAnUngroupedRange()
    {
        SetMaxProcessorCount(64);
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.CpuGroupCountValue = 2;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[0] = 4;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[1] = 3;
        GCToOSInterface.CpuGroupBeginValues[0] = 0;
        GCToOSInterface.CpuGroupBeginValues[1] = 4;

        AffinitySetScope scope = new AffinitySetScope(64);
        nuint mask = 0;

        Assert.False(ParseRanges(Ranges("1"), scope.Set, ref mask));

        scope.Dispose();
    }
#endif

    //
    // Helpers.
    //

    private static void SetMaxProcessorCount(nuint maxCpuCount) =>
        GCToOSInterface.SetProcessAffinityMaxCpuCount(maxCpuCount);

    private static byte[] Ranges(string text) => Encoding.UTF8.GetBytes(text + "\0");

    private static bool ParseRanges(byte[] ranges, AffinitySet* set, ref nuint mask)
    {
        fixed (byte* first = ranges)
        {
            return GCConfig.ParseGCHeapAffinitizeRanges(first, set, ref mask);
        }
    }

    /// <summary>An affinity set with storage for a given number of processors.</summary>
    private readonly struct AffinitySetScope
    {
        public AffinitySetScope(nuint cpuCount)
        {
            nuint bitsPerEntry = (nuint)sizeof(nuint) * 8;
            nuint entries = (cpuCount + bitsPerEntry - 1) / bitsPerEntry;

            Storage = (nuint*)NativeMemory.AllocZeroed(entries, (nuint)sizeof(nuint));
            Set = (AffinitySet*)NativeMemory.AllocZeroed((nuint)sizeof(AffinitySet));
            Set->InitializeWithStorage(Storage, entries);
        }

        public AffinitySet* Set { get; }

        private nuint* Storage { get; }

        public void Dispose()
        {
            NativeMemory.Free(Set);
            NativeMemory.Free(Storage);
        }
    }

    private static void EnumerateConfigurationValues() => EnumerateConfigurationValues(null);

    private static void EnumerateConfigurationValues(void* context) =>
        GCConfig.EnumerateConfigurationValues(context, EnumerationRecorder.Callback);

    /// <summary>
    /// Whether the recorder can be handed to the port as a native function pointer. It is the
    /// address of a managed static (see <see cref="EnumerationRecorder"/>), which only works
    /// where the managed and the platform calling conventions coincide for a blittable
    /// signature. They do on every architecture the port supports; x86, where they do not, is
    /// also the one architecture IlcManagedGC rejects.
    /// </summary>
    public static bool CallingConventionsCoincide => RuntimeInformation.ProcessArchitecture != Architecture.X86;

    private static FieldInfo Field(string name)
    {
        FieldInfo field = typeof(GCConfig).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(field is not null, $"GCConfig has no field '{name}'.");
        return field;
    }

    private static long Value(string name) => Convert.ToInt64(Field(name).GetValue(null), CultureInfo.InvariantCulture);

    private static long DeclaredValue(string name)
    {
        FieldInfo field = Field(name);
        foreach (KeyValuePair<FieldInfo, object> declared in s_declaredValues)
        {
            if (declared.Key == field)
            {
                return Convert.ToInt64(declared.Value, CultureInfo.InvariantCulture);
            }
        }

        Assert.Fail($"No captured declared value for '{name}'.");
        return 0;
    }

    /// <summary>The cached configs, in the order the port declares their backing fields.</summary>
    private static IEnumerable<string> CachedConfigFieldNames() =>
        typeof(GCConfig)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .Where(name => name.StartsWith("s_", StringComparison.Ordinal)
                        && !name.StartsWith("s_Updated", StringComparison.Ordinal)
                        && !name.EndsWith("Provided", StringComparison.Ordinal))
            .Select(name => name.Substring("s_".Length));

    private static KeyValuePair<FieldInfo, object>[] CaptureDeclaredValues() =>
        typeof(GCConfig)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(byte) || field.FieldType == typeof(long))
            .Select(field => new KeyValuePair<FieldInfo, object>(field, field.GetValue(null)))
            .ToArray();

    private static void RestoreDeclaredValues()
    {
        foreach (KeyValuePair<FieldInfo, object> declared in s_declaredValues)
        {
            declared.Key.SetValue(null, declared.Value);
        }
    }

    /// <summary>
    /// The configuration values EnumerateConfigurationValues reported, in order. The callback is
    /// an unmanaged function pointer, as the EE's is, so what it can reach is static.
    /// </summary>
    /// <remarks>
    /// The port calls the callback through a suppressed-transition pointer, exactly as the C++
    /// calls it as native code, so the target cannot be an <c>[UnmanagedCallersOnly]</c> method:
    /// its reverse P/Invoke prologue would find the calling thread already in cooperative mode
    /// and fail. What is passed instead is the address of an ordinary static method, which the
    /// runtime compiles to the platform C ABI for a blittable signature -- the same property that
    /// lets the native EE call the managed <c>IGCHeap</c> slots of this port directly -- so the
    /// recorder runs as the plain managed code it is, on the cooperative test thread, with no
    /// transition in either direction.
    /// </remarks>
    private static class EnumerationRecorder
    {
        public static List<ReportedValue> Values { get; } = new();

        /// <summary>The recorder, typed as the EE's callback.</summary>
        public static delegate* unmanaged<void*, byte*, byte*, GCConfigurationType, long, void> Callback =>
            (delegate* unmanaged<void*, byte*, byte*, GCConfigurationType, long, void>)
            (delegate*<void*, byte*, byte*, GCConfigurationType, long, void>)&Record;

        public static void Reset() => Values.Clear();

        public static ReportedValue Single(string name) => Values.Single(value => value.Name == name);

        private static void Record(void* context, byte* name, byte* publicKey, GCConfigurationType type, long data) =>
            Values.Add(new ReportedValue(
                (IntPtr)context,
                GCToEEInterface.ReadString(name),
                GCToEEInterface.ReadString(publicKey),
                type,
                data,
                // A string is only valid for the duration of the callback, so it has to be read
                // here rather than afterwards -- and whether it is still the EE's is what says
                // that the holder frees it after the callback rather than before.
                type == GCConfigurationType.StringUtf8 ? GCToEEInterface.ReadString((byte*)data) : null,
                type == GCConfigurationType.StringUtf8 && GCToEEInterface.OutstandingStrings.Contains((IntPtr)data)));
    }

    private sealed class ReportedValue
    {
        public ReportedValue(IntPtr context, string name, string publicKey, GCConfigurationType type, long data, string text, bool wasOutstanding)
        {
            Context = context;
            Name = name;
            PublicKey = publicKey;
            Type = type;
            Data = data;
            Text = text;
            WasOutstanding = wasOutstanding;
        }

        public IntPtr Context { get; }

        public string Name { get; }

        public string PublicKey { get; }

        public GCConfigurationType Type { get; }

        public long Data { get; }

        /// <summary>The string a StringUtf8 value pointed at, read during the callback.</summary>
        public string Text { get; }

        /// <summary>Whether the EE still owned that string when the callback ran.</summary>
        public bool WasOutstanding { get; }
    }

    //
    // The native table, read out of the embedded gcconfig.h and gcconfig.cpp.
    //

    private sealed class NativeConfig
    {
        public NativeConfig(string kind, string name, string privateKey, string publicKey, long @default)
        {
            Kind = kind;
            Name = name;
            PrivateKey = privateKey;
            PublicKey = publicKey;
            Default = @default;
        }

        /// <summary>"BOOL_CONFIG", "INT_CONFIG" or "STRING_CONFIG".</summary>
        public string Kind { get; }

        public string Name { get; }

        public string PrivateKey { get; }

        /// <summary>The public key, or null where the table says NULL.</summary>
        public string PublicKey { get; }

        /// <summary>The declared default, or zero for a string config, which has none.</summary>
        public long Default { get; }
    }

    private static IReadOnlyList<NativeConfig> NativeTable { get; } = ReadNativeTable();

    /// <summary>The configs RefreshHeapHardLimitSettings re-reads, in the order it reads them.</summary>
    private static IReadOnlyList<NativeConfig> NativeRefreshedConfigs { get; } = ReadNativeRefreshedConfigs();

    private static IReadOnlyList<NativeConfig> ReadNativeTable()
    {
        string header = ReadResource("gcconfig.h");
        int start = header.IndexOf("#define GC_CONFIGURATION_KEYS", StringComparison.Ordinal);
        int end = header.IndexOf("// This class is responsible", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "Could not find GC_CONFIGURATION_KEYS in gcconfig.h.");

        string table = header[start..end].Replace("\\\n", " ", StringComparison.Ordinal);
        List<KeyValuePair<int, NativeConfig>> configs = new();

        foreach (string kind in new[] { "BOOL_CONFIG", "INT_CONFIG", "STRING_CONFIG" })
        {
            int index = 0;
            while ((index = IndexOfMacro(table, kind, index)) >= 0)
            {
                int open = table.IndexOf('(', index);
                List<string> arguments = SplitMacroArguments(table, open);
                configs.Add(new KeyValuePair<int, NativeConfig>(index, new NativeConfig(
                    kind,
                    arguments[0],
                    Unquote(arguments[1]),
                    Unquote(arguments[2]),
                    kind == "STRING_CONFIG" ? 0 : ParseDefault(arguments[3]))));
                index = open + 1;
            }
        }

        Assert.NotEmpty(configs);

        // Back into the order the table declares them, which is the order everything walks.
        return configs.OrderBy(config => config.Key).Select(config => config.Value).ToArray();
    }

    private static IReadOnlyList<NativeConfig> ReadNativeRefreshedConfigs()
    {
        string source = ReadResource("gcconfig.cpp");
        int start = source.IndexOf("void GCConfig::RefreshHeapHardLimitSettings()", StringComparison.Ordinal);
        int end = source.IndexOf("void GCConfig::Initialize()", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "Could not find RefreshHeapHardLimitSettings in gcconfig.cpp.");

        List<NativeConfig> configs = new();
        foreach (string line in source[start..end].Split('\n'))
        {
            int call = line.IndexOf("GetIntConfigValue(", StringComparison.Ordinal);
            if (call < 0)
            {
                continue;
            }

            List<string> arguments = SplitMacroArguments(line, line.IndexOf('(', call));
            string name = arguments[2].Trim();
            name = name.Substring(name.IndexOf("s_", StringComparison.Ordinal) + "s_".Length).TrimEnd(')');
            configs.Add(new NativeConfig("INT_CONFIG", name, Unquote(arguments[0]), Unquote(arguments[1]), 0));
        }

        Assert.NotEmpty(configs);
        return configs;
    }

    /// <summary>The offset of a macro invocation, skipping the ones that are part of a longer name.</summary>
    private static int IndexOfMacro(string table, string macro, int from)
    {
        while ((from = table.IndexOf(macro, from, StringComparison.Ordinal)) >= 0)
        {
            bool startsAName = from > 0 && (char.IsLetterOrDigit(table[from - 1]) || table[from - 1] == '_');
            int after = from + macro.Length;
            while (after < table.Length && table[after] == ' ')
            {
                after++;
            }

            // The #define of the macro itself is a declaration, not an invocation.
            bool isDefinition = table[..from].TrimEnd().EndsWith("#define", StringComparison.Ordinal);

            if (!startsAName && !isDefinition && after < table.Length && table[after] == '(')
            {
                return from;
            }

            from += macro.Length;
        }

        return -1;
    }

    /// <summary>The arguments of a macro invocation whose '(' is at <paramref name="open"/>.</summary>
    private static List<string> SplitMacroArguments(string text, int open)
    {
        List<string> arguments = new();
        System.Text.StringBuilder current = new();
        int depth = 0;
        bool inString = false;

        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                current.Append(c);
                if (c == '"' && text[i - 1] != '\\')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    current.Append(c);
                    continue;
                case '(':
                    depth++;
                    if (depth == 1)
                    {
                        continue;
                    }

                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        arguments.Add(current.ToString().Trim());
                        return arguments;
                    }

                    break;
                case ',' when depth == 1:
                    arguments.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
            }

            current.Append(c);
        }

        Assert.Fail("Unterminated macro invocation.");
        return arguments;
    }

    private static string Unquote(string argument) =>
        argument == "NULL" ? null : argument.Trim('"');

    private static long ParseDefault(string text) => text switch
    {
        "false" => 0,
        "true" => 1,
        "HEAPVERIFY_NONE" => (long)HeapVerifyFlags.HEAPVERIFY_NONE,

        // The one default that is a macro of gc.h. GCInterfaceOffsets.h pins its value, and the
        // native build asserts that entry against the header, so reading it from there ties the
        // managed default to the C++ macro rather than to a number written out twice.
        "LARGE_OBJECT_SIZE" => LargeObjectSize,
        _ => long.Parse(text, CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// The LARGE_OBJECT_SIZE of gc.h, read out of the table that pins it. This is a property
    /// rather than a cached field because the table above is read during static initialization,
    /// which runs in declaration order.
    /// </summary>
    private static long LargeObjectSize => ReadLargeObjectSize();

    private static long ReadLargeObjectSize()
    {
        foreach (string line in ReadResource("GCInterfaceOffsets.h").Split('\n'))
        {
            if (!line.StartsWith("GC_CONST(", StringComparison.Ordinal) || !line.Contains("LARGE_OBJECT_SIZE", StringComparison.Ordinal))
            {
                continue;
            }

            string[] arguments = line[(line.IndexOf('(') + 1)..line.LastIndexOf(')')].Split(',');
            return long.Parse(arguments[IntPtr.Size == 8 ? 1 : 0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        Assert.Fail("GCInterfaceOffsets.h does not pin LARGE_OBJECT_SIZE.");
        return 0;
    }

    private static string ReadResource(string name)
    {
        using Stream stream = typeof(GCConfigTests).Assembly.GetManifestResourceStream(name);
        Assert.True(stream is not null, $"The '{name}' resource is missing from the test assembly.");
        using StreamReader reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
