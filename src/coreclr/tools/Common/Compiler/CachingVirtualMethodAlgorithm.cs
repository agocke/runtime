// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

using Internal.TypeSystem;

namespace ILCompiler
{
    public class CachingVirtualMethodAlgorithm : MetadataVirtualMethodAlgorithm
    {
        private readonly ConcurrentDictionary<TypeDesc, (MethodDesc Slot, MethodDesc Implementation)[]> _vtableCache
            = new ConcurrentDictionary<TypeDesc, (MethodDesc Slot, MethodDesc Implementation)[]>();

        private readonly Func<TypeDesc, (MethodDesc Slot, MethodDesc Implementation)[]> _vtableCreator;
#if !READYTORUN
        private readonly InterfaceBindingCache _interfaceBindings;

        public CachingVirtualMethodAlgorithm()
        {
            _vtableCreator = ComputeVtable;
            _interfaceBindings = new InterfaceBindingCache(ResolveInterfaceUncached);
        }

        public override MethodDesc ResolveInterfaceMethodToVirtualMethodOnType(MethodDesc interfaceMethod, TypeDesc currentType)
            => _interfaceBindings.Get(currentType, interfaceMethod, InterfaceBindingKind.Instance).Implementation;

        public override MethodDesc ResolveInterfaceMethodToStaticVirtualMethodOnType(MethodDesc interfaceMethod, TypeDesc currentType)
            => _interfaceBindings.Get(currentType, interfaceMethod, InterfaceBindingKind.Static).Implementation;

        public override DefaultInterfaceMethodResolution ResolveInterfaceMethodToDefaultImplementationOnType(
            MethodDesc interfaceMethod, TypeDesc currentType, out MethodDesc impl)
        {
            InterfaceBindingResult result = _interfaceBindings.Get(currentType, interfaceMethod, InterfaceBindingKind.Default);
            impl = result.Implementation;
            return result.Resolution;
        }

        private InterfaceBindingResult ResolveInterfaceUncached(TypeDesc type, MethodDesc method, InterfaceBindingKind kind)
        {
            switch (kind)
            {
                case InterfaceBindingKind.Instance:
                    return new InterfaceBindingResult(base.ResolveInterfaceMethodToVirtualMethodOnType(method, type));
                case InterfaceBindingKind.Static:
                    return new InterfaceBindingResult(base.ResolveInterfaceMethodToStaticVirtualMethodOnType(method, type));
                case InterfaceBindingKind.Default:
                    DefaultInterfaceMethodResolution resolution = base.ResolveInterfaceMethodToDefaultImplementationOnType(method, type, out MethodDesc impl);
                    return new InterfaceBindingResult(impl, resolution);
                default:
                    throw new UnreachableException();
            }
        }
#else
        public CachingVirtualMethodAlgorithm()
            => _vtableCreator = ComputeVtable;
#endif

        private (MethodDesc Slot, MethodDesc Implementation)[] ComputeVtable(TypeDesc type)
        {
#if !READYTORUN
            TypeDiscoveryCensus.Count(TypeDiscoveryCensus.Unit.VTableCreations);
#endif
            // Do not recompute the same things for all the generics
            Debug.Assert(type.IsTypeDefinition);

            var result = new ArrayBuilder<(MethodDesc Slot, MethodDesc Implementation)>(3 /* At least Equals/GetHashCode/ToString */);

            foreach (MethodDesc slotDecl in base.ComputeAllVirtualSlots(type))
            {
                result.Add((slotDecl, base.FindVirtualFunctionTargetMethodOnObjectType(slotDecl, type)));
            }

            return result.ToArray();
        }

        public virtual IEnumerable<(MethodDesc Slot, MethodDesc Implementation)> EnumerateResolvedSlots(TypeDesc type)
        {
            foreach ((MethodDesc slotDefinition, MethodDesc implementationDefinition) in
                _vtableCache.GetOrAdd(type.GetTypeDefinition(), _vtableCreator))
            {
                MethodDesc slot = type.FindMethodOnTypeWithMatchingTypicalMethod(slotDefinition);
                MethodDesc implementation = slot.HasInstantiation ? null
                    : type.FindMethodOnTypeWithMatchingTypicalMethod(implementationDefinition);
                yield return (slot, implementation);
            }
        }

        public override IEnumerable<MethodDesc> ComputeAllVirtualSlots(TypeDesc type)
        {
            // This just enumerates virtual methods, not worth caching.
            if (type.IsInterface)
                return base.ComputeAllVirtualSlots(type);

            return GetCachedSlots(this, type);

            static IEnumerable<MethodDesc> GetCachedSlots(CachingVirtualMethodAlgorithm thisObj, TypeDesc type)
            {
                foreach ((MethodDesc slot, _) in thisObj._vtableCache.GetOrAdd(type.GetTypeDefinition(), thisObj._vtableCreator))
                {
                    yield return type.FindMethodOnTypeWithMatchingTypicalMethod(slot);
                }
            }
        }

        public override MethodDesc FindVirtualFunctionTargetMethodOnObjectType(MethodDesc targetMethod, TypeDesc objectType)
        {
            MetadataType uninstantiatedType = (MetadataType)objectType.GetTypeDefinition();
            MethodDesc targetMethodDefinition = targetMethod.GetMethodDefinition();

            MethodDesc slotDefiningMethod = targetMethodDefinition;
            if (uninstantiatedType != objectType)
            {
                slotDefiningMethod = uninstantiatedType.FindMethodOnTypeWithMatchingTypicalMethod(slotDefiningMethod);
            }
            slotDefiningMethod = FindSlotDefiningMethodForVirtualMethod(slotDefiningMethod);

            foreach (var kvp in _vtableCache.GetOrAdd(objectType.GetTypeDefinition(), _vtableCreator))
            {
#if !READYTORUN
                TypeDiscoveryCensus.Count(TypeDiscoveryCensus.Unit.SlotComparisons);
#endif
                if (kvp.Slot == slotDefiningMethod)
                {
                    MethodDesc result = kvp.Implementation;

                    if (uninstantiatedType != objectType)
                        result = objectType.FindMethodOnTypeWithMatchingTypicalMethod(result);

                    if (targetMethod != targetMethodDefinition)
                        result = result.MakeInstantiatedMethod(targetMethod.Instantiation);

                    return result;
                }
            }

            return null;
        }
    }
}
