﻿using Sla.DECCORE;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using static ghidra.XmlScan;

namespace Sla.DECCORE
{
    /// \brief The java-language token emitter
    ///
    /// This builds heavily on the c-language PrintC emitter.  Most operator tokens, the format of
    /// function prototypes, and code structuring are shared.  Specifics of the java constant pool are handled
    /// through the overloaded opCpoolRefOp().
    ///
    /// Java data-types are mapped into the decompiler's data-type system in a specific way. The primitives
    /// \b int, \b long, \b short, \b byte, \b boolean, \b float, and \b double all map directly. The
    /// \b char primitive is treated as a 2 byte unsigned integer. A TypeStruct object holds the field
    /// layout for a java class, then java objects get mapped as follows:
    ///   - Class reference = pointer to TYPE_UINT
    ///   - Array of \b int, \b long, \b short, or \b byte = pointer to TYPE_INT
    ///   - Array of \b float or \b double = pointer to TYPE_FLOAT
    ///   - Array of \b boolean = pointer to TYPE_BOOL
    ///   - Array of class objects = pointer to TYPE_PTR
    ///
    /// There are some adjustments to the printing of data-types and LOAD/STORE expressions
    /// to account for this mapping.
    internal class PrintJava : PrintC
    {
        /// The \b instanceof keyword
        private static OpToken instanceof;

        ///< Does the given data-type reference a java array
        /// References to java array objects where the underlying element is a java primitive look like:
        ///   - Pointer to int
        ///   - Pointer to bool
        ///   - Pointer to float
        ///
        /// An array of java class objects is represented as a pointer to pointer data-type.
        /// \param ct is the given data-type
        /// \return \b true if the data-type references a java array object
        private static bool isArrayType(Datatype ct)
        {
            if (ct->getMetatype() != TYPE_PTR)  // Java arrays are always Ghidra pointer types
                return false;
            ct = ((TypePointer*)ct)->getPtrTo();
            switch (ct->getMetatype())
            {
                case TYPE_UINT:     // Pointer to unsigned is placeholder for class reference, not an array
                    if (ct->isCharPrint())
                        return true;
                    break;
                case TYPE_INT:
                case TYPE_BOOL:
                case TYPE_FLOAT:    // Pointer to primitive type is an array
                case TYPE_PTR:  // Pointer to class reference is an array
                    return true;
                default:
                    break;
            }
            return false;
        }

        ///< Do we need '[0]' syntax.
        /// Assuming the given Varnode is a dereferenced pointer, determine whether
        /// it needs to be represented using '[0]' syntax.
        /// \param vn is the given Varnode
        /// \return \b true if '[0]' syntax is required
        private static bool needZeroArray(Varnode vn)
        {
            if (!isArrayType(vn->getType()))
                return false;
            if (vn->isExplicit()) return true;
            if (!vn->isWritten()) return true;
            OpCode opc = vn->getDef()->code();
            if ((opc == CPUI_PTRADD) || (opc == CPUI_PTRSUB) || (opc == CPUI_CPOOLREF))
                return false;
            return true;
        }

        ///< Set options that are specific to Java
        private void resetDefaultsPrintJava()
        {
            option_NULL = true;         // Automatically use 'null' token
            option_convention = false;      // Automatically hide convention name
            mods |= hide_thisparam;     // turn on hiding of 'this' parameter
        }

        private override void printUnicode(TextWriter s, int4 onechar)
        {
            if (unicodeNeedsEscape(onechar))
            {
                switch (onechar)
                {       // Special escape characters
                    case 0:
                        s << "\\0";
                        return;
                    case 8:
                        s << "\\b";
                        return;
                    case 9:
                        s << "\\t";
                        return;
                    case 10:
                        s << "\\n";
                        return;
                    case 12:
                        s << "\\f";
                        return;
                    case 13:
                        s << "\\r";
                        return;
                    case 92:
                        s << "\\\\";
                        return;
                    case '"':
                        s << "\\\"";
                        return;
                    case '\'':
                        s << "\\\'";
                        return;
                }
                // Generic unicode escape
                if (onechar < 65536)
                {
                    s << "\\ux" << setfill('0') << setw(4) << hex << onechar;
                }
                else
                    s << "\\ux" << setfill('0') << setw(8) << hex << onechar;
                return;
            }
            StringManager::writeUtf8(s, onechar);       // Emit normally
        }

