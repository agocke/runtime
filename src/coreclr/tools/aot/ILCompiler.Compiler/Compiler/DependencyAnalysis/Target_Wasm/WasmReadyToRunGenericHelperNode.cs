// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using ILCompiler.DependencyAnalysis.Wasm;

namespace ILCompiler.DependencyAnalysis
{
    public partial class ReadyToRunGenericHelperNode
    {
        protected override void EmitCode(NodeFactory factory, ref WasmEmitter encoder, bool relocsOnly)
        {
            // TODO-WASM: implement generic helper stubs
            encoder.Builder.EmitByte(0x00); // 0 local declarations
            encoder.Builder.EmitByte(0x00); // unreachable
            encoder.Builder.EmitByte(0x0b); // end
        }

        protected virtual void EmitLoadGenericContext(NodeFactory factory, ref WasmEmitter encoder, bool relocsOnly)
        {
            // TODO-WASM: implement generic context loading
            encoder.Builder.EmitByte(0x00); // unreachable
        }
    }

    public partial class ReadyToRunGenericLookupFromTypeNode
    {
        protected override void EmitLoadGenericContext(NodeFactory factory, ref WasmEmitter encoder, bool relocsOnly)
        {
            // TODO-WASM: implement generic context loading from type
            encoder.Builder.EmitByte(0x00); // unreachable
        }
    }
}
