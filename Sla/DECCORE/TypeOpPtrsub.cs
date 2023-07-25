﻿using ghidra;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sla.DECCORE
{
    /// \brief Information about the PTRSUB op-code
    internal class TypeOpPtrsub : TypeOp
    {
        public TypeOpPtrsub(TypeFactory t)
            : base(t, CPUI_PTRSUB,"->")

        {
            // As an operation this is really addition
            // So it should be commutative
            // But the typing information doesn't really
            // allow this to be commutative.
            opflags = PcodeOp::binary | PcodeOp::nocollapse;
            addlflags = arithmetic_op;
            behave = new OpBehavior(CPUI_PTRSUB, false); // Dummy behavior
        }

        public override Datatype getOutputLocal(PcodeOp op)
        {               // Output is ptr to type of subfield
            return tlst->getBase(op->getOut()->getSize(), TYPE_INT);
        }

        public override Datatype getInputLocal(PcodeOp op, int4 slot)
        {
            return tlst->getBase(op->getIn(slot)->getSize(), TYPE_INT);
        }

        public override Datatype getInputCast(PcodeOp op, int4 slot, CastStrategy castStrategy)
        {
            if (slot == 0)
            {       // The operation expects the type of the VARNODE
                    // not the (possibly different) type of the HIGH
                Datatype* reqtype = op->getIn(0)->getTypeReadFacing(op);
                Datatype* curtype = op->getIn(0)->getHighTypeReadFacing(op);
                return castStrategy->castStandard(reqtype, curtype, false, false);
            }
            return TypeOp::getInputCast(op, slot, castStrategy);
        }

        public override Datatype getOutputToken(PcodeOp op, CastStrategy castStrategy)
        {
            TypePointer* ptype = (TypePointer*)op->getIn(0)->getHighTypeReadFacing(op);
            if (ptype->getMetatype() == TYPE_PTR)
            {
                uintb offset = AddrSpace::addressToByte(op->getIn(1)->getOffset(), ptype->getWordSize());
                uintb unusedOffset;
                TypePointer* unusedParent;
                Datatype* rettype = ptype->downChain(offset, unusedParent, unusedOffset, false, *tlst);
                if ((offset == 0) && (rettype != (Datatype*)0))
                    return rettype;
                rettype = tlst->getBase(1, TYPE_UNKNOWN);
                return tlst->getTypePointer(op->getOut()->getSize(), rettype, ptype->getWordSize());
            }
            return TypeOp::getOutputToken(op, castStrategy);
        }

        public override Datatype propagateType(Datatype alttype, PcodeOp op, Varnode invn, Varnode outvn,
            int4 inslot, int4 outslot)
        {
            if ((inslot != -1) && (outslot != -1)) return (Datatype*)0; // Must propagate input <-> output
            type_metatype metain = alttype->getMetatype();
            if (metain != TYPE_PTR) return (Datatype*)0;
            Datatype* newtype;
            if (inslot == -1)       // Propagating output to input
                newtype = op->getIn(outslot)->getTempType();    // Don't propagate pointer types this direction
            else
                newtype = TypeOpIntAdd::propagateAddIn2Out(alttype, tlst, op, inslot);
            return newtype;
        }

        public override push(PrintLanguage lng, PcodeOp op, PcodeOp readOp)
        {
            lng->opPtrsub(op);
        }

        public override void printRaw(TextWriter s, PcodeOp op)
        {
            Varnode::printRaw(s, op->getOut());
            s << " = ";
            Varnode::printRaw(s, op->getIn(0));
            s << ' ' << name << ' ';
            Varnode::printRaw(s, op->getIn(1));
        }
    }
}