        public PrintJava(Architecture g, string nm="java-language")
        {
            resetDefaultsPrintJava();
            nullToken = "null";         // Java standard lower-case 'null'
            if (castStrategy != (CastStrategy*)0)
                delete castStrategy;

            castStrategy = new CastStrategyJava();
        }

        public override void resetDefaults()
        {
            PrintC::resetDefaults();
            resetDefaultsPrintJava();
        }

        public override void docFunction(Funcdata fd)
        {
            bool singletonFunction = false;
            if (curscope == (const Scope*)0) {
                singletonFunction = true;
                // Always assume we are in the scope of the parent class
                pushScope(fd->getScopeLocal()->getParent());
            }
            PrintC::docFunction(fd);
            if (singletonFunction)
                popScope();
        }

        /// Print a data-type up to the identifier, store off array sizes
        /// for printing after the identifier. Find the root type (the one with an identifier)
        /// and the count number of wrapping arrays.
        /// \param ct is the given data-type
        /// \param noident is \b true if no identifier will be pushed with this declaration
        public override void pushTypeStart(Datatype ct,bool noident)
        {
            int4 arrayCount = 0;
            for (; ; )
            {
                if (ct->getMetatype() == TYPE_PTR)
                {
                    if (isArrayType(ct))
                        arrayCount += 1;
                    ct = ((TypePointer*)ct)->getPtrTo();
                }
                else if (ct->getName().size() != 0)
                    break;
                else
                {
                    ct = glb->types->getTypeVoid();
                    break;
                }
            }
            OpToken* tok;

            if (noident)
                tok = &type_expr_nospace;
            else
                tok = &type_expr_space;

            pushOp(tok, (const PcodeOp*)0);
            for (int4 i = 0; i < arrayCount; ++i)
                pushOp(&subscript, (const PcodeOp*)0);

            if (ct->getName().size() == 0)
            {   // Check for anonymous type
                // We could support a struct or enum declaration here
                string nm = genericTypeName(ct);
                pushAtom(Atom(nm, typetoken, EmitMarkup::type_color, ct));
            }
            else
            {
                pushAtom(Atom(ct->getDisplayName(), typetoken, EmitMarkup::type_color, ct));
            }
            for (int4 i = 0; i < arrayCount; ++i)
                pushAtom(Atom(EMPTY_STRING, blanktoken, EmitMarkup::no_color));     // Fill in the blank array index
        }

        public override void pushTypeEnd(Datatype ct)
        { // This routine doesn't have to do anything
        }

        public override bool doEmitWideCharPrefix() => false;

        public override void adjustTypeOperators()
        {
            scope.print1 = ".";
            shift_right.print1 = ">>>";
            TypeOp::selectJavaOperators(glb->inst, true);
        }

        public override void opLoad(PcodeOp op)
        {
            uint4 m = mods | print_load_value;
            bool printArrayRef = needZeroArray(op->getIn(1));
            if (printArrayRef)
                pushOp(&subscript, op);
            pushVn(op->getIn(1), op, m);
            if (printArrayRef)
                push_integer(0, 4, false, (Varnode*)0, op);
        }

        public override void opStore(PcodeOp op)
        {
            uint4 m = mods | print_store_value; // Inform sub-tree that we are storing
            pushOp(&assignment, op);    // This is an assignment
            if (needZeroArray(op->getIn(1)))
            {
                pushOp(&subscript, op);
                pushVn(op->getIn(1), op, m);
                push_integer(0, 4, false, (Varnode*)0, op);
                pushVn(op->getIn(2), op, mods);
            }
            else
            {
                // implied vn's pushed on in reverse order for efficiency
                // see PrintLanguage::pushVnImplied
                pushVn(op->getIn(2), op, mods);
                pushVn(op->getIn(1), op, m);
            }
        }

