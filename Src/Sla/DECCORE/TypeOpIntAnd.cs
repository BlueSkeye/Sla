﻿using Sla.CORE;

namespace Sla.DECCORE
{
    /// \brief Information about the INT_AND op-code
    internal class TypeOpIntAnd : TypeOpBinary
    {
        public TypeOpIntAnd(TypeFactory t)
            : base(t, OpCode.CPUI_INT_AND, "&", type_metatype.TYPE_UINT,
                  type_metatype.TYPE_UINT)
        {
            opflags = PcodeOp.Flags.binary | PcodeOp.Flags.commutative;
            addlflags = OperationType.logical_op | OperationType.inherits_sign;
            behave = new OpBehaviorIntAnd();
        }

        public override Datatype getOutputToken(PcodeOp op, CastStrategy castStrategy)
        {
            return castStrategy.arithmeticOutputStandard(op);
        }

        public override Datatype propagateType(Datatype alttype, PcodeOp op, Varnode invn, Varnode outvn,
            int inslot, int outslot)
        {
            if (!alttype.isPowerOfTwo())
                // Only propagate flag enums
                return (Datatype)null;
            Datatype newtype;
            if (invn.isSpacebase()) {
                AddrSpace spc = tlst.getArch().getDefaultDataSpace();
                newtype = tlst.getTypePointer(alttype.getSize(), tlst.getBase(1,
                    type_metatype.TYPE_UNKNOWN), spc.getWordSize());
            }
            else
                newtype = alttype;
            return newtype;
        }

        public override void push(PrintLanguage lng, PcodeOp op, PcodeOp readOp)
        {
            lng.opIntAnd(op);
        }
    }
}
