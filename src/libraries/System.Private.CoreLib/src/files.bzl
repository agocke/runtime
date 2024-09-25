
all_files = [
    "Internal/AssemblyAttributes.cs",
    "Internal/Console.cs",
    "Internal/Padding.cs",
    "Internal/Runtime/CompilerHelpers/ThrowHelpers.cs",
    "Internal/Runtime/InteropServices/ComponentActivator.cs",
    "Internal/Runtime/InteropServices/IsolatedComponentLoadContext.cs",
    "Microsoft/Win32/SafeHandles/CriticalHandleMinusOneIsInvalid.cs",
    "Microsoft/Win32/SafeHandles/CriticalHandleZeroOrMinusOneIsInvalid.cs",
    "Microsoft/Win32/SafeHandles/SafeHandleMinusOneIsInvalid.cs",
    "Microsoft/Win32/SafeHandles/SafeHandleZeroOrMinusOneIsInvalid.cs",
    "Microsoft/Win32/SafeHandles/SafeFileHandle.cs",
    "Microsoft/Win32/SafeHandles/SafeWaitHandle.cs",
    "System/AccessViolationException.cs",
    "System/Action.cs",
    "System/Activator.cs",
    # "System/Activator.RuntimeType.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/AggregateException.cs",
    "System/AppContext.cs",
    "System/AppContext.AnyOS.cs",
    "System/AppContextConfigHelper.cs",
    "System/AppDomain.cs",
    "System/AppDomainSetup.cs",
    "System/AppDomainUnloadedException.cs",
    "System/ApplicationException.cs",
    "System/ApplicationId.cs",
    "System/ArgumentException.cs",
    "System/ArgumentNullException.cs",
    "System/ArgumentOutOfRangeException.cs",
    "System/ArithmeticException.cs",
    "System/Array.cs",
    "System/Array.Enumerators.cs",
    "System/ArraySegment.cs",
    "System/ArrayTypeMismatchException.cs",
    "System/AssemblyLoadEventArgs.cs",
    "System/AssemblyLoadEventHandler.cs",
    "System/AsyncCallback.cs",
    "System/Attribute.cs",
    "System/AttributeTargets.cs",
    "System/AttributeUsageAttribute.cs",
    "System/BadImageFormatException.cs",
    "System/BitConverter.cs",
    "System/Boolean.cs",
    "System/Buffer.cs",
    "System/Buffers/ArrayPool.cs",
    "System/Buffers/ArrayPoolEventSource.cs",
    "System/Buffers/Binary/BinaryPrimitives.ReadBigEndian.cs",
    "System/Buffers/Binary/BinaryPrimitives.ReadLittleEndian.cs",
    "System/Buffers/Binary/BinaryPrimitives.ReverseEndianness.cs",
    "System/Buffers/Binary/BinaryPrimitives.WriteBigEndian.cs",
    "System/Buffers/Binary/BinaryPrimitives.WriteLittleEndian.cs",
    "System/Buffers/ConfigurableArrayPool.cs",
    "System/Buffers/IMemoryOwner.cs",
    "System/Buffers/IPinnable.cs",
    "System/Buffers/MemoryHandle.cs",
    "System/Buffers/MemoryManager.cs",
    "System/Buffers/OperationStatus.cs",
    "System/Buffers/StandardFormat.cs",
    "System/Buffers/Text/Base64Helper/Base64DecoderHelper.cs",
    "System/Buffers/Text/Base64Helper/Base64Helper.cs",
    "System/Buffers/Text/Base64Helper/Base64ValidatorHelper.cs",
    "System/Buffers/Text/Base64Helper/Base64EncoderHelper.cs",
    "System/Buffers/Text/Base64Encoder.cs",
    "System/Buffers/Text/Base64Decoder.cs",
    "System/Buffers/Text/Base64Url/Base64UrlDecoder.cs",
    "System/Buffers/Text/Base64Url/Base64UrlEncoder.cs",
    "System/Buffers/Text/Base64Url/Base64UrlValidator.cs",
    "System/Buffers/Text/Base64Validator.cs",
    "System/Buffers/Text/FormattingHelpers.CountDigits.cs",
    "System/Buffers/Text/FormattingHelpers.CountDigits.Int128.cs",
    "System/Buffers/Text/Utf8Constants.cs",
    "System/Buffers/Text/Utf8Formatter/FormattingHelpers.cs",
    "System/Buffers/Text/Utf8Formatter/Utf8Formatter.Boolean.cs",
    "System/Buffers/Text/Utf8Formatter/Utf8Formatter.Date.cs",
    "System/Buffers/Text/Utf8Formatter/Utf8Formatter.Decimal.cs",
    "System/Buffers/Text/Utf8Formatter/Utf8Formatter.Float.cs",
    "System/Buffers/Text/Utf8Formatter/Utf8Formatter.Guid.cs",
    "System/Buffers/Text/Utf8Formatter/Utf8Formatter.Integer.cs",
    "System/Buffers/Text/Utf8Formatter/Utf8Formatter.TimeSpan.cs",
    "System/Buffers/Text/Utf8Parser/ParserHelpers.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Boolean.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Date.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Date.Default.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Date.G.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Date.Helpers.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Date.O.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Date.R.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Decimal.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Float.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Guid.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Integer.Signed.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Integer.Signed.D.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Integer.Signed.N.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Integer.Unsigned.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Integer.Unsigned.D.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Integer.Unsigned.N.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Integer.Unsigned.X.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.Number.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.TimeSpan.BigG.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.TimeSpan.C.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.TimeSpan.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.TimeSpan.LittleG.cs",
    "System/Buffers/Text/Utf8Parser/Utf8Parser.TimeSpanSplitter.cs",
    "System/Buffers/SharedArrayPool.cs",
    "System/Buffers/Utilities.cs",
    "System/ByReference.cs",
    "System/Byte.cs",
    "System/CannotUnloadAppDomainException.cs",
    "System/Char.cs",
    "System/CharEnumerator.cs",
    "System/CLSCompliantAttribute.cs",
    "System/CodeDom/Compiler/GeneratedCodeAttribute.cs",
    "System/CodeDom/Compiler/IndentedTextWriter.cs",
    "System/Collections/ArrayList.cs",
    "System/Collections/Comparer.cs",
    "System/Collections/CompatibleComparer.cs",
    "System/Collections/Concurrent/ConcurrentQueue.cs",
    "System/Collections/Concurrent/ConcurrentQueueSegment.cs",
    "System/Collections/Concurrent/IProducerConsumerCollection.cs",
    "System/Collections/Concurrent/IProducerConsumerCollectionDebugView.cs",
    "System/Collections/DictionaryEntry.cs",
    "System/Collections/Generic/ArraySortHelper.cs",
    "System/Collections/Generic/CollectionExtensions.cs",
    "System/Collections/Generic/Comparer.cs",
    "System/Collections/Generic/Dictionary.cs",
    "System/Collections/Generic/DebugViewDictionaryItem.cs",
    "System/Collections/Generic/EqualityComparer.cs",
    "System/Collections/Generic/HashSet.cs",
    "System/Collections/Generic/HashSetEqualityComparer.cs",
    "System/Collections/Generic/IAlternateEqualityComparer.cs",
    "System/Collections/Generic/IAsyncEnumerable.cs",
    "System/Collections/Generic/IAsyncEnumerator.cs",
    "System/Collections/Generic/ICollection.cs",
    "System/Collections/Generic/ICollectionDebugView.cs",
    "System/Collections/Generic/IComparer.cs",
    "System/Collections/Generic/IDictionary.cs",
    "System/Collections/Generic/IDictionaryDebugView.cs",
    "System/Collections/Generic/IEnumerable.cs",
    "System/Collections/Generic/IEnumerator.cs",
    "System/Collections/Generic/IEqualityComparer.cs",
    "System/Collections/Generic/IInternalStringEqualityComparer.cs",
    "System/Collections/Generic/IList.cs",
    "System/Collections/Generic/InsertionBehavior.cs",
    "System/Collections/Generic/IReadOnlyCollection.cs",
    "System/Collections/Generic/IReadOnlyDictionary.cs",
    "System/Collections/Generic/IReadOnlyList.cs",
    "System/Collections/Generic/ISet.cs",
    "System/Collections/Generic/IReadOnlySet.cs",
    "System/Collections/Generic/KeyNotFoundException.cs",
    "System/Collections/Generic/KeyValuePair.cs",
    "System/Collections/Generic/List.cs",
    "System/Collections/Generic/Queue.cs",
    "System/Collections/Generic/QueueDebugView.cs",
    "System/Collections/Generic/RandomizedStringEqualityComparer.cs",
    "System/Collections/Generic/ReferenceEqualityComparer.cs",
    "System/Collections/Generic/NonRandomizedStringEqualityComparer.cs",
    "System/Collections/Generic/ValueListBuilder.cs",
    "System/Collections/HashHelpers.cs",
    "System/Collections/HashHelpers.SerializationInfoTable.cs",
    "System/Collections/Hashtable.cs",
    "System/Collections/ICollection.cs",
    "System/Collections/IComparer.cs",
    "System/Collections/IDictionary.cs",
    "System/Collections/IDictionaryEnumerator.cs",
    "System/Collections/IEnumerable.cs",
    "System/Collections/IEnumerator.cs",
    "System/Collections/IEqualityComparer.cs",
    "System/Collections/IHashCodeProvider.cs",
    "System/Collections/IList.cs",
    "System/Collections/IStructuralComparable.cs",
    "System/Collections/IStructuralEquatable.cs",
    "System/Collections/ListDictionaryInternal.cs",
    "System/Collections/ObjectModel/Collection.cs",
    "System/Collections/ObjectModel/CollectionHelpers.cs",
    "System/Collections/ObjectModel/ReadOnlyCollection.cs",
    "System/Collections/ObjectModel/ReadOnlyDictionary.cs",
    "System/ComponentModel/DefaultValueAttribute.cs",
    "System/ComponentModel/EditorBrowsableAttribute.cs",
    "System/ComponentModel/EditorBrowsableState.cs",
    "System/ComponentModel/Win32Exception.cs",
    "System/Configuration/Assemblies/AssemblyHashAlgorithm.cs",
    "System/Configuration/Assemblies/AssemblyVersionCompatibility.cs",
    "System/Context.cs",
    "System/Convert.Base64.cs",
    "System/Convert.cs",
    "System/CoreLib.cs",
    "System/CurrentSystemTimeZone.cs",
    "System/DataMisalignedException.cs",
    "System/DateOnly.cs",
    "System/DateTime.cs",
    "System/DateTimeKind.cs",
    "System/DateTimeOffset.cs",
    # "System/DateTimeOffset.NonAndroid.cs" Condition="'$(TargetsAndroid)' != 'true'",
    # "System/DateTimeOffset.Android.cs" Condition="'$(TargetsAndroid)' == 'true'",
    "System/DayOfWeek.cs",
    "System/DBNull.cs",
    "System/Decimal.cs",
    "System/Decimal.DecCalc.cs",
    "System/DefaultBinder.cs",
    "System/Delegate.cs",
    "System/Diagnostics/CodeAnalysis/ConstantExpectedAttribute.cs",
    "System/Diagnostics/CodeAnalysis/DynamicallyAccessedMemberTypes.cs",
    "System/Diagnostics/CodeAnalysis/DynamicallyAccessedMembersAttribute.cs",
    "System/Diagnostics/CodeAnalysis/DynamicDependencyAttribute.cs",
    "System/Diagnostics/CodeAnalysis/ExcludeFromCodeCoverageAttribute.cs",
    "System/Diagnostics/CodeAnalysis/ExperimentalAttribute.cs",
    "System/Diagnostics/CodeAnalysis/FeatureGuardAttribute.cs",
    "System/Diagnostics/CodeAnalysis/FeatureSwitchDefinitionAttribute.cs",
    "System/Diagnostics/CodeAnalysis/NullableAttributes.cs",
    "System/Diagnostics/CodeAnalysis/UnscopedRefAttribute.cs",
    "System/Diagnostics/CodeAnalysis/RequiresAssemblyFilesAttribute.cs",
    "System/Diagnostics/CodeAnalysis/RequiresDynamicCodeAttribute.cs",
    "System/Diagnostics/CodeAnalysis/RequiresUnreferencedCodeAttribute.cs",
    "System/Diagnostics/CodeAnalysis/SetsRequiredMembersAttribute.cs",
    "System/Diagnostics/CodeAnalysis/StringSyntaxAttribute.cs",
    "System/Diagnostics/CodeAnalysis/SuppressMessageAttribute.cs",
    "System/Diagnostics/CodeAnalysis/UnconditionalSuppressMessageAttribute.cs",
    "System/Diagnostics/ConditionalAttribute.cs",
    "System/Diagnostics/Contracts/ContractException.cs",
    "System/Diagnostics/Contracts/ContractFailedEventArgs.cs",
    "System/Diagnostics/Contracts/Contracts.cs",
    "System/Diagnostics/Debug.cs",
    "System/Diagnostics/DebuggableAttribute.cs",
    "System/Diagnostics/Debugger.cs",
    "System/Diagnostics/DebuggerBrowsableAttribute.cs",
    "System/Diagnostics/DebuggerDisableUserUnhandledExceptionsAttribute.cs",
    "System/Diagnostics/DebuggerDisplayAttribute.cs",
    "System/Diagnostics/DebuggerHiddenAttribute.cs",
    "System/Diagnostics/DebuggerNonUserCodeAttribute.cs",
    "System/Diagnostics/DebuggerStepperBoundaryAttribute.cs",
    "System/Diagnostics/DebuggerStepThroughAttribute.cs",
    "System/Diagnostics/DebuggerTypeProxyAttribute.cs",
    "System/Diagnostics/DebuggerVisualizerAttribute.cs",
    "System/Diagnostics/DebugProvider.cs",
    "System/Diagnostics/DiagnosticMethodInfo.cs",
    "System/Diagnostics/StackFrame.cs",
    # "System/Diagnostics/StackFrameExtensions.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/Diagnostics/StackTrace.cs",
    "System/Diagnostics/StackTraceHiddenAttribute.cs",
    "System/Diagnostics/Stopwatch.cs",
    "System/Diagnostics/SymbolStore/ISymbolDocumentWriter.cs",
    "System/Diagnostics/UnreachableException.cs",
    "System/DivideByZeroException.cs",
    "System/DllNotFoundException.cs",
    "System/Double.cs",
    "System/DuplicateWaitObjectException.cs",
    "System/Empty.cs",
    "System/EntryPointNotFoundException.cs",
    "System/Enum.cs",
    # "System/Enum.EnumInfo.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/Environment.cs",
    "System/Environment.SpecialFolder.cs",
    "System/Environment.SpecialFolderOption.cs",
    "System/EnvironmentVariableTarget.cs",
    "System/EventArgs.cs",
    "System/EventHandler.cs",
    "System/Exception.cs",
    "System/ExecutionEngineException.cs",
    "System/FieldAccessException.cs",
    "System/FlagsAttribute.cs",
    "System/FormatException.cs",
    "System/FormattableString.cs",
    "System/Function.cs",
    "System/GC.cs",
    "System/GCMemoryInfo.cs",
    "System/Gen2GcCallback.cs",
    "System/Globalization/Calendar.cs",
    "System/Globalization/CalendarAlgorithmType.cs",
    "System/Globalization/CalendarData.cs",
    # "System/Globalization/CalendarData.Browser.cs" Condition="'$(TargetsBrowser)' == 'true'",
    # "System/Globalization/CalendarData.iOS.cs" Condition="'$(IsiOSLike)' == 'true'",
    "System/Globalization/CalendarData.Icu.cs",
    "System/Globalization/CalendarData.Nls.cs",
    "System/Globalization/CalendarWeekRule.cs",
    "System/Globalization/CalendricalCalculationsHelper.cs",
    "System/Globalization/CharUnicodeInfo.cs",
    "System/Globalization/CharUnicodeInfoData.cs",
    "System/Globalization/ChineseLunisolarCalendar.cs",
    "System/Globalization/CompareInfo.cs",
    "System/Globalization/CompareInfo.Icu.cs",
    "System/Globalization/CompareInfo.Invariant.cs",
    "System/Globalization/CompareInfo.Nls.cs",
    "System/Globalization/CompareInfo.Utf8.cs",
    # "System/Globalization/CompareInfo.WebAssembly.cs" Condition="'$(TargetsBrowser)' == 'true'",
    # "System/Globalization/CompareInfo.iOS.cs" Condition="'$(IsiOSLike)' == 'true'",
    "System/Globalization/CompareOptions.cs",
    "System/Globalization/CultureData.cs",
    # "System/Globalization/CultureData.Browser.cs" Condition="'$(TargetsBrowser)' == 'true'",
    "System/Globalization/CultureData.Icu.cs",
    # "System/Globalization/CultureData.iOS.cs" Condition="'$(IsiOSLike)' == 'true'",
    "System/Globalization/CultureData.Nls.cs",
    "System/Globalization/CultureInfo.cs",
    "System/Globalization/CultureNotFoundException.cs",
    "System/Globalization/CultureTypes.cs",
    "System/Globalization/DateTimeFormat.cs",
    "System/Globalization/DateTimeFormatInfo.cs",
    "System/Globalization/DateTimeFormatInfoScanner.cs",
    "System/Globalization/DateTimeParse.cs",
    "System/Globalization/DateTimeStyles.cs",
    "System/Globalization/DaylightTime.cs",
    "System/Globalization/DigitShapes.cs",
    "System/Globalization/EastAsianLunisolarCalendar.cs",
    "System/Globalization/GlobalizationExtensions.cs",
    "System/Globalization/GlobalizationMode.cs",
    "System/Globalization/GregorianCalendar.cs",
    "System/Globalization/GregorianCalendarHelper.cs",
    "System/Globalization/GregorianCalendarTypes.cs",
    "System/Globalization/HebrewCalendar.cs",
    "System/Globalization/HebrewNumber.cs",
    "System/Globalization/HijriCalendar.cs",
    "System/Globalization/IcuLocaleData.cs",
    "System/Globalization/IdnMapping.cs",
    "System/Globalization/IdnMapping.Icu.cs",
    "System/Globalization/IdnMapping.Nls.cs",
    "System/Globalization/InvariantModeCasing.cs",
    "System/Globalization/ISOWeek.cs",
    "System/Globalization/JapaneseCalendar.cs",
    "System/Globalization/JapaneseCalendar.Icu.cs",
    "System/Globalization/JapaneseCalendar.Nls.cs",
    "System/Globalization/JapaneseLunisolarCalendar.cs",
    "System/Globalization/JulianCalendar.cs",
    "System/Globalization/KoreanCalendar.cs",
    "System/Globalization/KoreanLunisolarCalendar.cs",
    "System/Globalization/Normalization.cs",
    "System/Globalization/Normalization.Icu.cs",
    "System/Globalization/Normalization.Nls.cs",
    "System/Globalization/NumberFormatInfo.cs",
    "System/Globalization/NumberStyles.cs",
    "System/Globalization/Ordinal.cs",
    "System/Globalization/Ordinal.Utf8.cs",
    "System/Globalization/OrdinalCasing.Icu.cs",
    "System/Globalization/PersianCalendar.cs",
    "System/Globalization/RegionInfo.cs",
    "System/Globalization/SortKey.cs",
    "System/Globalization/SortVersion.cs",
    "System/Globalization/StringInfo.cs",
    "System/Globalization/StrongBidiCategory.cs",
    "System/Globalization/SurrogateCasing.cs",
    "System/Globalization/TaiwanCalendar.cs",
    "System/Globalization/TaiwanLunisolarCalendar.cs",
    "System/Globalization/TextElementEnumerator.cs",
    "System/Globalization/TextInfo.cs",
    "System/Globalization/TextInfo.Icu.cs",
    "System/Globalization/TextInfo.Nls.cs",
    # "System/Globalization/TextInfo.iOS.cs" Condition="'$(IsiOSLike)' == 'true'",
    # "System/Globalization/TextInfo.WebAssembly.cs" Condition="'$(TargetsBrowser)' == 'true'",
    "System/Globalization/ThaiBuddhistCalendar.cs",
    "System/Globalization/TimeSpanFormat.cs",
    "System/Globalization/TimeSpanParse.cs",
    "System/Globalization/TimeSpanStyles.cs",
    "System/Globalization/UmAlQuraCalendar.cs",
    "System/Globalization/UnicodeCategory.cs",
    "System/Guid.cs",
    "System/Half.cs",
    "System/HashCode.cs",
    "System/IAsyncDisposable.cs",
    "System/IAsyncResult.cs",
    "System/ICloneable.cs",
    "System/IComparable.cs",
    "System/IConvertible.cs",
    "System/ICustomFormatter.cs",
    "System/IDisposable.cs",
    "System/IEquatable.cs",
    "System/IFormatProvider.cs",
    "System/IFormattable.cs",
    "System/Index.cs",
    "System/SearchValues/Any1CharPackedSearchValues.cs",
    "System/SearchValues/Any1CharPackedIgnoreCaseSearchValues.cs",
    "System/SearchValues/Any2CharPackedIgnoreCaseSearchValues.cs",
    "System/SearchValues/Any3CharPackedSearchValues.cs",
    "System/SearchValues/Any2CharPackedSearchValues.cs",
    "System/SearchValues/Any1SearchValues.cs",
    "System/SearchValues/Any2SearchValues.cs",
    "System/SearchValues/Any3SearchValues.cs",
    "System/SearchValues/BitVector256.cs",
    "System/SearchValues/ProbabilisticMapState.cs",
    "System/SearchValues/ProbabilisticWithAsciiCharSearchValues.cs",
    "System/SearchValues/Any4SearchValues.cs",
    "System/SearchValues/Any5SearchValues.cs",
    "System/SearchValues/AsciiByteSearchValues.cs",
    "System/SearchValues/AsciiCharSearchValues.cs",
    "System/SearchValues/IndexOfAnyAsciiSearcher.cs",
    "System/SearchValues/AnyByteSearchValues.cs",
    "System/SearchValues/RangeByteSearchValues.cs",
    "System/SearchValues/RangeCharSearchValues.cs",
    "System/SearchValues/ProbabilisticCharSearchValues.cs",
    "System/SearchValues/BitmapCharSearchValues.cs",
    "System/SearchValues/SearchValues.cs",
    "System/SearchValues/SearchValues.T.cs",
    "System/SearchValues/SearchValuesDebugView.cs",
    "System/SearchValues/EmptySearchValues.cs",
    "System/SearchValues/ProbabilisticMap.cs",
    "System/SearchValues/Strings/Helpers/AhoCorasick.cs",
    "System/SearchValues/Strings/Helpers/AhoCorasickBuilder.cs",
    "System/SearchValues/Strings/Helpers/AhoCorasickNode.cs",
    "System/SearchValues/Strings/Helpers/CharacterFrequencyHelper.cs",
    "System/SearchValues/Strings/Helpers/RabinKarp.cs",
    "System/SearchValues/Strings/Helpers/StringSearchValuesHelper.cs",
    "System/SearchValues/Strings/Helpers/TeddyBucketizer.cs",
    "System/SearchValues/Strings/Helpers/TeddyHelper.cs",
    "System/SearchValues/Strings/AsciiStringSearchValuesTeddyBucketizedN2.cs",
    "System/SearchValues/Strings/AsciiStringSearchValuesTeddyBucketizedN3.cs",
    "System/SearchValues/Strings/AsciiStringSearchValuesTeddyNonBucketizedN2.cs",
    "System/SearchValues/Strings/AsciiStringSearchValuesTeddyNonBucketizedN3.cs",
    "System/SearchValues/Strings/AsciiStringSearchValuesTeddyBase.cs",
    "System/SearchValues/Strings/MultiStringIgnoreCaseSearchValuesFallback.cs",
    "System/SearchValues/Strings/SingleStringSearchValuesThreeChars.cs",
    "System/SearchValues/Strings/SingleStringSearchValuesFallback.cs",
    "System/SearchValues/Strings/StringSearchValues.cs",
    "System/SearchValues/Strings/StringSearchValuesBase.cs",
    "System/SearchValues/Strings/StringSearchValuesAhoCorasick.cs",
    "System/SearchValues/Strings/StringSearchValuesRabinKarp.cs",
    "System/IndexOutOfRangeException.cs",
    "System/InlineArrays.cs",
    "System/InsufficientExecutionStackException.cs",
    "System/InsufficientMemoryException.cs",
    "System/Int16.cs",
    "System/Int32.cs",
    "System/Int64.cs",
    "System/Int128.cs",
    "System/IntPtr.cs",
    "System/InvalidCastException.cs",
    "System/InvalidOperationException.cs",
    "System/InvalidProgramException.cs",
    "System/InvalidTimeZoneException.cs",
    "System/IO/BinaryReader.cs",
    "System/IO/BinaryWriter.cs",
    "System/IO/BufferedStream.cs",
    "System/IO/Directory.cs",
    "System/IO/DirectoryInfo.cs",
    "System/IO/DirectoryNotFoundException.cs",
    "System/IO/EncodingCache.cs",
    "System/IO/EnumerationOptions.cs",
    "System/IO/EndOfStreamException.cs",
    "System/IO/File.cs",
    "System/IO/FileAccess.cs",
    "System/IO/FileAttributes.cs",
    "System/IO/FileInfo.cs",
    "System/IO/FileLoadException.cs",
    "System/IO/FileMode.cs",
    "System/IO/FileNotFoundException.cs",
    "System/IO/FileOptions.cs",
    "System/IO/FileShare.cs",
    "System/IO/FileStream.cs",
    "System/IO/FileStreamOptions.cs",
    "System/IO/FileSystem.cs",
    "System/IO/FileSystemInfo.cs",
    "System/IO/HandleInheritability.cs",
    "System/IO/InvalidDataException.cs",
    "System/IO/IOException.cs",
    "System/IO/Iterator.cs",
    "System/IO/MatchCasing.cs",
    "System/IO/MatchType.cs",
    "System/IO/MemoryStream.cs",
    "System/IO/Path.cs",
    "System/IO/PathTooLongException.cs",
    "System/IO/PinnedBufferMemoryStream.cs",
    "System/IO/RandomAccess.cs",
    "System/IO/ReadLinesIterator.cs",
    "System/IO/SearchOption.cs",
    "System/IO/SearchTarget.cs",
    "System/IO/SeekOrigin.cs",
    "System/IO/Stream.cs",
    "System/IO/StreamReader.cs",
    "System/IO/StreamWriter.cs",
    "System/IO/StringReader.cs",
    "System/IO/StringWriter.cs",
    "System/IO/TextReader.cs",
    "System/IO/TextWriter.cs",
    "System/IO/TextWriter.CreateBroadcasting.cs",
    "System/IO/UnixFileMode.cs",
    "System/IO/UnmanagedMemoryAccessor.cs",
    "System/IO/UnmanagedMemoryStream.cs",
    "System/IO/UnmanagedMemoryStreamWrapper.cs",
    "System/IO/Enumeration/FileSystemEntry.cs",
    "System/IO/Enumeration/FileSystemEnumerator.cs",
    "System/IO/Enumeration/FileSystemEnumerable.cs",
    "System/IO/Enumeration/FileSystemEnumerableFactory.cs",
    "System/IO/Enumeration/FileSystemName.cs",
    "System/IO/Strategies/BufferedFileStreamStrategy.cs",
    "System/IO/Strategies/DerivedFileStreamStrategy.cs",
    "System/IO/Strategies/FileStreamHelpers.cs",
    "System/IO/Strategies/FileStreamStrategy.cs",
    "System/IO/Strategies/OSFileStreamStrategy.cs",
    "System/IObservable.cs",
    "System/IObserver.cs",
    "System/IProgress.cs",
    "System/ISpanFormattable.cs",
    "System/IUtfChar.cs",
    "System/IUtf8SpanFormattable.cs",
    "System/IUtf8SpanParsable.cs",
    "System/Lazy.cs",
    "System/LazyOfTTMetadata.cs",
    "System/LoaderOptimization.cs",
    "System/LoaderOptimizationAttribute.cs",
    "System/LocalAppContextSwitches.cs",
    "System/LocalDataStoreSlot.cs",
    "System/MarshalByRefObject.cs",
    "System/Marvin.cs",
    "System/Marvin.OrdinalIgnoreCase.cs",
    "System/Math.cs",
    "System/MathF.cs",
    "System/MemberAccessException.cs",
    "System/Memory.cs",
    "System/MemoryDebugView.cs",
    "System/MemoryExtensions.cs",
    "System/MemoryExtensions.Globalization.cs",
    "System/MemoryExtensions.Globalization.Utf8.cs",
    "System/MemoryExtensions.Trim.cs",
    "System/MemoryExtensions.Trim.Utf8.cs",
    "System/MethodAccessException.cs",
    "System/MidpointRounding.cs",
    "System/MissingFieldException.cs",
    "System/MissingMemberException.cs",
    "System/MissingMethodException.cs",
    "System/MulticastDelegate.cs",
    "System/MulticastNotSupportedException.cs",
    "System/Net/WebUtility.cs",
    "System/NonSerializedAttribute.cs",
    "System/NotFiniteNumberException.cs",
    "System/NotImplementedException.cs",
    "System/NotSupportedException.cs",
    "System/Nullable.cs",
    "System/NullReferenceException.cs",
    "System/Number.BigInteger.cs",
    "System/Number.DiyFp.cs",
    "System/Number.Dragon4.cs",
    "System/Number.Formatting.cs",
    "System/Number.Grisu3.cs",
    "System/Number.NumberToFloatingPointBits.cs",
    "System/Number.Parsing.cs",
    "System/Numerics/BitOperations.cs",
    "System/Numerics/Matrix3x2.cs",
    "System/Numerics/Matrix3x2.Impl.cs",
    "System/Numerics/Matrix4x4.cs",
    "System/Numerics/Matrix4x4.Impl.cs",
    "System/Numerics/Plane.cs",
    "System/Numerics/Plane.Extensions.cs",
    "System/Numerics/Quaternion.cs",
    "System/Numerics/Quaternion.Extensions.cs",
    "System/Numerics/TotalOrderIeee754Comparer.cs",
    "System/Numerics/Vector.cs",
    "System/Numerics/Vector_1.cs",
    "System/Numerics/Vector2.cs",
    "System/Numerics/Vector2.Extensions.cs",
    "System/Numerics/Vector3.cs",
    "System/Numerics/Vector3.Extensions.cs",
    "System/Numerics/Vector4.cs",
    "System/Numerics/Vector4.Extensions.cs",
    "System/Numerics/VectorDebugView_1.cs",
    "System/Object.cs",
    "System/ObjectDisposedException.cs",
    "System/ObsoleteAttribute.cs",
    "System/OperatingSystem.cs",
    "System/OperationCanceledException.cs",
    "System/OutOfMemoryException.cs",
    "System/OverflowException.cs",
    "System/ParamArrayAttribute.cs",
    "System/ParseNumbers.cs",
    "System/PasteArguments.cs",
    "System/PlatformID.cs",
    "System/PlatformNotSupportedException.cs",
    "System/Progress.cs",
    "System/Random.cs",
    "System/Random.ImplBase.cs",
    "System/Random.Net5CompatImpl.cs",
    # "nclude="$(MSBuildThisFileDirectory)System/Random.Xoshiro128StarStarImpl.cs",
    # "nclude="$(MSBuildThisFileDirectory)System/Random.Xoshiro256StarStarImpl.cs",
    "System/Range.cs",
    "System/RankException.cs",
    "System/ReadOnlyMemory.cs",
    "System/ReadOnlySpan.cs",
    "System/Reflection/AmbiguousMatchException.cs",
    "System/Reflection/Assembly.cs",
    "System/Reflection/AssemblyAlgorithmIdAttribute.cs",
    "System/Reflection/AssemblyCompanyAttribute.cs",
    "System/Reflection/AssemblyConfigurationAttribute.cs",
    "System/Reflection/AssemblyContentType.cs",
    "System/Reflection/AssemblyCopyrightAttribute.cs",
    "System/Reflection/AssemblyCultureAttribute.cs",
    "System/Reflection/AssemblyDefaultAliasAttribute.cs",
    "System/Reflection/AssemblyDelaySignAttribute.cs",
    "System/Reflection/AssemblyDescriptionAttribute.cs",
    "System/Reflection/AssemblyFileVersionAttribute.cs",
    "System/Reflection/AssemblyFlagsAttribute.cs",
    "System/Reflection/AssemblyInformationalVersionAttribute.cs",
    "System/Reflection/AssemblyKeyFileAttribute.cs",
    "System/Reflection/AssemblyKeyNameAttribute.cs",
    "System/Reflection/AssemblyMetadataAttribute.cs",
    "System/Reflection/AssemblyName.cs",
    "System/Reflection/AssemblyNameHelpers.StrongName.cs",
    "System/Reflection/AssemblyNameFlags.cs",
    "System/Reflection/AssemblyNameProxy.cs",
    "System/Reflection/AssemblyProductAttribute.cs",
    "System/Reflection/AssemblySignatureKeyAttribute.cs",
    "System/Reflection/AssemblyTitleAttribute.cs",
    "System/Reflection/AssemblyTrademarkAttribute.cs",
    "System/Reflection/AssemblyVersionAttribute.cs",
    "System/Reflection/Binder.cs",
    "System/Reflection/BindingFlags.cs",
    "System/Reflection/CallingConventions.cs",
    "System/Reflection/ConstructorInfo.cs",
    # "System/Reflection/ConstructorInvoker.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/Reflection/CorElementType.cs",
    "System/Reflection/CustomAttributeData.cs",
    "System/Reflection/CustomAttributeExtensions.cs",
    "System/Reflection/CustomAttributeFormatException.cs",
    "System/Reflection/CustomAttributeNamedArgument.cs",
    "System/Reflection/CustomAttributeTypedArgument.cs",
    "System/Reflection/DefaultMemberAttribute.cs",
    "System/Reflection/Emit/AssemblyBuilder.cs",
    "System/Reflection/Emit/AssemblyBuilderAccess.cs",
    "System/Reflection/Emit/ConstructorBuilder.cs",
    "System/Reflection/Emit/ConstructorOnTypeBuilderInstantiation.cs",
    # "System/Reflection/Emit/DynamicMethod.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/Reflection/Emit/EmptyCAHolder.cs",
    "System/Reflection/Emit/EnumBuilder.cs",
    "System/Reflection/Emit/EventBuilder.cs",
    "System/Reflection/Emit/FieldBuilder.cs",
    "System/Reflection/Emit/FieldOnTypeBuilderInstantiation.cs",
    "System/Reflection/Emit/FlowControl.cs",
    "System/Reflection/Emit/GenericTypeParameterBuilder.cs",
    "System/Reflection/Emit/ILGenerator.cs",
    "System/Reflection/Emit/Label.cs",
    "System/Reflection/Emit/LocalBuilder.cs",
    "System/Reflection/Emit/MethodBuilder.cs",
    "System/Reflection/Emit/MethodBuilderInstantiation.cs",
    "System/Reflection/Emit/MethodOnTypeBuilderInstantiation.cs",
    "System/Reflection/Emit/ModuleBuilder.cs",
    "System/Reflection/Emit/Opcode.cs",
    "System/Reflection/Emit/OpCodes.cs",
    "System/Reflection/Emit/OpCodeType.cs",
    "System/Reflection/Emit/OperandType.cs",
    "System/Reflection/Emit/PackingSize.cs",
    "System/Reflection/Emit/ParameterBuilder.cs",
    "System/Reflection/Emit/PEFileKinds.cs",
    "System/Reflection/Emit/PropertyBuilder.cs",
    "System/Reflection/Emit/StackBehaviour.cs",
    "System/Reflection/Emit/SymbolType.cs",
    "System/Reflection/Emit/TypeBuilder.cs",
    "System/Reflection/Emit/TypeBuilderInstantiation.cs",
    "System/Reflection/Emit/TypeNameBuilder.cs",
    "System/Reflection/EventAttributes.cs",
    "System/Reflection/EventInfo.cs",
    "System/Reflection/ExceptionHandlingClause.cs",
    "System/Reflection/ExceptionHandlingClauseOptions.cs",
    # "System/Reflection/FieldAccessor.cs" Condition="'$(FeatureNativeAot)' != 'true' and '$(FeatureMono)' != 'true'",
    "System/Reflection/FieldAttributes.cs",
    "System/Reflection/FieldInfo.cs",
    "System/Reflection/GenericParameterAttributes.cs",
    "System/Reflection/ICustomAttributeProvider.cs",
    "System/Reflection/ImageFileMachine.cs",
    "System/Reflection/InterfaceMapping.cs",
    "System/Reflection/IntrospectionExtensions.cs",
    "System/Reflection/InvalidFilterCriteriaException.cs",
    "System/Reflection/InvocationFlags.cs",
    # "System/Reflection/InvokerEmitUtil.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    # "System/Reflection/InvokeUtils.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/Reflection/IReflect.cs",
    "System/Reflection/IReflectableType.cs",
    "System/Reflection/LocalVariableInfo.cs",
    "System/Reflection/ManifestResourceInfo.cs",
    "System/Reflection/MemberFilter.cs",
    "System/Reflection/MemberInfo.cs",
    "System/Reflection/MemberTypes.cs",
    "System/Reflection/MethodAttributes.cs",
    "System/Reflection/MethodBase.cs",
    # "System/Reflection/MethodBaseInvoker.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    # "System/Reflection/MethodBaseInvoker.Constructor.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/Reflection/MethodBody.cs",
    "System/Reflection/MethodImplAttributes.cs",
    "System/Reflection/MethodInfo.cs",
    "System/Reflection/MethodInfo.Internal.cs",
    # "System/Reflection/MethodInvoker.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    # "System/Reflection/MethodInvokerCommon.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/Reflection/Missing.cs",
    "System/Reflection/ModifiedHasElementType.cs",
    "System/Reflection/ModifiedType.cs",
    "System/Reflection/ModifiedFunctionPointerType.cs",
    "System/Reflection/ModifiedGenericType.cs",
    "System/Reflection/Module.cs",
    "System/Reflection/ModuleResolveEventHandler.cs",
    "System/Reflection/NullabilityInfo.cs",
    "System/Reflection/NullabilityInfoContext.cs",
    "System/Reflection/ObfuscateAssemblyAttribute.cs",
    "System/Reflection/ObfuscationAttribute.cs",
    "System/Reflection/ParameterAttributes.cs",
    "System/Reflection/ParameterInfo.cs",
    "System/Reflection/ParameterModifier.cs",
    "System/Reflection/Pointer.cs",
    "System/Reflection/PortableExecutableKinds.cs",
    "System/Reflection/ProcessorArchitecture.cs",
    "System/Reflection/PropertyAttributes.cs",
    "System/Reflection/PropertyInfo.cs",
    "System/Reflection/ReflectionContext.cs",
    "System/Reflection/ReflectionTypeLoadException.cs",
    "System/Reflection/ResourceAttributes.cs",
    "System/Reflection/ResourceLocation.cs",
    # "System/Reflection/RuntimeConstructorInfo.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/Reflection/RuntimeMethodBody.cs",
    # "System/Reflection/RuntimeMethodInfo.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/Reflection/RuntimeReflectionExtensions.cs",
    "System/Reflection/SignatureArrayType.cs",
    "System/Reflection/SignatureByRefType.cs",
    "System/Reflection/SignatureCallingConvention.cs",
    "System/Reflection/SignatureConstructedGenericType.cs",
    "System/Reflection/SignatureGenericMethodParameterType.cs",
    "System/Reflection/SignatureGenericParameterType.cs",
    "System/Reflection/SignatureHasElementType.cs",
    "System/Reflection/SignaturePointerType.cs",
    "System/Reflection/SignatureType.cs",
    "System/Reflection/SignatureTypeExtensions.cs",
    "System/Reflection/StrongNameKeyPair.cs",
    "System/Reflection/TargetException.cs",
    "System/Reflection/TargetInvocationException.cs",
    "System/Reflection/TargetParameterCountException.cs",
    "System/Reflection/TypeAttributes.cs",
    "System/Reflection/TypeDelegator.cs",
    "System/Reflection/TypeFilter.cs",
    "System/Reflection/TypeInfo.cs",
    "System/Reflection/TypeNameResolver.cs",
    "System/Reflection/Metadata/MetadataUpdateHandlerAttribute.cs",
    "System/ResolveEventArgs.cs",
    "System/ResolveEventHandler.cs",
    "System/Resources/FastResourceComparer.cs",
    "System/Resources/FileBasedResourceGroveler.cs",
    "System/Resources/IResourceGroveler.cs",
    "System/Resources/IResourceReader.cs",
    "System/Resources/ManifestBasedResourceGroveler.cs",
    "System/Resources/MissingManifestResourceException.cs",
    "System/Resources/MissingSatelliteAssemblyException.cs",
    "System/Resources/NeutralResourcesLanguageAttribute.cs",
    "System/Resources/ResourceFallbackManager.cs",
    "System/Resources/ResourceManager.cs",
    "System/Resources/ResourceReader.Core.cs",
    "System/Resources/ResourceReader.cs",
    "System/Resources/ResourceSet.cs",
    "System/Resources/ResourceTypeCode.cs",
    "System/Resources/RuntimeResourceSet.cs",
    "System/Resources/SatelliteContractVersionAttribute.cs",
    "System/Resources/UltimateResourceFallbackLocation.cs",
    "System/Runtime/AmbiguousImplementationException.cs",
    "System/Runtime/CompilerServices/AccessedThroughPropertyAttribute.cs",
    "System/Runtime/CompilerServices/AsyncIteratorMethodBuilder.cs",
    "System/Runtime/CompilerServices/AsyncIteratorStateMachineAttribute.cs",
    "System/Runtime/CompilerServices/AsyncMethodBuilderAttribute.cs",
    "System/Runtime/CompilerServices/AsyncMethodBuilderCore.cs",
    "System/Runtime/CompilerServices/AsyncStateMachineAttribute.cs",
    "System/Runtime/CompilerServices/AsyncTaskMethodBuilder.cs",
    "System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs",
    "System/Runtime/CompilerServices/AsyncValueTaskMethodBuilder.cs",
    "System/Runtime/CompilerServices/AsyncValueTaskMethodBuilderT.cs",
    "System/Runtime/CompilerServices/AsyncVoidMethodBuilder.cs",
    # "System/Runtime/CompilerServices/CastCache.cs" Condition="'$(FeatureMono)' != 'true'",
    # "System/Runtime/CompilerServices/GenericCache.cs" Condition="'$(FeatureMono)' != 'true'",
    "System/Runtime/CompilerServices/CallerArgumentExpressionAttribute.cs",
    "System/Runtime/CompilerServices/CallerFilePathAttribute.cs",
    "System/Runtime/CompilerServices/CallerLineNumberAttribute.cs",
    "System/Runtime/CompilerServices/CallerMemberNameAttribute.cs",
    "System/Runtime/CompilerServices/CallingConventions.cs",
    "System/Runtime/CompilerServices/CollectionBuilderAttribute.cs",
    "System/Runtime/CompilerServices/CompExactlyDependsOnAttribute.cs",
    "System/Runtime/CompilerServices/CompilationRelaxations.cs",
    "System/Runtime/CompilerServices/CompilationRelaxationsAttribute.cs",
    "System/Runtime/CompilerServices/CompilerFeatureRequiredAttribute.cs",
    "System/Runtime/CompilerServices/CompilerGeneratedAttribute.cs",
    "System/Runtime/CompilerServices/CompilerGlobalScopeAttribute.cs",
    "System/Runtime/CompilerServices/ConditionalWeakTable.cs",
    "System/Runtime/CompilerServices/ConfiguredAsyncDisposable.cs",
    "System/Runtime/CompilerServices/ConfiguredCancelableAsyncEnumerable.cs",
    "System/Runtime/CompilerServices/ConfiguredValueTaskAwaitable.cs",
    "System/Runtime/CompilerServices/ContractHelper.cs",
    "System/Runtime/CompilerServices/CreateNewOnMetadataUpdateAttribute.cs",
    "System/Runtime/CompilerServices/CustomConstantAttribute.cs",
    "System/Runtime/CompilerServices/DateTimeConstantAttribute.cs",
    "System/Runtime/CompilerServices/DecimalConstantAttribute.cs",
    "System/Runtime/CompilerServices/DefaultDependencyAttribute.cs",
    "System/Runtime/CompilerServices/DependencyAttribute.cs",
    "System/Runtime/CompilerServices/DisablePrivateReflectionAttribute.cs",
    "System/Runtime/CompilerServices/DisableRuntimeMarshallingAttribute.cs",
    "System/Runtime/CompilerServices/DiscardableAttribute.cs",
    "System/Runtime/CompilerServices/EnumeratorCancellationAttribute.cs",
    "System/Runtime/CompilerServices/ExtensionAttribute.cs",
    "System/Runtime/CompilerServices/FixedAddressValueTypeAttribute.cs",
    "System/Runtime/CompilerServices/FixedBufferAttribute.cs",
    "System/Runtime/CompilerServices/FormattableStringFactory.cs",
    "System/Runtime/CompilerServices/IAsyncStateMachine.cs",
    "System/Runtime/CompilerServices/IAsyncStateMachineBox.cs",
    "System/Runtime/CompilerServices/IndexerNameAttribute.cs",
    "System/Runtime/CompilerServices/INotifyCompletion.cs",
    "System/Runtime/CompilerServices/InternalsVisibleToAttribute.cs",
    "System/Runtime/CompilerServices/IntrinsicAttribute.cs",
    "System/Runtime/CompilerServices/IsByRefLikeAttribute.cs",
    "System/Runtime/CompilerServices/InlineArrayAttribute.cs",
    "System/Runtime/CompilerServices/IsConst.cs",
    "System/Runtime/CompilerServices/IsExternalInit.cs",
    "System/Runtime/CompilerServices/IsReadOnlyAttribute.cs",
    "System/Runtime/CompilerServices/IsVolatile.cs",
    "System/Runtime/CompilerServices/InterpolatedStringHandlerAttribute.cs",
    "System/Runtime/CompilerServices/InterpolatedStringHandlerArgumentAttribute.cs",
    "System/Runtime/CompilerServices/DefaultInterpolatedStringHandler.cs",
    "System/Runtime/CompilerServices/IsUnmanagedAttribute.cs",
    "System/Runtime/CompilerServices/IteratorStateMachineAttribute.cs",
    "System/Runtime/CompilerServices/ITuple.cs",
    "System/Runtime/CompilerServices/LoadHint.cs",
    "System/Runtime/CompilerServices/MethodCodeType.cs",
    "System/Runtime/CompilerServices/MethodImplAttribute.cs",
    "System/Runtime/CompilerServices/MethodImplOptions.cs",
    "System/Runtime/CompilerServices/ModuleInitializerAttribute.cs",
    "System/Runtime/CompilerServices/MetadataUpdateOriginalTypeAttribute.cs",
    "System/Runtime/CompilerServices/NullableAttribute.cs",
    "System/Runtime/CompilerServices/NullableContextAttribute.cs",
    "System/Runtime/CompilerServices/NullablePublicOnlyAttribute.cs",
    "System/Runtime/CompilerServices/ReferenceAssemblyAttribute.cs",
    "System/Runtime/CompilerServices/ParamCollectionAttribute.cs",
    "System/Runtime/CompilerServices/PoolingAsyncValueTaskMethodBuilder.cs",
    "System/Runtime/CompilerServices/PoolingAsyncValueTaskMethodBuilderT.cs",
    "System/Runtime/CompilerServices/PreserveBaseOverridesAttribute.cs",
    "System/Runtime/CompilerServices/OverloadResolutionPriorityAttribute.cs",
    "System/Runtime/CompilerServices/RefSafetyRulesAttribute.cs",
    "System/Runtime/CompilerServices/RequiredMemberAttribute.cs",
    "System/Runtime/CompilerServices/RequiresLocationAttribute.cs",
    "System/Runtime/CompilerServices/RuntimeCompatibilityAttribute.cs",
    "System/Runtime/CompilerServices/RuntimeFeature.cs",
    # "System/Runtime/CompilerServices/RuntimeFeature.NonNativeAot.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/Runtime/CompilerServices/RuntimeHelpers.cs",
    "System/Runtime/CompilerServices/RuntimeWrappedException.cs",
    "System/Runtime/CompilerServices/ScopedRefAttribute.cs",
    "System/Runtime/CompilerServices/SkipLocalsInitAttribute.cs",
    "System/Runtime/CompilerServices/SpecialNameAttribute.cs",
    # "System/Runtime/CompilerServices/StackAllocatedBox.cs" Condition="'$(FeatureMono)' != 'true'",
    "System/Runtime/CompilerServices/StateMachineAttribute.cs",
    "System/Runtime/CompilerServices/StringFreezingAttribute.cs",
    "System/Runtime/CompilerServices/StrongBox.cs",
    "System/Runtime/CompilerServices/SuppressIldasmAttribute.cs",
    "System/Runtime/CompilerServices/SwitchExpressionException.cs",
    "System/Runtime/CompilerServices/TaskAwaiter.cs",
    "System/Runtime/CompilerServices/TupleElementNamesAttribute.cs",
    "System/Runtime/CompilerServices/TypeForwardedFromAttribute.cs",
    "System/Runtime/CompilerServices/TypeForwardedToAttribute.cs",
    "System/Runtime/CompilerServices/Unsafe.cs",
    "System/Runtime/CompilerServices/UnsafeAccessorAttribute.cs",
    "System/Runtime/CompilerServices/UnsafeValueTypeAttribute.cs",
    "System/Runtime/CompilerServices/ValueTaskAwaiter.cs",
    "System/Runtime/CompilerServices/YieldAwaitable.cs",
    "System/Runtime/ConstrainedExecution/Cer.cs",
    "System/Runtime/ConstrainedExecution/Consistency.cs",
    "System/Runtime/ConstrainedExecution/CriticalFinalizerObject.cs",
    "System/Runtime/ConstrainedExecution/PrePrepareMethodAttribute.cs",
    "System/Runtime/ConstrainedExecution/ReliabilityContractAttribute.cs",
    "System/Runtime/ExceptionServices/ExceptionDispatchInfo.cs",
    "System/Runtime/ExceptionServices/FirstChanceExceptionEventArgs.cs",
    "System/Runtime/ExceptionServices/HandleProcessCorruptedStateExceptionsAttribute.cs",
    # "System/Runtime/GCFrameRegistration.cs" Condition="'$(FeatureMono)' != 'true'",
    "System/Runtime/GCSettings.cs",
    "System/Runtime/InteropServices/AllowReversePInvokeCallsAttribute.cs",
    "System/Runtime/InteropServices/Architecture.cs",
    "System/Runtime/InteropServices/ArrayWithOffset.cs",
    "System/Runtime/InteropServices/BestFitMappingAttribute.cs",
    "System/Runtime/InteropServices/BStrWrapper.cs",
    "System/Runtime/InteropServices/CallingConvention.cs",
    "System/Runtime/InteropServices/CharSet.cs",
    "System/Runtime/InteropServices/ClassInterfaceAttribute.cs",
    "System/Runtime/InteropServices/ClassInterfaceType.cs",
    "System/Runtime/InteropServices/CLong.cs",
    "System/Runtime/InteropServices/CoClassAttribute.cs",
    "System/Runtime/InteropServices/CollectionsMarshal.cs",
    "System/Runtime/InteropServices/ComDefaultInterfaceAttribute.cs",
    "System/Runtime/InteropServices/ComEventInterfaceAttribute.cs",
    "System/Runtime/InteropServices/COMException.cs",
    "System/Runtime/InteropServices/ComImportAttribute.cs",
    "System/Runtime/InteropServices/ComInterfaceType.cs",
    "System/Runtime/InteropServices/ComMemberType.cs",
    "System/Runtime/InteropServices/ComSourceInterfacesAttribute.cs",
    "System/Runtime/InteropServices/ComTypes/IBindCtx.cs",
    "System/Runtime/InteropServices/ComTypes/IConnectionPoint.cs",
    "System/Runtime/InteropServices/ComTypes/IConnectionPointContainer.cs",
    "System/Runtime/InteropServices/ComTypes/IEnumConnectionPoints.cs",
    "System/Runtime/InteropServices/ComTypes/IEnumConnections.cs",
    "System/Runtime/InteropServices/ComTypes/IEnumMoniker.cs",
    "System/Runtime/InteropServices/ComTypes/IEnumString.cs",
    "System/Runtime/InteropServices/ComTypes/IEnumVARIANT.cs",
    "System/Runtime/InteropServices/ComTypes/IMoniker.cs",
    "System/Runtime/InteropServices/ComTypes/IPersistFile.cs",
    "System/Runtime/InteropServices/ComTypes/IRunningObjectTable.cs",
    "System/Runtime/InteropServices/ComTypes/IStream.cs",
    "System/Runtime/InteropServices/ComTypes/ITypeComp.cs",
    "System/Runtime/InteropServices/ComTypes/ITypeInfo.cs",
    "System/Runtime/InteropServices/ComTypes/ITypeInfo2.cs",
    "System/Runtime/InteropServices/ComTypes/ITypeLib.cs",
    "System/Runtime/InteropServices/ComTypes/ITypeLib2.cs",
    "System/Runtime/InteropServices/ComVisibleAttribute.cs",
    "System/Runtime/InteropServices/ComWrappers.cs",
    "System/Runtime/InteropServices/CriticalHandle.cs",
    "System/Runtime/InteropServices/CULong.cs",
    "System/Runtime/InteropServices/CurrencyWrapper.cs",
    "System/Runtime/InteropServices/CustomQueryInterfaceMode.cs",
    "System/Runtime/InteropServices/CustomQueryInterfaceResult.cs",
    "System/Runtime/InteropServices/DefaultCharSetAttribute.cs",
    "System/Runtime/InteropServices/DefaultDllImportSearchPathsAttribute.cs",
    "System/Runtime/InteropServices/DefaultParameterValueAttribute.cs",
    "System/Runtime/InteropServices/DispatchWrapper.cs",
    "System/Runtime/InteropServices/DispIdAttribute.cs",
    "System/Runtime/InteropServices/DllImportAttribute.cs",
    "System/Runtime/InteropServices/DllImportSearchPath.cs",
    "System/Runtime/InteropServices/ErrorWrapper.cs",
    "System/Runtime/InteropServices/ExternalException.cs",
    "System/Runtime/InteropServices/FieldOffsetAttribute.cs",
    "System/Runtime/InteropServices/GCHandle.cs",
    "System/Runtime/InteropServices/GCHandleType.cs",
    "System/Runtime/InteropServices/GuidAttribute.cs",
    "System/Runtime/InteropServices/HandleRef.cs",
    "System/Runtime/InteropServices/ICustomAdapter.cs",
    "System/Runtime/InteropServices/ICustomFactory.cs",
    "System/Runtime/InteropServices/ICustomMarshaler.cs",
    "System/Runtime/InteropServices/ICustomQueryInterface.cs",
    "System/Runtime/InteropServices/IDynamicInterfaceCastable.cs",
    "System/Runtime/InteropServices/InAttribute.cs",
    "System/Runtime/InteropServices/InterfaceTypeAttribute.cs",
    "System/Runtime/InteropServices/InvalidComObjectException.cs",
    "System/Runtime/InteropServices/InvalidOleVariantTypeException.cs",
    "System/Runtime/InteropServices/LayoutKind.cs",
    "System/Runtime/InteropServices/LCIDConversionAttribute.cs",
    "System/Runtime/InteropServices/LibraryImportAttribute.cs",
    "System/Runtime/InteropServices/Marshal.cs",
    "System/Runtime/InteropServices/MarshalAsAttribute.cs",
    "System/Runtime/InteropServices/MarshalDirectiveException.cs",
    "System/Runtime/InteropServices/Marshalling/AnsiStringMarshaller.cs",
    "System/Runtime/InteropServices/Marshalling/ArrayMarshaller.cs",
    "System/Runtime/InteropServices/Marshalling/BStrStringMarshaller.cs",
    "System/Runtime/InteropServices/Marshalling/ComVariant.cs",
    "System/Runtime/InteropServices/Marshalling/ContiguousCollectionMarshallerAttribute.cs",
    "System/Runtime/InteropServices/Marshalling/CustomMarshallerAttribute.cs",
    "System/Runtime/InteropServices/Marshalling/MarshalMode.cs",
    "System/Runtime/InteropServices/Marshalling/MarshalUsingAttribute.cs",
    "System/Runtime/InteropServices/Marshalling/NativeMarshallingAttribute.cs",
    "System/Runtime/InteropServices/Marshalling/PointerArrayMarshaller.cs",
    "System/Runtime/InteropServices/Marshalling/ReadOnlySpanMarshaller.cs",
    "System/Runtime/InteropServices/Marshalling/SafeHandleMarshaller.cs",
    "System/Runtime/InteropServices/Marshalling/SpanMarshaller.cs",
    "System/Runtime/InteropServices/Marshalling/Utf16StringMarshaller.cs",
    "System/Runtime/InteropServices/Marshalling/Utf8StringMarshaller.cs",
    "System/Runtime/InteropServices/MemoryMarshal.cs",
    "System/Runtime/InteropServices/NativeLibrary.cs",
    "System/Runtime/InteropServices/NativeMemory.cs",
    "System/Runtime/InteropServices/NFloat.cs",
    "System/Runtime/InteropServices/OptionalAttribute.cs",
    "System/Runtime/InteropServices/OSPlatform.cs",
    "System/Runtime/InteropServices/OutAttribute.cs",
    "System/Runtime/InteropServices/PosixSignal.cs",
    "System/Runtime/InteropServices/PosixSignalContext.cs",
    "System/Runtime/InteropServices/PosixSignalRegistration.cs",
    "System/Runtime/InteropServices/PreserveSigAttribute.cs",
    "System/Runtime/InteropServices/ProgIdAttribute.cs",
    "System/Runtime/InteropServices/RuntimeInformation.cs",
    "System/Runtime/InteropServices/SafeArrayRankMismatchException.cs",
    "System/Runtime/InteropServices/SafeArrayTypeMismatchException.cs",
    "System/Runtime/InteropServices/SafeBuffer.cs",
    "System/Runtime/InteropServices/SafeHandle.cs",
    "System/Runtime/InteropServices/SEHException.cs",
    "System/Runtime/InteropServices/StringMarshalling.cs",
    "System/Runtime/InteropServices/StructLayoutAttribute.cs",
    "System/Runtime/InteropServices/SuppressGCTransitionAttribute.cs",
    "System/Runtime/InteropServices/Swift/SwiftTypes.cs",
    "System/Runtime/InteropServices/TypeIdentifierAttribute.cs",
    "System/Runtime/InteropServices/UnknownWrapper.cs",
    "System/Runtime/InteropServices/UnmanagedCallConvAttribute.cs",
    "System/Runtime/InteropServices/UnmanagedCallersOnlyAttribute.cs",
    "System/Runtime/InteropServices/UnmanagedFunctionPointerAttribute.cs",
    "System/Runtime/InteropServices/UnmanagedType.cs",
    "System/Runtime/InteropServices/VarEnum.cs",
    "System/Runtime/InteropServices/VariantWrapper.cs",
    "System/Runtime/InteropServices/WasmImportLinkageAttribute.cs",
    "System/Runtime/Intrinsics/Arm/Enums.cs",
    "System/Runtime/Intrinsics/ISimdVector_2.cs",
    "System/Runtime/Intrinsics/Scalar.cs",
    "System/Runtime/Intrinsics/SimdVectorExtensions.cs",
    "System/Runtime/Intrinsics/Vector128.cs",
    "System/Runtime/Intrinsics/Vector128_1.cs",
    "System/Runtime/Intrinsics/Vector128DebugView_1.cs",
    "System/Runtime/Intrinsics/Vector256.cs",
    "System/Runtime/Intrinsics/Vector256_1.cs",
    "System/Runtime/Intrinsics/Vector256DebugView_1.cs",
    "System/Runtime/Intrinsics/Vector512.cs",
    "System/Runtime/Intrinsics/Vector512_1.cs",
    "System/Runtime/Intrinsics/Vector512DebugView_1.cs",
    "System/Runtime/Intrinsics/Vector64.cs",
    "System/Runtime/Intrinsics/Vector64_1.cs",
    "System/Runtime/Intrinsics/Vector64DebugView_1.cs",
    "System/Runtime/Intrinsics/VectorMath.cs",
    "System/Runtime/Intrinsics/X86/Enums.cs",
    "System/Runtime/JitInfo.cs",
    "System/Runtime/Loader/AssemblyLoadContext.cs",
    "System/Runtime/Loader/LibraryNameVariation.cs",
    "System/Runtime/MemoryFailPoint.cs",
    "System/Runtime/NgenServicingAttributes.cs",
    "System/Runtime/ProfileOptimization.cs",
    "System/Runtime/Remoting/ObjectHandle.cs",
    "System/Runtime/Serialization/DeserializationToken.cs",
    "System/Runtime/Serialization/DeserializationTracker.cs",
    "System/Runtime/Serialization/IDeserializationCallback.cs",
    "System/Runtime/Serialization/IFormatterConverter.cs",
    "System/Runtime/Serialization/IObjectReference.cs",
    "System/Runtime/Serialization/ISafeSerializationData.cs",
    "System/Runtime/Serialization/ISerializable.cs",
    "System/Runtime/Serialization/OnDeserializedAttribute.cs",
    "System/Runtime/Serialization/OnDeserializingAttribute.cs",
    "System/Runtime/Serialization/OnSerializedAttribute.cs",
    "System/Runtime/Serialization/OnSerializingAttribute.cs",
    "System/Runtime/Serialization/OptionalFieldAttribute.cs",
    "System/Runtime/Serialization/SafeSerializationEventArgs.cs",
    "System/Runtime/Serialization/SerializationException.cs",
    "System/Runtime/Serialization/SerializationInfo.cs",
    "System/Runtime/Serialization/SerializationInfo.SerializationGuard.cs",
    "System/Runtime/Serialization/SerializationInfoEnumerator.cs",
    "System/Runtime/Serialization/StreamingContext.cs",
    "System/Runtime/Versioning/ComponentGuaranteesAttribute.cs",
    "System/Runtime/Versioning/ComponentGuaranteesOptions.cs",
    "System/Runtime/Versioning/FrameworkName.cs",
    "System/Runtime/Versioning/PlatformAttributes.cs",
    "System/Runtime/Versioning/RequiresPreviewFeaturesAttribute.cs",
    "System/Runtime/Versioning/ResourceConsumptionAttribute.cs",
    "System/Runtime/Versioning/ResourceExposureAttribute.cs",
    "System/Runtime/Versioning/ResourceScope.cs",
    "System/Runtime/Versioning/TargetFrameworkAttribute.cs",
    "System/Runtime/Versioning/VersioningHelper.cs",
    # "System/RuntimeType.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/SByte.cs",
    "System/Security/AllowPartiallyTrustedCallersAttribute.cs",
    "System/Security/CryptographicException.cs",
    "System/Security/DynamicSecurityMethodAttribute.cs",
    "System/Security/IPermission.cs",
    "System/Security/ISecurityEncodable.cs",
    "System/Security/IStackWalk.cs",
    "System/Security/PartialTrustVisibilityLevel.cs",
    "System/Security/Permissions/CodeAccessSecurityAttribute.cs",
    "System/Security/Permissions/PermissionState.cs",
    "System/Security/Permissions/SecurityAction.cs",
    "System/Security/Permissions/SecurityAttribute.cs",
    "System/Security/Permissions/SecurityPermissionAttribute.cs",
    "System/Security/Permissions/SecurityPermissionFlag.cs",
    "System/Security/PermissionSet.cs",
    "System/Security/Principal/IIdentity.cs",
    "System/Security/Principal/IPrincipal.cs",
    "System/Security/Principal/PrincipalPolicy.cs",
    "System/Security/Principal/TokenImpersonationLevel.cs",
    "System/Security/SecureString.cs",
    "System/Security/SecurityCriticalAttribute.cs",
    "System/Security/SecurityCriticalScope.cs",
    "System/Security/SecurityElement.cs",
    "System/Security/SecurityException.cs",
    "System/Security/SecurityRulesAttribute.cs",
    "System/Security/SecurityRuleSet.cs",
    "System/Security/SecuritySafeCriticalAttribute.cs",
    "System/Security/SecurityTransparentAttribute.cs",
    "System/Security/SecurityTreatAsSafeAttribute.cs",
    "System/Security/SuppressUnmanagedCodeSecurityAttribute.cs",
    "System/Security/UnverifiableCodeAttribute.cs",
    "System/Security/VerificationException.cs",
    "System/SerializableAttribute.cs",
    "System/Single.cs",
    "System/Span.cs",
    "System/SpanDebugView.cs",
    "System/SpanHelpers.BinarySearch.cs",
    "System/SpanHelpers.Byte.cs",
    "System/SpanHelpers.ByteMemOps.cs",
    "System/SpanHelpers.Char.cs",
    "System/SpanHelpers.cs",
    "System/SpanHelpers.Packed.cs",
    "System/SpanHelpers.T.cs",
    "System/SR.cs",
    "System/StackOverflowException.cs",
    "System/StartupHookProvider.cs",
    "System/String.Comparison.cs",
    "System/String.cs",
    "System/String.Manipulation.cs",
    "System/String.Searching.cs",
    "System/StringComparer.cs",
    "System/StringComparison.cs",
    "System/StringNormalizationExtensions.cs",
    "System/StringSplitOptions.cs",
    "System/SystemException.cs",
    "System/Text/Ascii.cs",
    "System/Text/Ascii.CaseConversion.cs",
    "System/Text/Ascii.Equality.cs",
    "System/Text/Ascii.Transcoding.cs",
    "System/Text/Ascii.Trimming.cs",
    "System/Text/Ascii.Utility.cs",
    "System/Text/Ascii.Utility.Helpers.cs",
    "System/Text/ASCIIEncoding.cs",
    "System/Text/CodePageDataItem.cs",
    "System/Text/CompositeFormat.cs",
    "System/Text/Decoder.cs",
    "System/Text/DecoderExceptionFallback.cs",
    "System/Text/DecoderFallback.cs",
    "System/Text/DecoderNLS.cs",
    "System/Text/DecoderReplacementFallback.cs",
    "System/Text/Encoder.cs",
    "System/Text/EncoderExceptionFallback.cs",
    "System/Text/EncoderFallback.cs",
    "System/Text/EncoderLatin1BestFitFallback.cs",
    "System/Text/EncoderLatin1BestFitFallback.Data.cs",
    "System/Text/EncoderNLS.cs",
    "System/Text/EncoderReplacementFallback.cs",
    "System/Text/Encoding.cs",
    "System/Text/Encoding.Internal.cs",
    "System/Text/EncodingData.cs",
    "System/Text/EncodingInfo.cs",
    "System/Text/EncodingProvider.cs",
    "System/Text/EncodingTable.cs",
    "System/Text/Latin1Encoding.cs",
    "System/Text/Latin1Encoding.Sealed.cs",
    "System/Text/Latin1Utility.cs",
    "System/Text/Latin1Utility.Helpers.cs",
    "System/Text/NormalizationForm.cs",
    "System/Text/Rune.cs",
    "System/Text/SpanLineEnumerator.cs",
    "System/Text/SpanRuneEnumerator.cs",
    "System/Text/StringBuilder.cs",
    # "System/Text/StringBuilder.Debug.cs" Condition="'$(Configuration)' == 'Debug'",
    "System/Text/StringRuneEnumerator.cs",
    "System/Text/TranscodingStream.cs",
    "System/Text/TrimType.cs",
    "System/Text/Unicode/GraphemeClusterBreakType.cs",
    "System/Text/Unicode/TextSegmentationUtility.cs",
    "System/Text/Unicode/Utf16Utility.cs",
    "System/Text/Unicode/Utf16Utility.Validation.cs",
    "System/Text/Unicode/Utf8.cs",
    "System/Text/Unicode/Utf8Utility.cs",
    "System/Text/Unicode/Utf8Utility.Helpers.cs",
    "System/Text/Unicode/Utf8Utility.Transcoding.cs",
    "System/Text/Unicode/Utf8Utility.Validation.cs",
    "System/Text/UnicodeDebug.cs",
    "System/Text/UnicodeEncoding.cs",
    "System/Text/UnicodeUtility.cs",
    "System/Text/UTF32Encoding.cs",
    "System/Text/UTF7Encoding.cs",
    "System/Text/UTF8Encoding.cs",
    "System/Text/UTF8Encoding.Sealed.cs",
    "System/Text/ValueStringBuilder.AppendFormat.cs",
    "System/ThreadAttributes.cs",
    "System/Threading/AbandonedMutexException.cs",
    "System/Threading/ApartmentState.cs",
    "System/Threading/AsyncLocal.cs",
    "System/Threading/AutoResetEvent.cs",
    "System/Threading/CancellationToken.cs",
    "System/Threading/CancellationTokenRegistration.cs",
    "System/Threading/CancellationTokenSource.cs",
    "System/Threading/CompressedStack.cs",
    "System/Threading/StackCrawlMark.cs",
    "System/Threading/DeferredDisposableLifetime.cs",
    "System/Threading/EventResetMode.cs",
    "System/Threading/EventWaitHandle.cs",
    "System/Threading/ExecutionContext.cs",
    "System/Threading/IOCompletionCallback.cs",
    "System/Threading/IOCompletionCallbackHelper.cs",
    "System/Threading/IThreadPoolWorkItem.cs",
    "System/Threading/Interlocked.cs",
    "System/Threading/LazyInitializer.cs",
    "System/Threading/LazyThreadSafetyMode.cs",
    "System/Threading/Lock.cs",
    # "System/Threading/Lock.NonNativeAot.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/Threading/LockRecursionException.cs",
    "System/Threading/LowLevelLock.cs",
    "System/Threading/LowLevelSpinWaiter.cs",
    "System/Threading/LowLevelMonitor.cs",
    "System/Threading/ManualResetEvent.cs",
    "System/Threading/ManualResetEventSlim.cs",
    "System/Threading/Monitor.cs",
    "System/Threading/Mutex.cs",
    "System/Threading/NativeOverlapped.cs",
    "System/Threading/Overlapped.cs",
    "System/Threading/ParameterizedThreadStart.cs",
    "System/Threading/ReaderWriterLockSlim.cs",
    "System/Threading/Semaphore.cs",
    "System/Threading/SemaphoreFullException.cs",
    "System/Threading/SemaphoreSlim.cs",
    "System/Threading/SendOrPostCallback.cs",
    "System/Threading/SpinLock.cs",
    "System/Threading/SpinWait.cs",
    "System/Threading/SynchronizationContext.cs",
    "System/Threading/SynchronizationLockException.cs",
    "System/Threading/Tasks/AsyncCausalityTracerConstants.cs",
    "System/Threading/Tasks/CachedCompletedInt32Task.cs",
    "System/Threading/Tasks/ConcurrentExclusiveSchedulerPair.cs",
    "System/Threading/Tasks/ConfigureAwaitOptions.cs",
    "System/Threading/Tasks/Future.cs",
    "System/Threading/Tasks/FutureFactory.cs",
    "System/Threading/Tasks/LoggingExtensions.cs",
    "System/Threading/Tasks/Sources/IValueTaskSource.cs",
    "System/Threading/Tasks/Sources/ManualResetValueTaskSourceCore.cs",
    "System/Threading/Tasks/Task.cs",
    "System/Threading/Tasks/TaskAsyncEnumerableExtensions.cs",
    "System/Threading/Tasks/TaskAsyncEnumerableExtensions.ToBlockingEnumerable.cs",
    "System/Threading/Tasks/TaskCache.cs",
    "System/Threading/Tasks/TaskCanceledException.cs",
    "System/Threading/Tasks/TaskCompletionSource.cs",
    "System/Threading/Tasks/TaskCompletionSource_T.cs",
    "System/Threading/Tasks/TaskContinuation.cs",
    "System/Threading/Tasks/TaskExceptionHolder.cs",
    "System/Threading/Tasks/TaskExtensions.cs",
    "System/Threading/Tasks/TaskFactory.cs",
    "System/Threading/Tasks/TaskScheduler.cs",
    "System/Threading/Tasks/TaskSchedulerException.cs",
    "System/Threading/Tasks/ThreadPoolTaskScheduler.cs",
    "System/Threading/Tasks/TplEventSource.cs",
    "System/Threading/Tasks/ValueTask.cs",
    "System/Threading/Thread.cs",
    "System/Threading/ProcessorIdCache.cs",
    "System/Threading/ThreadAbortException.cs",
    "System/Threading/ThreadBlockingInfo.cs",
    "System/Threading/ThreadExceptionEventArgs.cs",
    "System/Threading/ThreadInt64PersistentCounter.cs",
    "System/Threading/ThreadInterruptedException.cs",
    "System/Threading/ThreadLocal.cs",
    "System/Threading/ThreadPoolWorkQueue.cs",
    "System/Threading/ThreadPriority.cs",
    "System/Threading/ThreadStart.cs",
    "System/Threading/ThreadStartException.cs",
    "System/Threading/ThreadState.cs",
    "System/Threading/ThreadStateException.cs",
    "System/Threading/Timeout.cs",
    "System/Threading/TimeoutHelper.cs",
    "System/Threading/PeriodicTimer.cs",
    "System/Threading/Timer.cs",
    # "System/Threading/TimerQueue.Portable.cs" Condition="'$(FeaturePortableTimer)' == 'true'",
    "System/Threading/Volatile.cs",
    "System/Threading/WaitHandle.cs",
    "System/Threading/WaitHandleCannotBeOpenedException.cs",
    "System/Threading/WaitHandleExtensions.cs",
    "System/Threading/Win32ThreadPoolNativeOverlapped.cs",
    "System/Threading/Win32ThreadPoolNativeOverlapped.ExecutionContextCallbackArgs.cs",
    "System/Threading/Win32ThreadPoolNativeOverlapped.OverlappedData.cs",
    "System/ThreadStaticAttribute.cs",
    "System/ThrowHelper.cs",
    "System/TimeOnly.cs",
    "System/TimeoutException.cs",
    "System/TimeSpan.cs",
    "System/TimeZone.cs",
    "System/TimeZoneInfo.AdjustmentRule.cs",
    "System/TimeZoneInfo.cs",
    # "System/TimeZoneInfo.FullGlobalizationData.cs" Condition="'$(UseMinimalGlobalizationData)' != 'true'",
    "System/TimeZoneInfo.StringSerializer.cs",
    "System/TimeZoneInfo.TransitionTime.cs",
    "System/TimeZoneNotFoundException.cs",
    "System/Tuple.cs",
    "System/TupleSlim.cs",
    "System/TupleExtensions.cs",
    "System/Type.cs",
    "System/Type.Enum.cs",
    "System/Type.Helpers.cs",
    "System/TypeAccessException.cs",
    "System/TypeCode.cs",
    # "System/TypedReference.cs" Condition="'$(FeatureNativeAot)' != 'true'",
    "System/TypeInitializationException.cs",
    "System/TypeLoadException.cs",
    "System/TypeUnloadedException.cs",
    "System/UInt16.cs",
    "System/UInt32.cs",
    "System/UInt64.cs",
    "System/UInt128.cs",
    "System/UIntPtr.cs",
    "System/UnauthorizedAccessException.cs",
    "System/UnhandledExceptionEventArgs.cs",
    "System/UnhandledExceptionEventHandler.cs",
    "System/UnitySerializationHolder.cs",
    "System/ValueTuple.cs",
    "System/Version.cs",
    "System/Void.cs",
    "System/WeakReference.cs",
    "System/WeakReference.T.cs",
    "System/ComAwareWeakReference.cs",
#    "r/Interop.CompareInfo.cs" Condition="'$(TargetsBrowser)' == 'true'">
#    "</Link>
#    "
#    "r/Interop.Calendar.cs" Condition="'$(TargetsBrowser)' == 'true'">
#    "ink>
#    "
#    "r/Interop.Locale.cs" Condition="'$(TargetsBrowser)' == 'true'">
#    ".cs</Link>
#    "
#    "r/Interop.TextInfo.cs" Condition="'$(TargetsBrowser)' == 'true'">
#    "ink>
#    "
#    "p.Calendar.cs">
#    "ink>
#    "
#    "p.Calendar.iOS.cs" Condition="'$(IsiOSLike)' == 'true'">
#    "s</Link>
#    "
#    "p.Casing.cs">
#    "k>
#    "
#    "p.Casing.iOS.cs" Condition="'$(IsiOSLike)' == 'true'">
#    "/Link>
#    "
#    "p.Collation.cs">
#    "Link>
#    "
#    "p.Collation.iOS.cs" Condition="'$(IsiOSLike)' == 'true'">
#    "cs</Link>
#    "
#    "p.ICU.cs">
#    "
#    "
#    "p.ICU.iOS.cs" Condition="'$(IsiOSLike)' == 'true'">
#    "nk>
#    "
#    "p.Idna.cs">
#    "
#    "
#    "p.Locale.cs">
#    "k>
#    "
#    "p.Locale.iOS.cs" Condition="'$(IsiOSLike)' == 'true'">
#    "/Link>
#    "
#    "p.Normalization.cs">
#    "cs</Link>
#    "
#    "p.Normalization.iOS.cs" Condition="'$(IsiOSLike)' == 'true'">
#    "iOS.cs</Link>
#    "
#    "p.ResultCode.cs">
#    "/Link>
#    "
#    "p.TimeZoneDisplayNameType.cs" Condition="'$(UseMinimalGlobalizationData)' != 'true'">
#    "yNameType.cs</Link>
#    "
#    "p.TimeZoneInfo.cs" Condition="'$(UseMinimalGlobalizationData)' != 'true'">
#    "s</Link>
#    "
#    "p.TimeZoneInfo.iOS.cs" Condition="'$(IsiOSLike)' == 'true'">
#    "OS.cs</Link>
#    "
#    "p.Utils.cs">
#    ">
#    "
#    "s/Interop.Errors.cs">
#    "k>
#    "
#    "ogous to the Windows BOOL type on Unix -->
#    "s/Interop.BOOL.cs">
#    "s</Link>
#    "
#    "s/Kernel32/Interop.Globalization.cs">
#    "op.Globalization.cs</Link>
#    "
#    "s/Kernel32/Interop.ResolveLocaleName.cs">
#    "op.ResolveLocaleName.cs</Link>
#    "
#    "s/Normaliz/Interop.Idna.cs">
#    "op.Idna.cs</Link>
#    "
#    "s/Normaliz/Interop.Normalization.cs">
#    "op.Normalization.cs</Link>
#    "
#    "Marshalling.cs">
#    "Link>
#    "
#    ".cs">
#    "
#    "
#    "vider.cs">
#    "
#    "
#    "pContextSwitches.Common.cs">
#    ".Common.cs</Link>
#    "
#    "erter.cs">
#    "
#    "
#    "s.cs">
#    "
#    "
#    "emented.cs">
#    ">
#    "
#    "Formatting.Common.cs">
#    "ink>
#    "
#    "NumberBuffer.cs">
#    "
#    "
#    "Parsing.Common.cs">
#    ">
#    "
#    "s/Crc32ReflectedTable.cs">
#    "Table.cs</Link>
#    "
#    "ions.cs">
#    "
#    "
#    "NonSecretPurposes.cs">
#    "s.cs</Link>
#    "
#    "
#    "
#    "
#    "ions/Concurrent/IProducerConsumerQueue.cs">
#    "erConsumerQueue.cs</Link>
#    "
#    "ions/Concurrent/MultiProducerMultiConsumerQueue.cs">
#    "oducerMultiConsumerQueue.cs</Link>
#    "
#    "ions/Concurrent/SingleProducerSingleConsumerQueue.cs">
#    "roducerSingleConsumerQueue.cs</Link>
#    "
#    "ions/Generic/EnumerableHelpers.cs">
#    "merableHelpers.cs</Link>
#    "
#    "ions/Generic/BitHelper.cs">
#    "Helper.cs</Link>
#    "
#    "Internal.cs">
#    "k>
#    "
#    "Internal.CaseSensitivity.cs">
#    "sitivity.cs</Link>
#    "
#    "ion/AssemblyNameParser.cs">
#    "Parser.cs</Link>
#    "
#    "ion/AssemblyNameFormatter.cs">
#    "Formatter.cs</Link>
#    "
#    "em.Reflection.Metadata/src/System/Reflection/Metadata/AssemblyNameInfo.cs">
#    "emblyNameInfo.cs</Link>
#    "
#    "em.Reflection.Metadata/src/System/Reflection/Metadata/TypeName.cs">
#    "eName.cs</Link>
#    "
#    "ion/Metadata/TypeNameHelpers.cs">
#    "eNameHelpers.cs</Link>
#    "
#    "em.Reflection.Metadata/src/System/Reflection/Metadata/TypeNameParser.cs">
#    "eNameParser.cs</Link>
#    "
#    "em.Reflection.Metadata/src/System/Reflection/Metadata/TypeNameParserHelpers.cs">
#    "eNameParserHelpers.cs</Link>
#    "
#    "/Versioning/NonVersionableAttribute.cs">
#    "ersionableAttribute.cs</Link>
#    "
#    "ringBuilderCache.cs">
#    ".cs</Link>
#    "
#    "lueStringBuilder.cs">
#    ".cs</Link>
#    "
#    "lueStringBuilder.AppendSpanFormattable.cs">
#    ".AppendSpanFormattable.cs</Link>
#    "
#    "ng/ITimer.cs">
#    "nk>
#    "
#    "ng/OpenExistingResult.cs">
#    "esult.cs</Link>
#    "
#    "ng/Tasks/TaskToAsyncResult.cs">
#    "syncResult.cs</Link>
#    "
#  </"
#  <I"e' and '$(FeatureNativeAot)' != 'true'">
#    "p.HostPolicy.cs">
#    "/Link>
#    "
#    "System/Runtime/Loader/AssemblyDependencyResolver.cs",
#  </"
#  <I"e' or '$(FeatureNativeAot)' == 'true'">
#    "System/Runtime/Loader/AssemblyDependencyResolver.PlatformNotSupported.cs",
#  </"
#  <I"e'">
#    "System/Runtime/InteropServices/PosixSignalRegistration.PlatformNotSupported.cs",
#  </"
#  <I"
#    "s/Advapi32/Interop.ActivityControl.cs">
#    "op.ActivityControl.cs</Link>
#    "
#    "s/Advapi32/Interop.EtwEnableCallback.cs">
#    "op.EtwEnableCallback.cs</Link>
#    "
#    "s/Advapi32/Interop.EVENT_INFO_CLASS.cs">
#    "op.EVENT_INFO_CLASS.cs</Link>
#    "
#    "System/Diagnostics/Tracing/ActivityTracker.cs",
#    "System/Diagnostics/Tracing/DiagnosticCounter.cs",
#    "System/Diagnostics/Tracing/CounterGroup.cs",
#    "System/Diagnostics/Tracing/CounterPayload.cs",
#    "System/Diagnostics/Tracing/EventActivityOptions.cs",
#    "System/Diagnostics/Tracing/EventCounter.cs",
#    "System/Diagnostics/Tracing/EventDescriptor.cs",
#    "System/Diagnostics/Tracing/EventPipe.cs" Condition="'$(FeaturePerfTracing)' == 'true'",
#    "System/Diagnostics/Tracing/EventPipe.Internal.cs" Condition="'$(FeaturePerfTracing)' == 'true' and '$(FeatureMono)' != 'true'",
#    "System/Diagnostics/Tracing/EventPipeEventDispatcher.cs" Condition="'$(FeaturePerfTracing)' == 'true'",
#    "System/Diagnostics/Tracing/EventPipeEventProvider.cs" Condition="'$(FeaturePerfTracing)' == 'true'",
#    "System/Diagnostics/Tracing/EventPipeMetadataGenerator.cs" Condition="'$(FeaturePerfTracing)' == 'true'",
#    "System/Diagnostics/Tracing/EventPipePayloadDecoder.cs" Condition="'$(FeaturePerfTracing)' == 'true'",
#    "System/Diagnostics/Tracing/EventProvider.cs",
#    "System/Diagnostics/Tracing/EventSource.cs",
#    "System/Diagnostics/Tracing/EventSourceException.cs",
#    "System/Diagnostics/Tracing/FrameworkEventSource.cs",
#    "System/Diagnostics/Tracing/IncrementingEventCounter.cs",
#    "System/Diagnostics/Tracing/IncrementingPollingCounter.cs",
#    "System/Diagnostics/Tracing/NativeRuntimeEventSource.cs",
#    "System/Diagnostics/Tracing/NativeRuntimeEventSource.Threading.cs" Condition="'$(FeaturePerfTracing)' != 'true'",
#    "System/Diagnostics/Tracing/NativeRuntimeEventSource.Threading.NativeSinks.cs" Condition="'$(FeaturePerfTracing)' == 'true'",
#    "System/Diagnostics/Tracing/NativeRuntimeEventSource.Threading.NativeSinks.Internal.cs" Condition="'$(FeaturePerfTracing)' == 'true' and '$(FeatureMono)' != 'true'",
#    "System/Diagnostics/Tracing/PollingCounter.cs",
#    "System/Diagnostics/Tracing/RuntimeEventSource.cs" Condition="'$(FeaturePerfTracing)' == 'true'",
#    "System/Diagnostics/Tracing/Winmeta.cs",
#    "System/Diagnostics/Tracing/TraceLogging/ArrayTypeInfo.cs",
#    "System/Diagnostics/Tracing/TraceLogging/ConcurrentSet.cs",
#    "System/Diagnostics/Tracing/TraceLogging/ConcurrentSetItem.cs",
#    "System/Diagnostics/Tracing/TraceLogging/DataCollector.cs",
#    "System/Diagnostics/Tracing/TraceLogging/EmptyStruct.cs",
#    "System/Diagnostics/Tracing/TraceLogging/EnumerableTypeInfo.cs",
#    "System/Diagnostics/Tracing/TraceLogging/EnumHelper.cs",
#    "System/Diagnostics/Tracing/TraceLogging/EventDataAttribute.cs",
#    "System/Diagnostics/Tracing/TraceLogging/EventFieldAttribute.cs",
#    "System/Diagnostics/Tracing/TraceLogging/EventFieldFormat.cs",
#    "System/Diagnostics/Tracing/TraceLogging/EventIgnoreAttribute.cs",
#    "System/Diagnostics/Tracing/TraceLogging/EventPayload.cs",
#    "System/Diagnostics/Tracing/TraceLogging/EventSourceOptions.cs",
#    "System/Diagnostics/Tracing/TraceLogging/FieldMetadata.cs",
#    "System/Diagnostics/Tracing/TraceLogging/InvokeTypeInfo.cs",
#    "System/Diagnostics/Tracing/TraceLogging/NameInfo.cs",
#    "System/Diagnostics/Tracing/TraceLogging/PropertyAnalysis.cs",
#    "System/Diagnostics/Tracing/TraceLogging/PropertyValue.cs",
#    "System/Diagnostics/Tracing/TraceLogging/SimpleEventTypes.cs",
#    "System/Diagnostics/Tracing/TraceLogging/SimpleTypeInfos.cs",
#    "System/Diagnostics/Tracing/TraceLogging/Statics.cs",
#    "System/Diagnostics/Tracing/TraceLogging/TraceLoggingDataCollector.cs",
#    "System/Diagnostics/Tracing/TraceLogging/TraceLoggingDataType.cs",
#    "System/Diagnostics/Tracing/TraceLogging/TraceLoggingEventHandleTable.cs" Condition="'$(FeaturePerfTracing)' == 'true'",
#    "System/Diagnostics/Tracing/TraceLogging/TraceLoggingEventSource.cs",
#    "System/Diagnostics/Tracing/TraceLogging/TraceLoggingEventTraits.cs",
#    "System/Diagnostics/Tracing/TraceLogging/TraceLoggingEventTypes.cs",
#    "System/Diagnostics/Tracing/TraceLogging/TraceLoggingMetadataCollector.cs",
#    "System/Diagnostics/Tracing/TraceLogging/TraceLoggingTypeInfo.cs",
#    "System/Diagnostics/Tracing/TraceLogging/TypeAnalysis.cs",
#    "System/Diagnostics/Tracing/TraceLogging/XplatEventLogger.cs" Condition="'$(FeatureXplatEventSource)' == 'true'",
#    "System/Runtime/CompilerServices/QCallHandles.cs" Condition="'$(FeatureNativeAot)' != 'true'",
#  </"
#  <I"rue'">
#    "s/Advapi32/Interop.EncryptDecrypt.cs">
#    "op.EncryptDecrypt.cs</Link>
#    "
#    "s/Advapi32/Interop.EventActivityIdControl.cs">
#    "op.EventActivityIdControl.cs</Link>
#    "
#    "s/Advapi32/Interop.EventRegister.cs">
#    "op.EventRegister.cs</Link>
#    "
#    "s/Advapi32/Interop.EventSetInformation.cs">
#    "op.EventSetInformation.cs</Link>
#    "
#    "s/Advapi32/Interop.EventTraceGuidsEx.cs">
#    "op.EventTraceGuidsEx.cs</Link>
#    "
#    "s/Advapi32/Interop.EventUnregister.cs">
#    "op.EventUnregister.cs</Link>
#    "
#    "s/Advapi32/Interop.EventWriteString.cs">
#    "op.EventWriteString.cs</Link>
#    "
#    "s/Advapi32/Interop.EventWriteTransfer.cs">
#    "op.EventWriteTransfer.cs</Link>
#    "
#    "s/Advapi32/Interop.GetTokenInformation_void.cs">
#    "op.GetTokenInformation_void.cs</Link>
#    "
#    "s/Advapi32/Interop.LookupAccountNameW.cs">
#    "op.LookupAccountNameW.cs</Link>
#    "
#    "s/Advapi32/Interop.OpenProcessToken.cs">
#    "op.OpenProcessToken.cs</Link>
#    "
#    "s/Advapi32/Interop.RegCloseKey.cs">
#    "op.RegCloseKey.cs</Link>
#    "
#    "s/Advapi32/Interop.RegCreateKeyEx.cs">
#    "op.RegCreateKeyEx.cs</Link>
#    "
#    "s/Advapi32/Interop.RegDeleteKeyEx.cs">
#    "op.RegDeleteKeyEx.cs</Link>
#    "
#    "s/Advapi32/Interop.RegDeleteValue.cs">
#    "op.RegDeleteValue.cs</Link>
#    "
#    "s/Advapi32/Interop.RegEnumKeyEx.cs">
#    "op.RegEnumKeyEx.cs</Link>
#    "
#    "s/Advapi32/Interop.RegEnumValue.cs">
#    "op.RegEnumValue.cs</Link>
#    "
#    "s/Advapi32/Interop.RegFlushKey.cs">
#    "op.RegFlushKey.cs</Link>
#    "
#    "s/Advapi32/Interop.RegistryConstants.cs">
#    "op.RegistryConstants.cs</Link>
#    "
#    "s/Advapi32/Interop.RegOpenKeyEx.cs">
#    "op.RegOpenKeyEx.cs</Link>
#    "
#    "s/Advapi32/Interop.RegQueryValueEx.cs">
#    "op.RegQueryValueEx.cs</Link>
#    "
#    "s/Advapi32/Interop.RegSetValueEx.cs">
#    "op.RegSetValueEx.cs</Link>
#    "
#    "s/Advapi32/Interop.TOKEN_ACCESS_LEVELS.cs">
#    "op.TOKEN_ACCESS_LEVELS.cs</Link>
#    "
#    "s/Advapi32/Interop.TOKEN_ELEVATION.cs">
#    "op.TOKEN_ELEVATION.cs</Link>
#    "
#    "s/Advapi32/Interop.TOKEN_INFORMATION_CLASS.cs">
#    "op.TOKEN_INFORMATION_CLASS.cs</Link>
#    "
#    "s/BCrypt/Interop.BCryptGenRandom.cs">
#    ".BCryptGenRandom.cs</Link>
#    "
#    "s/BCrypt/Interop.BCryptGenRandom.GetRandomBytes.cs">
#    ".BCryptGenRandom.GetRandomBytes.cs</Link>
#    "
#    "s/BCrypt/Interop.NTSTATUS.cs">
#    ".NTSTATUS.cs</Link>
#    "
#    "s/Crypt32/Interop.CryptProtectMemory.cs">
#    "p.CryptProtectMemory.cs</Link>
#    "
#    "s/Interop.BOOLEAN.cs">
#    "N.cs</Link>
#    "
#    "s/Interop.Libraries.cs">
#    "ies.cs</Link>
#    "
#    "s/Kernel32/Interop.CancelIoEx.cs">
#    "op.CancelIoEx.cs</Link>
#    "
#    "ws/Kernel32/Interop.CancelSynchronousIo.cs">
#    "elSynchronousIo.cs</Link>
#    "
#    "s/Kernel32/Interop.CompletionPort.cs">
#    "op.CompletionPort.cs</Link>
#    "
#    "s/Kernel32/Interop.ConditionVariable.cs">
#    "op.ConditionVariable.cs</Link>
#    "
#    "s/Kernel32/Interop.CopyFile.cs">
#    "op.CopyFile.cs</Link>
#    "
#    "s/Kernel32/Interop.CopyFileEx.cs">
#    "op.CopyFileEx.cs</Link>
#    "
#    "s/Kernel32/Interop.CreateDirectory.cs">
#    "op.CreateDirectory.cs</Link>
#    "
#    "s/Kernel32/Interop.CreateFile.cs">
#    "op.CreateFile.cs</Link>
#    "
#    "ws/Kernel32/Interop.Timer.cs">
#    "r.cs</Link>
#    "
#    "ws/Kernel32/Interop.ThreadPoolIO.cs">
#    "adPoolIO.cs</Link>
#    "
#    "ws/Kernel32/Interop.ThreadPool.cs">
#    "adPool.cs</Link>
#    "
#    "s/NtDll/Interop.NtCreateFile.cs">
#    "NtCreateFile.cs</Link>
#    "
#    "s/NtDll/Interop.NtStatus.cs">
#    "NtStatus.cs</Link>
#    "
#    "s/NtDll/Interop.IO_STATUS_BLOCK.cs">
#    "IO_STATUS_BLOCK.cs</Link>
#    "
#    "s/NtDll/Interop.RtlNtStatusToDosError.cs">
#    "RtlNtStatusToDosError.cs</Link>
#    "
#    "s/Kernel32/Interop.BY_HANDLE_FILE_INFORMATION.cs">
#    "op.BY_HANDLE_FILE_INFORMATION.cs</Link>
#    "
#    "s/Kernel32/Interop.DeleteFile.cs">
#    "op.DeleteFile.cs</Link>
#    "
#    "s/Kernel32/Interop.DeleteVolumeMountPoint.cs">
#    "op.DeleteVolumeMountPoint.cs</Link>
#    "
#    "s/Kernel32/Interop.CreateFile_IntPtr.cs">
#    "op.CreateFile_IntPtr.cs</Link>
#    "
#    "s/Kernel32/Interop.CreateSymbolicLink.cs">
#    "op.CreateSymbolicLink.cs</Link>
#    "
#    "s/Kernel32/Interop.CriticalSection.cs">
#    "op.CriticalSection.cs</Link>
#    "
#    "s/Kernel32/Interop.DeviceIoControl.cs">
#    "op.DeviceIoControl.cs</Link>
#    "
#    "s/Kernel32/Interop.ExpandEnvironmentStrings.cs">
#    "op.ExpandEnvironmentStrings.cs</Link>
#    "
#    "s/Kernel32/Interop.FILE_BASIC_INFO.cs">
#    "op.FILE_BASIC_INFO.cs</Link>
#    "
#    "s/Kernel32/Interop.FILE_ALLOCATION_INFO.cs">
#    "op.FILE_ALLOCATION_INFO.cs</Link>
#    "
#    "s/Kernel32/Interop.FILE_END_OF_FILE_INFO.cs">
#    "op.FILE_END_OF_FILE_INFO.cs</Link>
#    "
#    "s/Kernel32/Interop.FILE_STANDARD_INFO.cs">
#    "op.FILE_STANDARD_INFO.cs</Link>
#    "
#    "s/Kernel32/Interop.FILE_TIME.cs">
#    "op.FILE_TIME.cs</Link>
#    "
#    "s/Kernel32/Interop.FileAttributes.cs">
#    "op.FileAttributes.cs</Link>
#    "
#    "s/Kernel32/Interop.FindNextFile.cs">
#    "op.FindNextFile.cs</Link>
#    "
#    "s/Kernel32/Interop.FileOperations.cs">
#    "op.FileOperations.cs</Link>
#    "
#    "s/Kernel32/Interop.FileTimeToSystemTime.cs">
#    "op.FileTimeToSystemTime.cs</Link>
#    "
#    "s/Kernel32/Interop.FileTypes.cs">
#    "op.FileTypes.cs</Link>
#    "
#    "s/Kernel32/Interop.FindClose.cs">
#    "op.FindClose.cs</Link>
#    "
#    "s/Kernel32/Interop.FindFirstFileEx.cs">
#    "op.FindFirstFileEx.cs</Link>
#    "
#    "s/Kernel32/Interop.FlushFileBuffers.cs">
#    "op.FlushFileBuffers.cs</Link>
#    "
#    "s/Kernel32/Interop.FreeLibrary.cs">
#    "op.FreeLibrary.cs</Link>
#    "
#    "s/Kernel32/Interop.GenericOperations.cs">
#    "op.GenericOperations.cs</Link>
#    "
#    "s/Kernel32/Interop.GetLastError.cs">
#    "op.GetLastError.cs</Link>
#    "
#    "s/Kernel32/Interop.GET_FILEEX_INFO_LEVELS.cs">
#    "op.GET_FILEEX_INFO_LEVELS.cs</Link>
#    "
#    "s/Kernel32/Interop.GetCommandLine.cs">
#    "op.GetCommandLine.cs</Link>
#    "
#    "s/Kernel32/Interop.GetComputerName.cs">
#    "op.GetComputerName.cs</Link>
#    "
#    "s/Kernel32/Interop.GetConsoleOutputCP.cs">
#    "op.GetConsoleOutputCP.cs</Link>
#    "
#    "s/Kernel32/Interop.GetCPInfo.cs">
#    "op.GetCPInfo.cs</Link>
#    "
#    "s/Kernel32/Interop.GetCurrentDirectory.cs">
#    "op.GetCurrentDirectory.cs</Link>
#    "
#    "s/Kernel32/Interop.GetCurrentProcess.cs">
#    "op.GetCurrentProcess.cs</Link>
#    "
#    "ws/Kernel32/Interop.GetCurrentProcessId.cs">
#    "op.GetCurrentProcessId.cs</Link>
#    "
#    "s/Kernel32/Interop.GetCurrentThreadId.cs">
#    "op.GetCurrentThreadId.cs</Link>
#    "
#    "s/Kernel32/Interop.GetFileAttributesEx.cs">
#    "op.GetFileAttributesEx.cs</Link>
#    "
#    "s/Kernel32/Interop.GetFileInformationByHandle.cs">
#    "op.GetFileInformationByHandle.cs</Link>
#    "
#    "s/Kernel32/Interop.GetFileInformationByHandleEx.cs">
#    "op.GetFileInformationByHandleEx.cs</Link>
#    "
#    "s/Kernel32/Interop.GetFileType_SafeHandle.cs">
#    "op.GetFileType_SafeHandle.cs</Link>
#    "
#    "s/Kernel32/Interop.GetFinalPathNameByHandle.cs">
#    "op.GetFinalPathNameByHandle.cs</Link>
#    "
#    "s/Kernel32/Interop.GetFullPathNameW.cs">
#    "op.GetFullPathNameW.cs</Link>
#    "
#    "s/Kernel32/Interop.GetLogicalDrives.cs">
#    "op.GetLogicalDrives.cs</Link>
#    "
#    "s/Kernel32/Interop.GetLongPathNameW.cs">
#    "op.GetLongPathNameW.cs</Link>
#    "
#    "s/Kernel32/Interop.GetModuleFileName.cs">
#    "op.GetModuleFileName.cs</Link>
#    "
#    "s/Kernel32/Interop.GetNativeSystemInfo.cs">
#    "op.GetNativeSystemInfo.cs</Link>
#    "
#    "s/Kernel32/Interop.GetOverlappedResult.cs">
#    "op.GetOverlappedResult.cs</Link>
#    "
#    "s/Kernel32/Interop.GetProcessMemoryInfo.cs">
#    "op.GetProcessMemoryInfo.cs</Link>
#    "
#    "s/Kernel32/Interop.GetProcessTimes_IntPtr.cs">
#    "op.GetProcessTimes_IntPtr.cs</Link>
#    "
#    "s/Kernel32/Interop.GetStdHandle.cs">
#    "op.GetStdHandle.cs</Link>
#    "
#    "s/Kernel32/Interop.GetSystemDirectoryW.cs">
#    "op.GetSystemDirectoryW.cs</Link>
#    "
#    "s/Kernel32/Interop.GetSystemInfo.cs">
#    "op.GetSystemInfo.cs</Link>
#    "
#    "s/Kernel32/Interop.GetSystemTime.cs">
#    "op.GetSystemTime.cs</Link>
#    "
#    "s/Kernel32/Interop.GetSystemTimes.cs">
#    "op.GetSystemTimes.cs</Link>
#    "
#    "s/Kernel32/Interop.GetVolumeInformation.cs">
#    "op.GetVolumeInformation.cs</Link>
#    "
#    "s/Kernel32/Interop.GlobalMemoryStatusEx.cs">
#    "op.GlobalMemoryStatusEx.cs</Link>
#    "
#    "s/Kernel32/Interop.HandleTypes.cs">
#    "op.HandleTypes.cs</Link>
#    "
#    "s/Kernel32/Interop.IsWow64Process_IntPtr.cs">
#    "op.IsWow64Process_IntPtr.cs</Link>
#    "
#    "s/Kernel32/Interop.LoadLibraryEx_IntPtr.cs">
#    "op.LoadLibraryEx_IntPtr.cs</Link>
#    "
#    "s/Kernel32/Interop.LocalAlloc.cs">
#    "op.LocalAlloc.cs</Link>
#    "
#    "s/Kernel32/Interop.LocalFree.cs">
#    "op.LocalFree.cs</Link>
#    "
#    "s/Kernel32/Interop.LocalReAlloc.cs">
#    "op.LocalReAlloc.cs</Link>
#    "
#    "s/Kernel32/Interop.LockFile.cs">
#    "op.LockFile.cs</Link>
#    "
#    "s/Kernel32/Interop.MAX_PATH.cs">
#    "op.MAX_PATH.cs</Link>
#    "
#    "s/Kernel32/Interop.MemOptions.cs">
#    "op.MemOptions.cs</Link>
#    "
#    "s/Kernel32/Interop.MEMORY_BASIC_INFORMATION.cs">
#    "op.MEMORY_BASIC_INFORMATION.cs</Link>
#    "
#    "s/Kernel32/Interop.MEMORYSTATUSEX.cs">
#    "op.MEMORYSTATUSEX.cs</Link>
#    "
#    "s/Kernel32/Interop.MultiByteToWideChar.cs">
#    "op.MultiByteToWideChar.cs</Link>
#    "
#    "s/Kernel32/Interop.MoveFileEx.cs">
#    "op.MoveFileEx.cs</Link>
#    "
#    "ws/Kernel32/Interop.OpenThread.cs">
#    "Thread.cs</Link>
#    "
#    "s/Kernel32/Interop.OSVERSIONINFOEX.cs">
#    "op.OSVERSIONINFOEX.cs</Link>
#    "
#    "s/Kernel32/Interop.OutputDebugString.cs">
#    "op.OutputDebugString.cs</Link>
#    "
#    "s/Kernel32/Interop.QueryPerformanceCounter.cs">
#    "op.QueryPerformanceCounter.cs</Link>
#    "
#    "s/Kernel32/Interop.QueryPerformanceFrequency.cs">
#    "op.QueryPerformanceFrequency.cs</Link>
#    "
#    "s/Kernel32/Interop.QueryUnbiasedInterruptTime.cs">
#    "op.QueryUnbiasedInterruptTime.cs</Link>
#    "
#    "s/Kernel32/Interop.ReadFile_SafeHandle_IntPtr.cs">
#    "op.ReadFile_SafeHandle_IntPtr.cs</Link>
#    "
#    "s/Kernel32/Interop.ReadFile_SafeHandle_NativeOverlapped.cs">
#    "op.ReadFile_SafeHandle_NativeOverlapped.cs</Link>
#    "
#    "s/Kernel32/Interop.FileScatterGather.cs">
#    "op.FileScatterGather.cs</Link>
#    "
#    "s/Kernel32/Interop.RemoveDirectory.cs">
#    "op.RemoveDirectory.cs</Link>
#    "
#    "s/Kernel32/Interop.REPARSE_DATA_BUFFER.cs">
#    "op.REPARSE_DATA_BUFFER.cs</Link>
#    "
#    "s/Kernel32/Interop.ReplaceFile.cs">
#    "op.ReplaceFile.cs</Link>
#    "
#    "s/Kernel32/Interop.SECURITY_ATTRIBUTES.cs">
#    "op.SECURITY_ATTRIBUTES.cs</Link>
#    "
#    "s/Kernel32/Interop.STORAGE_READ_CAPACITY.cs">
#    "op.STORAGE_READ_CAPACITY.cs</Link>
#    "
#    "s/Interop.UNICODE_STRING.cs">
#    "E_STRING.cs</Link>
#    "
#    "s/Kernel32/Interop.SecurityOptions.cs">
#    "op.SecurityOptions.cs</Link>
#    "
#    "s/Interop.SECURITY_QUALITY_OF_SERVICE.cs">
#    "TY_QUALITY_OF_SERVICE.cs</Link>
#    "
#    "s/Interop.OBJECT_ATTRIBUTES.cs">
#    "_ATTRIBUTES.cs</Link>
#    "
#    "s/Kernel32/Interop.SetConsoleCtrlHandler.cs">
#    "soleCtrlHandler.cs</Link>
#    "
#    "s/Kernel32/Interop.SetCurrentDirectory.cs">
#    "op.SetCurrentDirectory.cs</Link>
#    "
#    "s/Kernel32/Interop.SetFileAttributes.cs">
#    "op.SetFileAttributes.cs</Link>
#    "
#    "s/Kernel32/Interop.SetFileInformationByHandle.cs">
#    "op.SetFileInformationByHandle.cs</Link>
#    "
#    "s/Kernel32/Interop.SetFilePointerEx.cs">
#    "op.SetFilePointerEx.cs</Link>
#    "
#    "s/Kernel32/Interop.SetLastError.cs">
#    "op.SetLastError.cs</Link>
#    "
#    "s/Kernel32/Interop.SetThreadErrorMode.cs">
#    "op.SetThreadErrorMode.cs</Link>
#    "
#    "s/Kernel32/Interop.SYSTEM_INFO.cs">
#    "op.SYSTEM_INFO.cs</Link>
#    "
#    "s/Kernel32/Interop.SystemTimeToFileTime.cs">
#    "op.SystemTimeToFileTime.cs</Link>
#    "
#    "s/Kernel32/Interop.Threading.cs">
#    "op.Threading.cs</Link>
#    "
#    "s/Kernel32/Interop.TimeZone.cs">
#    "op.TimeZone.cs</Link>
#    "
#    "s/Kernel32/Interop.TimeZone.Registry.cs">
#    "op.TimeZone.Registry.cs</Link>
#    "
#    "s/Kernel32/Interop.TzSpecificLocalTimeToSystemTime.cs">
#    "op.TzSpecificLocalTimeToSystemTime.cs</Link>
#    "
#    "s/Kernel32/Interop.VirtualAlloc_Ptr.cs">
#    "op.VirtualAlloc_Ptr.cs</Link>
#    "
#    "s/Kernel32/Interop.VirtualFree.cs">
#    "op.VirtualFree.cs</Link>
#    "
#    "s/Kernel32/Interop.VirtualQuery_Ptr.cs">
#    "op.VirtualQuery_Ptr.cs</Link>
#    "
#    "s/Kernel32/Interop.WideCharToMultiByte.cs">
#    "op.WideCharToMultiByte.cs</Link>
#    "
#    "s/Kernel32/Interop.WIN32_FILE_ATTRIBUTE_DATA.cs">
#    "op.WIN32_FILE_ATTRIBUTE_DATA.cs</Link>
#    "
#    "s/Kernel32/Interop.WIN32_FIND_DATA.cs">
#    "op.WIN32_FIND_DATA.cs</Link>
#    "
#    "s/Kernel32/Interop.WriteFile_IntPtr.cs">
#    "op.WriteFile_IntPtr.cs</Link>
#    "
#    "s/Kernel32/Interop.WriteFile_SafeHandle_IntPtr.cs">
#    "op.WriteFile_SafeHandle_IntPtr.cs</Link>
#    "
#    "s/Kernel32/Interop.WriteFile_SafeHandle_NativeOverlapped.cs">
#    "op.WriteFile_SafeHandle_NativeOverlapped.cs</Link>
#    "
#    "s/NtDll/Interop.FILE_FULL_DIR_INFORMATION.cs">
#    "FILE_FULL_DIR_INFORMATION.cs</Link>
#    "
#    "s/NtDll/Interop.FILE_INFORMATION_CLASS.cs">
#    "FILE_INFORMATION_CLASS.cs</Link>
#    "
#    "s/NtDll/Interop.NtQueryDirectoryFile.cs">
#    "NtQueryDirectoryFile.cs</Link>
#    "
#    "s/NtDll/Interop.NtQueryInformationFile.cs">
#    "NtQueryInformationFile.cs</Link>
#    "
#    "s/NtDll/Interop.NtQuerySystemInformation.cs">
#    "NtQuerySystemInformation.cs</Link>
#    "
#    "s/NtDll/Interop.RtlGetVersion.cs">
#    "RtlGetVersion.cs</Link>
#    "
#    "s/NtDll/Interop.SYSTEM_LEAP_SECOND_INFORMATION.cs">
#    "SYSTEM_LEAP_SECOND_INFORMATION.cs</Link>
#    "
#    "s/OleAut32/Interop.SysAllocStringByteLen.cs">
#    "op.SysAllocStringByteLen.cs</Link>
#    "
#    "s/OleAut32/Interop.SysAllocStringLen.cs">
#    "op.SysAllocStringLen.cs</Link>
#    "
#    "s/OleAut32/Interop.SysFreeString.cs">
#    "op.SysFreeString.cs</Link>
#    "
#    "s/Ole32/Interop.CLSIDFromProgID.cs">
#    "CLSIDFromProgID.cs</Link>
#    "
#    "s/Ole32/Interop.CoCreateGuid.cs">
#    "CoCreateGuid.cs</Link>
#    "
#    "ws/Ole32/Interop.CoGetStandardMarshal.cs">
#    "CoGetStandardMarshal.cs</Link>
#    "
#    "s/Ole32/Interop.CoTaskMemAlloc.cs">
#    "CoTaskMemAlloc.cs</Link>
#    "
#    "s/Ole32/Interop.PropVariantClear.cs">
#    "PropVariantClear.cs</Link>
#    "
#    "s/Secur32/Interop.GetUserNameExW.cs">
#    "p.GetUserNameExW.cs</Link>
#    "
#    "s/Shell32/Interop.SHGetKnownFolderPath.cs">
#    "p.SHGetKnownFolderPath.cs</Link>
#    "
#    "s/Ucrtbase/Interop.MemAlloc.cs">
#    "op.MemAlloc.cs</Link>
#    "
#    "s/User32/Interop.Constants.cs">
#    ".Constants.cs</Link>
#    "
#    "s/User32/Interop.LoadString.cs">
#    ".LoadString.cs</Link>
#    "
#    "s/Interop.LongFileTime.cs">
#    "leTime</Link>
#    "
#    "s/User32/Interop.SendMessageTimeout.cs">
#    ".SendMessageTimeout.cs</Link>
#    "
#    "s/User32/Interop.GetProcessWindowStation.cs">
#    ".GetProcessWindowStation.cs</Link>
#    "
#    "s/User32/Interop.GetUserObjectInformation.cs">
#    ".GetUserObjectInformation.cs</Link>
#    "
#    "s/User32/Interop.USEROBJECTFLAGS.cs">
#    ".USEROBJECTFLAGS.cs</Link>
#    "
#    "s/Kernel32/Interop.GetModuleHandle.cs">
#    "op.GetModuleHandle.cs</Link>
#    "
#    "ws/Kernel32/Interop.GetCurrentProcessorNumberEx.cs">
#    "op.GetCurrentProcessorNumberEx.cs</Link>
#    "
#    "ws/Kernel32/Interop.Sleep.cs">
#    "op.Sleep.cs</Link>
#    "
#    "2/SafeHandles/SafeTokenHandle.cs">
#    "feTokenHandle.cs</Link>
#    "
#    "2/SafeHandles/SafeThreadHandle.cs">
#    "feThreadHandle.cs</Link>
#    "
#    "System.Attributes.Windows.cs">
#    "s.Windows.cs</Link>
#    "
#    "System.DirectoryCreation.Windows.cs">
#    "Creation.Windows.cs</Link>
#    "
#    "Internal.Windows.cs">
#    ".cs</Link>
#    "
#    "ng/AsyncOverSyncWithIoCancellation.cs">
#    "WithIoCancellation.cs</Link>
#    "
#    "Internal/Console.Windows.cs",
#    "Internal/Win32/RegistryKey.cs",
#    "Microsoft/Win32/SafeHandles/SafeFileHandle.Windows.cs",
#    "Microsoft/Win32/SafeHandles/SafeFileHandle.OverlappedValueTaskSource.Windows.cs",
#    "Microsoft/Win32/SafeHandles/SafeFindHandle.Windows.cs",
#    "Microsoft/Win32/SafeHandles/SafeRegistryHandle.cs",
#    "Microsoft/Win32/SafeHandles/SafeThreadPoolIOHandle.cs",
#    "System/AppDomain.Windows.cs",
#    "System/DateTime.Windows.cs",
#    "System/Environment.Win32.cs",
#    "System/Environment.Windows.cs",
#    "System/Diagnostics/DebugProvider.Windows.cs",
#    "System/Diagnostics/Stopwatch.Windows.cs",
#    "System/Diagnostics/Tracing/RuntimeEventSourceHelper.Windows.cs" Condition="'$(FeaturePerfTracing)' == 'true'",
#    "System/Globalization/CalendarData.Windows.cs",
#    "System/Globalization/CultureData.Windows.cs",
#    "System/Globalization/CultureInfo.Windows.cs",
#    "System/Globalization/GlobalizationMode.Windows.cs",
#    "System/Globalization/HijriCalendar.Win32.cs",
#    "System/Guid.Windows.cs",
#    "System/IO/Directory.Windows.cs",
#    "System/IO/DisableMediaInsertionPrompt.cs",
#    "System/IO/DriveInfoInternal.Windows.cs",
#    "System/IO/File.Windows.cs",
#    "System/IO/FileSystem.Windows.cs",
#    "System/IO/FileSystemInfo.Windows.cs",
#    "System/IO/Path.Windows.cs",
#    "System/IO/PathHelper.Windows.cs",
#    "System/IO/RandomAccess.Windows.cs",
#    "System/IO/Enumeration/FileSystemEntry.Windows.cs",
#    "System/IO/Enumeration/FileSystemEnumerator.Windows.cs",
#    "System/IO/Strategies/AsyncWindowsFileStreamStrategy.cs",
#    "System/IO/Strategies/FileStreamHelpers.Windows.cs",
#    "System/IO/Strategies/SyncWindowsFileStreamStrategy.cs",
#    "System/PasteArguments.Windows.cs",
#    "System/Runtime/Loader/LibraryNameVariation.Windows.cs",
#    "System/Runtime/MemoryFailPoint.Windows.cs",
#    "System/Runtime/InteropServices/Marshal.Windows.cs",
#    "System/Runtime/InteropServices/NativeMemory.Windows.cs",
#    "System/Runtime/InteropServices/PosixSignalRegistration.Windows.cs",
#    "System/Runtime/InteropServices/RuntimeInformation.Windows.cs",
#    "System/Runtime/InteropServices/StandardOleMarshalObject.Windows.cs",
#    "System/Security/SecureString.Windows.cs",
#    "System/Threading/LowLevelMonitor.Windows.cs",
#    "System/Threading/Mutex.Windows.cs",
#    "System/Threading/RegisteredWaitHandle.WindowsThreadPool.cs",
#    "System/Threading/Thread.Windows.cs",
#    "System/Threading/TimerQueue.Windows.cs",
#    "System/Threading/TimerQueue.WindowsThreadPool.cs",
#    "System/Threading/ThreadPool.Windows.cs",
#    "System/Threading/ThreadPoolBoundHandle.WindowsThreadPool.cs",
#    "System/Threading/PreAllocatedOverlapped.WindowsThreadPool.cs",
#    "System/Threading/WindowsThreadPool.cs",
#    "System/TimeZoneInfo.Win32.cs",
#  </"
#  <I"= 'true'">
#    "System/Runtime/InteropServices/ComWrappers.PlatformNotSupported.cs",
#  </"
#  <I" 'true'">
#    "System/Runtime/InteropServices/Marshal.NoCom.cs",
#    "System/Runtime/InteropServices/ComEventsHelpers.NoCom.cs",
#  </"
#  <!"ws API on Unix. This is bridge for that PAL layer. See issue dotnet/runtime/#31721. -->
#  <I"rue' or '$(FeatureCoreCLR)'=='true'">
#    "Microsoft/Win32/SafeHandles/SafeWaitHandle.Windows.cs",
#    "s/Kernel32/Interop.CloseHandle.cs">
#    "op.CloseHandle.cs</Link>
#    "
#    "s/Kernel32/Interop.Constants.cs">
#    "op.Constants.cs</Link>
#    "
#    "s/Kernel32/Interop.EventWaitHandle.cs">
#    "op.EventWaitHandle.cs</Link>
#    "
#    "s/Kernel32/Interop.GetEnvironmentVariable.cs">
#    "op.GetEnvironmentVariable.cs</Link>
#    "
#    "s/Kernel32/Interop.GetEnvironmentStrings.cs">
#    "op.GetEnvironmentStrings.cs</Link>
#    "
#    "s/Kernel32/Interop.FreeEnvironmentStrings.cs">
#    "op.FreeEnvironmentStrings.cs</Link>
#    "
#    "s/Kernel32/Interop.FormatMessage.cs">
#    "op.FormatMessage.cs</Link>
#    "
#    "s/Kernel32/Interop.Mutex.cs">
#    "op.Mutex.cs</Link>
#    "
#    "s/Kernel32/Interop.Semaphore.cs">
#    "op.Semaphore.cs</Link>
#    "
#    "s/Kernel32/Interop.SetEnvironmentVariable.cs">
#    "op.SetEnvironmentVariable.cs</Link>
#    "
#    "2Marshal.cs">
#    "k>
#    "
#    "FixedBufferExtensions.cs">
#    "sions.cs</Link>
#    "
#    "System/Environment.Variables.Windows.cs",
#    "System/Threading/Semaphore.Windows.cs",
#    "System/Threading/EventWaitHandle.Windows.cs",
#  </"
#  <I"' or '$(TargetsBrowser)' == 'true' or '$(TargetsWasi)' == 'true'">
#    "nterop.Errors.cs">
#    "</Link>
#    "
#    "nterop.IOErrors.cs">
#    "cs</Link>
#    "
#    "nterop.Libraries.cs">
#    ".cs</Link>
#    "
#    "nterop.DefaultPathBufferSize.cs">
#    "thBufferSize.cs</Link>
#    "
#    "ystem.Native/Interop.Access.cs">
#    "erop.Access.cs</Link>
#    "
#    "ystem.Native/Interop.ChDir.cs">
#    "erop.ChDir.cs</Link>
#    "
#    "ystem.Native/Interop.ChMod.cs">
#    "erop.ChMod.cs</Link>
#    "
#    "ystem.Native/Interop.FChMod.cs">
#    "erop.FChMod.cs</Link>
#    "
#    "ystem.Native/Interop.Close.cs">
#    "erop.Close.cs</Link>
#    "
#    "ystem.Native/Interop.CopyFile.cs">
#    "erop.CopyFile.cs</Link>
#    "
#    "ystem.Native/Interop.DynamicLoad.cs">
#    "erop.DynamicLoad.cs</Link>
#    "
#    "ystem.Native/Interop.ErrNo.cs">
#    "erop.ErrNo.cs</Link>
#    "
#    "ystem.Native/Interop.UnixFileSystemTypes.cs">
#    "erop.UnixFileSystemTypes.cs</Link>
#    "
#    "ystem.Native/Interop.FLock.cs">
#    "erop.FLock.cs</Link>
#    "
#    "ystem.Native/Interop.FSync.cs">
#    "erop.FSync.cs</Link>
#    "
#    "ystem.Native/Interop.FTruncate.cs">
#    "erop.FTruncate.cs</Link>
#    "
#    "ystem.Native/Interop.GetCpuUtilization.cs">
#    "erop.GetCpuUtilization.cs</Link>
#    "
#    "ystem.Native/Interop.GetCwd.cs">
#    "erop.GetCwd.cs</Link>
#    "
#    "ystem.Native/Interop.GetDefaultTimeZone.AnyMobile.cs" Condition="'$(TargetsAndroid)' == 'true' or '$(TargetsLinuxBionic)' == 'true' or '$(IsiOSLike)' == 'true'">
#    "erop.GetDefaultTimeZone.AnyMobile.cs</Link>
#    "
#    "ystem.Native/Interop.GetTimeZoneData.Wasm.cs" Condition="'$(TargetsWasi)' == 'true' or '$(TargetsBrowser)' == 'true'">
#    "/Interop.GetTimeZoneData.Wasm.cs</Link>
#    "
#    "System.Native/Interop.GetEnv.cs">
#    "erop.GetEnv.cs</Link>
#    "
#    "System.Native/Interop.GetEnviron.cs">
#    "erop.GetEnviron.cs</Link>
#    "
#    "ystem.Native/Interop.GetHostName.cs">
#    "erop.GetHostName.cs</Link>
#    "
#    "ystem.Native/Interop.GetOSArchitecture.cs">
#    "erop.GetOSArchitecture.cs</Link>
#    "
#    "ystem.Native/Interop.GetProcessPath.cs">
#    "erop.GetProcessPath.cs</Link>
#    "
#    "ystem.Native/Interop.GetRandomBytes.cs">
#    "erop.GetRandomBytes.cs</Link>
#    "
#    "ystem.Native/Interop.GetSystemTimeAsTicks.cs">
#    "erop.GetSystemTimeAsTicks.cs</Link>
#    "
#    "ystem.Native/Interop.GetTimestamp.cs">
#    "erop.GetTimestamp.cs</Link>
#    "
#    "ystem.Native/Interop.GetUnixVersion.cs">
#    "erop.GetUnixVersion.cs</Link>
#    "
#    "ystem.Native/Interop.GetUnixRelease.cs">
#    "erop.GetUnixRelease.cs</Link>
#    "
#    "ystem.Native/Interop.IOVector.cs">
#    "erop.IOVector.cs</Link>
#    "
#    "ystem.Native/Interop.LChflags.cs">
#    "erop.LChflags.cs</Link>
#    "
#    "ystem.Native/Interop.Link.cs">
#    "erop.Link.cs</Link>
#    "
#    "ystem.Native/Interop.LockFileRegion.cs">
#    "erop.LockFileRegion.cs</Link>
#    "
#    "ystem.Native/Interop.Log.cs">
#    "erop.Log.cs</Link>
#    "
#    "ystem.Native/Interop.LowLevelMonitor.cs">
#    "erop.LowLevelMonitor.cs</Link>
#    "
#    "ystem.Native/Interop.LSeek.cs">
#    "erop.LSeek.cs</Link>
#    "
#    "ystem.Native/Interop.MemAlloc.cs">
#    "erop.MemAlloc.cs</Link>
#    "
#    "ystem.Native/Interop.MkDir.cs">
#    "erop.MkDir.cs</Link>
#    "
#    "ystem.Native/Interop.MkdTemp.cs">
#    "erop.MkdTemp.cs</Link>
#    "
#    "ystem.Native/Interop.MksTemps.cs">
#    "erop.MksTemps.cs</Link>
#    "
#    "ystem.Native/Interop.MountPoints.cs">
#    "erop.MountPoints.cs</Link>
#    "
#    "ystem.Native/Interop.Open.cs">
#    "erop.Open.cs</Link>
#    "
#    "ystem.Native/Interop.OpenFlags.cs">
#    "erop.OpenFlags.cs</Link>
#    "
#    "ystem.Native/Interop.PathConf.cs">
#    "erop.PathConf.cs</Link>
#    "
#    "ystem.Native/Interop.PosixFAdvise.cs">
#    "erop.PosixFAdvise.cs</Link>
#    "
#    "ystem.Native/Interop.FAllocate.cs">
#    "erop.FAllocate.cs</Link>
#    "
#    "ystem.Native/Interop.PRead.cs">
#    "erop.PRead.cs</Link>
#    "
#    "ystem.Native/Interop.PReadV.cs">
#    "erop.PReadV.cs</Link>
#    "
#    "ystem.Native/Interop.PWrite.cs">
#    "erop.PWrite.cs</Link>
#    "
#    "ystem.Native/Interop.PWriteV.cs">
#    "erop.PWriteV.cs</Link>
#    "
#    "ystem.Native/Interop.Read.cs">
#    "erop.Read.cs</Link>
#    "
#    "ystem.Native/Interop.ReadDir.cs">
#    "erop.ReadDir.cs</Link>
#    "
#    "ystem.Native/Interop.ReadLink.cs">
#    "erop.ReadLink.cs</Link>
#    "
#    "ystem.Native/Interop.Rename.cs">
#    "erop.Rename.cs</Link>
#    "
#    "ystem.Native/Interop.RmDir.cs">
#    "erop.RmDir.cs</Link>
#    "
#    "System.Native/Interop.SchedGetCpu.cs">
#    "erop.SchedGetCpu.cs</Link>
#    "
#    "ystem.Native/Interop.Stat.cs">
#    "erop.Stat.cs</Link>
#    "
#    "ystem.Native/Interop.Stat.Span.cs">
#    "erop.Stat.Span.cs</Link>
#    "
#    "ystem.Native/Interop.SymLink.cs">
#    "erop.SymLink.cs</Link>
#    "
#    "ystem.Native/Interop.SysConf.cs">
#    "erop.SysConf.cs</Link>
#    "
#    "ystem.Native/Interop.SysLog.cs">
#    "erop.SysLog.cs</Link>
#    "
#    "ystem.Native/Interop.Threading.cs">
#    "erop.Threading.cs</Link>
#    "
#    "ystem.Native/Interop.Unlink.cs">
#    "erop.Unlink.cs</Link>
#    "
#    "ystem.Native/Interop.UTimensat.cs">
#    "erop.UTimensat.cs</Link>
#    "
#    "ystem.Native/Interop.Write.cs">
#    "erop.Write.cs</Link>
#    "
#    "lueUtf8Converter.cs">
#    ".cs</Link>
#    "
#    "Internal.Unix.cs">
#    "</Link>
#    "
#    "Internal/Console.Unix.cs" Condition="'$(TargetsAndroid)' != 'true' and '$(IsiOSLike)' != 'true'",
#    "Internal/Console.Android.cs" Condition="'$(TargetsAndroid)' == 'true'",
#    "Internal/Console.iOS.cs" Condition="'$(IsiOSLike)' == 'true'",
#    "d/Interop.Logcat.cs" Condition="'$(TargetsAndroid)' == 'true' or '$(TargetsLinuxBionic)' == 'true'">
#    ".cs</Link>
#    "
#    "Interop.Libraries.cs" Condition="'$(TargetsLinuxBionic)' == 'true'">
#    "s.cs</Link>
#    "
#    "d/Interop.Libraries.cs" Condition="'$(TargetsAndroid)' == 'true'">
#    "ies.cs</Link>
#    "
#    "Microsoft/Win32/SafeHandles/SafeFileHandle.ThreadPoolValueTaskSource.cs",
#    "Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs",
#    "System/AppDomain.Unix.cs",
#    "System/DateTime.Unix.cs",
#    "System/Diagnostics/DebugProvider.Unix.cs",
#    "System/Diagnostics/Stopwatch.Unix.cs",
#    "System/Diagnostics/Tracing/RuntimeEventSourceHelper.Unix.cs" Condition="'$(FeaturePerfTracing)' == 'true'",
#    "System/Environment.Android.cs" Condition="'$(TargetsAndroid)' == 'true' or '$(TargetsLinuxBionic)' == 'true'",
#    "System/Environment.NoRegistry.cs",
#    "System/Environment.UnixOrBrowser.cs",
#    "System/Environment.OSVersion.OSX.cs" Condition="'$(IsApplePlatform)' == 'true' AND '$(TargetsMacCatalyst)' != 'true'",
#    "System/Environment.OSVersion.MacCatalyst.cs" Condition="'$(TargetsMacCatalyst)' == 'true'",
#    "System/Environment.GetFolderPathCore.Unix.cs" Condition="'$(IsiOSLike)' != 'true' and '$(TargetsAndroid)' != 'true' and '$(TargetsLinuxBionic)' != 'true'",
#    "System/Environment.Variables.Unix.cs" Condition="'$(FeatureCoreCLR)' != 'true'",
#    "System/Globalization/CalendarData.Unix.cs",
#    "System/Globalization/CultureData.Unix.cs",
#    "System/Globalization/CultureInfo.Unix.cs",
#    "System/Globalization/GlobalizationMode.Unix.cs",
#    "System/Globalization/GlobalizationMode.LoadICU.Unix.cs" Condition="'$(IsiOSLike)' != 'true'",
#    "System/Globalization/GlobalizationMode.LoadICU.iOS.cs" Condition="'$(IsiOSLike)' == 'true'",
#    "System/Globalization/HijriCalendar.Unix.cs",
#    "System/Guid.Unix.cs",
#    "System/IO/Directory.Unix.cs",
#    "System/IO/File.Unix.cs",
#    "System/IO/FileStatus.Unix.cs",
#    "System/IO/FileSystem.Unix.cs",
#    "System/IO/FileSystem.Exists.Unix.cs",
#    "System/IO/FileSystemInfo.Unix.cs",
#    "System/IO/Path.Unix.cs",
#    " Include="$(MSBuildThisFileDirectory)System/IO/Path.Unix.iOS.cs",
#    " Include="$(MSBuildThisFileDirectory)System/IO/Path.Unix.NoniOS.cs",
#    "System/IO/PersistedFiles.Names.Unix.cs",
#    "System/IO/RandomAccess.Unix.cs",
#    "System/IO/Enumeration/FileSystemEntry.Unix.cs",
#    "System/IO/Enumeration/FileSystemEnumerator.Unix.cs",
#    "System/IO/Strategies/FileStreamHelpers.Unix.cs",
#    "System/IO/Strategies/UnixFileStreamStrategy.cs",
#    "System/PasteArguments.Unix.cs",
#    "System/Runtime/Loader/LibraryNameVariation.Unix.cs",
#    "System/Runtime/MemoryFailPoint.Unix.cs",
#    "System/Runtime/InteropServices/Marshal.Unix.cs",
#    "System/Runtime/InteropServices/NativeMemory.Unix.cs",
#    "System/Runtime/InteropServices/RuntimeInformation.Unix.cs" Condition="'$(TargetsBrowser)' != 'true' and '$(TargetsWasi)' != 'true'",
#    "System/Runtime/InteropServices/StandardOleMarshalObject.Unix.cs",
#    "System/Security/SecureString.Unix.cs",
#    "System/Threading/LowLevelMonitor.Unix.cs",
#    "System/Threading/Thread.Unix.cs",
#    "System/Threading/TimerQueue.Unix.cs" Condition="'$(FeaturePortableTimer)' == 'true'",
#    "System/TimeZoneInfo.Unix.cs",
#    "System/TimeZoneInfo.Unix.Android.cs" Condition="'$(TargetsAndroid)' == 'true' or '$(TargetsLinuxBionic)' == 'true'",
#    "System/TimeZoneInfo.Unix.NonAndroid.cs" Condition="'$(TargetsAndroid)' != 'true' and '$(TargetsLinuxBionic)' != 'true'",
#  </"
#  <I"e' or '$(TargetsBrowser)' == 'true' or '$(TargetsWasi)' == 'true') and '$(IsApplePlatform)' != 'true'">
#    "System/IO/FileStatus.SetTimes.OtherUnix.cs",
#  </"
#  <I"'">
#    "ystem.Native/Interop.GetEUid.cs" Link="Common/Interop/Unix/Interop.GetEUid.cs",
#    "ystem.Native/Interop.IsMemberOfGroup.cs" Link="Common/Interop/Unix/Interop.IsMemberOfGroup.cs",
#    "ystem.Native/Interop.GetPwUid.cs">
#    "erop.GetPwUid.cs</Link>
#    "
#    "System.Native/Interop.GetPid.cs">
#    "erop.GetPid.cs</Link>
#    "
#    "D/Interop.Process.GetProcInfo.cs" Condition="'$(TargetsFreeBSD)' == 'true'" Link="Common/Interop/FreeBSD/Interop.Process.GetProcInfo.cs",
#    "stem.Native/Interop.Sysctl.cs" Condition="'$(TargetsFreeBSD)' == 'true'" Link="Common/Interop/BSD/System.Native/Interop.Sysctl.cs",
#    "procfs/Interop.ProcFsStat.TryReadStatusFile.cs" Condition="'$(TargetsLinux)' == 'true'" Link="Common/Interop/Linux/Interop.ProcFsStat.TryReadStatusFile.cs",
#    "ngParser.cs" Condition="'$(TargetsLinux)' == 'true'" Link="Common/System/IO/StringParser.cs",
#    "terop.libproc.GetProcessInfoById.cs" Condition="'$(TargetsOSX)' == 'true' or '$(TargetsMacCatalyst)' == 'true'" Link="Common/Interop/OSX/Interop.libproc.GetProcessInfoById.cs",
#    "procfs/Interop.ProcFsStat.TryReadProcessStatusInfo.cs" Condition="'$(Targetsillumos)' == 'true' or '$(TargetsSolaris)' == 'true'" Link="Common/Interop/SunOS/Interop.ProcFsStat.TryReadProcessStatusInfo.cs",
#    "os-release/Interop.OSReleaseFile.cs" Condition="'$(TargetsBrowser)' != 'true' and '$(TargetsWasi)' != 'true'",
#    "System/Environment.Unix.cs",
#    "System/Environment.FreeBSD.cs" Condition="'$(TargetsFreeBSD)' == 'true'",
#    "System/Environment.Linux.cs" Condition="'$(TargetsLinux)' == 'true'",
#    "System/Environment.OSX.cs" Condition="'$(TargetsOSX)' == 'true' or '$(TargetsMacCatalyst)' == 'true'",
#    "System/Environment.iOS.cs" Condition="'$(IsiOSLike)' == 'true'",
#    "System/Environment.OSVersion.Unix.cs" Condition="'$(IsApplePlatform)' != 'true'",
#    "System/Environment.SunOS.cs" Condition="'$(Targetsillumos)' == 'true' or '$(TargetsSolaris)' == 'true'",
#    "System/TimeZoneInfo.FullGlobalizationData.Unix.cs" Condition="'$(UseMinimalGlobalizationData)' != 'true'",
#    "System/IO/DriveInfoInternal.Unix.cs",
#    "System/IO/PersistedFiles.Unix.cs",
#  </"
#  <I"' and '$(IsMobileLike)' != 'true'">
#    "ystem.Native/Interop.InitializeTerminalAndSignalHandling.cs" Link="Common/Interop/Unix/Interop.InitializeTerminalAndSignalHandling.cs",
#    "ystem.Native/Interop.PosixSignal.cs" Link="Common/Interop/Unix/Interop.PosixSignal.cs",
#    "System/Runtime/InteropServices/PosixSignalRegistration.Unix.cs",
#  </"
#  <I"rue' or '$(TargetsWasi)' == 'true'">
#    "System/AppContext.Browser.cs",
#    "System/Environment.Browser.cs",
#    "System/IO/DriveInfoInternal.Browser.cs",
#    "System/IO/PersistedFiles.Browser.cs",
#    "System/Runtime/InteropServices/RuntimeInformation.Browser.cs",
#  </"
#  <I"nData)' == 'true'">
#    "System/TimeZoneInfo.MinimalGlobalizationData.cs",
#  </"
#  <I"true'">
#    "terop.libobjc.cs">
#    "</Link>
#    "
#    "terop.Libraries.cs">
#    "cs</Link>
#    "
#    "terop.libc.cs">
#    "ink>
#    "
#    "System/IO/FileStatus.SetTimes.OSX.cs",
#    "System/IO/FileSystem.TryCloneFile.OSX.cs",
#  </"
#  <I"true'">
#    "stem.Native/Interop.SearchPath.cs">
#    ".cs</Link>
#    "
#    "stem.Native/Interop.SearchPathTempDirectory.cs">
#    "TempDirectory.cs</Link>
#    "
#  </"
#  <I"= 'true'">
#    "stem.Native/Interop.iOSSupportVersion.cs" Link="Common/Interop/OSX/System.Native/Interop.iOSSupportVersion.cs",
#  </"
#  <I"' == 'true'">
#    "System/Runtime/Intrinsics/X86/Aes.cs",
#    "System/Runtime/Intrinsics/X86/Avx.cs" Condition="'$(FeatureMono)' != 'true'",
#    "System/Runtime/Intrinsics/X86/Avx.PlatformNotSupported.cs" Condition="'$(FeatureMono)' == 'true'",
#    "System/Runtime/Intrinsics/X86/Avx2.cs" Condition="'$(FeatureMono)' != 'true'",
#    "System/Runtime/Intrinsics/X86/Avx2.PlatformNotSupported.cs" Condition="'$(FeatureMono)' == 'true'",
#    "System/Runtime/Intrinsics/X86/Avx10v1.cs" Condition="'$(FeatureMono)' != 'true'",
#    "System/Runtime/Intrinsics/X86/Avx10v1.PlatformNotSupported.cs" Condition="'$(FeatureMono)' == 'true'",
#    "System/Runtime/Intrinsics/X86/Avx512BW.cs" Condition="'$(FeatureMono)' != 'true'",
#    "System/Runtime/Intrinsics/X86/Avx512BW.PlatformNotSupported.cs" Condition="'$(FeatureMono)' == 'true'",
#    "System/Runtime/Intrinsics/X86/Avx512CD.cs" Condition="'$(FeatureMono)' != 'true'",
#    "System/Runtime/Intrinsics/X86/Avx512CD.PlatformNotSupported.cs" Condition="'$(FeatureMono)' == 'true'",
#    "System/Runtime/Intrinsics/X86/Avx512DQ.cs" Condition="'$(FeatureMono)' != 'true'",
#    "System/Runtime/Intrinsics/X86/Avx512DQ.PlatformNotSupported.cs" Condition="'$(FeatureMono)' == 'true'",
#    "System/Runtime/Intrinsics/X86/Avx512F.cs" Condition="'$(FeatureMono)' != 'true'",
#    "System/Runtime/Intrinsics/X86/Avx512F.PlatformNotSupported.cs" Condition="'$(FeatureMono)' == 'true'",
#    "System/Runtime/Intrinsics/X86/Avx512Vbmi.cs" Condition="'$(FeatureMono)' != 'true'",
#    "System/Runtime/Intrinsics/X86/Avx512Vbmi.PlatformNotSupported.cs" Condition="'$(FeatureMono)' == 'true'",
#    "System/Runtime/Intrinsics/X86/AvxVnni.cs" Condition="'$(FeatureMono)' != 'true'",
#    "System/Runtime/Intrinsics/X86/AvxVnni.PlatformNotSupported.cs" Condition="'$(FeatureMono)' == 'true'",
#    "System/Runtime/Intrinsics/X86/Bmi1.cs",
#    "System/Runtime/Intrinsics/X86/Bmi2.cs",
#    "System/Runtime/Intrinsics/X86/Fma.cs" Condition="'$(FeatureMono)' != 'true'",
#    "System/Runtime/Intrinsics/X86/Fma.PlatformNotSupported.cs" Condition="'$(FeatureMono)' == 'true'",
#    "System/Runtime/Intrinsics/X86/Lzcnt.cs",
#    "System/Runtime/Intrinsics/X86/Pclmulqdq.cs",
#    "System/Runtime/Intrinsics/X86/Popcnt.cs",
#    "System/Runtime/Intrinsics/X86/Sse.cs",
#    "System/Runtime/Intrinsics/X86/Sse2.cs",
#    "System/Runtime/Intrinsics/X86/Sse3.cs",
#    "System/Runtime/Intrinsics/X86/Sse41.cs",
#    "System/Runtime/Intrinsics/X86/Sse42.cs",
#    "System/Runtime/Intrinsics/X86/Ssse3.cs",
#    "System/Runtime/Intrinsics/X86/X86Base.cs",
#    "System/Runtime/Intrinsics/X86/X86Serialize.cs" Condition="'$(FeatureMono)' != 'true'",
#    "System/Runtime/Intrinsics/X86/X86Serialize.PlatformNotSupported.cs" Condition="'$(FeatureMono)' == 'true'",
#  </"
#  <I"' != 'true'">
#    "System/Runtime/Intrinsics/X86/Aes.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Avx.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Avx2.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Avx512BW.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Avx512CD.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Avx512DQ.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Avx512F.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Avx10v1.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Avx512Vbmi.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/AvxVnni.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Bmi1.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Bmi2.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Fma.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Lzcnt.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Pclmulqdq.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Popcnt.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Sse.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Sse2.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Sse3.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Sse41.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Sse42.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/Ssse3.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/X86Base.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/X86/X86Serialize.PlatformNotSupported.cs",
#  </"
#  <I"' == 'true'">
#    "System/Runtime/Intrinsics/Arm/AdvSimd.cs",
#    "System/Runtime/Intrinsics/Arm/Aes.cs",
#    "System/Runtime/Intrinsics/Arm/ArmBase.cs",
#    "System/Runtime/Intrinsics/Arm/Crc32.cs",
#    "System/Runtime/Intrinsics/Arm/Dp.cs",
#    "System/Runtime/Intrinsics/Arm/Rdm.cs",
#    "System/Runtime/Intrinsics/Arm/Sha1.cs",
#    "System/Runtime/Intrinsics/Arm/Sha256.cs",
#    "System/Runtime/Intrinsics/Arm/Sve.cs",
#  </"
#  <I"' != 'true'">
#    "System/Runtime/Intrinsics/Arm/AdvSimd.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/Arm/Aes.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/Arm/ArmBase.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/Arm/Crc32.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/Arm/Dp.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/Arm/Rdm.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/Arm/Sha1.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/Arm/Sha256.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/Arm/Sve.PlatformNotSupported.cs",
#  </"
#  <I")' == 'true'">
#    "System/Runtime/Intrinsics/Wasm/WasmBase.cs",
#    "System/Runtime/Intrinsics/Wasm/PackedSimd.cs",
#  </"
#  <I")' != 'true'">
#    "System/Runtime/Intrinsics/Wasm/WasmBase.PlatformNotSupported.cs",
#    "System/Runtime/Intrinsics/Wasm/PackedSimd.PlatformNotSupported.cs",
#  </"
#  <I"ool)' == 'true'">
#    "System/Threading/CompleteWaitThreadPoolWorkItem.cs",
#    "System/Threading/ThreadPool.Portable.Windows.cs" Condition="'$(TargetsWindows)' == 'true'",
#    "System/Threading/ThreadPool.Unix.cs" Condition="'$(TargetsUnix)' == 'true' or '$(TargetsBrowser)' == 'true' or '$(TargetsWasi)' == 'true'",
#    "System/Threading/PortableThreadPool.cs",
#    "System/Threading/PortableThreadPool.Blocking.cs",
#    "System/Threading/PortableThreadPool.GateThread.cs",
#    "System/Threading/PortableThreadPool.HillClimbing.cs",
#    "System/Threading/PortableThreadPool.HillClimbing.Complex.cs",
#    "System/Threading/PortableThreadPool.IO.Windows.cs" Condition="'$(TargetsWindows)' == 'true'",
#    "System/Threading/PortableThreadPool.ThreadCounts.cs",
#    "System/Threading/PortableThreadPool.WaitThread.cs",
#    "System/Threading/PortableThreadPool.WorkerThread.cs",
#    "System/Threading/PortableThreadPool.WorkerTracking.cs",
#    "System/Threading/PortableThreadPool.Unix.cs" Condition="'$(TargetsUnix)' == 'true' or ('$(TargetsBrowser)' == 'true' and '$(FeatureWasmManagedThreads)' != 'true') or '$(TargetsWasi)' == 'true'",
#    "System/Threading/PortableThreadPool.Windows.cs" Condition="'$(TargetsWindows)' == 'true'",
#    "System/Threading/LowLevelLifoSemaphore.cs",
#    "System/Threading/LowLevelLifoSemaphore.Windows.cs" Condition="'$(TargetsWindows)' == 'true'",
#    "System/Threading/PreAllocatedOverlapped.Windows.cs" Condition="'$(TargetsWindows)' == 'true'",
#    "System/Threading/PreAllocatedOverlapped.Unix.cs" Condition="'$(TargetsUnix)' == 'true' or '$(TargetsBrowser)' == 'true' or '$(TargetsWasi)' == 'true'",
#    "System/Threading/PreAllocatedOverlapped.Portable.cs" Condition="('$(TargetsBrowser)' != 'true' and '$(TargetsWasi)' != 'true') or '$(FeatureWasmManagedThreads)' == 'true'",
#    "System/Threading/RegisteredWaitHandle.Windows.cs" Condition="'$(TargetsWindows)' == 'true'",
#    "System/Threading/RegisteredWaitHandle.Unix.cs" Condition="'$(TargetsWindows)' != 'true'",
#    "System/Threading/RegisteredWaitHandle.Portable.cs",
#    "System/Threading/ThreadPoolBoundHandle.Portable.cs",
#    "System/Threading/ThreadPoolBoundHandle.Unix.cs" Condition="'$(TargetsWindows)' != 'true'",
#    "System/Threading/ThreadPoolBoundHandle.Windows.cs" Condition="'$(TargetsWindows)' == 'true'",
#    "System/Threading/ThreadPoolBoundHandleOverlapped.cs",
#    "System/Threading/ThreadPoolCallbackWrapper.cs" Condition="'$(FeatureNativeAot)' != 'true'",
#  </"
#  <I"= 'true'">
#    "System/Runtime/InteropServices/ObjectiveC/ObjectiveCMarshal.cs",
#    "System/Runtime/InteropServices/ObjectiveC/ObjectiveCTrackedTypeAttribute.cs",
#    "stem.Native/Interop.AutoreleasePool.cs" Link="Common/Interop/OSX/System.Native/Interop.AutoreleasePool.cs",
#    "System/Threading/AutoreleasePool.cs",
#    "System/Threading/ThreadPoolWorkQueue.AutoreleasePool.OSX.cs",
#  </"
#  <I"= 'true'">
#    "System/Runtime/InteropServices/ObjectiveC/ObjectiveCMarshal.PlatformNotSupported.cs",
#    "System/Runtime/InteropServices/ObjectiveC/ObjectiveCTrackedTypeAttribute.cs",
#  </"
#  <I"rue' and ('$(TargetsUnix)' == 'true' or '$(TargetsBrowser)' == 'true' or '$(TargetsWasi)' == 'true')">
#    "Microsoft/Win32/SafeHandles/SafeWaitHandle.Unix.cs",
#    "System/Threading/EventWaitHandle.Unix.cs",
#    "System/Threading/Mutex.Unix.cs",
#    "System/Threading/Semaphore.Unix.cs",
#    "System/Threading/WaitHandle.Unix.cs",
#    "System/Threading/WaitSubsystem.HandleManager.Unix.cs",
#    "System/Threading/WaitSubsystem.ThreadWaitInfo.Unix.cs",
#    "System/Threading/WaitSubsystem.Unix.cs",
#    "System/Threading/WaitSubsystem.WaitableObject.Unix.cs",
#  </"
#  <I"rue' and '$(TargetsWindows)' == 'true'">
#    "System/Threading/WaitHandle.Windows.cs",
#  </"
#  <I"
    "System/IParsable.cs",
    "System/ISpanParsable.cs",
    "System/Numerics/IAdditionOperators.cs",
    "System/Numerics/IAdditiveIdentity.cs",
    "System/Numerics/IBinaryFloatingPointIeee754.cs",
    "System/Numerics/IBinaryInteger.cs",
    "System/Numerics/IBinaryNumber.cs",
    "System/Numerics/IBitwiseOperators.cs",
    "System/Numerics/IComparisonOperators.cs",
    "System/Numerics/IDecrementOperators.cs",
    "System/Numerics/IDivisionOperators.cs",
    "System/Numerics/IEqualityOperators.cs",
    "System/Numerics/IExponentialFunctions.cs",
    "System/Numerics/IFloatingPoint.cs",
    "System/Numerics/IFloatingPointConstants.cs",
    "System/Numerics/IFloatingPointIeee754.cs",
    "System/Numerics/IHyperbolicFunctions.cs",
    "System/Numerics/IIncrementOperators.cs",
    "System/Numerics/ILogarithmicFunctions.cs",
    "System/Numerics/IMinMaxValue.cs",
    "System/Numerics/IModulusOperators.cs",
    "System/Numerics/IMultiplicativeIdentity.cs",
    "System/Numerics/IMultiplyOperators.cs",
    "System/Numerics/INumber.cs",
    "System/Numerics/INumberBase.cs",
    "System/Numerics/IPowerFunctions.cs",
    "System/Numerics/IRootFunctions.cs",
    "System/Numerics/IShiftOperators.cs",
    "System/Numerics/ISignedNumber.cs",
    "System/Numerics/ISubtractionOperators.cs",
    "System/Numerics/ITrigonometricFunctions.cs",
    "System/Numerics/IUnaryNegationOperators.cs",
    "System/Numerics/IUnaryPlusOperators.cs",
    "System/Numerics/IUnsignedNumber.cs",
]