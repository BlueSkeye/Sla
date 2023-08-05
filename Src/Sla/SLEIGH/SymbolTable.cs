﻿using Sla.CORE;
using Sla.SLEIGH;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading.Tasks;

namespace Sla.SLEIGH
{
    internal class SymbolTable
    {
        private List<SleighSymbol> symbollist;
        private List<SymbolScope> table;
        private SymbolScope curscope;

        private SymbolScope skipScope(int i)
        {
            SymbolScope* res = curscope;
            while (i > 0)
            {
                if (res.parent == (SymbolScope)null) return res;
                res = res.parent;
                --i;
            }
            return res;
        }

        private SleighSymbol findSymbolInternal(SymbolScope scope, string nm)
        {
            SleighSymbol* res;

            while (scope != (SymbolScope)null)
            {
                res = scope.findSymbol(nm);
                if (res != (SleighSymbol)null)
                    return res;
                scope = scope.getParent(); // Try higher scope
            }
            return (SleighSymbol)null;
        }

        private void renumber()
        {               // Renumber all the scopes and symbols
                        // so that there are no gaps
            List<SymbolScope*> newtable;
            List<SleighSymbol*> newsymbol;
            // First renumber the scopes
            SymbolScope* scope;
            for (int i = 0; i < table.size(); ++i)
            {
                scope = table[i];
                if (scope != (SymbolScope)null)
                {
                    scope.id = newtable.size();
                    newtable.Add(scope);
                }
            }
            // Now renumber the symbols
            SleighSymbol* sym;
            for (int i = 0; i < symbollist.size(); ++i)
            {
                sym = symbollist[i];
                if (sym != (SleighSymbol)null)
                {
                    sym.scopeid = table[sym.scopeid].id;
                    sym.id = newsymbol.size();
                    newsymbol.Add(sym);
                }
            }
            table = newtable;
            symbollist = newsymbol;
        }

        public SymbolTable()
        {
            curscope = (SymbolScope)null;
        }
        
        ~SymbolTable()
        {
            List<SymbolScope*>::iterator iter;
            for (iter = table.begin(); iter != table.end(); ++iter)
                delete* iter;
            List<SleighSymbol*>::iterator siter;
            for (siter = symbollist.begin(); siter != symbollist.end(); ++siter)
                delete* siter;
        }

        public SymbolScope getCurrentScope() => curscope;

        public SymbolScope getGlobalScope() => table[0];

        public void setCurrentScope(SymbolScope scope)
        {
            curscope = scope;
        }

        // Add new scope off of current scope, make it current
        public void addScope()
        {
            curscope = new SymbolScope(curscope, table.size());
            table.Add(curscope);
        }

        // Make parent of current scope current
        public void popScope()
        {
            if (curscope != (SymbolScope)null)
                curscope = curscope.getParent();
        }

        public void addGlobalSymbol(SleighSymbol a)
        {
            a.id = symbollist.size();
            symbollist.Add(a);
            SymbolScope* scope = getGlobalScope();
            a.scopeid = scope.getId();
            SleighSymbol* res = scope.addSymbol(a);
            if (res != a)
                throw new SleighError("Duplicate symbol name '" + a.getName() + "'");
        }

        public void addSymbol(SleighSymbol a)
        {
            a.id = symbollist.size();
            symbollist.Add(a);
            a.scopeid = curscope.getId();
            SleighSymbol* res = curscope.addSymbol(a);
            if (res != a)
                throw new SleighError("Duplicate symbol name: " + a.getName());
        }

        public SleighSymbol findSymbol(string nm) => findSymbolInternal(curscope, nm);

        public SleighSymbol findSymbol(string nm, int skip)
            => findSymbolInternal(skipScope(skip), nm);

        public SleighSymbol findGlobalSymbol(string nm) => findSymbolInternal(table[0],nm);

        public SleighSymbol findSymbol(uint id) => symbollist[id];

