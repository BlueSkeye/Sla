﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Sla.DECCORE
{
    internal class RuleSubfloatConvert : Rule
    {
        public RuleSubfloatConvert(string g)
            : base(g, 0, "subfloat_convert")
        {
        }

        public override Rule clone(ActionGroupList grouplist)
        {
            if (!grouplist.contains(getGroup())) return (Rule*)0;
            return new RuleSubfloatConvert(getGroup());
        }

        /// \class RuleSubfloatConvert
        /// \brief Perform SubfloatFlow analysis triggered by FLOAT_FLOAT2FLOAT
        public override void getOpList(List<uint4> oplist)
        {
            oplist.push_back(CPUI_FLOAT_FLOAT2FLOAT);
        }

        public override int4 applyOp(PcodeOp op, Funcdata data)
        {
            Varnode* invn = op->getIn(0);
            Varnode* outvn = op->getOut();
            int4 insize = invn->getSize();
            int4 outsize = outvn->getSize();
            if (outsize > insize)
            {
                SubfloatFlow subflow(&data,outvn,insize);
                if (!subflow.doTrace()) return 0;
                subflow.apply();
            }
            else
            {
                SubfloatFlow subflow(&data,invn,outsize);
                if (!subflow.doTrace()) return 0;
                subflow.apply();
            }
            return 1;
        }
    }
}
