// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// ------------------------------------------------------------------------------
// Changes to this file must follow the https://aka.ms/api-review process.
// ------------------------------------------------------------------------------

namespace System.Diagnostics.Tracing
{
    [System.FlagsAttribute]
    public enum EventActivityOptions
    {
        None = 0,
        Disable = 2,
        Recursive = 4,
        Detachable = 8,
    }
    [System.AttributeUsageAttribute(System.AttributeTargets.Method)]
    public sealed partial class EventAttribute : System.Attribute
    {
        public EventAttribute(int eventId) { }
        public System.Diagnostics.Tracing.EventActivityOptions ActivityOptions { get { throw null; } set { } }
        public System.Diagnostics.Tracing.EventChannel Channel { get { throw null; } set { } }
        public int EventId { get { throw null; } }
        public System.Diagnostics.Tracing.EventKeywords Keywords { get { throw null; } set { } }
        public System.Diagnostics.Tracing.EventLevel Level { get { throw null; } set { } }
        public string? Message { get { throw null; } set { } }
        public System.Diagnostics.Tracing.EventOpcode Opcode { get { throw null; } set { } }
        public System.Diagnostics.Tracing.EventTags Tags { get { throw null; } set { } }
        public System.Diagnostics.Tracing.EventTask Task { get { throw null; } set { } }
        public byte Version { get { throw null; } set { } }
    }
    public enum EventChannel : byte
    {
        None = (byte)0,
        Admin = (byte)16,
        Operational = (byte)17,
        Analytic = (byte)18,
        Debug = (byte)19,
    }
    public enum EventCommand
    {
        Disable = -3,
        Enable = -2,
        SendManifest = -1,
        Update = 0,
    }
    public partial class EventCommandEventArgs : System.EventArgs
    {
        internal EventCommandEventArgs() { }
        public System.Collections.Generic.IDictionary<string, string?>? Arguments { get { throw null; } }
        public System.Diagnostics.Tracing.EventCommand Command { get { throw null; } }
        public bool DisableEvent(int eventId) { throw null; }
        public bool EnableEvent(int eventId) { throw null; }
    }
    [System.AttributeUsageAttribute(System.AttributeTargets.Class | System.AttributeTargets.Struct, Inherited=false)]
    public partial class EventDataAttribute : System.Attribute
    {
        public EventDataAttribute() { }
        public string? Name { get { throw null; } set { } }
    }
    [System.AttributeUsageAttribute(System.AttributeTargets.Property)]
    public partial class EventFieldAttribute : System.Attribute
    {
        public EventFieldAttribute() { }
        public System.Diagnostics.Tracing.EventFieldFormat Format { get { throw null; } set { } }
        public System.Diagnostics.Tracing.EventFieldTags Tags { get { throw null; } set { } }
    }
    public enum EventFieldFormat
    {
        Default = 0,
        String = 2,
        Boolean = 3,
        Hexadecimal = 4,
        Xml = 11,
        Json = 12,
        HResult = 15,
    }
    [System.FlagsAttribute]
    public enum EventFieldTags
    {
        None = 0,
    }
    [System.AttributeUsageAttribute(System.AttributeTargets.Property)]
    public partial class EventIgnoreAttribute : System.Attribute
    {
        public EventIgnoreAttribute() { }
    }
    [System.FlagsAttribute]
    public enum EventKeywords : long
    {
        All = (long)-1,
        None = (long)0,
        MicrosoftTelemetry = (long)562949953421312,
        WdiContext = (long)562949953421312,
        WdiDiagnostic = (long)1125899906842624,
        Sqm = (long)2251799813685248,
        AuditFailure = (long)4503599627370496,
        CorrelationHint = (long)4503599627370496,
        AuditSuccess = (long)9007199254740992,
        EventLogClassic = (long)36028797018963968,
    }
    public enum EventLevel
    {
        LogAlways = 0,
        Critical = 1,
        Error = 2,
        Warning = 3,
        Informational = 4,
        Verbose = 5,
    }
    public abstract partial class EventListener : System.IDisposable
    {
        protected EventListener() { }
        public event System.EventHandler<System.Diagnostics.Tracing.EventSourceCreatedEventArgs>? EventSourceCreated { add { } remove { } }
        public event System.EventHandler<System.Diagnostics.Tracing.EventWrittenEventArgs>? EventWritten { add { } remove { } }
        public void DisableEvents(System.Diagnostics.Tracing.EventSource eventSource) { }
        public virtual void Dispose() { }
        public void EnableEvents(System.Diagnostics.Tracing.EventSource eventSource, System.Diagnostics.Tracing.EventLevel level) { }
        public void EnableEvents(System.Diagnostics.Tracing.EventSource eventSource, System.Diagnostics.Tracing.EventLevel level, System.Diagnostics.Tracing.EventKeywords matchAnyKeyword) { }
        public void EnableEvents(System.Diagnostics.Tracing.EventSource eventSource, System.Diagnostics.Tracing.EventLevel level, System.Diagnostics.Tracing.EventKeywords matchAnyKeyword, System.Collections.Generic.IDictionary<string, string?>? arguments) { }
        protected static int EventSourceIndex(System.Diagnostics.Tracing.EventSource eventSource) { throw null; }
        protected internal virtual void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource eventSource) { }
        protected internal virtual void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs eventData) { }
    }
    [System.FlagsAttribute]
    public enum EventManifestOptions
    {
        None = 0,
        Strict = 1,
        AllCultures = 2,
        OnlyIfNeededForRegistration = 4,
        AllowEventSourceOverride = 8,
    }
    public enum EventOpcode
    {
        Info = 0,
        Start = 1,
        Stop = 2,
        DataCollectionStart = 3,
        DataCollectionStop = 4,
        Extension = 5,
        Reply = 6,
        Resume = 7,
        Suspend = 8,
        Send = 9,
        Receive = 240,
    }
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicMethods | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicNestedTypes)]
    public partial class EventSource : System.IDisposable
    {
        protected EventSource() { }
        protected EventSource(bool throwOnEventWriteErrors) { }
        protected EventSource(System.Diagnostics.Tracing.EventSourceSettings settings) { }
        protected EventSource(System.Diagnostics.Tracing.EventSourceSettings settings, params string[]? traits) { }
        public EventSource(string eventSourceName) { }
        public EventSource(string eventSourceName, System.Diagnostics.Tracing.EventSourceSettings config) { }
        public EventSource(string eventSourceName, System.Diagnostics.Tracing.EventSourceSettings config, params string[]? traits) { }
        public EventSource(string eventSourceName, System.Guid eventSourceGuid) { }
        public EventSource(string eventSourceName, System.Guid eventSourceGuid, System.Diagnostics.Tracing.EventSourceSettings settings, string[]? traits = null) { }
        public System.Exception? ConstructionException { get { throw null; } }
        public static System.Guid CurrentThreadActivityId { get { throw null; } }
        public System.Guid Guid { get { throw null; } }
        public string Name { get { throw null; } }
        public System.Diagnostics.Tracing.EventSourceSettings Settings { get { throw null; } }
        public event System.EventHandler<System.Diagnostics.Tracing.EventCommandEventArgs>? EventCommandExecuted { add { } remove { } }
        public void Dispose() { }
        protected virtual void Dispose(bool disposing) { }
        ~EventSource() { }
        public static string? GenerateManifest([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicMethods | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicNestedTypes)] System.Type eventSourceType, string? assemblyPathToIncludeInManifest) { throw null; }
        public static string? GenerateManifest([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicMethods | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicNestedTypes)] System.Type eventSourceType, string? assemblyPathToIncludeInManifest, System.Diagnostics.Tracing.EventManifestOptions flags) { throw null; }
        public static System.Guid GetGuid(System.Type eventSourceType) { throw null; }
        public static string GetName(System.Type eventSourceType) { throw null; }
        public static System.Collections.Generic.IEnumerable<System.Diagnostics.Tracing.EventSource> GetSources() { throw null; }
        public string? GetTrait(string key) { throw null; }
        public bool IsEnabled() { throw null; }
        public bool IsEnabled(System.Diagnostics.Tracing.EventLevel level, System.Diagnostics.Tracing.EventKeywords keywords) { throw null; }
        public bool IsEnabled(System.Diagnostics.Tracing.EventLevel level, System.Diagnostics.Tracing.EventKeywords keywords, System.Diagnostics.Tracing.EventChannel channel) { throw null; }
        protected virtual void OnEventCommand(System.Diagnostics.Tracing.EventCommandEventArgs command) { }
        public static void SendCommand(System.Diagnostics.Tracing.EventSource eventSource, System.Diagnostics.Tracing.EventCommand command, System.Collections.Generic.IDictionary<string, string?>? commandArguments) { }
        public static void SetCurrentThreadActivityId(System.Guid activityId) { }
        public static void SetCurrentThreadActivityId(System.Guid activityId, out System.Guid oldActivityThatWillContinue) { throw null; }
        public override string ToString() { throw null; }
        public void Write(string? eventName) { }
        public void Write(string? eventName, System.Diagnostics.Tracing.EventSourceOptions options) { }
        protected void WriteEvent(int eventId) { }
        protected void WriteEvent(int eventId, byte[]? arg1) { }
        protected void WriteEvent(int eventId, int arg1) { }
        protected void WriteEvent(int eventId, int arg1, int arg2) { }
        protected void WriteEvent(int eventId, int arg1, int arg2, int arg3) { }
        protected void WriteEvent(int eventId, int arg1, string? arg2) { }
        protected void WriteEvent(int eventId, long arg1) { }
        protected void WriteEvent(int eventId, long arg1, byte[]? arg2) { }
        protected void WriteEvent(int eventId, long arg1, long arg2) { }
        protected void WriteEvent(int eventId, long arg1, long arg2, long arg3) { }
        protected void WriteEvent(int eventId, long arg1, string? arg2) { }
        protected void WriteEvent(int eventId, params EventSourcePrimitive[] args) { }
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("EventSource will serialize the whole object graph. Trimmer will not safely handle this case because properties may be trimmed. This can be suppressed if the object is a primitive type")]
        protected void WriteEvent(int eventId, params object?[] args) { }
        protected void WriteEvent(int eventId, string? arg1) { }
        protected void WriteEvent(int eventId, string? arg1, int arg2) { }
        protected void WriteEvent(int eventId, string? arg1, int arg2, int arg3) { }
        protected void WriteEvent(int eventId, string? arg1, long arg2) { }
        protected void WriteEvent(int eventId, string? arg1, string? arg2) { }
        protected void WriteEvent(int eventId, string? arg1, string? arg2, string? arg3) { }
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("EventSource will serialize the whole object graph. Trimmer will not safely handle this case because properties may be trimmed. This can be suppressed if the object is a primitive type")]
        [System.CLSCompliantAttribute(false)]
        protected unsafe void WriteEventCore(int eventId, int eventDataCount, System.Diagnostics.Tracing.EventSource.EventData* data) { }
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("EventSource will serialize the whole object graph. Trimmer will not safely handle this case because properties may be trimmed. This can be suppressed if the object is a primitive type")]
        protected void WriteEventWithRelatedActivityId(int eventId, System.Guid relatedActivityId, params object?[] args) { }
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("EventSource will serialize the whole object graph. Trimmer will not safely handle this case because properties may be trimmed. This can be suppressed if the object is a primitive type")]
        [System.CLSCompliantAttribute(false)]
        protected unsafe void WriteEventWithRelatedActivityIdCore(int eventId, System.Guid* relatedActivityId, int eventDataCount, System.Diagnostics.Tracing.EventSource.EventData* data) { }
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("EventSource will serialize the whole object graph. Trimmer will not safely handle this case because properties may be trimmed. This can be suppressed if the object is a primitive type")]
        public void Write<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(string? eventName, System.Diagnostics.Tracing.EventSourceOptions options, T data) { }
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("EventSource will serialize the whole object graph. Trimmer will not safely handle this case because properties may be trimmed. This can be suppressed if the object is a primitive type")]
        public void Write<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(string? eventName, ref System.Diagnostics.Tracing.EventSourceOptions options, ref System.Guid activityId, ref System.Guid relatedActivityId, ref T data) { }
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("EventSource will serialize the whole object graph. Trimmer will not safely handle this case because properties may be trimmed. This can be suppressed if the object is a primitive type")]
        public void Write<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(string? eventName, ref System.Diagnostics.Tracing.EventSourceOptions options, ref T data) { }
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("EventSource will serialize the whole object graph. Trimmer will not safely handle this case because properties may be trimmed. This can be suppressed if the object is a primitive type")]
        public void Write<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(string? eventName, T data) { }
        public void Write<T>(string? eventName, in T data, System.Diagnostics.Tracing.ITraceLoggingTypeDescriptor<T> descriptor) { }
        public void Write<T>(string? eventName, System.Diagnostics.Tracing.EventSourceOptions options, in T data, System.Diagnostics.Tracing.ITraceLoggingTypeDescriptor<T> descriptor) { }
        public void Write<T>(string? eventName, ref System.Diagnostics.Tracing.EventSourceOptions options, ref System.Guid activityId, ref System.Guid relatedActivityId, in T data, System.Diagnostics.Tracing.ITraceLoggingTypeDescriptor<T> descriptor) { }
        [System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public readonly struct EventSourcePrimitive
        {
            public static implicit operator EventSourcePrimitive(bool value) => throw null;
            public static implicit operator EventSourcePrimitive(byte value) => throw null;
            public static implicit operator EventSourcePrimitive(short value) => throw null;
            public static implicit operator EventSourcePrimitive(int value) => throw null;
            public static implicit operator EventSourcePrimitive(long value) => throw null;

            [CLSCompliant(false)]
            public static implicit operator EventSourcePrimitive(sbyte value) => throw null;
            [CLSCompliant(false)]
            public static implicit operator EventSourcePrimitive(ushort value) => throw null;
            [CLSCompliant(false)]
            public static implicit operator EventSourcePrimitive(uint value) => throw null;
            [CLSCompliant(false)]
            public static implicit operator EventSourcePrimitive(ulong value) => throw null;
            [CLSCompliant(false)]
            // Added to prevent going through the nuint -> ulong conversion
            public static implicit operator EventSourcePrimitive(nuint value) => throw null;

            public static implicit operator EventSourcePrimitive(float value) => throw null;
            public static implicit operator EventSourcePrimitive(double value) => throw null;
            public static implicit operator EventSourcePrimitive(decimal value) => throw null;

            public static implicit operator EventSourcePrimitive(string? value) => throw null;
            public static implicit operator EventSourcePrimitive(byte[]? value) => throw null;

            public static implicit operator EventSourcePrimitive(Guid value) => throw null;
            public static implicit operator EventSourcePrimitive(DateTime value) => throw null;
            public static implicit operator EventSourcePrimitive(nint value) => throw null;
            public static implicit operator EventSourcePrimitive(char value) => throw null;

            public static implicit operator EventSourcePrimitive(Enum value) => throw null;
        }
        [System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Sequential)]
        protected internal partial struct EventData
        {
            private int _dummyPrimitive;
            public System.IntPtr DataPointer { get { throw null; } set { } }
            public int Size { get { throw null; } set { } }
        }
    }
    [System.AttributeUsageAttribute(System.AttributeTargets.Class)]
    public sealed partial class EventSourceAttribute : System.Attribute
    {
        public EventSourceAttribute() { }
        public string? Guid { get { throw null; } set { } }
        public string? LocalizationResources { get { throw null; } set { } }
        public string? Name { get { throw null; } set { } }
    }
    public partial class EventSourceCreatedEventArgs : System.EventArgs
    {
        public EventSourceCreatedEventArgs() { }
        public System.Diagnostics.Tracing.EventSource? EventSource { get { throw null; } }
    }
    public partial class EventSourceException : System.Exception
    {
        public EventSourceException() { }
        [System.ObsoleteAttribute("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        protected EventSourceException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { }
        public EventSourceException(string? message) { }
        public EventSourceException(string? message, System.Exception? innerException) { }
    }
    [System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public partial struct EventSourceOptions
    {
        private int _dummyPrimitive;
        public System.Diagnostics.Tracing.EventActivityOptions ActivityOptions { get { throw null; } set { } }
        public System.Diagnostics.Tracing.EventKeywords Keywords { get { throw null; } set { } }
        public System.Diagnostics.Tracing.EventLevel Level { get { throw null; } set { } }
        public System.Diagnostics.Tracing.EventOpcode Opcode { get { throw null; } set { } }
        public System.Diagnostics.Tracing.EventTags Tags { get { throw null; } set { } }
    }
    [System.FlagsAttribute]
    public enum EventSourceSettings
    {
        Default = 0,
        ThrowOnEventWriteErrors = 1,
        EtwManifestEventFormat = 4,
        EtwSelfDescribingEventFormat = 8,
    }
    [System.FlagsAttribute]
    public enum EventTags
    {
        None = 0,
    }
    public enum EventTask
    {
        None = 0,
    }
    public partial class EventWrittenEventArgs : System.EventArgs
    {
        internal EventWrittenEventArgs() { }
        public System.Guid ActivityId { get { throw null; } }
        public System.Diagnostics.Tracing.EventChannel Channel { get { throw null; } }
        public int EventId { get { throw null; } }
        public string? EventName { get { throw null; } }
        public System.Diagnostics.Tracing.EventSource EventSource { get { throw null; } }
        public System.Diagnostics.Tracing.EventKeywords Keywords { get { throw null; } }
        public System.Diagnostics.Tracing.EventLevel Level { get { throw null; } }
        public string? Message { get { throw null; } }
        public System.Diagnostics.Tracing.EventOpcode Opcode { get { throw null; } }
        public long OSThreadId { get { throw null; } }
        public System.Collections.ObjectModel.ReadOnlyCollection<object?>? Payload { get { throw null; } }
        public System.Collections.ObjectModel.ReadOnlyCollection<string>? PayloadNames { get { throw null; } }
        public System.Guid RelatedActivityId { get { throw null; } }
        public System.Diagnostics.Tracing.EventTags Tags { get { throw null; } }
        public System.Diagnostics.Tracing.EventTask Task { get { throw null; } }
        public System.DateTime TimeStamp { get { throw null; } }
        public byte Version { get { throw null; } }
    }
    [System.AttributeUsageAttribute(System.AttributeTargets.Method)]
    public sealed partial class NonEventAttribute : System.Attribute
    {
        public NonEventAttribute() { }
    }
    public enum TraceLoggingDataType
    {
        Nil = 0,
        Utf16String = 1,
        MbcsString = 2,
        Int8 = 3,
        UInt8 = 4,
        Int16 = 5,
        UInt16 = 6,
        Int32 = 7,
        UInt32 = 8,
        Int64 = 9,
        UInt64 = 10,
        Float = 11,
        Double = 12,
        Boolean32 = 13,
        Binary = 14,
        Guid = 15,
        FileTime = 17,
        SystemTime = 18,
        HexInt32 = 20,
        HexInt64 = 21,
        CountedUtf16String = 22,
        CountedMbcsString = 23,
        Struct = 24,
        Char16 = 518,
        Char8 = 516,
        Boolean8 = 772,
        HexInt8 = 1028,
        HexInt16 = 1030,
        Utf16Xml = 2817,
        MbcsXml = 2818,
        CountedUtf16Xml = 2838,
        CountedMbcsXml = 2839,
        Utf16Json = 3073,
        MbcsJson = 3074,
        CountedUtf16Json = 3094,
        CountedMbcsJson = 3095,
        HResult = 3847,
    }
    public readonly partial struct TraceLoggingFieldDescriptor
    {
        public TraceLoggingFieldDescriptor(string name, System.Diagnostics.Tracing.TraceLoggingDataType type) { throw null; }
        public TraceLoggingFieldDescriptor(string name, params System.Diagnostics.Tracing.TraceLoggingFieldDescriptor[] children) { throw null; }
        public TraceLoggingFieldDescriptor(string name, System.Diagnostics.Tracing.TraceLoggingDataType elementType, bool isArray) { throw null; }
        public string Name { get { throw null; } }
        public System.Diagnostics.Tracing.TraceLoggingDataType DataType { get { throw null; } }
        public System.Diagnostics.Tracing.TraceLoggingFieldDescriptor[] Children { get { throw null; } }
        public bool IsArray { get { throw null; } }
    }
    public ref partial struct EventDataWriter
    {
        public void WriteBoolean(bool value) { }
        public void WriteByte(byte value) { }
        [System.CLSCompliantAttribute(false)]
        public void WriteSByte(sbyte value) { }
        public void WriteChar(char value) { }
        public void WriteInt16(short value) { }
        [System.CLSCompliantAttribute(false)]
        public void WriteUInt16(ushort value) { }
        public void WriteInt32(int value) { }
        [System.CLSCompliantAttribute(false)]
        public void WriteUInt32(uint value) { }
        public void WriteInt64(long value) { }
        [System.CLSCompliantAttribute(false)]
        public void WriteUInt64(ulong value) { }
        public void WriteIntPtr(nint value) { }
        [System.CLSCompliantAttribute(false)]
        public void WriteUIntPtr(nuint value) { }
        public void WriteSingle(float value) { }
        public void WriteDouble(double value) { }
        public void WriteGuid(System.Guid value) { }
        public void WriteDateTime(System.DateTime value) { }
        public void WriteDateTimeOffset(System.DateTimeOffset value) { }
        public void WriteTimeSpan(System.TimeSpan value) { }
        public void WriteDecimal(decimal value) { }
        public void WriteString(string? value) { }
        public void WriteArray<T>(T[]? value) where T : unmanaged { }
        public void BeginGroup() { }
        public void EndGroup() { }
    }
    public partial interface ITraceLoggingTypeDescriptor<T>
    {
        System.Diagnostics.Tracing.TraceLoggingEventMetadata Metadata { get; }
        void Write(System.Diagnostics.Tracing.EventDataWriter writer, T data);
    }
    public sealed partial class TraceLoggingEventMetadata
    {
        public TraceLoggingEventMetadata(string name, params System.Diagnostics.Tracing.TraceLoggingFieldDescriptor[] fields) { }
    }
}
