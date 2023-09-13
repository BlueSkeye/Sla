﻿using Sla.DECCORE;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace Sla.EXTRA
{
    internal class IfcIsolate : IfaceDecompCommand
    {
        /// \class IfcIsolate
        /// \brief Mark a symbol as isolated from speculative merging: `isolate <name>`
        public override void execute(TextReader s)
        {
            string symbolName;

            s.ReadSpaces() >> symbolName;
            if (symbolName.size() == 0)
                throw new IfaceParseError("Missing symbol name");

            Symbol* sym;
            List<Symbol*> symList;
            dcp.readSymbol(symbolName, symList);
            if (symList.empty())
                throw new IfaceExecutionError("No symbol named: " + symbolName);
            if (symList.size() == 1)
                sym = symList[0];
            else
                throw new IfaceExecutionError("More than one symbol named: " + symbolName);
            sym.setIsolated(true);
        }
    }
}
