// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// This file contains the classes, methods, and field used by the EE from corelib

//
// To use this, define one of the following macros & include the file like so:
//
// #define DEFINE_CLASS(id, nameSpace, stringName)         CLASS__ ## id,
// #define DEFINE_METHOD(classId, id, stringName, gSign)
// #define DEFINE_FIELD(classId, id, stringName)
// #include "corelib.h"
//
// Note: To determine if the namespace you want to use in DEFINE_CLASS is supported or not,
//       examine vm\namespace.h. If it is not present, define it there and then proceed to use it below.
//

//
// Note: This file gets parsed by the IL Linker (https://github.com/dotnet/runtime/blob/main/src/tools/illink) which may throw an exception during parsing.
// Specifically, this (https://github.com/dotnet/runtime/blob/main/src/tools/illink/src/ILLink.Tasks/CreateRuntimeRootDescriptorFile.cs) will try to
// parse this header, and it may throw an exception while doing that. If you edit this file and get a build failure on msbuild.exe D:\repos\coreclr\build.proj
// you might want to check out the parser linked above.
//

//
// Note: The SM_* and IM_* are signatures defined in file:metasig.h using IM() and SM() macros.
//

#ifndef DEFINE_CLASS
#define DEFINE_CLASS(id, nameSpace, stringName)
#endif

#ifndef DEFINE_METHOD
#define DEFINE_METHOD(classId, id, stringName, gSign)
#endif

#ifndef DEFINE_FIELD
#define DEFINE_FIELD(classId, id, stringName)
#endif

#ifndef DEFINE_PROPERTY
#define DEFINE_PROPERTY(classId, id, stringName, gSign) DEFINE_METHOD(classId, GET_ ## id, get_ ## stringName, IM_Ret ## gSign)
#endif

#ifndef DEFINE_STATIC_PROPERTY
#define DEFINE_STATIC_PROPERTY(classId, id, stringName, gSign) DEFINE_METHOD(classId, GET_ ## id, get_ ## stringName, SM_Ret ## gSign)
#endif

