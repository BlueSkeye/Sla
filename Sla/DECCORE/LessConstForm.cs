﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Sla.DECCORE
{
    internal class LessConstForm
    {
        private SplitVarnode @in;
        private Varnode vn;
        private Varnode cvn;
        private int4 inslot;
        private bool signcompare;
        private bool hilessequalform;
        private SplitVarnode constin;

        // Sometimes double precision compares only involve the high portion of the value
        // The canonical example being determining whether val > 0, where we only have to
        // calculate (hi > 0).  This rule takes
        //    hi COMPARE #const
        // and transforms it to 
        //    whole COMPARE #constextend
        // where #constextend is built from #const by postpending either all 0 bits or 1 bits
        public bool applyRule(SplitVarnode i, PcodeOp op, bool workishi, Funcdata data)
        {
            if (!workishi) return false;
            if (i.getHi() == (Varnode*)0) return false; // We don't necessarily need the lo part
            @in = i;
            vn = @in.getHi();
            inslot = op->getSlot(vn);
            cvn = op->getIn(1 - inslot);
            int4 losize = @in.getSize() - vn->getSize();

            if (!cvn->isConstant()) return false;

            signcompare = ((op->code() == CPUI_INT_SLESSEQUAL) || (op->code() == CPUI_INT_SLESS));
            hilessequalform = ((op->code() == CPUI_INT_SLESSEQUAL) || (op->code() == CPUI_INT_LESSEQUAL));

            uintb val = cvn->getOffset() << 8 * losize;
            if (hilessequalform != (inslot == 1))
                val |= calc_mask(losize);

            // This rule can apply and mess up less,equal rules, so we only apply it if it directly affects a branch
            PcodeOp* desc = op->getOut()->loneDescend();
            if (desc == (PcodeOp*)0) return false;
            if (desc->code() != CPUI_CBRANCH) return false;

            constin.initPartial(in.getSize(), val);

            if (inslot == 0)
            {
                if (SplitVarnode::prepareBoolOp(@in, constin, op))
                {
                    SplitVarnode::replaceBoolOp(data, op, @in, constin, op->code());
                    return true;
                }
            }
            else
            {
                if (SplitVarnode::prepareBoolOp(constin, @in, op))
                {
                    SplitVarnode::replaceBoolOp(data, op, constin, @in, op->code());
                    return true;
                }
            }

            return false;
        }
    }
}