        public override void opCallind(PcodeOp op)
        {
            pushOp(&function_call, op);
            const Funcdata* fd = op->getParent()->getFuncdata();
            FuncCallSpecs* fc = fd->getCallSpecs(op);
            if (fc == (FuncCallSpecs*)0)
                throw LowlevelError("Missing indirect function callspec");
            int4 skip = getHiddenThisSlot(op, fc);
            int4 count = op->numInput() - 1;
            count -= (skip < 0) ? 0 : 1;
            if (count > 1)
            {   // Multiple parameters
                pushVn(op->getIn(0), op, mods);
                for (int4 i = 0; i < count - 1; ++i)
                    pushOp(&comma, op);
                // implied vn's pushed on in reverse order for efficiency
                // see PrintLanguage::pushVnImplied
                for (int4 i = op->numInput() - 1; i >= 1; --i)
                {
                    if (i == skip) continue;
                    pushVn(op->getIn(i), op, mods);
                }
            }
            else if (count == 1)
            {   // One parameter
                if (skip == 1)
                    pushVn(op->getIn(2), op, mods);
                else
                    pushVn(op->getIn(1), op, mods);
                pushVn(op->getIn(0), op, mods);
            }
            else
            {           // A void function
                pushVn(op->getIn(0), op, mods);
                pushAtom(Atom(EMPTY_STRING, blanktoken, EmitMarkup::no_color));
            }
        }

        public override void opCpoolRefOp(PcodeOp op)
        {
            const Varnode* outvn = op->getOut();
            const Varnode* vn0 = op->getIn(0);
            vector<uintb> refs;
            for (int4 i = 1; i < op->numInput(); ++i)
                refs.push_back(op->getIn(i)->getOffset());
            const CPoolRecord* rec = glb->cpool->getRecord(refs);
            if (rec == (const CPoolRecord*)0) {
                pushAtom(Atom("UNKNOWNREF", syntax, EmitMarkup::const_color, op, outvn));
            }
  else
            {
                switch (rec->getTag())
                {
                    case CPoolRecord::string_literal:
                        {
                            ostringstream str;
                            int4 len = rec->getByteDataLength();
                            if (len > 2048)
                                len = 2048;
                            str << '\"';
                            escapeCharacterData(str, rec->getByteData(), len, 1, false);
                            if (len == rec->getByteDataLength())
                                str << '\"';
                            else
                            {
                                str << "...\"";
                            }
                            pushAtom(Atom(str.str(), vartoken, EmitMarkup::const_color, op, outvn));
                            break;
                        }
                    case CPoolRecord::class_reference:
                        pushAtom(Atom(rec->getToken(), vartoken, EmitMarkup::type_color, op, outvn));
                        break;
                    case CPoolRecord::instance_of:
                        {
                            Datatype* dt = rec->getType();
                            while (dt->getMetatype() == TYPE_PTR)
                            {
                                dt = ((TypePointer*)dt)->getPtrTo();
                            }
                            pushOp(&instanceof, op);
                            pushVn(vn0, op, mods);
                            pushAtom(Atom(dt->getDisplayName(), syntax, EmitMarkup::type_color, op, outvn));
                            break;
                        }
                    case CPoolRecord::primitive:        // Should be eliminated
                    case CPoolRecord::pointer_method:
                    case CPoolRecord::pointer_field:
                    case CPoolRecord::array_length:
                    case CPoolRecord::check_cast:
                    default:
                        {
                            Datatype* ct = rec->getType();
                            EmitMarkup::syntax_highlight color = EmitMarkup::var_color;
                            if (ct->getMetatype() == TYPE_PTR)
                            {
                                ct = ((TypePointer*)ct)->getPtrTo();
                                if (ct->getMetatype() == TYPE_CODE)
                                    color = EmitMarkup::funcname_color;
                            }
                            if (vn0->isConstant())
                            {   // If this is NOT relative to an object reference
                                pushAtom(Atom(rec->getToken(), vartoken, color, op, outvn));
                            }
                            else
                            {
                                pushOp(&object_member, op);
                                pushVn(vn0, op, mods);
                                pushAtom(Atom(rec->getToken(), syntax, color, op, outvn));
                            }
                        }
                }
            }
        }
    }
}
