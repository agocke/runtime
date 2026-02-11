// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// This program uses code hyperlinks available as part of the HyperAddin Visual Studio plug-in.
// It is available from http://www.codeplex.com/hyperAddin

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace System.Diagnostics.Tracing
{
    public partial class EventSource
    {
        private byte[]? m_providerMetadata;
        private protected virtual ReadOnlySpan<byte> ProviderMetadata => m_providerMetadata;
        private const string EventSourceRequiresUnreferenceMessage = "EventSource will serialize the whole object graph. Trimmer will not safely handle this case because properties may be trimmed. This can be suppressed if the object is a primitive type";
        private const string EventSourceSuppressMessage = "Parameters to this method are primitive and are trimmer safe";

#if FEATURE_PERFTRACING
        private readonly TraceLoggingEventHandleTable m_eventHandleTable = null!;
#endif

        /// <summary>
        /// Construct an EventSource with a given name for non-contract based events (e.g. those using the Write() API).
        /// </summary>
        /// <param name="eventSourceName">
        /// The name of the event source. Must not be null.
        /// </param>
        public EventSource(
            string eventSourceName)
            : this(eventSourceName, EventSourceSettings.EtwSelfDescribingEventFormat)
        { }

        /// <summary>
        /// Construct an EventSource with a given name for non-contract based events (e.g. those using the Write() API).
        /// </summary>
        /// <param name="eventSourceName">
        /// The name of the event source. Must not be null.
        /// </param>
        /// <param name="config">
        /// Configuration options for the EventSource as a whole.
        /// </param>
        public EventSource(
            string eventSourceName,
            EventSourceSettings config)
            : this(eventSourceName, config, null) { }

        /// <summary>
        /// Construct an EventSource with a given name for non-contract based events (e.g. those using the Write() API).
        ///
        /// Also specify a list of key-value pairs called traits (you must pass an even number of strings).
        /// The first string is the key and the second is the value.   These are not interpreted by EventSource
        /// itself but may be interpreted the listeners.  Can be fetched with GetTrait(string).
        /// </summary>
        /// <param name="eventSourceName">
        /// The name of the event source. Must not be null.
        /// </param>
        /// <param name="config">
        /// Configuration options for the EventSource as a whole.
        /// </param>
        /// <param name="traits">A collection of key-value strings (must be an even number).</param>
        public EventSource(
            string eventSourceName,
            EventSourceSettings config,
            params string[]? traits)
            : this(
                eventSourceName,
                GenerateGuidFromName((eventSourceName ?? throw new ArgumentNullException(nameof(eventSourceName))).ToUpperInvariant()),
                config, traits)
        {
        }

        /// <summary>
        /// Writes an event with no fields and default options.
        /// (Native API: EventWriteTransfer)
        /// </summary>
        /// <param name="eventName">The name of the event.</param>
        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:UnrecognizedReflectionPattern",
                   Justification = EventSourceSuppressMessage)]
        public unsafe void Write(string? eventName)
        {
            if (!this.IsEnabled())
            {
                return;
            }

            EventSourceOptions options = default;
            this.WriteImpl(eventName, ref options, null, null, null, SimpleEventTypes<EmptyStruct>.Instance);
        }

        /// <summary>
        /// Writes an event with no fields.
        /// (Native API: EventWriteTransfer)
        /// </summary>
        /// <param name="eventName">The name of the event.</param>
        /// <param name="options">
        /// Options for the event, such as the level, keywords, and opcode. Unset
        /// options will be set to default values.
        /// </param>
        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:UnrecognizedReflectionPattern",
                   Justification = EventSourceSuppressMessage)]
        public unsafe void Write(string? eventName, EventSourceOptions options)
        {
            if (!this.IsEnabled())
            {
                return;
            }

            this.WriteImpl(eventName, ref options, null, null, null, SimpleEventTypes<EmptyStruct>.Instance);
        }

        /// <summary>
        /// Writes an event.
        /// (Native API: EventWriteTransfer)
        /// </summary>
        /// <typeparam name="T">
        /// The type that defines the event and its payload. This must be an
        /// anonymous type or a type with an [EventData] attribute.
        /// </typeparam>
        /// <param name="eventName">
        /// The name for the event. If null, the event name is automatically
        /// determined based on T, either from the Name property of T's EventData
        /// attribute or from typeof(T).Name.
        /// </param>
        /// <param name="data">
        /// The object containing the event payload data. The type T must be
        /// an anonymous type or a type with an [EventData] attribute. The
        /// public instance properties of data will be written recursively to
        /// create the fields of the event.
        /// </param>
        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2112:ReflectionToRequiresUnreferencedCode",
                    Justification = "EnsureDescriptorsInitialized's use of GetType preserves this method which " +
                                    "requires unreferenced code, but EnsureDescriptorsInitialized does not access this member and is safe to call.")]
        [RequiresUnreferencedCode(EventSourceRequiresUnreferenceMessage)]
        public unsafe void Write<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
            string? eventName,
            T data)
        {
            if (!this.IsEnabled())
            {
                return;
            }

            EventSourceOptions options = default;
            this.WriteImpl(eventName, ref options, data, null, null, SimpleEventTypes<T>.Instance);
        }

        /// <summary>
        /// Writes an event.
        /// (Native API: EventWriteTransfer)
        /// </summary>
        /// <typeparam name="T">
        /// The type that defines the event and its payload. This must be an
        /// anonymous type or a type with an [EventData] attribute.
        /// </typeparam>
        /// <param name="eventName">
        /// The name for the event. If null, the event name is automatically
        /// determined based on T, either from the Name property of T's EventData
        /// attribute or from typeof(T).Name.
        /// </param>
        /// <param name="options">
        /// Options for the event, such as the level, keywords, and opcode. Unset
        /// options will be set to default values.
        /// </param>
        /// <param name="data">
        /// The object containing the event payload data. The type T must be
        /// an anonymous type or a type with an [EventData] attribute. The
        /// public instance properties of data will be written recursively to
        /// create the fields of the event.
        /// </param>
        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2112:ReflectionToRequiresUnreferencedCode",
                    Justification = "EnsureDescriptorsInitialized's use of GetType preserves this method which " +
                                    "requires unreferenced code, but EnsureDescriptorsInitialized does not access this member and is safe to call.")]
        [RequiresUnreferencedCode(EventSourceRequiresUnreferenceMessage)]
        public unsafe void Write<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
            string? eventName,
            EventSourceOptions options,
            T data)
        {
            if (!this.IsEnabled())
            {
                return;
            }

            this.WriteImpl(eventName, ref options, data, null, null, SimpleEventTypes<T>.Instance);
        }

        /// <summary>
        /// Writes an event.
        /// This overload is for use with extension methods that wish to efficiently
        /// forward the options or data parameter without performing an extra copy.
        /// (Native API: EventWriteTransfer)
        /// </summary>
        /// <typeparam name="T">
        /// The type that defines the event and its payload. This must be an
        /// anonymous type or a type with an [EventData] attribute.
        /// </typeparam>
        /// <param name="eventName">
        /// The name for the event. If null, the event name is automatically
        /// determined based on T, either from the Name property of T's EventData
        /// attribute or from typeof(T).Name.
        /// </param>
        /// <param name="options">
        /// Options for the event, such as the level, keywords, and opcode. Unset
        /// options will be set to default values.
        /// </param>
        /// <param name="data">
        /// The object containing the event payload data. The type T must be
        /// an anonymous type or a type with an [EventData] attribute. The
        /// public instance properties of data will be written recursively to
        /// create the fields of the event.
        /// </param>
        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2112:ReflectionToRequiresUnreferencedCode",
                    Justification = "EnsureDescriptorsInitialized's use of GetType preserves this method which " +
                                    "requires unreferenced code, but EnsureDescriptorsInitialized does not access this member and is safe to call.")]
        [RequiresUnreferencedCode(EventSourceRequiresUnreferenceMessage)]
        public unsafe void Write<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
            string? eventName,
            ref EventSourceOptions options,
            ref T data)
        {
            if (!this.IsEnabled())
            {
                return;
            }

            this.WriteImpl(eventName, ref options, data, null, null, SimpleEventTypes<T>.Instance);
        }

        /// <summary>
        /// Writes an event.
        /// This overload is meant for clients that need to manipuate the activityId
        /// and related ActivityId for the event.
        /// </summary>
        /// <typeparam name="T">
        /// The type that defines the event and its payload. This must be an
        /// anonymous type or a type with an [EventData] attribute.
        /// </typeparam>
        /// <param name="eventName">
        /// The name for the event. If null, the event name is automatically
        /// determined based on T, either from the Name property of T's EventData
        /// attribute or from typeof(T).Name.
        /// </param>
        /// <param name="options">
        /// Options for the event, such as the level, keywords, and opcode. Unset
        /// options will be set to default values.
        /// </param>
        /// <param name="activityId">
        /// The GUID of the activity associated with this event.
        /// </param>
        /// <param name="relatedActivityId">
        /// The GUID of another activity that is related to this activity, or Guid.Empty
        /// if there is no related activity. Most commonly, the Start operation of a
        /// new activity specifies a parent activity as its related activity.
        /// </param>
        /// <param name="data">
        /// The object containing the event payload data. The type T must be
        /// an anonymous type or a type with an [EventData] attribute. The
        /// public instance properties of data will be written recursively to
        /// create the fields of the event.
        /// </param>
        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2112:ReflectionToRequiresUnreferencedCode",
                    Justification = "EnsureDescriptorsInitialized's use of GetType preserves this method which " +
                                    "requires unreferenced code, but EnsureDescriptorsInitialized does not access this member and is safe to call.")]
        [RequiresUnreferencedCode(EventSourceRequiresUnreferenceMessage)]
        public unsafe void Write<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
            string? eventName,
            ref EventSourceOptions options,
            ref Guid activityId,
            ref Guid relatedActivityId,
            ref T data)
        {
            if (!this.IsEnabled())
            {
                return;
            }

            fixed (Guid* pActivity = &activityId, pRelated = &relatedActivityId)
            {
                this.WriteImpl(
                    eventName,
                    ref options,
                    data,
                    pActivity,
                    relatedActivityId == Guid.Empty ? null : pRelated,
                    SimpleEventTypes<T>.Instance);
            }
        }

        /// <summary>
        /// Writes an extended event, where the values of the event are the
        /// combined properties of any number of values. This method is
        /// intended for use in advanced logging scenarios that support a
        /// dynamic set of event context providers.
        /// This method does a quick check on whether this event is enabled.
        /// </summary>
        /// <param name="eventName">
        /// The name for the event. If null, the name from eventTypes is used.
        /// (Note that providing the event name via the name parameter is slightly
        /// less efficient than using the name from eventTypes.)
        /// </param>
        /// <param name="options">
        /// Optional overrides for the event, such as the level, keyword, opcode,
        /// activityId, and relatedActivityId. Any settings not specified by options
        /// are obtained from eventTypes.
        /// </param>
        /// <param name="eventTypes">
        /// Information about the event and the types of the values in the event.
        /// Must not be null. Note that the eventTypes object should be created once and
        /// saved. It should not be recreated for each event.
        /// </param>
        /// <param name="activityID">
        /// A pointer to the activity ID GUID to log
        /// </param>
        /// <param name="childActivityID">
        /// A pointer to the child activity ID to log (can be null) </param>
        /// <param name="values">
        /// The values to include in the event. Must not be null. The number and types of
        /// the values must match the number and types of the fields described by the
        /// eventTypes parameter.
        /// </param>
        private unsafe void WriteMultiMerge(
            string? eventName,
            ref EventSourceOptions options,
            TraceLoggingEventTypes eventTypes,
             Guid* activityID,
             Guid* childActivityID,
            params object?[] values)
        {
            if (!this.IsEnabled())
            {
                return;
            }
            byte level = (options.valuesSet & EventSourceOptions.levelSet) != 0
                ? options.level
                : eventTypes.level;
            EventKeywords keywords = (options.valuesSet & EventSourceOptions.keywordsSet) != 0
                ? options.keywords
                : eventTypes.keywords;

            if (this.IsEnabled((EventLevel)level, keywords))
            {
                WriteMultiMergeInner(eventName, ref options, eventTypes, activityID, childActivityID, values);
            }
        }

        /// <summary>
        /// Writes an extended event, where the values of the event are the
        /// combined properties of any number of values. This method is
        /// intended for use in advanced logging scenarios that support a
        /// dynamic set of event context providers.
        /// Attention: This API does not check whether the event is enabled or not.
        /// Please use WriteMultiMerge to avoid spending CPU cycles for events that are
        /// not enabled.
        /// </summary>
        /// <param name="eventName">
        /// The name for the event. If null, the name from eventTypes is used.
        /// (Note that providing the event name via the name parameter is slightly
        /// less efficient than using the name from eventTypes.)
        /// </param>
        /// <param name="options">
        /// Optional overrides for the event, such as the level, keyword, opcode,
        /// activityId, and relatedActivityId. Any settings not specified by options
        /// are obtained from eventTypes.
        /// </param>
        /// <param name="eventTypes">
        /// Information about the event and the types of the values in the event.
        /// Must not be null. Note that the eventTypes object should be created once and
        /// saved. It should not be recreated for each event.
        /// </param>
        /// <param name="activityID">
        /// A pointer to the activity ID GUID to log
        /// </param>
        /// <param name="childActivityID">
        /// A pointer to the child activity ID to log (can be null)
        /// </param>
        /// <param name="values">
        /// The values to include in the event. Must not be null. The number and types of
        /// the values must match the number and types of the fields described by the
        /// eventTypes parameter.
        /// </param>
        private unsafe void WriteMultiMergeInner(
            string? eventName,
            ref EventSourceOptions options,
            TraceLoggingEventTypes eventTypes,
            Guid* activityID,
            Guid* childActivityID,
            params object?[] values)
        {
            int identity = 0;
            byte level = (options.valuesSet & EventSourceOptions.levelSet) != 0
                ? options.level
                : eventTypes.level;
            byte opcode = (options.valuesSet & EventSourceOptions.opcodeSet) != 0
                ? options.opcode
                : eventTypes.opcode;
            EventTags tags = (options.valuesSet & EventSourceOptions.tagsSet) != 0
                ? options.tags
                : eventTypes.Tags;
            EventKeywords keywords = (options.valuesSet & EventSourceOptions.keywordsSet) != 0
                ? options.keywords
                : eventTypes.keywords;

            NameInfo nameInfo = eventTypes.GetNameInfo(eventName ?? eventTypes.Name, tags);
            if (nameInfo == null)
            {
                return;
            }
            identity = nameInfo.identity;
            EventDescriptor descriptor = new EventDescriptor(identity, level, opcode, (long)keywords);

#if FEATURE_PERFTRACING
            IntPtr eventHandle = nameInfo.GetOrCreateEventHandle(m_eventPipeProvider, m_eventHandleTable, descriptor, eventTypes);
            Debug.Assert(eventHandle != IntPtr.Zero);
#else
            IntPtr eventHandle = IntPtr.Zero;
#endif

            int pinCount = eventTypes.pinCount;
            byte* scratch = stackalloc byte[eventTypes.scratchSize];
            EventData* descriptors = stackalloc EventData[eventTypes.dataCount + 3];
            for (int i = 0; i < eventTypes.dataCount + 3; i++)
                descriptors[i] = default;

            GCHandle* pins = stackalloc GCHandle[pinCount];
            for (int i = 0; i < pinCount; i++)
                pins[i] = default;

            var providerMetadata = ProviderMetadata;
            fixed (byte*
                pMetadata0 = providerMetadata,
                pMetadata1 = nameInfo.nameMetadata,
                pMetadata2 = eventTypes.typeMetadata)
            {
                descriptors[0].SetMetadata(pMetadata0, providerMetadata.Length, 2);
                descriptors[1].SetMetadata(pMetadata1, nameInfo.nameMetadata.Length, 1);
                descriptors[2].SetMetadata(pMetadata2, eventTypes.typeMetadata.Length, 1);

                try
                {
                    DataCollector.ThreadInstance.Enable(
                        scratch,
                        eventTypes.scratchSize,
                        descriptors + 3,
                        eventTypes.dataCount,
                        pins,
                        pinCount);

                    for (int i = 0; i < eventTypes.typeInfos.Length; i++)
                    {
                        TraceLoggingTypeInfo info = eventTypes.typeInfos[i];
                        info.WriteData(info.PropertyValueFactory(values[i]));
                    }

                    this.WriteEventRaw(
                        eventName,
                        ref descriptor,
                        eventHandle,
                        activityID,
                        childActivityID,
                        (int)(DataCollector.ThreadInstance.Finish() - descriptors),
                        (IntPtr)descriptors);
                }
                finally
                {
                    WriteCleanup(pins, pinCount);
                }
            }
        }

        /// <summary>
        /// Writes an extended event, where the values of the event have already
        /// been serialized in "data".
        /// </summary>
        /// <param name="eventName">
        /// The name for the event. If null, the name from eventTypes is used.
        /// (Note that providing the event name via the name parameter is slightly
        /// less efficient than using the name from eventTypes.)
        /// </param>
        /// <param name="options">
        /// Optional overrides for the event, such as the level, keyword, opcode,
        /// activityId, and relatedActivityId. Any settings not specified by options
        /// are obtained from eventTypes.
        /// </param>
        /// <param name="eventTypes">
        /// Information about the event and the types of the values in the event.
        /// Must not be null. Note that the eventTypes object should be created once and
        /// saved. It should not be recreated for each event.
        /// </param>
        /// <param name="activityID">
        /// A pointer to the activity ID GUID to log
        /// </param>
        /// <param name="childActivityID">
        /// A pointer to the child activity ID to log (can be null)
        /// </param>
        /// <param name="data">
        /// The previously serialized values to include in the event. Must not be null.
        /// The number and types of the values must match the number and types of the
        /// fields described by the eventTypes parameter.
        /// </param>
        internal unsafe void WriteMultiMerge(
            string? eventName,
            ref EventSourceOptions options,
            TraceLoggingEventTypes eventTypes,
            Guid* activityID,
            Guid* childActivityID,
            EventData* data)
        {
            if (!this.IsEnabled())
            {
                return;
            }

            fixed (EventSourceOptions* pOptions = &options)
            {
                NameInfo? nameInfo = this.UpdateDescriptor(eventName, eventTypes, ref options, out EventDescriptor descriptor);
                if (nameInfo == null)
                {
                    return;
                }

#if FEATURE_PERFTRACING
                IntPtr eventHandle = nameInfo.GetOrCreateEventHandle(m_eventPipeProvider, m_eventHandleTable, descriptor, eventTypes);
                Debug.Assert(eventHandle != IntPtr.Zero);
#else
                IntPtr eventHandle = IntPtr.Zero;
#endif

                // We make a descriptor for each EventData, and because we morph strings to counted strings
                // we may have 2 for each arg, so we allocate enough for this.
                int descriptorsLength = eventTypes.dataCount + eventTypes.typeInfos.Length * 2 + 3;
                EventData* descriptors = stackalloc EventData[descriptorsLength];
                for (int i = 0; i < descriptorsLength; i++)
                    descriptors[i] = default;

                var providerMetadata = ProviderMetadata;
                fixed (byte*
                    pMetadata0 = providerMetadata,
                    pMetadata1 = nameInfo.nameMetadata,
                    pMetadata2 = eventTypes.typeMetadata)
                {
                    descriptors[0].SetMetadata(pMetadata0, providerMetadata.Length, 2);
                    descriptors[1].SetMetadata(pMetadata1, nameInfo.nameMetadata.Length, 1);
                    descriptors[2].SetMetadata(pMetadata2, eventTypes.typeMetadata.Length, 1);
                    int numDescrs = 3;

                    for (int i = 0; i < eventTypes.typeInfos.Length; i++)
                    {
                        descriptors[numDescrs].m_Ptr = data[i].m_Ptr;
                        descriptors[numDescrs].m_Size = data[i].m_Size;

                        // old conventions for bool is 4 bytes, but meta-data assumes 1.
                        if (data[i].m_Size == 4 && eventTypes.typeInfos[i].DataType == typeof(bool))
                            descriptors[numDescrs].m_Size = 1;

                        numDescrs++;
                    }

                    this.WriteEventRaw(
                        eventName,
                        ref descriptor,
                        eventHandle,
                        activityID,
                        childActivityID,
                        numDescrs,
                        (IntPtr)descriptors);
                }
            }
        }

        private unsafe void WriteImpl(
            string? eventName,
            ref EventSourceOptions options,
            object? data,
            Guid* pActivityId,
            Guid* pRelatedActivityId,
            TraceLoggingEventTypes eventTypes)
        {
            try
            {
                fixed (EventSourceOptions* pOptions = &options)
                {
                    options.Opcode = options.IsOpcodeSet ? options.Opcode : GetOpcodeWithDefault(options.Opcode, eventName);
                    NameInfo? nameInfo = this.UpdateDescriptor(eventName, eventTypes, ref options, out EventDescriptor descriptor);
                    if (nameInfo == null)
                    {
                        return;
                    }

#if FEATURE_PERFTRACING
                    IntPtr eventHandle = nameInfo.GetOrCreateEventHandle(m_eventPipeProvider, m_eventHandleTable, descriptor, eventTypes);
                    Debug.Assert(eventHandle != IntPtr.Zero);
#else
                    IntPtr eventHandle = IntPtr.Zero;
#endif

                    int pinCount = eventTypes.pinCount;
                    byte* scratch = stackalloc byte[eventTypes.scratchSize];
                    EventData* descriptors = stackalloc EventData[eventTypes.dataCount + 3];
                    for (int i = 0; i < eventTypes.dataCount + 3; i++)
                        descriptors[i] = default;

                    GCHandle* pins = stackalloc GCHandle[pinCount];
                    for (int i = 0; i < pinCount; i++)
                        pins[i] = default;

                    var providerMetadata = ProviderMetadata;
                    fixed (byte*
                        pMetadata0 = providerMetadata,
                        pMetadata1 = nameInfo.nameMetadata,
                        pMetadata2 = eventTypes.typeMetadata)
                    {
                        descriptors[0].SetMetadata(pMetadata0, providerMetadata.Length, 2);
                        descriptors[1].SetMetadata(pMetadata1, nameInfo.nameMetadata.Length, 1);
                        descriptors[2].SetMetadata(pMetadata2, eventTypes.typeMetadata.Length, 1);

                        EventOpcode opcode = (EventOpcode)descriptor.Opcode;

                        Guid activityId = Guid.Empty;
                        Guid relatedActivityId = Guid.Empty;
                        if (pActivityId == null && pRelatedActivityId == null &&
                           ((options.ActivityOptions & EventActivityOptions.Disable) == 0))
                        {
                            if (opcode == EventOpcode.Start)
                            {
                                Debug.Assert(eventName != null, "GetOpcodeWithDefault should not returned Start when eventName is null");
                                m_activityTracker.OnStart(m_name, eventName, 0, ref activityId, ref relatedActivityId, options.ActivityOptions);
                            }
                            else if (opcode == EventOpcode.Stop)
                            {
                                Debug.Assert(eventName != null, "GetOpcodeWithDefault should not returned Stop when eventName is null");
                                m_activityTracker.OnStop(m_name, eventName, 0, ref activityId);
                            }
                            if (activityId != Guid.Empty)
                                pActivityId = &activityId;
                            if (relatedActivityId != Guid.Empty)
                                pRelatedActivityId = &relatedActivityId;
                        }

                        try
                        {
                            DataCollector.ThreadInstance.Enable(
                                scratch,
                                eventTypes.scratchSize,
                                descriptors + 3,
                                eventTypes.dataCount,
                                pins,
                                pinCount);

                            TraceLoggingTypeInfo info = eventTypes.typeInfos[0];
                            info.WriteData(info.PropertyValueFactory(data));

                            this.WriteEventRaw(
                                eventName,
                                ref descriptor,
                                eventHandle,
                                pActivityId,
                                pRelatedActivityId,
                                (int)(DataCollector.ThreadInstance.Finish() - descriptors),
                                (IntPtr)descriptors);

                            // TODO enable filtering for listeners.
                            if (m_Dispatchers != null)
                            {
                                var eventData = (EventPayload?)(eventTypes.typeInfos[0].GetData(data));
                                WriteToAllListeners(eventName, ref descriptor, nameInfo.tags, pActivityId, pRelatedActivityId, eventData);
                            }
                        }
                        catch (Exception ex)
                        {
                            if (ex is EventSourceException)
                                throw;
                            else
                                ThrowEventSourceException(eventName, ex);
                        }
                        finally
                        {
                            WriteCleanup(pins, pinCount);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is EventSourceException)
                    throw;
                else
                    ThrowEventSourceException(eventName, ex);
            }
        }

        private unsafe void WriteToAllListeners(string? eventName, ref EventDescriptor eventDescriptor, EventTags tags, Guid* pActivityId, Guid* pChildActivityId, EventPayload? payload)
        {
            // Self described events do not have an id attached. We mark it internally with -1.
            var eventCallbackArgs = new EventWrittenEventArgs(this, -1, pActivityId, pChildActivityId)
            {
                EventName = eventName,
                Level = (EventLevel)eventDescriptor.Level,
                Keywords = (EventKeywords)eventDescriptor.Keywords,
                Opcode = (EventOpcode)eventDescriptor.Opcode,
                Tags = tags
            };

            if (payload != null)
            {
                eventCallbackArgs.Payload = new ReadOnlyCollection<object?>((IList<object?>)payload.Values);
                eventCallbackArgs.PayloadNames = new ReadOnlyCollection<string>((IList<string>)payload.Keys);
            }

            DispatchToAllListeners(eventCallbackArgs);
        }

        [NonEvent]
        private static unsafe void WriteCleanup(GCHandle* pPins, int cPins)
        {
            DataCollector.ThreadInstance.Disable();

            for (int i = 0; i < cPins; i++)
            {
                if (pPins[i].IsAllocated)
                {
                    pPins[i].Free();
                }
            }
        }

        private void InitializeProviderMetadata()
        {
            bool hasProviderMetadata = ProviderMetadata.Length > 0;
#if !DEBUG
            if (hasProviderMetadata)
            {
                // Already set
                return;
            }
#endif
            if (m_traits != null)
            {
                List<byte> traitMetaData = new List<byte>(100);
                for (int i = 0; i < m_traits.Length - 1; i += 2)
                {
                    if (m_traits[i].StartsWith("ETW_", StringComparison.Ordinal))
                    {
                        ReadOnlySpan<char> etwTrait = m_traits[i].AsSpan(4);
                        if (!byte.TryParse(etwTrait, out byte traitNum))
                        {
                            if (etwTrait is "GROUP")
                            {
                                traitNum = 1;
                            }
                            else
                            {
                                throw new ArgumentException(SR.Format(SR.EventSource_UnknownEtwTrait, etwTrait.ToString()), "traits");
                            }
                        }
                        string value = m_traits[i + 1];
                        int lenPos = traitMetaData.Count;
                        traitMetaData.Add(0);                                           // Emit size (to be filled in later)
                        traitMetaData.Add(0);
                        traitMetaData.Add(traitNum);                                    // Emit Trait number
                        int valueLen = AddValueToMetaData(traitMetaData, value) + 3;    // Emit the value bytes +3 accounts for 3 bytes we emitted above.
                        traitMetaData[lenPos] = unchecked((byte)valueLen);              // Fill in size
                        traitMetaData[lenPos + 1] = unchecked((byte)(valueLen >> 8));
                    }
                }
                byte[] providerMetadata = Statics.MetadataForString(this.Name, 0, traitMetaData.Count, 0);
                int startPos = providerMetadata.Length - traitMetaData.Count;
                foreach (byte b in traitMetaData)
                {
                    providerMetadata[startPos++] = b;
                }

                m_providerMetadata = providerMetadata;
            }
            else
            {
                m_providerMetadata = Statics.MetadataForString(this.Name, 0, 0, 0);
            }

#if DEBUG
            if (hasProviderMetadata)
            {
                // Validate the provided ProviderMetadata still matches in debug
                Debug.Assert(ProviderMetadata.SequenceEqual(m_providerMetadata));
            }
#endif
        }

        private static int AddValueToMetaData(List<byte> metaData, string value)
        {
            if (value.Length == 0)
                return 0;

            int startPos = metaData.Count;
            char firstChar = value[0];

            if (firstChar == '@')
                metaData.AddRange(Encoding.UTF8.GetBytes(value.Substring(1)));
            else if (firstChar == '{')
                metaData.AddRange(new Guid(value).ToByteArray());
            else if (firstChar == '#')
            {
                for (int i = 1; i < value.Length; i++)
                {
                    if (value[i] != ' ')        // Skip spaces between bytes.
                    {
                        if (!(i + 1 < value.Length))
                        {
                            throw new ArgumentException(SR.EventSource_EvenHexDigits, "traits");
                        }
                        metaData.Add((byte)(HexDigit(value[i]) * 16 + HexDigit(value[i + 1])));
                        i++;
                    }
                }
            }
            else if ('A' <= firstChar || ' ' == firstChar)  // Is it alphabetic or space (excludes digits and most punctuation).
            {
                metaData.AddRange(Encoding.UTF8.GetBytes(value));
            }
            else
            {
                throw new ArgumentException(SR.Format(SR.EventSource_IllegalValue, value), "traits");
            }

            return metaData.Count - startPos;
        }

        /// <summary>
        /// Returns a value 0-15 if 'c' is a hexadecimal digit.   If  it throws an argument exception.
        /// </summary>
        private static int HexDigit(char c)
        {
            if ('0' <= c && c <= '9')
            {
                return c - '0';
            }
            if ('a' <= c)
            {
                c = unchecked((char)(c - ('a' - 'A')));        // Convert to lower case
            }
            if ('A' <= c && c <= 'F')
            {
                return c - 'A' + 10;
            }

            throw new ArgumentException(SR.Format(SR.EventSource_BadHexDigit, c), "traits");
        }

        /// <summary>
        /// Writes an event directly using caller-provided metadata, bypassing
        /// reflection and the <c>m_eventData</c> infrastructure entirely.
        /// The event is emitted in manifest-based format to ETW and EventPipe,
        /// and decoded for <see cref="EventListener"/> consumers via the
        /// field descriptors on <paramref name="info"/>.
        /// </summary>
        /// <param name="info">
        /// Pre-built event metadata. Must not be null. Callers should construct
        /// one instance per event shape and cache it.
        /// <see cref="EventDescriptorInfo.EventId"/> must be non-negative.
        /// </param>
        /// <param name="eventDataCount">
        /// Number of <see cref="EventData"/> entries pointed to by
        /// <paramref name="data"/>.
        /// </param>
        /// <param name="data">
        /// Pointer to the first <see cref="EventData"/> entry.
        /// </param>
        [CLSCompliant(false)]
        protected unsafe void WriteEventDirect(EventDescriptorInfo info, int eventDataCount, EventData* data)
        {
            ArgumentNullException.ThrowIfNull(info);
            ArgumentOutOfRangeException.ThrowIfNegative(info.EventId, "info.EventId");

            if (!IsEnabled(info.Level, info.Keywords))
            {
                return;
            }

            try
            {
                // ── Activity tracking ──
                Guid* pActivityId = null;
                Guid activityId = Guid.Empty;
                Guid relatedActivityId = Guid.Empty;

                if (info.Opcode == EventOpcode.Start)
                {
                    m_activityTracker.OnStart(m_name, info.EventName, 0,
                        ref activityId, ref relatedActivityId, EventActivityOptions.None);
                }
                else if (info.Opcode == EventOpcode.Stop)
                {
                    m_activityTracker.OnStop(m_name, info.EventName, 0, ref activityId);
                }

                if (activityId != Guid.Empty)
                    pActivityId = &activityId;
                Guid* pRelatedActivityId = relatedActivityId != Guid.Empty
                    ? &relatedActivityId
                    : null;

                // ── Build manifest-style EventDescriptor ──
                EventDescriptor descriptor = new EventDescriptor(
                    info.EventId,
                    0,  // version
                    0,  // channel
                    (byte)info.Level,
                    (byte)info.Opcode,
                    0,  // task
                    (long)info.Keywords);

                // ── Write to ETW ──
                if (m_etwProvider != null &&
                    !m_etwProvider.WriteEvent(ref descriptor, IntPtr.Zero, pActivityId, pRelatedActivityId, eventDataCount, (IntPtr)data))
                {
                    ThrowEventSourceException(info.EventName);
                }

#if FEATURE_PERFTRACING
                // ── Write to EventPipe ──
                if (m_eventPipeProvider != null)
                {
                    // Lazily create and cache EventPipe handle on the caller's object
                    IntPtr eventHandle = info.EventPipeEventHandle;
                    if (eventHandle == IntPtr.Zero)
                    {
                        byte[] epMetadata = info.GenerateEventPipeMetadata();
                        uint epMetadataLen = (uint)epMetadata.Length;
                        fixed (byte* pEpMeta = epMetadata)
                        {
                            eventHandle = m_eventPipeProvider._eventProvider.DefineEventHandle(
                                (uint)info.EventId,
                                info.EventName,
                                (long)info.Keywords,
                                0, // version
                                (uint)info.Level,
                                pEpMeta,
                                epMetadataLen);
                        }
                        Interlocked.CompareExchange(ref info.EventPipeEventHandle, eventHandle, IntPtr.Zero);
                        eventHandle = info.EventPipeEventHandle;
                    }

                    if (!m_eventPipeProvider.WriteEvent(ref descriptor, eventHandle, pActivityId, pRelatedActivityId, eventDataCount, (IntPtr)data))
                    {
                        ThrowEventSourceException(info.EventName);
                    }
                }
#endif

                // ── EventListener dispatch ──
                if (m_Dispatchers != null)
                {
                    var eventCallbackArgs = new EventWrittenEventArgs(this, info.EventId, pActivityId, pRelatedActivityId)
                    {
                        EventName = info.EventName,
                        Level = info.Level,
                        Keywords = info.Keywords,
                        Opcode = info.Opcode,
                        Tags = info.Tags,
                    };

                    DecodeObjectsDirect(info.Fields, data,
                        out object?[] payload, out string[] names);

                    eventCallbackArgs.Payload = new ReadOnlyCollection<object?>(payload);
                    eventCallbackArgs.PayloadNames = new ReadOnlyCollection<string>(names);

                    DispatchToAllListeners(eventCallbackArgs);
                }
            }
            catch (Exception ex)
            {
                if (ex is EventSourceException)
                    throw;
                else
                    ThrowEventSourceException(info.EventName, ex);
            }
        }

        /// <summary>
        /// Decodes <see cref="EventData"/> entries into boxed objects using
        /// <see cref="EventFieldType"/> instead of <see cref="Type"/>.
        /// This is the reflection-free equivalent of
        /// <see cref="DecodeObjects"/>.
        /// </summary>
        private static unsafe void DecodeObjectsDirect(
            EventFieldDescriptor[] fields,
            EventData* data,
            out object?[] payload,
            out string[] names)
        {
            payload = new object?[fields.Length];
            names = new string[fields.Length];

            for (int i = 0; i < fields.Length; i++, data++)
            {
                names[i] = fields[i].Name;
                payload[i] = DecodeFieldValue(fields[i].FieldType, ref data);
            }
        }

        /// <summary>
        /// Decodes a single value from an <see cref="EventData"/> entry based
        /// on the <see cref="EventFieldType"/>. This is the shared decode
        /// logic used by both <see cref="DecodeObjectsDirect"/> and
        /// <see cref="DecodeObjects"/>.
        /// For <see cref="EventFieldType.ByteArray"/>, this advances
        /// <paramref name="data"/> by one additional entry (the blob).
        /// </summary>
        private static unsafe object? DecodeFieldValue(EventFieldType fieldType, ref EventData* data)
        {
            IntPtr dataPointer = data->DataPointer;

            return fieldType switch
            {
                EventFieldType.String => dataPointer == IntPtr.Zero
                    ? null
                    : new string((char*)dataPointer, 0, (data->Size >> 1) - 1),
                EventFieldType.Int32 => *(int*)dataPointer,
                EventFieldType.UInt32 => *(uint*)dataPointer,
                EventFieldType.Int64 => *(long*)dataPointer,
                EventFieldType.UInt64 => *(ulong*)dataPointer,
                EventFieldType.Boolean => *(int*)dataPointer != 0,
                EventFieldType.Float => *(float*)dataPointer,
                EventFieldType.Double => *(double*)dataPointer,
                EventFieldType.Int8 => *(sbyte*)dataPointer,
                EventFieldType.UInt8 => *(byte*)dataPointer,
                EventFieldType.Int16 => *(short*)dataPointer,
                EventFieldType.UInt16 => *(ushort*)dataPointer,
                EventFieldType.Char => *(char*)dataPointer,
                EventFieldType.Guid => *(Guid*)dataPointer,
                EventFieldType.DateTime => DateTime.FromFileTimeUtc(*(long*)dataPointer),
                EventFieldType.Decimal => *(decimal*)dataPointer,
                EventFieldType.IntPtr => *(IntPtr*)dataPointer,
                EventFieldType.ByteArray => DecodeByteArrayField(ref data),
                _ => null,
            };
        }

        /// <summary>
        /// Decodes a <see cref="EventFieldType.ByteArray"/> field which
        /// consumes two <see cref="EventData"/> entries: a 4-byte length prefix
        /// followed by the blob.
        /// </summary>
        private static unsafe byte[] DecodeByteArrayField(ref EventData* data)
        {
            // First EventData is the int length.
            // Advance to the second EventData which holds the actual blob.
            data++;
            if (data->Size == 0)
            {
                return Array.Empty<byte>();
            }

            var blob = new byte[data->Size];
            Marshal.Copy(data->DataPointer, blob, 0, blob.Length);
            return blob;
        }

        private NameInfo? UpdateDescriptor(
            string? name,
            TraceLoggingEventTypes eventInfo,
            ref EventSourceOptions options,
            out EventDescriptor descriptor)
        {
            NameInfo? nameInfo = null;
            int identity = 0;
            byte level = (options.valuesSet & EventSourceOptions.levelSet) != 0
                ? options.level
                : eventInfo.level;
            byte opcode = (options.valuesSet & EventSourceOptions.opcodeSet) != 0
                ? options.opcode
                : eventInfo.opcode;
            EventTags tags = (options.valuesSet & EventSourceOptions.tagsSet) != 0
                ? options.tags
                : eventInfo.Tags;
            EventKeywords keywords = (options.valuesSet & EventSourceOptions.keywordsSet) != 0
                ? options.keywords
                : eventInfo.keywords;

            if (this.IsEnabled((EventLevel)level, keywords))
            {
                nameInfo = eventInfo.GetNameInfo(name ?? eventInfo.Name, tags);
                identity = nameInfo.identity;
            }

            descriptor = new EventDescriptor(identity, level, opcode, (long)keywords);
            return nameInfo;
        }
    }
}