#ifndef DEFINE_SET_PROPERTY
#define DEFINE_SET_PROPERTY(classId, id, stringName, gSign) \
    DEFINE_PROPERTY(classId, id, stringName, gSign) \
    DEFINE_METHOD(classId, SET_ ## id, set_ ## stringName, IM_## gSign ## _RetVoid)
#endif

#ifndef DEFINE_STATIC_SET_PROPERTY
#define DEFINE_STATIC_SET_PROPERTY(classId, id, stringName, gSign) \
    DEFINE_STATIC_PROPERTY(classId, id, stringName, gSign) \
    DEFINE_METHOD(classId, SET_ ## id, set_ ## stringName, SM_## gSign ## _RetVoid)
#endif

//
// DEFINE_CLASS_U and DEFINE_FIELD_U are debug-only checks to verify that the managed and unmanaged layouts are in sync
//
#ifndef DEFINE_CLASS_U
#define DEFINE_CLASS_U(nameSpace, stringName, unmanagedType)
#endif

#ifndef DEFINE_FIELD_U
#define DEFINE_FIELD_U(stringName, unmanagedContainingType, unmanagedOffset)
#endif

//
// BEGIN_ILLINK_FEATURE_SWITCH and END_ILLINK_FEATURE_SWITCH allow IL linker to guard types behind a feature switch.
// Current support is only around class scope and not for standalone members of classes.
// See usage in this file itself and on the link (the assembly name for feature switch in this file will be System.Private.CoreLib),
// https://github.com/dotnet/designs/blob/main/accepted/2020/feature-switch.md#generate-the-right-input-for-the-linker-in-sdk
//
// The FOR_ILLINK define is set when this file is being processed for the IL linker.
//
#ifndef BEGIN_ILLINK_FEATURE_SWITCH
#define BEGIN_ILLINK_FEATURE_SWITCH(featureName, featureValue, featureDefault)
#endif

#ifndef END_ILLINK_FEATURE_SWITCH
#define END_ILLINK_FEATURE_SWITCH()
#endif


// NOTE: Make this window really wide if you want to read the table...

DEFINE_CLASS(ACTIVATOR,             System,                 Activator)
DEFINE_METHOD(ACTIVATOR,            CREATE_INSTANCE_OF_T,   CreateInstance, GM_RetT)
DEFINE_METHOD(ACTIVATOR,            CREATE_DEFAULT_INSTANCE_OF_T,   CreateDefaultInstance,  GM_RetT)

DEFINE_CLASS(ACCESS_VIOLATION_EXCEPTION, System,            AccessViolationException)
DEFINE_FIELD(ACCESS_VIOLATION_EXCEPTION, IP,                _ip)
DEFINE_FIELD(ACCESS_VIOLATION_EXCEPTION, TARGET,            _target)
DEFINE_FIELD(ACCESS_VIOLATION_EXCEPTION, ACCESSTYPE,        _accessType)

DEFINE_CLASS(APPCONTEXT,            System,                 AppContext)
DEFINE_METHOD(APPCONTEXT,   SETUP,              Setup,          SM_PtrPtrChar_PtrPtrChar_Int_RetVoid)
DEFINE_METHOD(APPCONTEXT,   ON_PROCESS_EXIT,    OnProcessExit,  SM_RetVoid)
DEFINE_METHOD(APPCONTEXT,   ON_UNHANDLED_EXCEPTION,     OnUnhandledException,  SM_Obj_RetVoid)
DEFINE_FIELD(APPCONTEXT, FIRST_CHANCE_EXCEPTION,        FirstChanceException)

DEFINE_CLASS(ARG_ITERATOR,          System,                 ArgIterator)
DEFINE_CLASS_U(System,              ArgIterator,            VARARGS)  // Includes a SigPointer.
DEFINE_METHOD(ARG_ITERATOR,         CTOR2,                  .ctor,                      IM_RuntimeArgumentHandle_PtrVoid_RetVoid)

DEFINE_CLASS(ARGUMENT_HANDLE,       System,                 RuntimeArgumentHandle)

DEFINE_CLASS(ARRAY,                 System,                 Array)
DEFINE_METHOD(ARRAY,                CREATEINSTANCEMDARRAY,  CreateInstanceMDArray,      SM_IntPtr_UInt_VoidPtr_RetObj)

DEFINE_CLASS(ARRAY_WITH_OFFSET,     Interop,                ArrayWithOffset)
DEFINE_FIELD(ARRAY_WITH_OFFSET,     M_ARRAY,                m_array)
DEFINE_FIELD(ARRAY_WITH_OFFSET,     M_OFFSET,               m_offset)
DEFINE_FIELD(ARRAY_WITH_OFFSET,     M_COUNT,                m_count)

DEFINE_CLASS(ASSEMBLY_NAME,         Reflection,             AssemblyName)
DEFINE_METHOD(ASSEMBLY_NAME,        CTOR,                   .ctor,                     IM_PtrNativeAssemblyNameParts)
DEFINE_METHOD(ASSEMBLY_NAME,        PARSE_AS_ASSEMBLYSPEC,  ParseAsAssemblySpec,       SM_PtrCharPtrVoid)

DEFINE_CLASS(NATIVE_ASSEMBLY_NAME_PARTS,   Reflection,             NativeAssemblyNameParts)
// ASSEMBLYBASE is System.ReflectionAssembly while ASSEMBLY is System.Reflection.RuntimeAssembly
// Maybe we should reverse these two names
DEFINE_CLASS(ASSEMBLYBASE,          Reflection,             Assembly)

DEFINE_CLASS_U(Reflection,             RuntimeAssembly,            AssemblyBaseObject)
DEFINE_FIELD_U(_ModuleResolve,             AssemblyBaseObject,     m_pModuleEventHandler)
DEFINE_FIELD_U(m_fullname,                 AssemblyBaseObject,     m_fullname)
DEFINE_FIELD_U(m_syncRoot,                 AssemblyBaseObject,     m_pSyncRoot)
DEFINE_FIELD_U(m_assembly,                 AssemblyBaseObject,     m_pAssembly)
DEFINE_CLASS(ASSEMBLY,              Reflection,             RuntimeAssembly)
#ifdef FOR_ILLINK
DEFINE_METHOD(ASSEMBLY,             CTOR,                   .ctor,                      IM_RetVoid)
#endif // FOR_ILLINK

DEFINE_CLASS(ASYNCCALLBACK,         System,                 AsyncCallback)
DEFINE_CLASS(ATTRIBUTE,             System,                 Attribute)

DEFINE_CLASS_U(System,              Signature,          SignatureNative)

DEFINE_CLASS(BINDER,                Reflection,             Binder)

DEFINE_CLASS(BINDING_FLAGS,         Reflection,             BindingFlags)

DEFINE_CLASS_U(System,                 RuntimeType,            ReflectClassBaseObject)
DEFINE_FIELD_U(m_cache,                ReflectClassBaseObject,        m_cache)
DEFINE_FIELD_U(m_handle,               ReflectClassBaseObject,        m_typeHandle)
DEFINE_FIELD_U(m_keepalive,            ReflectClassBaseObject,        m_keepalive)
DEFINE_CLASS(CLASS,                 System,                 RuntimeType)
DEFINE_FIELD(CLASS,                 TYPEHANDLE,             m_handle)
DEFINE_METHOD(CLASS,                GET_PROPERTIES,         GetProperties,              IM_BindingFlags_RetArrPropertyInfo)
DEFINE_METHOD(CLASS,                GET_FIELDS,             GetFields,                  IM_BindingFlags_RetArrFieldInfo)
DEFINE_METHOD(CLASS,                GET_METHODS,            GetMethods,                 IM_BindingFlags_RetArrMethodInfo)
DEFINE_METHOD(CLASS,                INVOKE_MEMBER,          InvokeMember,               IM_Str_BindingFlags_Binder_Obj_ArrObj_ArrParameterModifier_CultureInfo_ArrStr_RetObj)
DEFINE_METHOD(CLASS,                GET_METHOD_BASE,        GetMethodBase,              SM_RuntimeType_RuntimeMethodHandleInternal_RetMethodBase)
DEFINE_METHOD(CLASS,                GET_FIELD_INFO,         GetFieldInfo,               SM_RuntimeType_IRuntimeFieldInfo_RetFieldInfo)
#ifdef FOR_ILLINK
DEFINE_METHOD(CLASS,                CTOR,                   .ctor,                      IM_RetVoid)
#endif // FOR_ILLINK

#ifdef FEATURE_COMINTEROP
DEFINE_METHOD(CLASS,                FORWARD_CALL_TO_INVOKE, ForwardCallToInvokeMember,  IM_Str_BindingFlags_Obj_ArrObj_ArrBool_ArrInt_ArrType_Type_RetObj)
#endif // FEATURE_COMINTEROP

BEGIN_ILLINK_FEATURE_SWITCH(System.Runtime.InteropServices.BuiltInComInterop.IsSupported, true, true)
#ifdef FEATURE_COMINTEROP
DEFINE_CLASS(BSTR_WRAPPER,          Interop,                BStrWrapper)
DEFINE_CLASS(CURRENCY_WRAPPER,      Interop,                CurrencyWrapper)
DEFINE_CLASS(DISPATCH_WRAPPER,      Interop,                DispatchWrapper)
DEFINE_CLASS(ERROR_WRAPPER,         Interop,                ErrorWrapper)
DEFINE_CLASS(UNKNOWN_WRAPPER,       Interop,                UnknownWrapper)
DEFINE_CLASS(VARIANT_WRAPPER,       Interop,                VariantWrapper)
#endif // FEATURE_COMINTEROP
END_ILLINK_FEATURE_SWITCH()

BEGIN_ILLINK_FEATURE_SWITCH(System.Runtime.InteropServices.BuiltInComInterop.IsSupported, true, true)
#ifdef FEATURE_COMINTEROP
DEFINE_CLASS_U(System,                 __ComObject,            ComObject)
DEFINE_FIELD_U(m_ObjectToDataMap,      ComObject,              m_ObjectToDataMap)
DEFINE_CLASS(COM_OBJECT,            System,                 __ComObject)
DEFINE_METHOD(COM_OBJECT,           RELEASE_ALL_DATA,       ReleaseAllData,             IM_RetVoid)
DEFINE_METHOD(COM_OBJECT,           GET_EVENT_PROVIDER,     GetEventProvider,           IM_Class_RetObj)
#ifdef FOR_ILLINK
DEFINE_METHOD(COM_OBJECT,           CTOR,                   .ctor,                      IM_RetVoid)
#endif // FOR_ILLINK

DEFINE_CLASS(LICENSE_INTEROP_PROXY,  InternalInteropServices, LicenseInteropProxy)
DEFINE_METHOD(LICENSE_INTEROP_PROXY, CREATE,                  Create,                  SM_RetObj)
DEFINE_METHOD(LICENSE_INTEROP_PROXY, GETCURRENTCONTEXTINFO,   GetCurrentContextInfo,   IM_RuntimeTypeHandle_RefBool_RefIntPtr_RetVoid)
DEFINE_METHOD(LICENSE_INTEROP_PROXY, SAVEKEYINCURRENTCONTEXT, SaveKeyInCurrentContext, IM_IntPtr_RetVoid)

#endif // FEATURE_COMINTEROP
END_ILLINK_FEATURE_SWITCH()

DEFINE_CLASS(CRITICAL_HANDLE,       Interop,                CriticalHandle)
DEFINE_FIELD(CRITICAL_HANDLE,       HANDLE,                 handle)
DEFINE_METHOD(CRITICAL_HANDLE,      RELEASE_HANDLE,         ReleaseHandle,              IM_RetBool)
DEFINE_METHOD(CRITICAL_HANDLE,      GET_IS_INVALID,         get_IsInvalid,              IM_RetBool)
DEFINE_METHOD(CRITICAL_HANDLE,      DISPOSE,                Dispose,                    IM_RetVoid)
DEFINE_METHOD(CRITICAL_HANDLE,      DISPOSE_BOOL,           Dispose,                    IM_Bool_RetVoid)

DEFINE_CLASS(HANDLE_REF,            Interop,                HandleRef)
DEFINE_FIELD(HANDLE_REF,            WRAPPER,                _wrapper)
DEFINE_FIELD(HANDLE_REF,            HANDLE,                 _handle)

DEFINE_CLASS(CRITICAL_FINALIZER_OBJECT, ConstrainedExecution, CriticalFinalizerObject)
DEFINE_METHOD(CRITICAL_FINALIZER_OBJECT, FINALIZE,          Finalize,                   IM_RetVoid)

DEFINE_CLASS_U(Reflection,             RuntimeConstructorInfo,  NoClass)
DEFINE_FIELD_U(m_handle,                   ReflectMethodObject, m_pMD)
DEFINE_CLASS(CONSTRUCTOR,           Reflection,             RuntimeConstructorInfo)

DEFINE_CLASS_U(System,                 RuntimeMethodInfoStub,     ReflectMethodObject)
DEFINE_FIELD_U(m_value,                   ReflectMethodObject, m_pMD)
DEFINE_CLASS(STUBMETHODINFO,      System,                 RuntimeMethodInfoStub)
DEFINE_FIELD(STUBMETHODINFO,      HANDLE,                 m_value)
DEFINE_METHOD(STUBMETHODINFO,     FROMPTR,                FromPtr,                     SM_IntPtr_RetObj)

DEFINE_CLASS(CONSTRUCTOR_INFO,      Reflection,             ConstructorInfo)

DEFINE_CLASS_U(Globalization,          CultureInfo,        CultureInfoBaseObject)
DEFINE_FIELD_U(_compareInfo,       CultureInfoBaseObject,  _compareInfo)
DEFINE_FIELD_U(_textInfo,          CultureInfoBaseObject,  _textInfo)
DEFINE_FIELD_U(_numInfo,           CultureInfoBaseObject,  _numInfo)
DEFINE_FIELD_U(_dateTimeInfo,      CultureInfoBaseObject,  _dateTimeInfo)
DEFINE_FIELD_U(_calendar,          CultureInfoBaseObject,  _calendar)
DEFINE_FIELD_U(_consoleFallbackCulture, CultureInfoBaseObject, _consoleFallbackCulture)
DEFINE_FIELD_U(_name,             CultureInfoBaseObject,  _name)
DEFINE_FIELD_U(_nonSortName,      CultureInfoBaseObject,  _nonSortName)
DEFINE_FIELD_U(_sortName,         CultureInfoBaseObject,  _sortName)
DEFINE_FIELD_U(_parent,           CultureInfoBaseObject,  _parent)
DEFINE_FIELD_U(_isReadOnly,        CultureInfoBaseObject,  _isReadOnly)
DEFINE_FIELD_U(_isInherited,      CultureInfoBaseObject,  _isInherited)
DEFINE_CLASS(CULTURE_INFO,          Globalization,          CultureInfo)
DEFINE_FIELD(CULTURE_INFO,          CURRENT_CULTURE,        s_userDefaultCulture)
DEFINE_METHOD(CULTURE_INFO,         INT_CTOR,               .ctor,                      IM_Int_RetVoid)
DEFINE_PROPERTY(CULTURE_INFO,       ID,                     LCID,                       Int)
DEFINE_FIELD(CULTURE_INFO,          CULTURE,                s_currentThreadCulture)
DEFINE_FIELD(CULTURE_INFO,          UI_CULTURE,             s_currentThreadUICulture)
DEFINE_STATIC_SET_PROPERTY(CULTURE_INFO, CURRENT_CULTURE,      CurrentCulture,     CultureInfo)
DEFINE_STATIC_SET_PROPERTY(CULTURE_INFO, CURRENT_UI_CULTURE,   CurrentUICulture,   CultureInfo)

DEFINE_CLASS(CURRENCY,              System,                 Currency)
DEFINE_METHOD(CURRENCY,             DECIMAL_CTOR,           .ctor,                      IM_Dec_RetVoid)

DEFINE_CLASS(DATE_TIME,             System,                 DateTime)
DEFINE_METHOD(DATE_TIME,            LONG_CTOR,              .ctor,                      IM_Long_RetVoid)

DEFINE_CLASS(DECIMAL,               System,                 Decimal)
DEFINE_METHOD(DECIMAL,              CURRENCY_CTOR,          .ctor,                      IM_Currency_RetVoid)

DEFINE_CLASS_U(System,                 Delegate,            NoClass)
DEFINE_FIELD_U(_target,                    DelegateObject,   _target)
DEFINE_FIELD_U(_methodBase,                DelegateObject,   _methodBase)
DEFINE_FIELD_U(_methodPtr,                 DelegateObject,   _methodPtr)
DEFINE_FIELD_U(_methodPtrAux,              DelegateObject,   _methodPtrAux)
DEFINE_CLASS(DELEGATE,              System,                 Delegate)
DEFINE_FIELD(DELEGATE,            TARGET,                 _target)
DEFINE_FIELD(DELEGATE,            METHOD_PTR,             _methodPtr)
DEFINE_FIELD(DELEGATE,            METHOD_PTR_AUX,         _methodPtrAux)
DEFINE_METHOD(DELEGATE,             CONSTRUCT_DELEGATE,     DelegateConstruct,          IM_Obj_IntPtr_RetVoid)
DEFINE_METHOD(DELEGATE,             GET_INVOKE_METHOD,      GetInvokeMethod,            IM_RetIntPtr)

DEFINE_CLASS(INT128,               System,                 Int128)
DEFINE_CLASS(UINT128,              System,                 UInt128)

DEFINE_CLASS(MATH,                  System,                 Math)
DEFINE_METHOD(MATH,                 CONVERT_TO_INT32_CHECKED,    ConvertToInt32Checked,    NoSig)
DEFINE_METHOD(MATH,                 CONVERT_TO_UINT32_CHECKED,   ConvertToUInt32Checked,   NoSig)
DEFINE_METHOD(MATH,                 CONVERT_TO_INT64_CHECKED,    ConvertToInt64Checked,    NoSig)
DEFINE_METHOD(MATH,                 CONVERT_TO_UINT64_CHECKED,   ConvertToUInt64Checked,   NoSig)

#ifdef TARGET_32BIT
DEFINE_METHOD(MATH,                 MULTIPLY_CHECKED_INT64,      MultiplyChecked,          SM_Long_Long_RetLong)
DEFINE_METHOD(MATH,                 MULTIPLY_CHECKED_UINT64,     MultiplyChecked,          SM_ULong_ULong_RetULong)
DEFINE_METHOD(MATH,                 DIV_INT32,                   DivInt32,                 NoSig)
DEFINE_METHOD(MATH,                 DIV_UINT32,                  DivUInt32,                NoSig)
DEFINE_METHOD(MATH,                 DIV_INT64,                   DivInt64,                 NoSig)
DEFINE_METHOD(MATH,                 DIV_UINT64,                  DivUInt64,                NoSig)
DEFINE_METHOD(MATH,                 MOD_INT32,                   ModInt32,                 NoSig)
DEFINE_METHOD(MATH,                 MOD_UINT32,                  ModUInt32,                NoSig)
DEFINE_METHOD(MATH,                 MOD_INT64,                   ModInt64,                 NoSig)
DEFINE_METHOD(MATH,                 MOD_UINT64,                  ModUInt64,                NoSig)
#endif // TARGET_32BIT

DEFINE_CLASS(DYNAMICMETHOD,         ReflectionEmit,         DynamicMethod)

DEFINE_CLASS(DYNAMICRESOLVER,       ReflectionEmit,         DynamicResolver)
DEFINE_FIELD(DYNAMICRESOLVER,       DYNAMIC_METHOD,         m_method)

DEFINE_CLASS(EMPTY,                 System,                 Empty)

DEFINE_CLASS(ENC_HELPER,            Diagnostics,            EditAndContinueHelper)
DEFINE_FIELD(ENC_HELPER,            OBJECT_REFERENCE,       _objectReference)

DEFINE_CLASS(ENCODING,              Text,                   Encoding)

DEFINE_CLASS(RUNE,                  Text,                   Rune)

DEFINE_CLASS(ENUM,                  System,                 Enum)

DEFINE_CLASS(ENVIRONMENT,           System,                 Environment)
DEFINE_METHOD(ENVIRONMENT,       GET_RESOURCE_STRING_LOCAL, GetResourceStringLocal,     SM_Str_RetStr)
DEFINE_METHOD(ENVIRONMENT,       INITIALIZE_COMMAND_LINE_ARGS, InitializeCommandLineArgs, SM_PtrChar_Int_PtrPtrChar_RetArrStr)

DEFINE_CLASS(EVENT,                 Reflection,             RuntimeEventInfo)

DEFINE_CLASS(EVENT_INFO,            Reflection,             EventInfo)

DEFINE_CLASS_U(System,                 Exception,      ExceptionObject)
DEFINE_FIELD_U(_exceptionMethod,   ExceptionObject,    _exceptionMethod)
DEFINE_FIELD_U(_message,           ExceptionObject,    _message)
DEFINE_FIELD_U(_data,              ExceptionObject,    _data)
DEFINE_FIELD_U(_innerException,    ExceptionObject,    _innerException)
DEFINE_FIELD_U(_helpURL,           ExceptionObject,    _helpURL)
DEFINE_FIELD_U(_stackTrace,        ExceptionObject,    _stackTrace)
DEFINE_FIELD_U(_watsonBuckets,     ExceptionObject,    _watsonBuckets)
DEFINE_FIELD_U(_stackTraceString,  ExceptionObject,    _stackTraceString)
DEFINE_FIELD_U(_remoteStackTraceString, ExceptionObject, _remoteStackTraceString)
DEFINE_FIELD_U(_source,            ExceptionObject,    _source)
DEFINE_FIELD_U(_ipForWatsonBuckets,ExceptionObject,    _ipForWatsonBuckets)
DEFINE_FIELD_U(_xptrs,             ExceptionObject,    _xptrs)
DEFINE_FIELD_U(_xcode,             ExceptionObject,    _xcode)
DEFINE_FIELD_U(_HResult,           ExceptionObject,    _HResult)
DEFINE_CLASS(EXCEPTION,             System,                 Exception)
DEFINE_METHOD(EXCEPTION,            INTERNAL_PRESERVE_STACK_TRACE, InternalPreserveStackTrace, IM_RetVoid)
// Following Exception members are only used when FEATURE_COMINTEROP
DEFINE_METHOD(EXCEPTION,            GET_CLASS_NAME,         GetClassName,               IM_RetStr)
DEFINE_PROPERTY(EXCEPTION,          MESSAGE,                Message,                    Str)
DEFINE_PROPERTY(EXCEPTION,          SOURCE,                 Source,                     Str)
DEFINE_METHOD(EXCEPTION,            GET_HELP_CONTEXT,       GetHelpContext,             IM_RefUInt_RetStr)


DEFINE_CLASS(SYSTEM_EXCEPTION,      System,                 SystemException)
DEFINE_METHOD(SYSTEM_EXCEPTION,     STR_EX_CTOR,            .ctor,                      IM_Str_Exception_RetVoid)


DEFINE_CLASS(TYPE_INIT_EXCEPTION,   System,                 TypeInitializationException)
DEFINE_METHOD(TYPE_INIT_EXCEPTION,  STR_EX_CTOR,            .ctor,                      IM_Str_Exception_RetVoid)

DEFINE_CLASS(THREAD_START_EXCEPTION,Threading,              ThreadStartException)
DEFINE_METHOD(THREAD_START_EXCEPTION,EX_CTOR,               .ctor,                      IM_Exception_RetVoid)

DEFINE_CLASS(VALUETASK_1, Tasks, ValueTask`1)

DEFINE_CLASS(VALUETASK, Tasks, ValueTask)
DEFINE_METHOD(VALUETASK, FROM_EXCEPTION, FromException, SM_Exception_RetValueTask)
DEFINE_METHOD(VALUETASK, FROM_EXCEPTION_1, FromException, GM_Exception_RetValueTaskOfT)
DEFINE_METHOD(VALUETASK, FROM_RESULT_T, FromResult, GM_T_RetValueTaskOfT)
DEFINE_METHOD(VALUETASK, GET_COMPLETED_TASK, get_CompletedTask, SM_RetValueTask)

DEFINE_CLASS(TASK_1, Tasks, Task`1)
DEFINE_METHOD(TASK_1, GET_AWAITER, GetAwaiter, NoSig)

DEFINE_CLASS(TASK, Tasks, Task)
DEFINE_METHOD(TASK, FROM_EXCEPTION, FromException, SM_Exception_RetTask)
DEFINE_METHOD(TASK, FROM_EXCEPTION_1, FromException, GM_Exception_RetTaskOfT)
DEFINE_METHOD(TASK, FROM_RESULT_T, FromResult, GM_T_RetTaskOfT)
DEFINE_METHOD(TASK, GET_COMPLETED_TASK, get_CompletedTask, SM_RetTask)
DEFINE_METHOD(TASK, GET_AWAITER, GetAwaiter, NoSig)

DEFINE_CLASS(TASK_AWAITER_1, CompilerServices, TaskAwaiter`1)
DEFINE_METHOD(TASK_AWAITER_1, GET_ISCOMPLETED, get_IsCompleted, NoSig)
DEFINE_METHOD(TASK_AWAITER_1, GET_RESULT, GetResult, NoSig)

DEFINE_CLASS(TASK_AWAITER, CompilerServices, TaskAwaiter)
DEFINE_METHOD(TASK_AWAITER, GET_ISCOMPLETED, get_IsCompleted, NoSig)
DEFINE_METHOD(TASK_AWAITER, GET_RESULT, GetResult, NoSig)

DEFINE_CLASS(TYPE_HANDLE,           System,                 RuntimeTypeHandle)
DEFINE_CLASS(RT_TYPE_HANDLE,        System,                 RuntimeTypeHandle)
DEFINE_METHOD(RT_TYPE_HANDLE,       PVOID_CTOR,             .ctor,                      IM_RuntimeType_RetVoid)
DEFINE_METHOD(RT_TYPE_HANDLE,       GETRUNTIMETYPEFROMHANDLE,GetRuntimeTypeFromHandle,  SM_IntPtr_RetRuntimeType)
DEFINE_METHOD(RT_TYPE_HANDLE,       GETRUNTIMETYPEFROMHANDLEMAYBENULL,GetRuntimeTypeFromHandleMaybeNull,  SM_IntPtr_RetRuntimeType)
DEFINE_METHOD(RT_TYPE_HANDLE,       TO_INTPTR,              ToIntPtr,                   SM_RuntimeTypeHandle_RetIntPtr)
#ifdef FEATURE_COMINTEROP
DEFINE_METHOD(RT_TYPE_HANDLE,       ALLOCATECOMOBJECT,      AllocateComObject,          SM_VoidPtr_RetObj)
#endif
DEFINE_FIELD(RT_TYPE_HANDLE,        M_TYPE,                 m_type)

// DEFINE_CLASS(TYPED_REFERENCE,  System,           TypedReference)
DEFINE_METHOD(TYPED_REFERENCE, GETREFANY,        GetRefAny,                   NoSig)

DEFINE_CLASS(TYPE_NAME_RESOLVER,    Reflection,             TypeNameResolver)
DEFINE_METHOD(TYPE_NAME_RESOLVER,   GET_TYPE_HELPER,        GetTypeHelper,              SM_Type_CharPtr_RuntimeAssembly_Bool_Bool_IntPtr_RetRuntimeType)

DEFINE_CLASS_U(Reflection,          RtFieldInfo,            NoClass)
DEFINE_FIELD_U(m_fieldHandle,       ReflectFieldObject,     m_pFD)
DEFINE_CLASS(RT_FIELD_INFO,         Reflection,             RtFieldInfo)
DEFINE_FIELD(RT_FIELD_INFO,         HANDLE,                 m_fieldHandle)

DEFINE_CLASS_U(System,              RuntimeFieldInfoStub,   ReflectFieldObject)
DEFINE_FIELD_U(m_fieldHandle,       ReflectFieldObject,     m_pFD)
DEFINE_CLASS(STUBFIELDINFO,         System,                 RuntimeFieldInfoStub)
DEFINE_METHOD(STUBFIELDINFO,        FROMPTR,                FromPtr,                     SM_IntPtr_RetObj)
#ifdef FOR_ILLINK
DEFINE_METHOD(STUBFIELDINFO,        CTOR,                   .ctor,                      IM_RetVoid)
#endif // FOR_ILLINK

DEFINE_CLASS(FIELD,                 Reflection,             RuntimeFieldInfo)

DEFINE_CLASS(FIELD_HANDLE,          System,                 RuntimeFieldHandle)
DEFINE_FIELD(FIELD_HANDLE,          M_FIELD,                m_ptr)
DEFINE_METHOD(FIELD_HANDLE,         GETFIELDADDR,           GetFieldAddr,       SM_Obj_VoidPtr_RetVoidPtr)
DEFINE_METHOD(FIELD_HANDLE,         GETSTATICFIELDADDR,     GetStaticFieldAddr, SM_VoidPtr_RetVoidPtr)

DEFINE_CLASS(I_RT_FIELD_INFO,       System,                 IRuntimeFieldInfo)

DEFINE_CLASS(FIELD_INFO,            Reflection,             FieldInfo)
DEFINE_METHOD(FIELD_INFO,           SET_VALUE,              SetValue,                   IM_Obj_Obj_BindingFlags_Binder_CultureInfo_RetVoid)
DEFINE_METHOD(FIELD_INFO,           GET_VALUE,              GetValue,                   IM_Obj_RetObj)


DEFINE_CLASS(GUID,                  System,                 Guid)

BEGIN_ILLINK_FEATURE_SWITCH(System.Runtime.InteropServices.BuiltInComInterop.IsSupported, true, true)
#ifdef FEATURE_COMINTEROP
DEFINE_CLASS(VARIANT,               System,                 Variant)
DEFINE_METHOD(VARIANT,              CONVERT_OBJECT_TO_VARIANT,MarshalHelperConvertObjectToVariant,SM_Obj_RefComVariant_RetVoid)
DEFINE_METHOD(VARIANT,              CAST_VARIANT,           MarshalHelperCastVariant,   SM_Obj_Int_RefComVariant_RetVoid)
DEFINE_METHOD(VARIANT,              CONVERT_VARIANT_TO_OBJECT,MarshalHelperConvertVariantToObject,SM_RefComVariant_RetObject)

#endif // FEATURE_COMINTEROP
END_ILLINK_FEATURE_SWITCH()

DEFINE_CLASS(IASYNCRESULT,          System,                 IAsyncResult)

DEFINE_CLASS(ICUSTOM_ATTR_PROVIDER, Reflection,             ICustomAttributeProvider)
DEFINE_METHOD(ICUSTOM_ATTR_PROVIDER,GET_CUSTOM_ATTRIBUTES,  GetCustomAttributes,        IM_Type_RetArrObj)

DEFINE_CLASS(ICUSTOM_MARSHALER,     Interop,                ICustomMarshaler)

DEFINE_CLASS(ICUSTOMADAPTER,    Interop,    ICustomAdapter)

DEFINE_CLASS(IDYNAMICINTERFACECASTABLE,         Interop,   IDynamicInterfaceCastable)
DEFINE_CLASS(DYNAMICINTERFACECASTABLEHELPERS,   Interop,   DynamicInterfaceCastableHelpers)
DEFINE_METHOD(DYNAMICINTERFACECASTABLEHELPERS,  IS_INTERFACE_IMPLEMENTED,       IsInterfaceImplemented,     SM_IDynamicInterfaceCastable_RuntimeType_Bool_RetBool)
DEFINE_METHOD(DYNAMICINTERFACECASTABLEHELPERS,  GET_INTERFACE_IMPLEMENTATION,   GetInterfaceImplementation, SM_IDynamicInterfaceCastable_RuntimeType_RetRtType)

BEGIN_ILLINK_FEATURE_SWITCH(System.Runtime.InteropServices.BuiltInComInterop.IsSupported, true, true)
#ifdef FEATURE_COMINTEROP
DEFINE_CLASS(ICUSTOM_QUERYINTERFACE,      Interop,          ICustomQueryInterface)
DEFINE_METHOD(ICUSTOM_QUERYINTERFACE,     GET_INTERFACE,    GetInterface,               IM_RefGuid_OutIntPtr_RetCustomQueryInterfaceResult)
DEFINE_CLASS(CUSTOMQUERYINTERFACERESULT,  Interop,          CustomQueryInterfaceResult)

DEFINE_CLASS(ENUMERATORTOENUMVARIANTMARSHALER,   CustomMarshalers,  EnumeratorToEnumVariantMarshaler)
DEFINE_METHOD(ENUMERATORTOENUMVARIANTMARSHALER,  INTERNALMARSHALNATIVETOMANAGED,    InternalMarshalNativeToManaged,   SM_IntPtr_RetObj)
#endif //FEATURE_COMINTEROP
END_ILLINK_FEATURE_SWITCH()

#ifdef FEATURE_COMWRAPPERS
DEFINE_CLASS(COMWRAPPERS,                 Interop,          ComWrappers)
DEFINE_CLASS(CREATEOBJECTFLAGS,           Interop,          CreateObjectFlags)
DEFINE_CLASS(MANAGED_OBJECT_WRAPPER_HOLDER,Interop,         ComWrappers+ManagedObjectWrapperHolder)
DEFINE_CLASS(NATIVE_OBJECT_WRAPPER,       Interop,         ComWrappers+NativeObjectWrapper)
DEFINE_METHOD(COMWRAPPERS,     CALL_ICUSTOMQUERYINTERFACE,  CallICustomQueryInterface,  SM_ManagedObjectWrapperHolder_RefGuid_RefIntPtr_RetInt)
DEFINE_METHOD(COMWRAPPERS, GET_OR_CREATE_COM_INTERFACE_FOR_OBJECT_WITH_GLOBAL_MARSHALLING_INSTANCE, GetOrCreateComInterfaceForObjectWithGlobalMarshallingInstance, SM_Obj_RetIntPtr)
DEFINE_METHOD(COMWRAPPERS, GET_OR_CREATE_OBJECT_FOR_COM_INSTANCE_WITH_GLOBAL_MARSHALLING_INSTANCE, GetOrCreateObjectForComInstanceWithGlobalMarshallingInstance, SM_IntPtr_CreateObjectFlags_RetObj)
DEFINE_FIELD(COMWRAPPERS, NAITVE_OBJECT_WRAPPER_TABLE, s_nativeObjectWrapperTable)
DEFINE_FIELD(COMWRAPPERS, ALL_MANAGED_OBJECT_WRAPPER_TABLE, s_allManagedObjectWrapperTable)

DEFINE_CLASS_U(Interop, ComWrappers+ManagedObjectWrapperHolder, ManagedObjectWrapperHolderObject)
DEFINE_FIELD_U(_wrappedObject, ManagedObjectWrapperHolderObject, _wrappedObject)
DEFINE_FIELD_U(_wrapper, ManagedObjectWrapperHolderObject, _wrapper)
DEFINE_CLASS_U(Interop, ComWrappers+NativeObjectWrapper, NativeObjectWrapperObject)
DEFINE_FIELD_U(_comWrappers, NativeObjectWrapperObject, _comWrappers)
DEFINE_FIELD_U(_externalComObject, NativeObjectWrapperObject, _externalComObject)
DEFINE_FIELD_U(_inner, NativeObjectWrapperObject, _inner)
DEFINE_FIELD_U(_aggregatedManagedObjectWrapper, NativeObjectWrapperObject, _aggregatedManagedObjectWrapper)
DEFINE_FIELD_U(_uniqueInstance, NativeObjectWrapperObject, _uniqueInstance)
DEFINE_FIELD_U(_proxyHandle, NativeObjectWrapperObject, _proxyHandle)
DEFINE_FIELD_U(_proxyHandleTrackingResurrection, NativeObjectWrapperObject, _proxyHandleTrackingResurrection)
DEFINE_CLASS_U(Interop, ComWrappers+ReferenceTrackerNativeObjectWrapper, ReferenceTrackerNativeObjectWrapperObject)
DEFINE_FIELD_U(_trackerObject, ReferenceTrackerNativeObjectWrapperObject, _trackerObject)
DEFINE_FIELD_U(_contextToken, ReferenceTrackerNativeObjectWrapperObject, _contextToken)
DEFINE_FIELD_U(_trackerObjectDisconnected, ReferenceTrackerNativeObjectWrapperObject, _trackerObjectDisconnected)
DEFINE_CLASS_U(Interop, GCHandleSet+Entry, GCHandleSetEntryObject)
DEFINE_FIELD_U(_next, GCHandleSetEntryObject, _next)
DEFINE_FIELD_U(_value, GCHandleSetEntryObject, _value)
DEFINE_CLASS_U(Interop, GCHandleSet, NoClass)
DEFINE_FIELD_U(_buckets, GCHandleSetObject, _buckets)
#endif //FEATURE_COMWRAPPERS

#ifdef FEATURE_OBJCMARSHAL
DEFINE_CLASS(OBJCMARSHAL,    ObjectiveC, ObjectiveCMarshal)
DEFINE_METHOD(OBJCMARSHAL,   AVAILABLEUNHANDLEDEXCEPTIONPROPAGATION, AvailableUnhandledExceptionPropagation, SM_RetBool)
DEFINE_METHOD(OBJCMARSHAL,   INVOKEUNHANDLEDEXCEPTIONPROPAGATION,    InvokeUnhandledExceptionPropagation,    SM_Exception_Obj_RefIntPtr_RetVoidPtr)
#endif // FEATURE_OBJCMARSHAL

DEFINE_CLASS(IENUMERATOR,           Collections,            IEnumerator)

DEFINE_CLASS(IENUMERABLE,           Collections,            IEnumerable)
DEFINE_CLASS(ICOLLECTION,           Collections,            ICollection)
DEFINE_CLASS(ILIST,                 Collections,            IList)
DEFINE_CLASS(IDISPOSABLE,           System,                 IDisposable)

DEFINE_CLASS(IREFLECT,              Reflection,             IReflect)
DEFINE_METHOD(IREFLECT,             GET_PROPERTIES,         GetProperties,              IM_BindingFlags_RetArrPropertyInfo)
DEFINE_METHOD(IREFLECT,             GET_FIELDS,             GetFields,                  IM_BindingFlags_RetArrFieldInfo)
DEFINE_METHOD(IREFLECT,             GET_METHODS,            GetMethods,                 IM_BindingFlags_RetArrMethodInfo)
DEFINE_METHOD(IREFLECT,             INVOKE_MEMBER,          InvokeMember,               IM_Str_BindingFlags_Binder_Obj_ArrObj_ArrParameterModifier_CultureInfo_ArrStr_RetObj)

BEGIN_ILLINK_FEATURE_SWITCH(System.Runtime.InteropServices.BuiltInComInterop.IsSupported, true, true)
#ifdef FEATURE_COMINTEROP
DEFINE_CLASS(LCID_CONVERSION_TYPE,  Interop,                LCIDConversionAttribute)
#endif // FEATURE_COMINTEROP
END_ILLINK_FEATURE_SWITCH()

DEFINE_CLASS(MARSHAL,               Interop,                Marshal)
#ifdef FEATURE_COMINTEROP
DEFINE_METHOD(MARSHAL,              GET_HR_FOR_EXCEPTION,              GetHRForException,             SM_Exception_RetInt)
#endif // FEATURE_COMINTEROP
DEFINE_METHOD(MARSHAL,              GET_FUNCTION_POINTER_FOR_DELEGATE,          GetFunctionPointerForDelegate,          SM_Delegate_RetIntPtr)
DEFINE_METHOD(MARSHAL,              GET_DELEGATE_FOR_FUNCTION_POINTER,          GetDelegateForFunctionPointer,          SM_IntPtr_Type_RetDelegate)
DEFINE_METHOD(MARSHAL,              GET_DELEGATE_FOR_FUNCTION_POINTER_INTERNAL, GetDelegateForFunctionPointerInternal,  SM_IntPtr_RuntimeType_RetDelegate)

DEFINE_METHOD(MARSHAL,              ALLOC_CO_TASK_MEM,                 AllocCoTaskMem,                SM_Int_RetIntPtr)
DEFINE_METHOD(MARSHAL,              FREE_CO_TASK_MEM,                  FreeCoTaskMem,                 SM_IntPtr_RetVoid)
DEFINE_FIELD(MARSHAL,               SYSTEM_MAX_DBCS_CHAR_SIZE,         SystemMaxDBCSCharSize)

DEFINE_METHOD(MARSHAL,              STRUCTURE_TO_PTR,                  StructureToPtr,                SM_Obj_IntPtr_Bool_RetVoid)
DEFINE_METHOD(MARSHAL,              PTR_TO_STRUCTURE,                  PtrToStructure,                SM_IntPtr_Obj_RetVoid)
DEFINE_METHOD(MARSHAL,              DESTROY_STRUCTURE,                 DestroyStructure,              SM_IntPtr_Type_RetVoid)
DEFINE_METHOD(MARSHAL,              SIZEOF_TYPE,                       SizeOf,                        SM_Type_RetInt)

DEFINE_CLASS(NATIVELIBRARY, Interop, NativeLibrary)
DEFINE_METHOD(NATIVELIBRARY,        LOADLIBRARYCALLBACKSTUB, LoadLibraryCallbackStub, SM_Str_AssemblyBase_Bool_UInt_RetIntPtr)

DEFINE_CLASS(VECTOR64T,             Intrinsics,             Vector64`1)
DEFINE_CLASS(VECTOR128T,            Intrinsics,             Vector128`1)
DEFINE_CLASS(VECTOR256T,            Intrinsics,             Vector256`1)
DEFINE_CLASS(VECTOR512T,            Intrinsics,             Vector512`1)

DEFINE_CLASS(VECTORT,               Numerics,               Vector`1)

DEFINE_CLASS(MEMBER,                Reflection,             MemberInfo)

DEFINE_CLASS(METHODBASEINVOKER,     Reflection,             MethodBaseInvoker)

DEFINE_CLASS(INSTANCE_CALLI_HELPER, Reflection,             InstanceCalliHelper)

DEFINE_CLASS_U(Reflection,          RuntimeMethodInfo,      NoClass)
DEFINE_FIELD_U(m_handle,            ReflectMethodObject,    m_pMD)
DEFINE_CLASS(METHOD,                Reflection,             RuntimeMethodInfo)
DEFINE_METHOD(METHOD,               INVOKE,                 Invoke,                     IM_Obj_BindingFlags_Binder_ArrObj_CultureInfo_RetObj)
DEFINE_METHOD(METHOD,               GET_PARAMETERS,         GetParameters,              IM_RetArrParameterInfo)

DEFINE_CLASS(METHOD_BASE,           Reflection,             MethodBase)
DEFINE_METHOD(METHOD_BASE,          GET_METHODDESC,         GetMethodDesc,              IM_RetIntPtr)

DEFINE_CLASS_U(Reflection,             RuntimeExceptionHandlingClause,    RuntimeExceptionHandlingClause)
DEFINE_FIELD_U(_methodBody,            RuntimeExceptionHandlingClause,        _methodBody)
DEFINE_FIELD_U(_flags,                 RuntimeExceptionHandlingClause,        _flags)
DEFINE_FIELD_U(_tryOffset,             RuntimeExceptionHandlingClause,        _tryOffset)
DEFINE_FIELD_U(_tryLength,             RuntimeExceptionHandlingClause,        _tryLength)
DEFINE_FIELD_U(_handlerOffset,         RuntimeExceptionHandlingClause,        _handlerOffset)
DEFINE_FIELD_U(_handlerLength,         RuntimeExceptionHandlingClause,        _handlerLength)
DEFINE_FIELD_U(_catchMetadataToken,    RuntimeExceptionHandlingClause,        _catchToken)
DEFINE_FIELD_U(_filterOffset,          RuntimeExceptionHandlingClause,        _filterOffset)
DEFINE_CLASS(RUNTIME_EH_CLAUSE,             Reflection,             RuntimeExceptionHandlingClause)
#ifdef FOR_ILLINK
DEFINE_METHOD(RUNTIME_EH_CLAUSE,            CTOR,                   .ctor,              IM_RetVoid)
#endif // FOR_ILLINK

DEFINE_CLASS_U(Reflection,             RuntimeLocalVariableInfo,        RuntimeLocalVariableInfo)
DEFINE_FIELD_U(_type,                  RuntimeLocalVariableInfo,        _type)
DEFINE_FIELD_U(_localIndex,            RuntimeLocalVariableInfo,        _localIndex)
DEFINE_FIELD_U(_isPinned,              RuntimeLocalVariableInfo,        _isPinned)
DEFINE_CLASS(RUNTIME_LOCAL_VARIABLE_INFO,   Reflection,             RuntimeLocalVariableInfo)
#ifdef FOR_ILLINK
DEFINE_METHOD(RUNTIME_LOCAL_VARIABLE_INFO,  CTOR,                   .ctor,              IM_RetVoid)
#endif // FOR_ILLINK

DEFINE_CLASS_U(Reflection,             RuntimeMethodBody,           RuntimeMethodBody)
DEFINE_FIELD_U(_IL,                    RuntimeMethodBody,         _IL)
DEFINE_FIELD_U(_exceptionHandlingClauses, RuntimeMethodBody,         _exceptionClauses)
DEFINE_FIELD_U(_localVariables,           RuntimeMethodBody,         _localVariables)
DEFINE_FIELD_U(_methodBase,               RuntimeMethodBody,         _methodBase)
DEFINE_FIELD_U(_localSignatureMetadataToken, RuntimeMethodBody,      _localVarSigToken)
DEFINE_FIELD_U(_maxStackSize,             RuntimeMethodBody,         _maxStackSize)
DEFINE_FIELD_U(_initLocals,               RuntimeMethodBody,         _initLocals)

DEFINE_CLASS(RUNTIME_METHOD_BODY,   Reflection, RuntimeMethodBody)
#ifdef FOR_ILLINK
DEFINE_METHOD(RUNTIME_METHOD_BODY,  CTOR,   .ctor,  IM_RetVoid)
#endif // FOR_ILLINK

DEFINE_CLASS(METHOD_INFO,           Reflection,             MethodInfo)

DEFINE_CLASS(METHOD_HANDLE_INTERNAL,System,                 RuntimeMethodHandleInternal)

DEFINE_CLASS(METHOD_HANDLE,         System,                 RuntimeMethodHandle)
DEFINE_FIELD(METHOD_HANDLE,         METHOD,                 m_value)
DEFINE_METHOD(METHOD_HANDLE,        TO_INTPTR,              ToIntPtr,           SM_RuntimeMethodHandle_RetIntPtr)

DEFINE_CLASS(MISSING,               Reflection,             Missing)
DEFINE_FIELD(MISSING,               VALUE,                  Value)

DEFINE_CLASS_U(Reflection,             RuntimeModule,               ReflectModuleBaseObject)
DEFINE_FIELD_U(m_runtimeType,               ReflectModuleBaseObject,    m_runtimeType)
DEFINE_FIELD_U(m_runtimeAssembly,           ReflectModuleBaseObject,    m_runtimeAssembly)
DEFINE_FIELD_U(m_pData,                     ReflectModuleBaseObject,    m_pData)
DEFINE_CLASS(MODULE,                Reflection,             RuntimeModule)

DEFINE_CLASS(TYPE_BUILDER,          ReflectionEmit,         TypeBuilder)
DEFINE_CLASS(ENUM_BUILDER,          ReflectionEmit,         EnumBuilder)

DEFINE_CLASS_U(System,                 MulticastDelegate,          DelegateObject)
DEFINE_FIELD_U(_invocationList,            DelegateObject,   _invocationList)
DEFINE_FIELD_U(_invocationCount,           DelegateObject,   _invocationCount)
DEFINE_CLASS(MULTICAST_DELEGATE,    System,                 MulticastDelegate)
DEFINE_FIELD(MULTICAST_DELEGATE,    INVOCATION_LIST,        _invocationList)
DEFINE_FIELD(MULTICAST_DELEGATE,    INVOCATION_COUNT,       _invocationCount)
DEFINE_METHOD(MULTICAST_DELEGATE,   CTOR_CLOSED,            CtorClosed,                 IM_Obj_IntPtr_RetVoid)
DEFINE_METHOD(MULTICAST_DELEGATE,   CTOR_CLOSED_STATIC,     CtorClosedStatic,           IM_Obj_IntPtr_RetVoid)
DEFINE_METHOD(MULTICAST_DELEGATE,   CTOR_RT_CLOSED,         CtorRTClosed,               IM_Obj_IntPtr_RetVoid)
DEFINE_METHOD(MULTICAST_DELEGATE,   CTOR_OPENED,            CtorOpened,                 IM_Obj_IntPtr_IntPtr_RetVoid)
DEFINE_METHOD(MULTICAST_DELEGATE,   CTOR_VIRTUAL_DISPATCH,  CtorVirtualDispatch,        IM_Obj_IntPtr_IntPtr_RetVoid)
DEFINE_METHOD(MULTICAST_DELEGATE,   CTOR_COLLECTIBLE_CLOSED_STATIC,     CtorCollectibleClosedStatic,           IM_Obj_IntPtr_IntPtr_RetVoid)
DEFINE_METHOD(MULTICAST_DELEGATE,   CTOR_COLLECTIBLE_OPENED,            CtorCollectibleOpened,                 IM_Obj_IntPtr_IntPtr_IntPtr_RetVoid)
DEFINE_METHOD(MULTICAST_DELEGATE,   CTOR_COLLECTIBLE_VIRTUAL_DISPATCH,  CtorCollectibleVirtualDispatch,        IM_Obj_IntPtr_IntPtr_IntPtr_RetVoid)

DEFINE_CLASS(NULL,                  System,                 DBNull)
DEFINE_FIELD(NULL,                  VALUE,          Value)

DEFINE_CLASS(NULLABLE,              System,                 Nullable`1)

DEFINE_CLASS(BYREFERENCE,           System,                 ByReference)
DEFINE_METHOD(BYREFERENCE,          CTOR,                   .ctor, NoSig)
DEFINE_FIELD(BYREFERENCE,           VALUE,                  Value)
DEFINE_CLASS(SPAN,                  System,                 Span`1)
DEFINE_METHOD(SPAN,                 CTOR_PTR_INT,           .ctor, IM_VoidPtr_Int_RetVoid)
DEFINE_METHOD(SPAN,                 GET_ITEM,               get_Item, IM_Int_RetRefT)
DEFINE_CLASS(READONLY_SPAN,         System,                 ReadOnlySpan`1)
DEFINE_METHOD(READONLY_SPAN,        GET_ITEM,               get_Item, IM_Int_RetReadOnlyRefT)

// Defined as element type alias
// DEFINE_CLASS(OBJECT,                System,                 Object)
DEFINE_METHOD(OBJECT,               CTOR,                   .ctor,                      IM_RetVoid)
DEFINE_METHOD(OBJECT,               FINALIZE,               Finalize,                   IM_RetVoid)
DEFINE_METHOD(OBJECT,               TO_STRING,              ToString,                   IM_RetStr)
DEFINE_METHOD(OBJECT,               GET_TYPE,               GetType,                    IM_RetType)
DEFINE_METHOD(OBJECT,               GET_HASH_CODE,          GetHashCode,                IM_RetInt)
DEFINE_METHOD(OBJECT,               EQUALS,                 Equals,                     IM_Obj_RetBool)

DEFINE_CLASS(__CANON,              System,                 __Canon)

BEGIN_ILLINK_FEATURE_SWITCH(System.Runtime.InteropServices.BuiltInComInterop.IsSupported, true, true)
#ifdef FEATURE_COMINTEROP
DEFINE_CLASS(OLE_AUT_BINDER,        System,                 OleAutBinder)
#endif // FEATURE_COMINTEROP
END_ILLINK_FEATURE_SWITCH()

DEFINE_CLASS(MONITOR,               Threading,              Monitor)
DEFINE_METHOD(MONITOR,              ENTER,                  Enter,                      SM_Obj_RetVoid)
DEFINE_METHOD(MONITOR,              EXIT,                   Exit,                       SM_Obj_RetVoid)
DEFINE_METHOD(MONITOR,              RELIABLEENTER,          Enter,                      SM_Obj_RefBool_RetVoid)
DEFINE_METHOD(MONITOR,              EXIT_IF_TAKEN,          ExitIfLockTaken,            SM_Obj_RefBool_RetVoid)

DEFINE_CLASS(THREAD_BLOCKING_INFO,  Threading,              ThreadBlockingInfo)
DEFINE_FIELD(THREAD_BLOCKING_INFO,  OFFSET_OF_LOCK_OWNER_OS_THREAD_ID, s_monitorObjectOffsetOfLockOwnerOSThreadId)
DEFINE_FIELD(THREAD_BLOCKING_INFO,  FIRST,                  t_first)

DEFINE_CLASS(PARAMETER,             Reflection,             ParameterInfo)

DEFINE_CLASS(PARAMETER_MODIFIER,    Reflection,             ParameterModifier)

DEFINE_CLASS(POINTER,               Reflection,             Pointer)

DEFINE_CLASS_U(Reflection, Pointer, ReflectionPointer)
DEFINE_FIELD_U(_ptr,                ReflectionPointer, _ptr)
DEFINE_FIELD_U(_ptrType,            ReflectionPointer, _ptrType)

DEFINE_CLASS(PROPERTY,              Reflection,             RuntimePropertyInfo)
DEFINE_METHOD(PROPERTY,             SET_VALUE,              SetValue,                   IM_Obj_Obj_BindingFlags_Binder_ArrObj_CultureInfo_RetVoid)
DEFINE_METHOD(PROPERTY,             GET_VALUE,              GetValue,                   IM_Obj_BindingFlags_Binder_ArrObj_CultureInfo_RetObj)
DEFINE_METHOD(PROPERTY,             GET_INDEX_PARAMETERS,   GetIndexParameters,         IM_RetArrParameterInfo)
DEFINE_METHOD(PROPERTY,             GET_TOKEN,              get_MetadataToken,          IM_RetInt)
DEFINE_METHOD(PROPERTY,             GET_MODULE,             GetRuntimeModule,           IM_RetModule)
DEFINE_METHOD(PROPERTY,             GET_SETTER,             GetSetMethod,               IM_Bool_RetMethodInfo)
DEFINE_METHOD(PROPERTY,             GET_GETTER,             GetGetMethod,               IM_Bool_RetMethodInfo)

DEFINE_CLASS(PROPERTY_INFO,         Reflection,             PropertyInfo)

DEFINE_CLASS(RESOLVER,              System,                 Resolver)
DEFINE_METHOD(RESOLVER,             GET_JIT_CONTEXT,        GetJitContext,              IM_RefInt_RetRuntimeType)
DEFINE_METHOD(RESOLVER,             GET_CODE_INFO,          GetCodeInfo,                IM_RefInt_RefInt_RefInt_RetArrByte)
DEFINE_METHOD(RESOLVER,             GET_LOCALS_SIGNATURE,   GetLocalsSignature,         IM_RetArrByte)
DEFINE_METHOD(RESOLVER,             GET_EH_INFO,            GetEHInfo,                  IM_Int_VoidPtr_RetVoid)
DEFINE_METHOD(RESOLVER,             GET_RAW_EH_INFO,        GetRawEHInfo,               IM_RetArrByte)
DEFINE_METHOD(RESOLVER,             GET_STRING_LITERAL,     GetStringLiteral,           IM_Int_RetStr)
DEFINE_METHOD(RESOLVER,             RESOLVE_TOKEN,          ResolveToken,               IM_Int_RefIntPtr_RefIntPtr_RefIntPtr_RetVoid)
DEFINE_METHOD(RESOLVER,             RESOLVE_SIGNATURE,      ResolveSignature,           IM_IntInt_RetArrByte)

DEFINE_CLASS(RESOURCE_MANAGER,      Resources,              ResourceManager)

DEFINE_CLASS(RTFIELD,               Reflection,             RtFieldInfo)
DEFINE_METHOD(RTFIELD,              GET_FIELDESC,           GetFieldDesc,            IM_RetIntPtr)

DEFINE_CLASS(EXECUTIONANDSYNCBLOCKSTORE, CompilerServices, ExecutionAndSyncBlockStore)
DEFINE_METHOD(EXECUTIONANDSYNCBLOCKSTORE, PUSH, Push, NoSig)
DEFINE_METHOD(EXECUTIONANDSYNCBLOCKSTORE, POP, Pop, NoSig)

DEFINE_CLASS(RUNTIME_HELPERS,       CompilerServices,       RuntimeHelpers)
DEFINE_METHOD(RUNTIME_HELPERS,      IS_BITWISE_EQUATABLE,    IsBitwiseEquatable, NoSig)
DEFINE_METHOD(RUNTIME_HELPERS,      GET_RAW_DATA,            GetRawData,         NoSig)
DEFINE_METHOD(RUNTIME_HELPERS,      GET_UNINITIALIZED_OBJECT, GetUninitializedObject, SM_Type_RetObj)
DEFINE_METHOD(RUNTIME_HELPERS,      ENUM_EQUALS,            EnumEquals, NoSig)
DEFINE_METHOD(RUNTIME_HELPERS,      ENUM_COMPARE_TO,        EnumCompareTo, NoSig)
DEFINE_METHOD(RUNTIME_HELPERS,      ALLOC_TAILCALL_ARG_BUFFER, AllocTailCallArgBuffer,  SM_Int_IntPtr_RetIntPtr)
DEFINE_METHOD(RUNTIME_HELPERS,      GET_TAILCALL_INFO,      GetTailCallInfo, NoSig)
DEFINE_METHOD(RUNTIME_HELPERS,      DISPATCH_TAILCALLS,     DispatchTailCalls,          NoSig)
#ifdef FEATURE_IJW
DEFINE_METHOD(RUNTIME_HELPERS,      COPY_CONSTRUCT,         CopyConstruct, NoSig)
#endif // FEATURE_IJW

DEFINE_CLASS(ASYNC_HELPERS,       CompilerServices,          AsyncHelpers)
DEFINE_METHOD(ASYNC_HELPERS,      ALLOC_CONTINUATION,        AllocContinuation, NoSig)
DEFINE_METHOD(ASYNC_HELPERS,      ALLOC_CONTINUATION_METHOD, AllocContinuationMethod, NoSig)
DEFINE_METHOD(ASYNC_HELPERS,      ALLOC_CONTINUATION_CLASS,  AllocContinuationClass, NoSig)
DEFINE_METHOD(ASYNC_HELPERS,      ALLOC_CONTINUATION_RESULT_BOX, AllocContinuationResultBox, SM_VoidPtr_RetObj)
DEFINE_METHOD(ASYNC_HELPERS,      FINALIZE_TASK_RETURNING_THUNK, FinalizeTaskReturningThunk, SM_Continuation_RetTask)
DEFINE_METHOD(ASYNC_HELPERS,      FINALIZE_TASK_RETURNING_THUNK_1, FinalizeTaskReturningThunk, GM_Continuation_RetTaskOfT)
DEFINE_METHOD(ASYNC_HELPERS,      FINALIZE_VALUETASK_RETURNING_THUNK, FinalizeValueTaskReturningThunk, SM_Continuation_RetValueTask)
DEFINE_METHOD(ASYNC_HELPERS,      FINALIZE_VALUETASK_RETURNING_THUNK_1, FinalizeValueTaskReturningThunk, GM_Continuation_RetValueTaskOfT)
DEFINE_METHOD(ASYNC_HELPERS,      UNSAFE_AWAIT_AWAITER_1,    UnsafeAwaitAwaiter, GM_T_RetVoid)
DEFINE_METHOD(ASYNC_HELPERS,      CAPTURE_EXECUTION_CONTEXT, CaptureExecutionContext, NoSig)
DEFINE_METHOD(ASYNC_HELPERS,      RESTORE_EXECUTION_CONTEXT, RestoreExecutionContext, NoSig)
DEFINE_METHOD(ASYNC_HELPERS,      CAPTURE_CONTINUATION_CONTEXT, CaptureContinuationContext, NoSig)

DEFINE_CLASS(SPAN_HELPERS,          System,                 SpanHelpers)
DEFINE_METHOD(SPAN_HELPERS,         MEMSET,                 Fill, SM_RefByte_Byte_UIntPtr_RetVoid)
DEFINE_METHOD(SPAN_HELPERS,         MEMZERO,                ClearWithoutReferences, SM_RefByte_UIntPtr_RetVoid)
DEFINE_METHOD(SPAN_HELPERS,         MEMCOPY,                Memmove, SM_RefByte_RefByte_UIntPtr_RetVoid)

DEFINE_CLASS(THROWHELPERS,     InternalCompilerHelpers,             ThrowHelpers)
DEFINE_METHOD(THROWHELPERS,    THROWARGUMENTEXCEPTION,              ThrowArgumentException, SM_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWARGUMENTOUTOFRANGEEXCEPTION,    ThrowArgumentOutOfRangeException, SM_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWINDEXOUTOFRANGEEXCEPTION,       ThrowIndexOutOfRangeException, SM_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWNOTIMPLEMENTEDEXCEPTION,        ThrowNotImplementedException, SM_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWPLATFORMNOTSUPPORTEDEXCEPTION,  ThrowPlatformNotSupportedException, SM_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWTYPENOTSUPPORTED,               ThrowTypeNotSupportedException, SM_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWOVERFLOWEXCEPTION,              ThrowOverflowException, SM_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWDIVIDEBYZEROEXCEPTION,          ThrowDivideByZeroException, SM_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWNULLREFEXCEPTION,               ThrowNullReferenceException, SM_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWVERIFICATIONEXCEPTION,          ThrowVerificationException, SM_Int_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWAMBIGUOUSRESOLUTIONEXCEPTION,   ThrowAmbiguousResolutionException, SM_PtrVoid_PtrVoid_PtrVoid_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWENTRYPOINTNOTFOUNDEXCEPTION,    ThrowEntryPointNotFoundException, SM_PtrVoid_PtrVoid_PtrVoid_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWMETHODACCESSEXCEPTION,          ThrowMethodAccessException, SM_PtrVoid_PtrVoid_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWFIELDACCESSEXCEPTION,           ThrowFieldAccessException,  SM_PtrVoid_PtrVoid_RetVoid)
DEFINE_METHOD(THROWHELPERS,    THROWCLASSACCESSEXCEPTION,           ThrowClassAccessException,  SM_PtrVoid_PtrVoid_RetVoid)

DEFINE_CLASS(UNSAFE,                CompilerServices,       Unsafe)
DEFINE_METHOD(UNSAFE,               AS_POINTER,             AsPointer, NoSig)
DEFINE_METHOD(UNSAFE,               BYREF_IS_NULL,          IsNullRef, NoSig)
DEFINE_METHOD(UNSAFE,               BYREF_NULLREF,          NullRef, NoSig)
DEFINE_METHOD(UNSAFE,               AS_REF_IN,              AsRef, GM_RefT_RetRefT)
DEFINE_METHOD(UNSAFE,               BYREF_AS,               As, GM_RefTFrom_RetRefTTo)
DEFINE_METHOD(UNSAFE,               OBJECT_AS,              As, GM_Obj_RetT)
DEFINE_METHOD(UNSAFE,               BYREF_ADD,              Add, GM_RefT_Int_RetRefT)
DEFINE_METHOD(UNSAFE,               BYREF_INTPTR_ADD,       Add, GM_RefT_IntPtr_RetRefT)
DEFINE_METHOD(UNSAFE,               BYREF_UINTPTR_ADD,      Add, GM_RefT_UIntPtr_RetRefT)
DEFINE_METHOD(UNSAFE,               PTR_ADD,                Add, GM_PtrVoid_Int_RetPtrVoid)
DEFINE_METHOD(UNSAFE,               BYREF_BYTE_OFFSET,      ByteOffset, NoSig)
DEFINE_METHOD(UNSAFE,               BYREF_INTPTR_ADD_BYTE_OFFSET,  AddByteOffset, GM_RefT_IntPtr_RetRefT)
DEFINE_METHOD(UNSAFE,               BYREF_UINTPTR_ADD_BYTE_OFFSET, AddByteOffset, GM_RefT_UIntPtr_RetRefT)
DEFINE_METHOD(UNSAFE,               BYREF_ARE_SAME,         AreSame, NoSig)
DEFINE_METHOD(UNSAFE,               PTR_BYREF_COPY,         Copy, GM_PtrVoid_RefT_RetVoid)
DEFINE_METHOD(UNSAFE,               BYREF_PTR_COPY,         Copy, GM_RefT_PtrVoid_RetVoid)
DEFINE_METHOD(UNSAFE,               PTR_COPY_BLOCK,          CopyBlock, SM_PtrVoid_PtrVoid_UInt_RetVoid)
DEFINE_METHOD(UNSAFE,               BYREF_COPY_BLOCK,       CopyBlock, SM_RefByte_RefByte_UInt_RetVoid)
DEFINE_METHOD(UNSAFE,               PTR_COPY_BLOCK_UNALIGNED, CopyBlockUnaligned, SM_PtrVoid_PtrVoid_UInt_RetVoid)
DEFINE_METHOD(UNSAFE,               BYREF_COPY_BLOCK_UNALIGNED, CopyBlockUnaligned, SM_RefByte_RefByte_UInt_RetVoid)
DEFINE_METHOD(UNSAFE,               BYREF_IS_ADDRESS_GREATER_THAN, IsAddressGreaterThan, NoSig)
DEFINE_METHOD(UNSAFE,               BYREF_IS_ADDRESS_LESS_THAN, IsAddressLessThan, NoSig)
DEFINE_METHOD(UNSAFE,               BYREF_INIT_BLOCK, InitBlockUnaligned, SM_RefByte_Byte_UInt_RetVoid)
DEFINE_METHOD(UNSAFE,               PTR_INIT_BLOCK, InitBlock, SM_PtrVoid_Byte_UInt_RetVoid)
DEFINE_METHOD(UNSAFE,               BYREF_INIT_BLOCK_UNALIGNED, InitBlock, SM_RefByte_Byte_UInt_RetVoid)
DEFINE_METHOD(UNSAFE,               PTR_INIT_BLOCK_UNALIGNED, InitBlockUnaligned, SM_PtrVoid_Byte_UInt_RetVoid)
DEFINE_METHOD(UNSAFE,               BYREF_READ_UNALIGNED,   ReadUnaligned, GM_RefByte_RetT)
DEFINE_METHOD(UNSAFE,               BYREF_WRITE_UNALIGNED,  WriteUnaligned, GM_RefByte_T_RetVoid)
DEFINE_METHOD(UNSAFE,               PTR_READ_UNALIGNED,     ReadUnaligned, GM_PtrVoid_RetT)
DEFINE_METHOD(UNSAFE,               PTR_WRITE_UNALIGNED,    WriteUnaligned, GM_PtrVoid_T_RetVoid)
DEFINE_METHOD(UNSAFE,               READ,                   Read, NoSig)
DEFINE_METHOD(UNSAFE,               SKIPINIT,               SkipInit, GM_RefT_RetVoid)
DEFINE_METHOD(UNSAFE,               BYREF_INT_SUBTRACT,     Subtract, GM_RefT_Int_RetRefT)
DEFINE_METHOD(UNSAFE,               BYREF_INTPTR_SUBTRACT,  Subtract, GM_RefT_IntPtr_RetRefT)
DEFINE_METHOD(UNSAFE,               BYREF_UINTPTR_SUBTRACT, Subtract, GM_RefT_UIntPtr_RetRefT)
DEFINE_METHOD(UNSAFE,               PTR_INT_SUBTRACT,       Subtract, GM_PtrVoid_Int_RetPtrVoid)
DEFINE_METHOD(UNSAFE,               BYREF_INTPTR_SUBTRACT_BYTE_OFFSET,  SubtractByteOffset, GM_RefT_IntPtr_RetRefT)
DEFINE_METHOD(UNSAFE,               BYREF_UINTPTR_SUBTRACT_BYTE_OFFSET, SubtractByteOffset, GM_RefT_UIntPtr_RetRefT)
DEFINE_METHOD(UNSAFE,               UNBOX,                  Unbox, NoSig)
DEFINE_METHOD(UNSAFE,               WRITE,                  Write, NoSig)

DEFINE_CLASS(MEMORY_MARSHAL,        Interop,                MemoryMarshal)
DEFINE_METHOD(MEMORY_MARSHAL,       GET_ARRAY_DATA_REFERENCE_MDARRAY, GetArrayDataReference, SM_Array_RetRefByte)

DEFINE_CLASS(INTERLOCKED,           Threading,              Interlocked)
DEFINE_METHOD(INTERLOCKED,          COMPARE_EXCHANGE_T,     CompareExchange, GM_RefT_T_T_RetT)
DEFINE_METHOD(INTERLOCKED,          COMPARE_EXCHANGE_OBJECT,CompareExchange, SM_RefObject_Object_Object_RetObject)
DEFINE_METHOD(INTERLOCKED,          COMPARE_EXCHANGE_BYTE,  CompareExchange, SM_RefByte_Byte_Byte_RetByte)
DEFINE_METHOD(INTERLOCKED,          COMPARE_EXCHANGE_USHRT, CompareExchange, SM_RefUShrt_UShrt_UShrt_RetUShrt)
DEFINE_METHOD(INTERLOCKED,          COMPARE_EXCHANGE_INT,   CompareExchange, SM_RefInt_Int_Int_RetInt)
DEFINE_METHOD(INTERLOCKED,          COMPARE_EXCHANGE_LONG,  CompareExchange, SM_RefLong_Long_Long_RetLong)

DEFINE_CLASS_U(CompilerServices,    ConditionalWeakTable`2,   NoClass)
DEFINE_FIELD_U(_lock,               ConditionalWeakTableObject, _lock)
DEFINE_FIELD_U(_container,          ConditionalWeakTableObject, _container)

DEFINE_CLASS_U(CompilerServices,    ConditionalWeakTable`2+Container, NoClass)
DEFINE_FIELD_U(_parent,             ConditionalWeakTableContainerObject, _parent)
DEFINE_FIELD_U(_buckets,            ConditionalWeakTableContainerObject, _buckets)
DEFINE_FIELD_U(_entries,            ConditionalWeakTableContainerObject, _entries)

DEFINE_CLASS_U(CompilerServices,    ConditionalWeakTable`2+Entry, ConditionalWeakTableContainerObject::Entry)
DEFINE_FIELD_U(depHnd,              ConditionalWeakTableContainerObject::Entry, depHnd)
DEFINE_FIELD_U(HashCode,            ConditionalWeakTableContainerObject::Entry, HashCode)
DEFINE_FIELD_U(Next,                ConditionalWeakTableContainerObject::Entry, Next)

DEFINE_CLASS(RAW_DATA,              CompilerServices,       RawData)
DEFINE_FIELD(RAW_DATA,              DATA,                   Data)

DEFINE_CLASS(RAW_ARRAY_DATA,        CompilerServices,       RawArrayData)
DEFINE_FIELD(RAW_ARRAY_DATA,        LENGTH,                 Length)
#ifdef TARGET_64BIT
DEFINE_FIELD(RAW_ARRAY_DATA,        PADDING,                Padding)
#endif
DEFINE_FIELD(RAW_ARRAY_DATA,        DATA,                   Data)

DEFINE_CLASS(PORTABLE_TAIL_CALL_FRAME, CompilerServices,              PortableTailCallFrame)
DEFINE_FIELD(PORTABLE_TAIL_CALL_FRAME, TAILCALL_AWARE_RETURN_ADDRESS, TailCallAwareReturnAddress)
DEFINE_FIELD(PORTABLE_TAIL_CALL_FRAME, NEXT_CALL,                     NextCall)

DEFINE_CLASS(TAIL_CALL_TLS,            CompilerServices,              TailCallTls)
DEFINE_FIELD(TAIL_CALL_TLS,            FRAME,                         Frame)
DEFINE_FIELD(TAIL_CALL_TLS,            ARG_BUFFER,                    ArgBuffer)

DEFINE_CLASS_U(CompilerServices,           PortableTailCallFrame, PortableTailCallFrame)
DEFINE_FIELD_U(TailCallAwareReturnAddress, PortableTailCallFrame, TailCallAwareReturnAddress)
DEFINE_FIELD_U(NextCall,                   PortableTailCallFrame, NextCall)

DEFINE_CLASS_U(CompilerServices,           TailCallTls,           TailCallTls)
DEFINE_FIELD_U(Frame,                      TailCallTls,           m_frame)
DEFINE_FIELD_U(ArgBuffer,                  TailCallTls,           m_argBuffer)

DEFINE_CLASS(CONTINUATION,              CompilerServices,   Continuation)
DEFINE_FIELD(CONTINUATION,              NEXT,               Next)
DEFINE_FIELD(CONTINUATION,              RESUME,             Resume)
DEFINE_FIELD(CONTINUATION,              STATE,              State)
DEFINE_FIELD(CONTINUATION,              FLAGS,              Flags)
DEFINE_FIELD(CONTINUATION,              DATA,               Data)
DEFINE_FIELD(CONTINUATION,              GCDATA,             GCData)

DEFINE_CLASS(RUNTIME_WRAPPED_EXCEPTION, CompilerServices,   RuntimeWrappedException)
DEFINE_METHOD(RUNTIME_WRAPPED_EXCEPTION, OBJ_CTOR,          .ctor,                      IM_Obj_RetVoid)
DEFINE_FIELD(RUNTIME_WRAPPED_EXCEPTION, WRAPPED_EXCEPTION,  _wrappedException)

DEFINE_CLASS(CALLCONV_CDECL,                 CompilerServices,       CallConvCdecl)
DEFINE_CLASS(CALLCONV_STDCALL,               CompilerServices,       CallConvStdcall)
DEFINE_CLASS(CALLCONV_THISCALL,              CompilerServices,       CallConvThiscall)
DEFINE_CLASS(CALLCONV_FASTCALL,              CompilerServices,       CallConvFastcall)
DEFINE_CLASS(CALLCONV_SUPPRESSGCTRANSITION,  CompilerServices,       CallConvSuppressGCTransition)
DEFINE_CLASS(CALLCONV_MEMBERFUNCTION,        CompilerServices,       CallConvMemberFunction)
DEFINE_CLASS(CALLCONV_SWIFT,                 CompilerServices,       CallConvSwift)

DEFINE_CLASS(SAFE_HANDLE,         Interop,                SafeHandle)
DEFINE_FIELD(SAFE_HANDLE,           HANDLE,                 handle)
DEFINE_METHOD(SAFE_HANDLE,          GET_IS_INVALID,         get_IsInvalid,              IM_RetBool)
DEFINE_METHOD(SAFE_HANDLE,          RELEASE_HANDLE,         ReleaseHandle,              IM_RetBool)
DEFINE_METHOD(SAFE_HANDLE,          DISPOSE,                Dispose,                    IM_RetVoid)
DEFINE_METHOD(SAFE_HANDLE,          DISPOSE_BOOL,           Dispose,                    IM_Bool_RetVoid)

DEFINE_CLASS(SECURITY_EXCEPTION,    Security,               SecurityException)

DEFINE_CLASS_U(Diagnostics,                StackFrameHelper,   StackFrameHelper)
DEFINE_FIELD_U(rgiOffset,                  StackFrameHelper,   rgiOffset)
DEFINE_FIELD_U(rgiILOffset,                StackFrameHelper,   rgiILOffset)
DEFINE_FIELD_U(dynamicMethods,             StackFrameHelper,   dynamicMethods)
DEFINE_FIELD_U(rgMethodHandle,             StackFrameHelper,   rgMethodHandle)
DEFINE_FIELD_U(rgAssemblyPath,             StackFrameHelper,   rgAssemblyPath)
DEFINE_FIELD_U(rgAssembly,                 StackFrameHelper,   rgAssembly)
DEFINE_FIELD_U(rgLoadedPeAddress,          StackFrameHelper,   rgLoadedPeAddress)
DEFINE_FIELD_U(rgiLoadedPeSize,            StackFrameHelper,   rgiLoadedPeSize)
DEFINE_FIELD_U(rgInMemoryPdbAddress,       StackFrameHelper,   rgInMemoryPdbAddress)
DEFINE_FIELD_U(rgiInMemoryPdbSize,         StackFrameHelper,   rgiInMemoryPdbSize)
DEFINE_FIELD_U(rgiMethodToken,             StackFrameHelper,   rgiMethodToken)
DEFINE_FIELD_U(rgFilename,                 StackFrameHelper,   rgFilename)
DEFINE_FIELD_U(rgiLineNumber,              StackFrameHelper,   rgiLineNumber)
DEFINE_FIELD_U(rgiColumnNumber,            StackFrameHelper,   rgiColumnNumber)
DEFINE_FIELD_U(rgiLastFrameFromForeignExceptionStackTrace,            StackFrameHelper,   rgiLastFrameFromForeignExceptionStackTrace)
DEFINE_FIELD_U(iFrameCount,                StackFrameHelper,   iFrameCount)

DEFINE_CLASS(EVENT_SOURCE,           Tracing,            EventSource)
DEFINE_METHOD(EVENT_SOURCE,          INITIALIZE_DEFAULT_EVENT_SOURCES, InitializeDefaultEventSources, SM_RetVoid)

DEFINE_CLASS(STARTUP_HOOK_PROVIDER,  System,                StartupHookProvider)
DEFINE_METHOD(STARTUP_HOOK_PROVIDER, MANAGED_STARTUP, ManagedStartup, SM_PtrChar_RetVoid)
DEFINE_METHOD(STARTUP_HOOK_PROVIDER, CALL_STARTUP_HOOK, CallStartupHook, SM_PtrChar_RetVoid)

DEFINE_CLASS(STREAM,                IO,                     Stream)
DEFINE_METHOD(STREAM,               BEGIN_READ,             BeginRead,  IM_ArrByte_Int_Int_AsyncCallback_Object_RetIAsyncResult)
DEFINE_METHOD(STREAM,               END_READ,               EndRead,    IM_IAsyncResult_RetInt)
DEFINE_METHOD(STREAM,               BEGIN_WRITE,            BeginWrite, IM_ArrByte_Int_Int_AsyncCallback_Object_RetIAsyncResult)
DEFINE_METHOD(STREAM,               END_WRITE,              EndWrite,   IM_IAsyncResult_RetVoid)

// Defined as element type alias
// DEFINE_CLASS(INTPTR,                System,                 IntPtr)
DEFINE_FIELD(INTPTR,                ZERO,                   Zero)

// Defined as element type alias
// DEFINE_CLASS(UINTPTR,                System,                UIntPtr)
DEFINE_FIELD(UINTPTR,               ZERO,                   Zero)

DEFINE_CLASS(BITCONVERTER,          System,                 BitConverter)
DEFINE_FIELD(BITCONVERTER,          ISLITTLEENDIAN,         IsLittleEndian)

// Defined as element type alias
// DEFINE_CLASS(STRING,                System,                 String)
DEFINE_FIELD(STRING,                M_FIRST_CHAR,           _firstChar)
DEFINE_FIELD(STRING,                EMPTY,                  Empty)
DEFINE_METHOD(STRING,               CTOR_CHARPTR,           .ctor,                      IM_PtrChar_RetVoid)
DEFINE_METHOD(STRING,               CTORF_CHARARRAY,        Ctor,                       SM_ArrChar_RetStr)
DEFINE_METHOD(STRING,               CTORF_CHARARRAY_START_LEN,Ctor,                     SM_ArrChar_Int_Int_RetStr)
DEFINE_METHOD(STRING,               CTORF_CHAR_COUNT,       Ctor,                       SM_Char_Int_RetStr)
DEFINE_METHOD(STRING,               CTORF_CHARPTR,          Ctor,                       SM_PtrChar_RetStr)
DEFINE_METHOD(STRING,               CTORF_CHARPTR_START_LEN,Ctor,                       SM_PtrChar_Int_Int_RetStr)
DEFINE_METHOD(STRING,               CTORF_READONLYSPANOFCHAR,Ctor,                      SM_ReadOnlySpanOfChar_RetStr)
DEFINE_METHOD(STRING,               CTORF_SBYTEPTR,         Ctor,                       SM_PtrSByt_RetStr)
DEFINE_METHOD(STRING,               CTORF_SBYTEPTR_START_LEN, Ctor,                     SM_PtrSByt_Int_Int_RetStr)
DEFINE_METHOD(STRING,               CTORF_SBYTEPTR_START_LEN_ENCODING, Ctor,            SM_PtrSByt_Int_Int_Encoding_RetStr)
DEFINE_METHOD(STRING,               INTERNAL_COPY,          InternalCopy,               SM_Str_IntPtr_Int_RetVoid)
DEFINE_METHOD(STRING,               WCSLEN,                 wcslen,                     SM_PtrChar_RetInt)
DEFINE_METHOD(STRING,               STRLEN,                 strlen,                     SM_PtrByte_RetInt)
DEFINE_METHOD(STRING,               STRCNS,                 StrCns,                     SM_UInt_IntPtr_RetStr)
DEFINE_PROPERTY(STRING,             LENGTH,                 Length,                     Int)

DEFINE_CLASS(STRING_BUILDER,        Text,                   StringBuilder)
DEFINE_PROPERTY(STRING_BUILDER,     LENGTH,                 Length,                     Int)
DEFINE_PROPERTY(STRING_BUILDER,     CAPACITY,               Capacity,                   Int)
DEFINE_METHOD(STRING_BUILDER,       CTOR_INT,               .ctor,                      IM_Int_RetVoid)
DEFINE_METHOD(STRING_BUILDER,       TO_STRING,              ToString,                   IM_RetStr)
DEFINE_METHOD(STRING_BUILDER,       INTERNAL_COPY,          InternalCopy,               IM_IntPtr_Int_RetVoid)
DEFINE_METHOD(STRING_BUILDER,       REPLACE_BUFFER_INTERNAL,ReplaceBufferInternal,      IM_PtrChar_Int_RetVoid)
DEFINE_METHOD(STRING_BUILDER,       REPLACE_BUFFER_ANSI_INTERNAL,ReplaceBufferAnsiInternal, IM_PtrSByt_Int_RetVoid)

DEFINE_CLASS_U(Threading,              SynchronizationContext, SynchronizationContextObject)
DEFINE_FIELD_U(_requireWaitNotification, SynchronizationContextObject, _requireWaitNotification)
DEFINE_CLASS(SYNCHRONIZATION_CONTEXT,    Threading,              SynchronizationContext)
DEFINE_METHOD(SYNCHRONIZATION_CONTEXT,  INVOKE_WAIT_METHOD_HELPER, InvokeWaitMethodHelper, SM_SyncCtx_ArrIntPtr_Bool_Int_RetInt)

#ifdef DEBUG
DEFINE_CLASS(STACKCRAWMARK,         Threading,       StackCrawlMark)
#endif

DEFINE_CLASS_U(Threading,              Thread,                     ThreadBaseObject)
DEFINE_FIELD_U(_name,                     ThreadBaseObject,   m_Name)
DEFINE_FIELD_U(_startHelper,              ThreadBaseObject,   m_StartHelper)
DEFINE_FIELD_U(_DONT_USE_InternalThread,  ThreadBaseObject,   m_InternalThread)
DEFINE_FIELD_U(_priority,                 ThreadBaseObject,   m_Priority)
DEFINE_FIELD_U(_isDead,                   ThreadBaseObject,   m_IsDead)
DEFINE_FIELD_U(_isThreadPool,             ThreadBaseObject,   m_IsThreadPool)

DEFINE_CLASS(DIRECTONTHREADLOCALDATA, Threading, Thread+DirectOnThreadLocalData)

DEFINE_CLASS(THREAD,                Threading,              Thread)
DEFINE_METHOD(THREAD,               START_CALLBACK,                          StartCallback,                               IM_RetVoid)
DEFINE_METHOD(THREAD,               POLLGC,                                  PollGC,                               NoSig)

#ifdef FEATURE_OBJCMARSHAL
DEFINE_CLASS(AUTORELEASEPOOL,       Threading,              AutoreleasePool)
DEFINE_METHOD(AUTORELEASEPOOL,      CREATEAUTORELEASEPOOL,  CreateAutoreleasePool,  SM_RetVoid)
DEFINE_METHOD(AUTORELEASEPOOL,      DRAINAUTORELEASEPOOL,   DrainAutoreleasePool,   SM_RetVoid)
#endif // FEATURE_OBJCMARSHAL

DEFINE_CLASS(TIMESPAN,              System,                 TimeSpan)


DEFINE_CLASS(TYPE,                  System,                 Type)
DEFINE_METHOD(TYPE,                 GET_TYPE_FROM_HANDLE,   GetTypeFromHandle,          SM_RuntimeTypeHandle_RetType)
DEFINE_PROPERTY(TYPE,               IS_IMPORT,              IsImport,                   Bool)

DEFINE_CLASS(TYPE_DELEGATOR,        Reflection,             TypeDelegator)

DEFINE_CLASS(FIRSTCHANCE_EVENTARGS,   ExceptionServices,      FirstChanceExceptionEventArgs)
DEFINE_METHOD(FIRSTCHANCE_EVENTARGS,  CTOR,                   .ctor,                      IM_Exception_RetVoid)

DEFINE_CLASS(EXCEPTION_DISPATCH_INFO, ExceptionServices,      ExceptionDispatchInfo)
DEFINE_METHOD(EXCEPTION_DISPATCH_INFO, CAPTURE, Capture, NoSig)
DEFINE_METHOD(EXCEPTION_DISPATCH_INFO, THROW, Throw, IM_RetVoid)

DEFINE_CLASS_U(Loader,             AssemblyLoadContext,           AssemblyLoadContextBaseObject)
DEFINE_FIELD_U(_unloadLock,                 AssemblyLoadContextBaseObject, _unloadLock)
DEFINE_FIELD_U(_resolvingUnmanagedDll,      AssemblyLoadContextBaseObject, _resolvingUnmanagedDll)
DEFINE_FIELD_U(_resolving,                  AssemblyLoadContextBaseObject, _resolving)
DEFINE_FIELD_U(_unloading,                  AssemblyLoadContextBaseObject, _unloading)
DEFINE_FIELD_U(_name,                       AssemblyLoadContextBaseObject, _name)
DEFINE_FIELD_U(_nativeAssemblyLoadContext,  AssemblyLoadContextBaseObject, _nativeAssemblyLoadContext)
DEFINE_FIELD_U(_id,                         AssemblyLoadContextBaseObject, _id)
DEFINE_FIELD_U(_state,                      AssemblyLoadContextBaseObject, _state)
DEFINE_FIELD_U(_isCollectible,              AssemblyLoadContextBaseObject, _isCollectible)
DEFINE_CLASS(ASSEMBLYLOADCONTEXT,  Loader,                AssemblyLoadContext)
DEFINE_METHOD(ASSEMBLYLOADCONTEXT,  RESOLVE,          Resolve,                      SM_IntPtr_AssemblyName_RetAssembly)
DEFINE_METHOD(ASSEMBLYLOADCONTEXT,  RESOLVEUNMANAGEDDLL,           ResolveUnmanagedDll,           SM_Str_IntPtr_RetIntPtr)
DEFINE_METHOD(ASSEMBLYLOADCONTEXT,  RESOLVEUNMANAGEDDLLUSINGEVENT, ResolveUnmanagedDllUsingEvent, SM_Str_AssemblyBase_IntPtr_RetIntPtr)
DEFINE_METHOD(ASSEMBLYLOADCONTEXT,  RESOLVEUSINGEVENT,             ResolveUsingResolvingEvent,    SM_IntPtr_AssemblyName_RetAssembly)
DEFINE_METHOD(ASSEMBLYLOADCONTEXT,  RESOLVESATELLITEASSEMBLY,      ResolveSatelliteAssembly,      SM_IntPtr_AssemblyName_RetAssembly)
DEFINE_FIELD(ASSEMBLYLOADCONTEXT,   ASSEMBLY_LOAD,              AssemblyLoad)
DEFINE_METHOD(ASSEMBLYLOADCONTEXT,  ON_ASSEMBLY_LOAD,           OnAssemblyLoad,             SM_Assembly_RetVoid)
DEFINE_METHOD(ASSEMBLYLOADCONTEXT,  ON_RESOURCE_RESOLVE,        OnResourceResolve,          SM_Assembly_Str_RetAssembly)
DEFINE_METHOD(ASSEMBLYLOADCONTEXT,  ON_TYPE_RESOLVE,            OnTypeResolve,              SM_Assembly_Str_RetAssembly)
DEFINE_METHOD(ASSEMBLYLOADCONTEXT,  ON_ASSEMBLY_RESOLVE,        OnAssemblyResolve,          SM_Assembly_Str_RetAssembly)
DEFINE_METHOD(ASSEMBLYLOADCONTEXT,  START_ASSEMBLY_LOAD,        StartAssemblyLoad,          SM_RefGuid_RefGuid_RetVoid)
DEFINE_METHOD(ASSEMBLYLOADCONTEXT,  STOP_ASSEMBLY_LOAD,         StopAssemblyLoad,           SM_RefGuid_RetVoid)
DEFINE_METHOD(ASSEMBLYLOADCONTEXT,  INITIALIZE_DEFAULT_CONTEXT, InitializeDefaultContext,   SM_RetVoid)

DEFINE_CLASS(VALUE_TYPE,            System,                 ValueType)
DEFINE_METHOD(VALUE_TYPE,           GET_HASH_CODE,          GetHashCode,            IM_RetInt)
DEFINE_METHOD(VALUE_TYPE,           EQUALS,                 Equals,                 IM_Obj_RetBool)

DEFINE_CLASS(GC,                    System,                 GC)
DEFINE_METHOD(GC,                   KEEP_ALIVE,             KeepAlive,                  SM_Obj_RetVoid)
DEFINE_METHOD(GC,                   RUN_FINALIZERS,         RunFinalizers,              SM_RetUInt)

DEFINE_CLASS_U(System,              WeakReference,          WeakReferenceObject)
DEFINE_FIELD_U(_taggedHandle,       WeakReferenceObject,    m_taggedHandle)
DEFINE_CLASS(WEAKREFERENCE,         System,                 WeakReference)
DEFINE_CLASS(WEAKREFERENCEGENERIC,  System,                 WeakReference`1)

DEFINE_CLASS(DEBUGGER,              Diagnostics,            Debugger)
DEFINE_METHOD(DEBUGGER,             USERBREAKPOINT,         UserBreakpoint,     SM_RetVoid)

DEFINE_CLASS(BUFFER,                System,                 Buffer)
DEFINE_METHOD(BUFFER,               MEMCPY_PTRBYTE_ARRBYTE, Memcpy,                 SM_PtrByte_Int_ArrByte_Int_Int_RetVoid)
DEFINE_METHOD(BUFFER,               MEMCPY,                 Memcpy,                 SM_PtrByte_PtrByte_Int_RetVoid)
DEFINE_METHOD(BUFFER,               MEMCOPYGC,              BulkMoveWithWriteBarrier, SM_RefByte_RefByte_UIntPtr_RetVoid)

DEFINE_CLASS(STUBHELPERS,           StubHelpers,            StubHelpers)
DEFINE_METHOD(STUBHELPERS,          GET_DELEGATE_TARGET,    GetDelegateTarget,          SM_Delegate_RetIntPtr)
#ifdef FEATURE_COMINTEROP
DEFINE_METHOD(STUBHELPERS,          GET_COM_HR_EXCEPTION_OBJECT,              GetCOMHRExceptionObject,            SM_Int_IntPtr_IntPtr_RetException)
DEFINE_METHOD(STUBHELPERS,          GET_COM_IP_FROM_RCW,                      GetCOMIPFromRCW,                    SM_Obj_IntPtr_RefIntPtr_RefBool_RetIntPtr)
#endif // FEATURE_COMINTEROP
DEFINE_METHOD(STUBHELPERS,          SET_LAST_ERROR,         SetLastError,               SM_RetVoid)
DEFINE_METHOD(STUBHELPERS,          CLEAR_LAST_ERROR,       ClearLastError,             SM_RetVoid)

DEFINE_METHOD(STUBHELPERS,          THROW_INTEROP_PARAM_EXCEPTION, ThrowInteropParamException,   SM_Int_Int_RetVoid)
DEFINE_METHOD(STUBHELPERS,          ADD_TO_CLEANUP_LIST_SAFEHANDLE,    AddToCleanupList,           SM_RefCleanupWorkListElement_SafeHandle_RetIntPtr)
DEFINE_METHOD(STUBHELPERS,          KEEP_ALIVE_VIA_CLEANUP_LIST,    KeepAliveViaCleanupList,       SM_RefCleanupWorkListElement_Obj_RetVoid)
DEFINE_METHOD(STUBHELPERS,          DESTROY_CLEANUP_LIST,   DestroyCleanupList,         SM_RefCleanupWorkListElement_RetVoid)
DEFINE_METHOD(STUBHELPERS,          GET_HR_EXCEPTION_OBJECT, GetHRExceptionObject,      SM_Int_RetException)
DEFINE_METHOD(STUBHELPERS,          GET_PENDING_EXCEPTION_OBJECT, GetPendingExceptionObject,      SM_RetException)
DEFINE_METHOD(STUBHELPERS,          CREATE_CUSTOM_MARSHALER, CreateCustomMarshaler, SM_IntPtr_Int_IntPtr_RetObj)
#ifdef FEATURE_COMINTEROP
DEFINE_METHOD(STUBHELPERS,          GET_IENUMERATOR_TO_ENUM_VARIANT_MARSHALER, GetIEnumeratorToEnumVariantMarshaler, SM_RetObj)
#endif // FEATURE_COMINTEROP

DEFINE_METHOD(STUBHELPERS,          CHECK_STRING_LENGTH,    CheckStringLength,          SM_Int_RetVoid)

DEFINE_METHOD(STUBHELPERS,          FMT_CLASS_UPDATE_NATIVE_INTERNAL,   FmtClassUpdateNativeInternal,   SM_Obj_PtrByte_RefCleanupWorkListElement_RetVoid)
DEFINE_METHOD(STUBHELPERS,          FMT_CLASS_UPDATE_CLR_INTERNAL,      FmtClassUpdateCLRInternal,      SM_Obj_PtrByte_RetVoid)
DEFINE_METHOD(STUBHELPERS,          LAYOUT_DESTROY_NATIVE_INTERNAL,     LayoutDestroyNativeInternal,    SM_Obj_PtrByte_RetVoid)
DEFINE_METHOD(STUBHELPERS,          MARSHAL_TO_MANAGED_VA_LIST,         MarshalToManagedVaList,         SM_IntPtr_IntPtr_RetVoid)
DEFINE_METHOD(STUBHELPERS,          MARSHAL_TO_UNMANAGED_VA_LIST,       MarshalToUnmanagedVaList,       SM_IntPtr_UInt_IntPtr_RetVoid)
DEFINE_METHOD(STUBHELPERS,          CALC_VA_LIST_SIZE,                  CalcVaListSize,                 SM_IntPtr_RetUInt)
DEFINE_METHOD(STUBHELPERS,          VALIDATE_OBJECT,                    ValidateObject,                 SM_Obj_IntPtr_Obj_RetVoid)
DEFINE_METHOD(STUBHELPERS,          VALIDATE_BYREF,                     ValidateByref,                  SM_IntPtr_IntPtr_Obj_RetVoid)
DEFINE_METHOD(STUBHELPERS,          GET_STUB_CONTEXT,                   GetStubContext,                 SM_RetIntPtr)
DEFINE_METHOD(STUBHELPERS,          LOG_PINNED_ARGUMENT,                LogPinnedArgument,              SM_IntPtr_IntPtr_RetVoid)
DEFINE_METHOD(STUBHELPERS,          NEXT_CALL_RETURN_ADDRESS,           NextCallReturnAddress,          SM_RetIntPtr)
DEFINE_METHOD(STUBHELPERS,          ASYNC_CALL_CONTINUATION,            AsyncCallContinuation,          SM_RetContinuation)
DEFINE_METHOD(STUBHELPERS,          SAFE_HANDLE_ADD_REF,    SafeHandleAddRef,           SM_SafeHandle_RefBool_RetIntPtr)
DEFINE_METHOD(STUBHELPERS,          SAFE_HANDLE_RELEASE,    SafeHandleRelease,          SM_SafeHandle_RetVoid)

#ifdef PROFILING_SUPPORTED
DEFINE_METHOD(STUBHELPERS,          PROFILER_BEGIN_TRANSITION_CALLBACK, ProfilerBeginTransitionCallback, SM_PtrVoid_RetPtrVoid)
DEFINE_METHOD(STUBHELPERS,          PROFILER_END_TRANSITION_CALLBACK,   ProfilerEndTransitionCallback,   SM_PtrVoid_RetVoid)
#endif

DEFINE_METHOD(STUBHELPERS,          MULTICAST_DEBUGGER_TRACE_HELPER,    MulticastDebuggerTraceHelper,    SM_Obj_Int_RetVoid)

DEFINE_CLASS(CLEANUP_WORK_LIST_ELEMENT,     StubHelpers,            CleanupWorkListElement)

DEFINE_CLASS(ANSICHARMARSHALER,     StubHelpers,            AnsiCharMarshaler)
DEFINE_METHOD(ANSICHARMARSHALER,    CONVERT_TO_NATIVE,      ConvertToNative,            SM_Char_Bool_Bool_RetByte)
DEFINE_METHOD(ANSICHARMARSHALER,    CONVERT_TO_MANAGED,     ConvertToManaged,           SM_Byte_RetChar)
DEFINE_METHOD(ANSICHARMARSHALER,    DO_ANSI_CONVERSION,     DoAnsiConversion,           SM_Str_Bool_Bool_RefInt_RetArrByte)

DEFINE_CLASS(CSTRMARSHALER,         StubHelpers,            CSTRMarshaler)
DEFINE_METHOD(CSTRMARSHALER,        CONVERT_TO_NATIVE,      ConvertToNative,            SM_Int_Str_IntPtr_RetIntPtr)
DEFINE_METHOD(CSTRMARSHALER,        CONVERT_FIXED_TO_NATIVE,ConvertFixedToNative,       SM_Int_Str_IntPtr_Int_RetVoid)
DEFINE_METHOD(CSTRMARSHALER,        CONVERT_TO_MANAGED,     ConvertToManaged,           SM_IntPtr_RetStr)
DEFINE_METHOD(CSTRMARSHALER,        CONVERT_FIXED_TO_MANAGED,ConvertFixedToManaged,     SM_IntPtr_Int_RetStr)

DEFINE_CLASS(FIXEDWSTRMARSHALER,   StubHelpers,            FixedWSTRMarshaler)
DEFINE_METHOD(FIXEDWSTRMARSHALER,  CONVERT_TO_NATIVE,      ConvertToNative,            SM_Str_IntPtr_Int_RetVoid)
DEFINE_METHOD(FIXEDWSTRMARSHALER,  CONVERT_TO_MANAGED,     ConvertToManaged,           SM_IntPtr_Int_RetStr)

DEFINE_CLASS(BSTRMARSHALER,         StubHelpers,            BSTRMarshaler)
DEFINE_METHOD(BSTRMARSHALER,        CONVERT_TO_NATIVE,      ConvertToNative,            SM_Str_IntPtr_RetIntPtr)
DEFINE_METHOD(BSTRMARSHALER,        CONVERT_TO_MANAGED,     ConvertToManaged,           SM_IntPtr_RetStr)
DEFINE_METHOD(BSTRMARSHALER,        CLEAR_NATIVE,           ClearNative,                SM_IntPtr_RetVoid)

DEFINE_CLASS(ANSIBSTRMARSHALER,     StubHelpers,            AnsiBSTRMarshaler)
DEFINE_METHOD(ANSIBSTRMARSHALER,    CONVERT_TO_NATIVE,      ConvertToNative,            SM_Int_Str_RetIntPtr)
DEFINE_METHOD(ANSIBSTRMARSHALER,    CONVERT_TO_MANAGED,     ConvertToManaged,           SM_IntPtr_RetStr)
DEFINE_METHOD(ANSIBSTRMARSHALER,    CLEAR_NATIVE,           ClearNative,                SM_IntPtr_RetVoid)

BEGIN_ILLINK_FEATURE_SWITCH(System.Runtime.InteropServices.BuiltInComInterop.IsSupported, true, true)
#ifdef FEATURE_COMINTEROP
DEFINE_CLASS(OBJECTMARSHALER,       StubHelpers,            ObjectMarshaler)
DEFINE_METHOD(OBJECTMARSHALER,      CONVERT_TO_NATIVE,      ConvertToNative,            SM_ObjIntPtr_RetVoid)
DEFINE_METHOD(OBJECTMARSHALER,      CONVERT_TO_MANAGED,     ConvertToManaged,           SM_IntPtr_RetObj)
DEFINE_METHOD(OBJECTMARSHALER,      CLEAR_NATIVE,           ClearNative,                SM_IntPtr_RetVoid)

DEFINE_CLASS(INTERFACEMARSHALER,    StubHelpers,            InterfaceMarshaler)
DEFINE_METHOD(INTERFACEMARSHALER,   CONVERT_TO_NATIVE,      ConvertToNative,            SM_Obj_IntPtr_IntPtr_Int_RetIntPtr)
DEFINE_METHOD(INTERFACEMARSHALER,   CONVERT_TO_MANAGED,     ConvertToManaged,           SM_RefIntPtr_IntPtr_IntPtr_Int_RetObj)
DEFINE_METHOD(INTERFACEMARSHALER,   CLEAR_NATIVE,           ClearNative,                SM_IntPtr_RetVoid)


DEFINE_CLASS(MNGD_SAFE_ARRAY_MARSHALER,  StubHelpers,                 MngdSafeArrayMarshaler)
DEFINE_METHOD(MNGD_SAFE_ARRAY_MARSHALER, CREATE_MARSHALER,            CreateMarshaler,            SM_IntPtr_IntPtr_Int_Int_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_SAFE_ARRAY_MARSHALER, CONVERT_SPACE_TO_NATIVE,     ConvertSpaceToNative,       SM_IntPtr_RefObj_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_SAFE_ARRAY_MARSHALER, CONVERT_CONTENTS_TO_NATIVE,  ConvertContentsToNative,    SM_IntPtr_RefObj_IntPtr_Obj_RetVoid)
DEFINE_METHOD(MNGD_SAFE_ARRAY_MARSHALER, CONVERT_SPACE_TO_MANAGED,    ConvertSpaceToManaged,      SM_IntPtr_RefObj_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_SAFE_ARRAY_MARSHALER, CONVERT_CONTENTS_TO_MANAGED, ConvertContentsToManaged,   SM_IntPtr_RefObj_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_SAFE_ARRAY_MARSHALER, CLEAR_NATIVE,                ClearNative,                SM_IntPtr_RefObj_IntPtr_RetVoid)

DEFINE_CLASS(COLORMARSHALER, StubHelpers, ColorMarshaler)
DEFINE_METHOD(COLORMARSHALER, CONVERT_TO_NATIVE,  ConvertToNative,  SM_Obj_RetInt)
DEFINE_METHOD(COLORMARSHALER, CONVERT_TO_MANAGED, ConvertToManaged, SM_Int_RetObj)
DEFINE_FIELD(COLORMARSHALER, COLOR_TYPE, s_colorType)
#endif // FEATURE_COMINTEROP
END_ILLINK_FEATURE_SWITCH()

DEFINE_CLASS(DATEMARSHALER,         StubHelpers,            DateMarshaler)
DEFINE_METHOD(DATEMARSHALER,        CONVERT_TO_NATIVE,      ConvertToNative,            SM_DateTime_RetDbl)
DEFINE_METHOD(DATEMARSHALER,        CONVERT_TO_MANAGED,     ConvertToManaged,           SM_Dbl_RetLong)

DEFINE_CLASS(VBBYVALSTRMARSHALER,   StubHelpers,            VBByValStrMarshaler)
DEFINE_METHOD(VBBYVALSTRMARSHALER,  CONVERT_TO_NATIVE,      ConvertToNative,            SM_Str_Bool_Bool_RefInt_RetIntPtr)
DEFINE_METHOD(VBBYVALSTRMARSHALER,  CONVERT_TO_MANAGED,     ConvertToManaged,           SM_IntPtr_Int_RetStr)
DEFINE_METHOD(VBBYVALSTRMARSHALER,  CLEAR_NATIVE,           ClearNative,                SM_IntPtr_RetVoid)

DEFINE_CLASS(MNGD_NATIVE_ARRAY_MARSHALER,  StubHelpers,                 MngdNativeArrayMarshaler)
DEFINE_METHOD(MNGD_NATIVE_ARRAY_MARSHALER, CREATE_MARSHALER,            CreateMarshaler,            SM_IntPtr_IntPtr_Int_Bool_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_NATIVE_ARRAY_MARSHALER, CONVERT_SPACE_TO_NATIVE,     ConvertSpaceToNative,       SM_IntPtr_RefObj_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_NATIVE_ARRAY_MARSHALER, CONVERT_CONTENTS_TO_NATIVE,  ConvertContentsToNative,    SM_IntPtr_RefObj_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_NATIVE_ARRAY_MARSHALER, CONVERT_SPACE_TO_MANAGED,    ConvertSpaceToManaged,      SM_IntPtr_RefObj_IntPtr_Int_RetVoid)
DEFINE_METHOD(MNGD_NATIVE_ARRAY_MARSHALER, CONVERT_CONTENTS_TO_MANAGED, ConvertContentsToManaged,   SM_IntPtr_RefObj_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_NATIVE_ARRAY_MARSHALER, CLEAR_NATIVE,                ClearNative,                SM_IntPtr_IntPtr_Int_RetVoid)
DEFINE_METHOD(MNGD_NATIVE_ARRAY_MARSHALER, CLEAR_NATIVE_CONTENTS,       ClearNativeContents,        SM_IntPtr_IntPtr_Int_RetVoid)

DEFINE_CLASS(MNGD_FIXED_ARRAY_MARSHALER,  StubHelpers,                 MngdFixedArrayMarshaler)
DEFINE_METHOD(MNGD_FIXED_ARRAY_MARSHALER, CREATE_MARSHALER,            CreateMarshaler,            SM_IntPtr_IntPtr_Int_Int_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_FIXED_ARRAY_MARSHALER, CONVERT_SPACE_TO_NATIVE,     ConvertSpaceToNative,       SM_IntPtr_RefObj_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_FIXED_ARRAY_MARSHALER, CONVERT_CONTENTS_TO_NATIVE,  ConvertContentsToNative,    SM_IntPtr_RefObj_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_FIXED_ARRAY_MARSHALER, CONVERT_SPACE_TO_MANAGED,    ConvertSpaceToManaged,      SM_IntPtr_RefObj_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_FIXED_ARRAY_MARSHALER, CONVERT_CONTENTS_TO_MANAGED, ConvertContentsToManaged,   SM_IntPtr_RefObj_IntPtr_RetVoid)
DEFINE_METHOD(MNGD_FIXED_ARRAY_MARSHALER, CLEAR_NATIVE_CONTENTS,       ClearNativeContents,        SM_IntPtr_RefObj_IntPtr_RetVoid)

DEFINE_CLASS(MNGD_REF_CUSTOM_MARSHALER,  StubHelpers,                 MngdRefCustomMarshaler)
DEFINE_METHOD(MNGD_REF_CUSTOM_MARSHALER, CONVERT_CONTENTS_TO_NATIVE,  ConvertContentsToNative,    SM_ICustomMarshaler_RefObj_PtrIntPtr_RetVoid)
DEFINE_METHOD(MNGD_REF_CUSTOM_MARSHALER, CONVERT_CONTENTS_TO_MANAGED, ConvertContentsToManaged,   SM_ICustomMarshaler_RefObj_PtrIntPtr_RetVoid)
DEFINE_METHOD(MNGD_REF_CUSTOM_MARSHALER, CLEAR_NATIVE,                ClearNative,                SM_ICustomMarshaler_RefObj_PtrIntPtr_RetVoid)
DEFINE_METHOD(MNGD_REF_CUSTOM_MARSHALER, CLEAR_MANAGED,               ClearManaged,               SM_ICustomMarshaler_RefObj_PtrIntPtr_RetVoid)

DEFINE_CLASS(ASANY_MARSHALER,            StubHelpers,                 AsAnyMarshaler)
DEFINE_METHOD(ASANY_MARSHALER,           CTOR,                        .ctor,                      IM_IntPtr_RetVoid)
DEFINE_METHOD(ASANY_MARSHALER,           CONVERT_TO_NATIVE,           ConvertToNative,            IM_Obj_Int_RetIntPtr)
DEFINE_METHOD(ASANY_MARSHALER,           CONVERT_TO_MANAGED,          ConvertToManaged,           IM_Obj_IntPtr_RetVoid)
DEFINE_METHOD(ASANY_MARSHALER,           CLEAR_NATIVE,                ClearNative,                IM_IntPtr_RetVoid)

DEFINE_CLASS(HANDLE_MARSHALER,           StubHelpers,                 HandleMarshaler)
DEFINE_METHOD(HANDLE_MARSHALER,          CONVERT_SAFEHANDLE_TO_NATIVE,ConvertSafeHandleToNative,  SM_SafeHandle_RefCleanupWorkListElement_RetIntPtr)
DEFINE_METHOD(HANDLE_MARSHALER,          THROW_SAFEHANDLE_FIELD_CHANGED, ThrowSafeHandleFieldChanged, SM_RetVoid)
DEFINE_METHOD(HANDLE_MARSHALER,          THROW_CRITICALHANDLE_FIELD_CHANGED, ThrowCriticalHandleFieldChanged, SM_RetVoid)

DEFINE_CLASS(COMVARIANT,            Marshalling,            ComVariant)

DEFINE_CLASS(SZARRAYHELPER,         System,                        SZArrayHelper)
// Note: The order of methods here has to match order they are implemented on the interfaces in
// IEnumerable`1
DEFINE_METHOD(SZARRAYHELPER,        GETENUMERATOR,          GetEnumerator,              NoSig)
// ICollection`1/IReadOnlyCollection`1
DEFINE_METHOD(SZARRAYHELPER,        GET_COUNT,              get_Count,                  NoSig)
DEFINE_METHOD(SZARRAYHELPER,        ISREADONLY,             get_IsReadOnly,             NoSig)
DEFINE_METHOD(SZARRAYHELPER,        ADD,                    Add,                        NoSig)
DEFINE_METHOD(SZARRAYHELPER,        CLEAR,                  Clear,                      NoSig)
DEFINE_METHOD(SZARRAYHELPER,        CONTAINS,               Contains,                   NoSig)
DEFINE_METHOD(SZARRAYHELPER,        COPYTO,                 CopyTo,                     NoSig)
DEFINE_METHOD(SZARRAYHELPER,        REMOVE,                 Remove,                     NoSig)
// IList`1/IReadOnlyList`1
DEFINE_METHOD(SZARRAYHELPER,        GET_ITEM,               get_Item,                   NoSig)
DEFINE_METHOD(SZARRAYHELPER,        SET_ITEM,               set_Item,                   NoSig)
DEFINE_METHOD(SZARRAYHELPER,        INDEXOF,                IndexOf,                    NoSig)
DEFINE_METHOD(SZARRAYHELPER,        INSERT,                 Insert,                     NoSig)
DEFINE_METHOD(SZARRAYHELPER,        REMOVEAT,               RemoveAt,                   NoSig)

DEFINE_CLASS(SZGENERICARRAYENUMERATOR, System, SZGenericArrayEnumerator`1)

DEFINE_CLASS(IENUMERABLEGENERIC,    CollectionsGeneric,     IEnumerable`1)
DEFINE_CLASS(IENUMERATORGENERIC,    CollectionsGeneric,     IEnumerator`1)
DEFINE_CLASS(ICOLLECTIONGENERIC,    CollectionsGeneric,     ICollection`1)
DEFINE_CLASS(ILISTGENERIC,          CollectionsGeneric,     IList`1)
DEFINE_CLASS(IREADONLYCOLLECTIONGENERIC,CollectionsGeneric, IReadOnlyCollection`1)
DEFINE_CLASS(IREADONLYLISTGENERIC,  CollectionsGeneric,     IReadOnlyList`1)
DEFINE_CLASS(IREADONLYDICTIONARYGENERIC,CollectionsGeneric, IReadOnlyDictionary`2)
DEFINE_CLASS(IDICTIONARYGENERIC,    CollectionsGeneric,     IDictionary`2)
DEFINE_CLASS(KEYVALUEPAIRGENERIC,   CollectionsGeneric,     KeyValuePair`2)

DEFINE_CLASS(LISTGENERIC,          CollectionsGeneric,      List`1)
DEFINE_FIELD(LISTGENERIC,          ITEMS,                   _items)
DEFINE_FIELD(LISTGENERIC,          SIZE,                    _size)

DEFINE_CLASS(ICOMPARABLEGENERIC,    System,                 IComparable`1)
DEFINE_METHOD(ICOMPARABLEGENERIC,   COMPARE_TO,             CompareTo,                  NoSig)

DEFINE_CLASS(IEQUATABLEGENERIC,     System,                 IEquatable`1)

DEFINE_CLASS_U(Reflection,             LoaderAllocator,          LoaderAllocatorObject)
DEFINE_FIELD_U(m_slots,                  LoaderAllocatorObject,      m_pSlots)
DEFINE_FIELD_U(m_slotsUsed,              LoaderAllocatorObject,      m_slotsUsed)
DEFINE_CLASS(LOADERALLOCATOR,           Reflection,             LoaderAllocator)
DEFINE_METHOD(LOADERALLOCATOR,          CTOR,                   .ctor,                    IM_RetVoid)

DEFINE_CLASS_U(Reflection,             LoaderAllocatorScout,     LoaderAllocatorScoutObject)
DEFINE_FIELD_U(m_nativeLoaderAllocator,  LoaderAllocatorScoutObject,      m_nativeLoaderAllocator)
DEFINE_CLASS(LOADERALLOCATORSCOUT,      Reflection,             LoaderAllocatorScout)

DEFINE_CLASS(CONTRACTEXCEPTION,     CodeContracts,  ContractException)

DEFINE_CLASS_U(CodeContracts,       ContractException,          ContractExceptionObject)
DEFINE_FIELD_U(_kind,               ContractExceptionObject,    _Kind)
DEFINE_FIELD_U(_userMessage,        ContractExceptionObject,    _UserMessage)
DEFINE_FIELD_U(_condition,          ContractExceptionObject,    _Condition)

DEFINE_CLASS(MODULEBASE,        Reflection,         Module)

DEFINE_CLASS(UTF8STRINGMARSHALLER, Marshalling, Utf8StringMarshaller)
DEFINE_METHOD(UTF8STRINGMARSHALLER, CONVERT_TO_MANAGED, ConvertToManaged, SM_PtrByte_RetStr)
DEFINE_METHOD(UTF8STRINGMARSHALLER, CONVERT_TO_UNMANAGED, ConvertToUnmanaged, SM_Str_RetPtrByte)
DEFINE_METHOD(UTF8STRINGMARSHALLER, FREE, Free, SM_PtrByte_RetVoid)

DEFINE_CLASS(UTF8STRINGMARSHALLER_IN, Marshalling, Utf8StringMarshaller+ManagedToUnmanagedIn)
DEFINE_METHOD(UTF8STRINGMARSHALLER_IN, FROM_MANAGED, FromManaged, IM_Str_SpanOfByte_RetVoid)
DEFINE_METHOD(UTF8STRINGMARSHALLER_IN, TO_UNMANAGED, ToUnmanaged, IM_RetPtrByte)
DEFINE_METHOD(UTF8STRINGMARSHALLER_IN, FREE, Free, IM_RetVoid)

DEFINE_CLASS(UTF8BUFFERMARSHALER, StubHelpers, UTF8BufferMarshaler)
DEFINE_METHOD(UTF8BUFFERMARSHALER, CONVERT_TO_NATIVE, ConvertToNative, NoSig)
DEFINE_METHOD(UTF8BUFFERMARSHALER, CONVERT_TO_MANAGED, ConvertToManaged, NoSig)

// Classes referenced in EqualityComparer<T>.Default optimization

DEFINE_CLASS(STRING_EQUALITYCOMPARER, CollectionsGeneric, StringEqualityComparer)
DEFINE_CLASS(ENUM_EQUALITYCOMPARER, CollectionsGeneric, EnumEqualityComparer`1)
DEFINE_CLASS(NULLABLE_EQUALITYCOMPARER, CollectionsGeneric, NullableEqualityComparer`1)
DEFINE_CLASS(GENERIC_EQUALITYCOMPARER, CollectionsGeneric, GenericEqualityComparer`1)
DEFINE_CLASS(OBJECT_EQUALITYCOMPARER, CollectionsGeneric, ObjectEqualityComparer`1)

// Classes referenced in Comparer<T>.Default optimization

DEFINE_CLASS(GENERIC_COMPARER, CollectionsGeneric, GenericComparer`1)
DEFINE_CLASS(OBJECT_COMPARER, CollectionsGeneric, ObjectComparer`1)
DEFINE_CLASS(ENUM_COMPARER, CollectionsGeneric, EnumComparer`1)
DEFINE_CLASS(NULLABLE_COMPARER, CollectionsGeneric, NullableComparer`1)

DEFINE_CLASS(INATTRIBUTE, Interop, InAttribute)

DEFINE_CLASS(CASTCACHE, CompilerServices, CastHelpers)
DEFINE_FIELD(CASTCACHE, TABLE, s_table)

#ifdef DEBUG
// GenericCache is generic, so we can't check layout with DEFINE_FIELD_U
// Instead we do an ad hoc check
DEFINE_CLASS(GENERICCACHE, CompilerServices, GenericCache`2)
DEFINE_FIELD(GENERICCACHE, TABLE, _table)
DEFINE_FIELD(GENERICCACHE, SENTINEL_TABLE, _sentinelTable)
DEFINE_FIELD(GENERICCACHE, LAST_FLUSH_SIZE, _lastFlushSize)
DEFINE_FIELD(GENERICCACHE, INITIAL_CACHE_SIZE, _initialCacheSize)
DEFINE_FIELD(GENERICCACHE, MAX_CACHE_SIZE, _maxCacheSize)
#endif

DEFINE_CLASS(CASTHELPERS, CompilerServices, CastHelpers)
DEFINE_METHOD(CASTHELPERS, ISINSTANCEOFANY,  IsInstanceOfAny,             SM_PtrVoid_Obj_RetObj)
DEFINE_METHOD(CASTHELPERS, ISINSTANCEOFCLASS,IsInstanceOfClass,           SM_PtrVoid_Obj_RetObj)
DEFINE_METHOD(CASTHELPERS, ISINSTANCEOFINTERFACE,  IsInstanceOfInterface, SM_PtrVoid_Obj_RetObj)
DEFINE_METHOD(CASTHELPERS, CHKCASTANY,       ChkCastAny,                  SM_PtrVoid_Obj_RetObj)
DEFINE_METHOD(CASTHELPERS, CHKCASTINTERFACE, ChkCastInterface,            SM_PtrVoid_Obj_RetObj)
DEFINE_METHOD(CASTHELPERS, CHKCASTCLASS,     ChkCastClass,                SM_PtrVoid_Obj_RetObj)
DEFINE_METHOD(CASTHELPERS, CHKCASTCLASSSPECIAL, ChkCastClassSpecial,      SM_PtrVoid_Obj_RetObj)
DEFINE_METHOD(CASTHELPERS, BOX,              Box,                         NoSig)
DEFINE_METHOD(CASTHELPERS, UNBOX,            Unbox,                       NoSig)
DEFINE_METHOD(CASTHELPERS, STELEMREF,        StelemRef,                   SM_ArrObject_IntPtr_Obj_RetVoid)
DEFINE_METHOD(CASTHELPERS, LDELEMAREF,       LdelemaRef,                  SM_ArrObject_IntPtr_PtrVoid_RetRefObj)
DEFINE_METHOD(CASTHELPERS, ARRAYTYPECHECK,   ArrayTypeCheck,              SM_Obj_Array_RetVoid)
DEFINE_METHOD(CASTHELPERS, BOX_NULLABLE,     Box_Nullable,                NoSig)
DEFINE_METHOD(CASTHELPERS, UNBOX_NULLABLE,   Unbox_Nullable,              NoSig)
DEFINE_METHOD(CASTHELPERS, UNBOX_TYPETEST,   Unbox_TypeTest,              NoSig)

DEFINE_CLASS(VIRTUALDISPATCHHELPERS, CompilerServices, VirtualDispatchHelpers)
DEFINE_METHOD(VIRTUALDISPATCHHELPERS, VIRTUALFUNCTIONPOINTER, VirtualFunctionPointer, NoSig)
DEFINE_METHOD(VIRTUALDISPATCHHELPERS, VIRTUALFUNCTIONPOINTER_DYNAMIC, VirtualFunctionPointer_Dynamic, NoSig)
DEFINE_FIELD(VIRTUALDISPATCHHELPERS, CACHE, s_virtualFunctionPointerCache)

DEFINE_CLASS_U(CompilerServices, VirtualDispatchHelpers+VirtualFunctionPointerArgs, VirtualFunctionPointerArgs)
DEFINE_FIELD_U(classHnd, VirtualFunctionPointerArgs, classHnd)
DEFINE_FIELD_U(methodHnd, VirtualFunctionPointerArgs, methodHnd)

DEFINE_CLASS(GENERICSHELPERS, CompilerServices, GenericsHelpers)
DEFINE_METHOD(GENERICSHELPERS, METHOD, Method, NoSig)
DEFINE_METHOD(GENERICSHELPERS, CLASS, Class, NoSig)
DEFINE_METHOD(GENERICSHELPERS, METHODWITHSLOTANDMODULE, MethodWithSlotAndModule, NoSig)
DEFINE_METHOD(GENERICSHELPERS, CLASSWITHSLOTANDMODULE, ClassWithSlotAndModule, NoSig)

DEFINE_CLASS(INITHELPERS, CompilerServices, InitHelpers)
DEFINE_METHOD(INITHELPERS, INITCLASS, InitClass, NoSig)
DEFINE_METHOD(INITHELPERS, INITINSTANTIATEDCLASS, InitInstantiatedClass, NoSig)

DEFINE_CLASS(STATICSHELPERS, CompilerServices, StaticsHelpers)
DEFINE_METHOD(STATICSHELPERS, GET_NONGC_STATIC, GetNonGCStaticBase, NoSig)
DEFINE_METHOD(STATICSHELPERS, GET_GC_STATIC, GetGCStaticBase, NoSig)
DEFINE_METHOD(STATICSHELPERS, GET_DYNAMIC_NONGC_STATIC, GetDynamicNonGCStaticBase, NoSig)
DEFINE_METHOD(STATICSHELPERS, GET_DYNAMIC_GC_STATIC, GetDynamicGCStaticBase, NoSig)
DEFINE_METHOD(STATICSHELPERS, GET_NONGC_THREADSTATIC, GetNonGCThreadStaticBase, NoSig)
DEFINE_METHOD(STATICSHELPERS, GET_GC_THREADSTATIC, GetGCThreadStaticBase, NoSig)
DEFINE_METHOD(STATICSHELPERS, GET_DYNAMIC_NONGC_THREADSTATIC, GetDynamicNonGCThreadStaticBase, NoSig)
DEFINE_METHOD(STATICSHELPERS, GET_DYNAMIC_GC_THREADSTATIC, GetDynamicGCThreadStaticBase, NoSig)
DEFINE_METHOD(STATICSHELPERS, GET_OPTIMIZED_NONGC_THREADSTATIC, GetOptimizedNonGCThreadStaticBase, NoSig)
DEFINE_METHOD(STATICSHELPERS, GET_OPTIMIZED_GC_THREADSTATIC, GetOptimizedGCThreadStaticBase, NoSig)

DEFINE_METHOD(STATICSHELPERS, STATICFIELDADDRESS_DYNAMIC, StaticFieldAddress_Dynamic, NoSig)
DEFINE_METHOD(STATICSHELPERS, STATICFIELDADDRESSUNBOX_DYNAMIC, StaticFieldAddressUnbox_Dynamic, NoSig)

DEFINE_CLASS_U(CompilerServices, GenericsHelpers+GenericHandleArgs, GenericHandleArgs)
DEFINE_FIELD_U(signature, GenericHandleArgs, signature)
DEFINE_FIELD_U(module, GenericHandleArgs, module)
DEFINE_FIELD_U(dictionaryIndexAndSlot, GenericHandleArgs, dictionaryIndexAndSlot)

#ifdef FEATURE_EH_FUNCLETS
DEFINE_CLASS(EH, Runtime, EH)
DEFINE_METHOD(EH, RH_THROW_EX, RhThrowEx, SM_Obj_RefExInfo_RetVoid)
DEFINE_METHOD(EH, RH_THROWHW_EX, RhThrowHwEx, SM_UInt_RefExInfo_RetVoid)
DEFINE_METHOD(EH, RH_RETHROW, RhRethrow, SM_RefExInfo_RefExInfo_RetVoid)
DEFINE_METHOD(EH, UNWIND_AND_INTERCEPT, RhUnwindAndIntercept, SM_RefExInfo_UIntPtr_RetVoid)
DEFINE_CLASS(EXCEPTIONSERVICES_INTERNALCALLS, ExceptionServices, InternalCalls)
DEFINE_CLASS(STACKFRAMEITERATOR, Runtime, StackFrameIterator)
#endif // FEATURE_EH_FUNCLETS

DEFINE_CLASS(EXINFO, Runtime, EH+ExInfo)

DEFINE_CLASS_U(System, GCMemoryInfoData, GCMemoryInfoData)
DEFINE_FIELD_U(_highMemoryLoadThresholdBytes, GCMemoryInfoData, highMemLoadThresholdBytes)
DEFINE_FIELD_U(_totalAvailableMemoryBytes, GCMemoryInfoData, totalAvailableMemoryBytes)
DEFINE_FIELD_U(_memoryLoadBytes, GCMemoryInfoData, lastRecordedMemLoadBytes)
DEFINE_FIELD_U(_heapSizeBytes, GCMemoryInfoData, lastRecordedHeapSizeBytes)
DEFINE_FIELD_U(_fragmentedBytes, GCMemoryInfoData, lastRecordedFragmentationBytes)
DEFINE_FIELD_U(_totalCommittedBytes, GCMemoryInfoData, totalCommittedBytes)
DEFINE_FIELD_U(_promotedBytes, GCMemoryInfoData, promotedBytes)
DEFINE_FIELD_U(_pinnedObjectsCount, GCMemoryInfoData, pinnedObjectCount)
DEFINE_FIELD_U(_finalizationPendingCount, GCMemoryInfoData, finalizationPendingCount)
DEFINE_FIELD_U(_index, GCMemoryInfoData, index)
DEFINE_FIELD_U(_generation, GCMemoryInfoData, generation)
DEFINE_FIELD_U(_pauseTimePercentage, GCMemoryInfoData, pauseTimePercent)
DEFINE_FIELD_U(_compacted, GCMemoryInfoData, isCompaction)
DEFINE_FIELD_U(_concurrent, GCMemoryInfoData, isConcurrent)
DEFINE_FIELD_U(_pauseDuration0, GCMemoryInfoData, pauseDuration0)
DEFINE_FIELD_U(_pauseDuration1, GCMemoryInfoData, pauseDuration1)
DEFINE_FIELD_U(_generationInfo0, GCMemoryInfoData, generationInfo0)
DEFINE_FIELD_U(_generationInfo1, GCMemoryInfoData, generationInfo1)
DEFINE_FIELD_U(_generationInfo2, GCMemoryInfoData, generationInfo2)
DEFINE_FIELD_U(_generationInfo3, GCMemoryInfoData, generationInfo3)
DEFINE_FIELD_U(_generationInfo4, GCMemoryInfoData, generationInfo4)

#undef DEFINE_CLASS
#undef DEFINE_METHOD
#undef DEFINE_FIELD
#undef DEFINE_CLASS_U
#undef DEFINE_FIELD_U
#undef BEGIN_ILLINK_FEATURE_SWITCH
#undef END_ILLINK_FEATURE_SWITCH
