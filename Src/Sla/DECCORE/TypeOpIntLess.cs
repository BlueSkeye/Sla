﻿using Sla.CORE;

namespace Sla.DECCORE
{
    /// \brief Information about the INT_LESS op-code
    internal class TypeOpIntLess : TypeOpBinary
    {
        public TypeOpIntLess(TypeFactory t)
            : base(t, OpCode.CPUI_INT_LESS,"<", type_metatype.TYPE_BOOL, type_metatype.TYPE_UINT)
        {
            opflags = PcodeOp.Flags.binary | PcodeOp.Flags.booloutput;
            addlflags = OperationType.inherits_sign;
            behave = new OpBehaviorIntLess();
        }

        public override void push(PrintLanguage lng, PcodeOp op, PcodeOp readOp)
        {
            lng.opIntLess(op);
        }

        public override Datatype? getInputCast(PcodeOp op, int slot, CastStrategy castStrategy)
        {
            Datatype reqtype = op.inputTypeLocal(slot);
            if (castStrategy.checkIntPromotionForCompare(op, slot))
                return reqtype;
            Datatype curtype = op.getIn(slot).getHighTypeReadFacing(op);
            return castStrategy.castStandard(reqtype, curtype, true, false);
        }

        public override Datatype propagateType(Datatype alttype, PcodeOp op, Varnode invn,
            Varnode outvn, int inslot, int outslot)
        {
            return TypeOpEqual.propagateAcrossCompare(alttype, tlst, invn, outvn, inslot,
                outslot);
        }
    }
}
