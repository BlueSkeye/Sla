﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sla.DECCORE
{
    /// \brief Information about the INSERT op-code
    internal class TypeOpInsert : TypeOpFunc
    {
        public TypeOpInsert(TypeFactory t)
            : base(t, CPUI_INSERT,"INSERT", TYPE_UNKNOWN, TYPE_INT)
        {
            opflags = PcodeOp::ternary;
            behave = new OpBehavior(CPUI_INSERT, false);    // Dummy behavior
        }

        public override Datatype getInputLocal(PcodeOp op, int4 slot)
        {
            if (slot == 0)
                return tlst->getBase(op->getIn(slot)->getSize(), TYPE_UNKNOWN);
            return TypeOpFunc::getInputLocal(op, slot);
        }

        public override void push(PrintLanguage lng, PcodeOp op, PcodeOp readOp)
        {
            lng->opInsertOp(op);
        }
    }
}