        public void replaceSymbol(SleighSymbol a, SleighSymbol b)
        {
            // Replace symbol a with symbol b
            // assuming a and b have the same name
            SleighSymbol sym;
            int i = table.size() - 1;

            while (i >= 0) {
                // Find the particular symbol
                sym = table[i].findSymbol(a.getName());
                if (sym == a) {
                    table[i].removeSymbol(a);
                    b.id = a.id;
                    b.scopeid = a.scopeid;
                    symbollist[b.id] = b;
                    table[i].addSymbol(b);
                    // delete a;
                    return;
                }
                --i;
            }
        }

        public void saveXml(TextWriter s)
        {
            s << "<symbol_table";
            s << " scopesize=\"" << dec << table.size() << "\"";
            s << " symbolsize=\"" << symbollist.size() << "\">\n";
            for (int i = 0; i < table.size(); ++i)
            {
                s << "<scope id=\"0x" << hex << table[i].getId() << "\"";
                s << " parent=\"0x";
                if (table[i].getParent() == (SymbolScope)null)
                    s << "0";
                else
                    s << hex << table[i].getParent().getId();
                s << "\"/>\n";
            }

            // First save the headers
            for (int i = 0; i < symbollist.size(); ++i)
                symbollist[i].saveXmlHeader(s);

            // Now save the content of each symbol
            for (int i = 0; i < symbollist.size(); ++i) // Must save IN ORDER
                symbollist[i].saveXml(s);
            s << "</symbol_table>\n";
        }

        public void restoreXml(Element el, SleighBase trans)
        {
            {
                uint size;
                istringstream s = new istringstream(el.getAttributeValue("scopesize"));
                s.unsetf(ios::dec | ios::hex | ios::oct);
                s >> size;
                table.resize(size, (SymbolScope)null);
            }
            {
                uint size;
                istringstream s = new istringstream(el.getAttributeValue("symbolsize"));
                s.unsetf(ios::dec | ios::hex | ios::oct);
                s >> size;
                symbollist.resize(size, (SleighSymbol)null);
            }
            List list = el.getChildren();
            List::const_iterator iter;
            iter = list.begin();
            for (int i = 0; i < table.size(); ++i)
            { // Restore the scopes
                Element* subel = *iter;
                if (subel.getName() != "scope")
                    throw new SleighError("Misnumbered symbol scopes");
                uint id;
                uint parent;
                {
                    istringstream s = new istringstream(subel.getAttributeValue("id"));
                    s.unsetf(ios::dec | ios::hex | ios::oct);
                    s >> id;
                }
                {
                    istringstream s = new istringstream(subel.getAttributeValue("parent"));
                    s.unsetf(ios::dec | ios::hex | ios::oct);
                    s >> parent;
                }
                SymbolScope* parscope = (parent == id) ? (SymbolScope)null : table[parent];
                table[id] = new SymbolScope(parscope, id);
                ++iter;
            }
            curscope = table[0];        // Current scope is global

            // Now restore the symbol shells
            for (int i = 0; i < symbollist.size(); ++i)
            {
                restoreSymbolHeader(*iter);
                ++iter;
            }
            // Now restore the symbol content
            while (iter != list.end())
            {
                Element* subel = *iter;
                uint id;
                SleighSymbol* sym;
                {
                    istringstream s = new istringstream(subel.getAttributeValue("id"));
                    s.unsetf(ios::dec | ios::hex | ios::oct);
                    s >> id;
                }
                sym = findSymbol(id);
                sym.restoreXml(subel, trans);
                ++iter;
            }
        }

        public void restoreSymbolHeader(Element el)
        {               // Put the shell of a symbol in the symbol table
                        // in order to allow recursion
            SleighSymbol sym;
            if (el.getName() == "userop_head")
                sym = new UserOpSymbol();
            else if (el.getName() == "epsilon_sym_head")
                sym = new EpsilonSymbol();
            else if (el.getName() == "value_sym_head")
                sym = new ValueSymbol();
            else if (el.getName() == "valuemap_sym_head")
                sym = new ValueMapSymbol();
            else if (el.getName() == "name_sym_head")
                sym = new NameSymbol();
            else if (el.getName() == "varnode_sym_head")
                sym = new VarnodeSymbol();
            else if (el.getName() == "context_sym_head")
                sym = new ContextSymbol();
            else if (el.getName() == "varlist_sym_head")
                sym = new VarnodeListSymbol();
            else if (el.getName() == "operand_sym_head")
                sym = new OperandSymbol();
            else if (el.getName() == "start_sym_head")
                sym = new StartSymbol();
            else if (el.getName() == "end_sym_head")
                sym = new EndSymbol();
            else if (el.getName() == "next2_sym_head")
                sym = new Next2Symbol();
            else if (el.getName() == "subtable_sym_head")
                sym = new SubtableSymbol();
            else if (el.getName() == "flowdest_sym_head")
                sym = new FlowDestSymbol();
            else if (el.getName() == "flowref_sym_head")
                sym = new FlowRefSymbol();
            else
                throw new SleighError("Bad symbol xml");
            sym.restoreXmlHeader(el);  // Restore basic elements of symbol
            symbollist[sym.id] = sym;  // Put the basic symbol in the table
            table[sym.scopeid].addSymbol(sym); // to allow recursion
        }

        public void purge()
        {
            // Get rid of unsavable symbols and scopes
            SleighSymbol sym;
            for (int i = 0; i < symbollist.Count; ++i) {
                sym = symbollist[i];
                if (sym == (SleighSymbol)null) continue;
                if (sym.scopeid != 0) {
                    // Not in global scope
                    if (sym.getType() == SleighSymbol.symbol_type.operand_symbol) continue;
                }
                else {
                    switch (sym.getType()) {
                        case SleighSymbol.symbol_type.space_symbol:
                        case SleighSymbol.symbol_type.token_symbol:
                        case SleighSymbol.symbol_type.epsilon_symbol:
                        case SleighSymbol.symbol_type.section_symbol:
                            break;
                        case SleighSymbol.symbol_type.macro_symbol:
                            {           // Delete macro's local symbols
                                MacroSymbol macro = (MacroSymbol)sym;
                                for (int j = 0; j < macro.getNumOperands(); ++j) {
                                    SleighSymbol opersym = macro.getOperand(j);
                                    table[opersym.scopeid].removeSymbol(opersym);
                                    symbollist[opersym.id] = (SleighSymbol)null;
                                    // delete opersym;
                                }
                                break;
                            }
                        case SleighSymbol.symbol_type.subtable_symbol:
                            {           // Delete unused subtables
                                SubtableSymbol subsym = (SubtableSymbol)sym;
                                if (subsym.getPattern() != (TokenPattern)null) continue;
                                for (int k = 0; k < subsym.getNumConstructors(); ++k) {
                                    // Go thru each constructor
                                    Constructor con = subsym.getConstructor(k);
                                    for (int j = 0; j < con.getNumOperands(); ++j) {
                                        // Go thru each operand
                                        OperandSymbol oper = con.getOperand(j);
                                        table[oper.scopeid].removeSymbol(oper);
                                        symbollist[oper.id] = (SleighSymbol)null;
                                        // delete oper;
                                    }
                                }
                                break;      // Remove the subtable symbol itself
                            }
                        default:
                            continue;
                    }
                }
                table[sym.scopeid].removeSymbol(sym); // Remove the symbol
                symbollist[i] = (SleighSymbol)null;
                // delete sym;
            }
            for (int i = 1; i < table.Count; ++i) {
                // Remove any empty scopes
                if (table[i].tree.empty()) {
                    // delete table[i];
                    table[i] = (SymbolScope)null;
                }
            }
            renumber();
        }
    }
}
