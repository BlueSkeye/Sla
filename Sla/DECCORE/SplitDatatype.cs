﻿using ghidra;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Sla.DECCORE
{
    /// \brief Split a p-code COPY, LOAD, or STORE op based on underlying composite data-type
    ///
    /// During the cleanup phase, if a COPY, LOAD, or STORE occurs on a partial structure or array
    /// (TypePartialStruct), try to break it up into multiple operations that each act on logical component
    /// of the structure or array.
    internal class SplitDatatype
    {
        /// \brief A helper class describing a pair of matching data-types for the split
        ///
        /// Data-types being copied simultaneously are split up into these matching pairs.
        private class Component
        {
            // friend class SplitDatatype;
            /// Data-type coming into the logical COPY operation
            internal Datatype inType;
            /// Data-type coming out of the logical COPY operation
            internal Datatype outType;
            /// Offset of this logical piece within the whole
            internal int4 offset;
            
            public Component(Datatype @in, Datatype @out, int4 off)
            {
                inType =@in;
                outType =@out;
                offset = off;
            }
        }

        /// \brief A helper class describing the pointer being passed to a LOAD or STORE
        ///
        /// It makes distinction between the immediate pointer to the LOAD or STORE and a \e root pointer
        /// to the main structure or array, which the immediate pointer may be at an offset from.
        private class RootPointer
        {
            // friend class SplitDatatype;
            /// LOAD or STORE op
            private PcodeOp loadStore;
            /// Base pointer data-type of LOAD or STORE
            private TypePointer ptrType;
            /// Direct pointer input for LOAD or STORE
            private Varnode firstPointer;
            /// The root pointer
            private Varnode pointer;
            /// Offset of the LOAD or STORE relative to root pointer
            private int4 baseOffset;

            /// Follow flow of \b pointer back thru INT_ADD or PTRSUB
            /// If \b pointer Varnode is written by an INT_ADD, PTRSUB, or PTRADD from a another pointer
            /// to a structure or array, update \b pointer Varnode, \b baseOffset, and \b ptrType to this.
            /// \return \b true if \b pointer was successfully updated
            private bool backUpPointer()
            {
                if (!pointer->isWritten())
                    return false;
                PcodeOp* addOp = pointer->getDef();
                OpCode opc = addOp->code();
                if (opc != CPUI_PTRSUB && opc != CPUI_INT_ADD && opc != CPUI_PTRADD)
                    return false;
                Varnode* cvn = addOp->getIn(1);
                if (!cvn->isConstant())
                    return false;
                Varnode* tmpPointer = addOp->getIn(0);
                Datatype* ct = tmpPointer->getTypeReadFacing(addOp);
                if (ct->getMetatype() != TYPE_PTR)
                    return false;
                Datatype* parent = ((TypePointer*)ct)->getPtrTo();
                type_metatype meta = parent->getMetatype();
                if (meta != TYPE_STRUCT && meta != TYPE_ARRAY)
                    return false;
                ptrType = (TypePointer*)ct;
                int4 off = (int4)cvn->getOffset();
                if (opc == CPUI_PTRADD)
                    off *= (int4)addOp->getIn(2)->getOffset();
                off = AddrSpace::addressToByteInt(off, ptrType->getWordSize());
                baseOffset += off;
                pointer = tmpPointer;
                return true;
            }

            /// Locate root pointer for underlying LOAD or STORE
            /// The LOAD or STORE pointer Varnode is examined. If it is a pointer to the given data-type, the
            /// root \b pointer is returned.  If not, we try to recursively walk back through either PTRSUB or INT_ADD instructions,
            /// until a pointer Varnode matching the data-type is found.  Any accumulated offset, relative to the original
            /// LOAD or STORE pointer is recorded in the \b baseOffset.  If a matching pointer is not found, \b false is returned.
            /// \param op is the LOAD or STORE
            /// \param valueType is the specific data-type to match
            /// \return \b true if the root pointer is found
            public bool find(PcodeOp op, Datatype valueType)
            {
                if (valueType->getMetatype() == TYPE_PARTIALSTRUCT)
                    valueType = ((TypePartialStruct*)valueType)->getParent();
                loadStore = op;
                baseOffset = 0;
                firstPointer = pointer = op->getIn(1);
                Datatype* ct = pointer->getTypeReadFacing(op);
                if (ct->getMetatype() != TYPE_PTR)
                    return false;
                ptrType = (TypePointer*)ct;
                if (ptrType->getPtrTo() != valueType)
                {
                    if (!backUpPointer())
                        return false;
                    if (ptrType->getPtrTo() != valueType)
                        return false;
                }
                for (int4 i = 0; i < 2; ++i)
                {
                    if (pointer->isAddrTied() || pointer->loneDescend() == (PcodeOp*)0) break;
                    if (!backUpPointer())
                        break;
                }
                return true;
            }

            /// Remove unused pointer calculations
            /// If the pointer Varnode is no longer used, recursively check and remove the op producing it,
            /// which will be either an INT_ADD or PTRSUB, until the root \b pointer is reached or
            /// a Varnode still being used is encountered.
            /// \param data is the containing function
            public void freePointerChain(Funcdata data)
            {
                while (firstPointer != pointer && !firstPointer->isAddrTied() && firstPointer->hasNoDescend())
                {
                    PcodeOp* tmpOp = firstPointer->getDef();
                    firstPointer = tmpOp->getIn(0);
                    data.opDestroy(tmpOp);
                }
            }
        }
        /// The containing function
        private Funcdata data;
        /// The data-type container
        private TypeFactory types;
        /// Sequence of all data-type pairs being copied
        private List<Component> dataTypePieces;
        /// Whether or not structures should be split
        private bool splitStructures;
        /// Whether or not arrays should be split
        private bool splitArrays;

        /// \brief Obtain the component of the given data-type at the specified offset
        ///
        /// The data-type must be a composite of some form. This method finds a component data-type
        /// starting exactly at the offset, if it exists.  The component may be nested more than 1 level deep.
        /// If the given data-type is of composite form and has no component defined at the specified offset,
        /// an undefined data-type matching the size of the \e hole is returned and \b isHole is set to \b true.
        /// \param ct is the given data-type
        /// \param offset is the specified offset
        /// \param isHole passes back whether a hole in the composite was encountered
        /// \return the component data-type at the offset or null, if no such component exists
        private Datatype getComponent(Datatype ct, int4 offset, bool isHole)
        {
            isHole = false;
            Datatype* curType = ct;
            uintb curOff = offset;
            do
            {
                curType = curType->getSubType(curOff, &curOff);
                if (curType == (Datatype*)0)
                {
                    int4 hole = ct->getHoleSize(offset);
                    if (hole > 0)
                    {
                        if (hole > 8)
                            hole = 8;
                        isHole = true;
                        return types->getBase(hole, TYPE_UNKNOWN);
                    }
                    return curType;
                }
            } while (curOff != 0 || curType->getMetatype() == TYPE_ARRAY);
            return curType;
        }

        /// Categorize if and how data-type should be split
        /// For the given data-type, taking into account configuration options, return:
        ///   - -1 for not splittable
        ///   - 0 for data-type that needs to be split
        ///   - 1 for data-type that can be split multiple ways
        /// \param ct is the given data-type
        /// \return the categorization
        private int4 categorizeDatatype(Datatype ct)
        {
            Datatype* subType;
            switch (ct->getMetatype())
            {
                case TYPE_ARRAY:
                    if (!splitArrays) break;
                    subType = ((TypeArray*)ct)->getBase();
                    if (subType->getMetatype() != TYPE_UNKNOWN || subType->getSize() != 1)
                        return 0;
                    else
                        return 1;   // unknown1 array does not need splitting and acts as (large) primitive
                case TYPE_PARTIALSTRUCT:
                    subType = ((TypePartialStruct*)ct)->getParent();
                    if (subType->getMetatype() == TYPE_ARRAY)
                    {
                        if (!splitArrays) break;
                        subType = ((TypeArray*)subType)->getBase();
                        if (subType->getMetatype() != TYPE_UNKNOWN || subType->getSize() != 1)
                            return 0;
                        else
                            return 1;   // unknown1 array does not need splitting and acts as (large) primitive
                    }
                    else if (subType->getMetatype() == TYPE_STRUCT)
                    {
                        if (!splitStructures) break;
                        return 0;
                    }
                    break;
                case TYPE_STRUCT:
                    if (!splitStructures) break;
                    if (ct->numDepend() > 1)
                        return 0;
                    break;
                case TYPE_INT:
                case TYPE_UINT:
                case TYPE_UNKNOWN:
                    return 1;
                default:
                    break;
            }
            return -1;
        }

        /// \brief Can the two given data-types be mutually split into matching logical components
        ///
        /// Test if the data-types have components with matching size and offset. If so, the component
        /// data-types and offsets are saved to the \b pieces array and \b true is returned.
        /// At least one of the data-types must be a partial data-type, but the other may be a
        /// TYPE_UNKNOWN, which this method assumes can be split into components of arbitrary size.
        /// \param inBase is the data-type coming into the operation
        /// \param outBase is the data-type coming out of the operation
        /// \param inConstant is \b true if the incoming data-type labels a constant
        /// \return \b true if the data-types have compatible components, \b false otherwise
        private bool testDatatypeCompatibility(Datatype inBase, Datatype outBase, bool inConstant)
        {
            int4 inCategory = categorizeDatatype(inBase);
            if (inCategory < 0)
                return false;
            int4 outCategory = categorizeDatatype(outBase);
            if (outCategory < 0)
                return false;
            if (outCategory != 0 && inCategory != 0)
                return false;
            if (!inConstant && inBase == outBase && inBase->getMetatype() == TYPE_STRUCT)
                return false;   // Don't split a whole structure unless it is getting initialized from a constant
            bool inHole;
            bool outHole;
            int4 curOff = 0;
            int4 sizeLeft = inBase->getSize();
            if (inCategory == 1)
            {
                while (sizeLeft > 0)
                {
                    Datatype* curOut = getComponent(outBase, curOff, outHole);
                    if (curOut == (Datatype*)0) return false;
                    // Throw away primitive data-type if it is a constant
                    Datatype* curIn = inConstant ? curOut : types->getBase(curOut->getSize(), TYPE_UNKNOWN);
                    dataTypePieces.emplace_back(curIn, curOut, curOff);
                    sizeLeft -= curOut->getSize();
                    curOff += curOut->getSize();
                    if (outHole)
                    {
                        if (dataTypePieces.size() == 1)
                            return false;       // Initial offset into structure is at a hole
                        if (sizeLeft == 0 && dataTypePieces.size() == 2)
                            return false;       // Two pieces, one is a hole.  Likely padding.
                    }
                }
            }
            else if (outCategory == 1)
            {
                while (sizeLeft > 0)
                {
                    Datatype* curIn = getComponent(inBase, curOff, inHole);
                    if (curIn == (Datatype*)0) return false;
                    Datatype* curOut = types->getBase(curIn->getSize(), TYPE_UNKNOWN);
                    dataTypePieces.emplace_back(curIn, curOut, curOff);
                    sizeLeft -= curIn->getSize();
                    curOff += curIn->getSize();
                    if (inHole)
                    {
                        if (dataTypePieces.size() == 1)
                            return false;       // Initial offset into structure is at a hole
                        if (sizeLeft == 0 && dataTypePieces.size() == 2)
                            return false;       // Two pieces, one is a hole.  Likely padding.
                    }
                }
            }
            else
            {   // Both in and out data-types have components
                while (sizeLeft > 0)
                {
                    Datatype* curIn = getComponent(inBase, curOff, inHole);
                    if (curIn == (Datatype*)0) return false;
                    Datatype* curOut = getComponent(outBase, curOff, outHole);
                    if (curOut == (Datatype*)0) return false;
                    while (curIn->getSize() != curOut->getSize())
                    {
                        if (curIn->getSize() > curOut->getSize())
                        {
                            if (inHole)
                                curIn = types->getBase(curOut->getSize(), TYPE_UNKNOWN);
                            else
                                curIn = getComponent(curIn, 0, inHole);
                            if (curIn == (Datatype*)0) return false;
                        }
                        else
                        {
                            if (outHole)
                                curOut = types->getBase(curIn->getSize(), TYPE_UNKNOWN);
                            else
                                curOut = getComponent(curOut, 0, outHole);
                            if (curOut == (Datatype*)0) return false;
                        }
                    }
                    dataTypePieces.emplace_back(curIn, curOut, curOff);
                    sizeLeft -= curIn->getSize();
                    curOff += curIn->getSize();
                }
            }
            return dataTypePieces.size() > 1;
        }

        /// \brief Test specific constraints for splitting the given COPY operation into pieces
        ///
        /// Don't split function inputs.  Don't split hidden COPYs.
        /// \return \b true if the split can proceed
        private bool testCopyConstraints(PcodeOp copyOp)
        {
            Varnode* inVn = copyOp->getIn(0);
            if (inVn->isInput()) return false;
            if (inVn->isAddrTied())
            {
                Varnode* outVn = copyOp->getOut();
                if (outVn->isAddrTied() && outVn->getAddr() == inVn->getAddr())
                    return false;
            }
            else if (inVn->isWritten() && inVn->getDef()->code() == CPUI_LOAD)
            {
                if (inVn->loneDescend() == copyOp)
                    return false;       // This situation is handled by splitCopy()
            }
            return true;
        }

        /// \brief If the given Varnode is an extended precision constant, create split constants
        ///
        /// Look for ZEXT(#c) and CONCAT(#c1,#c2) forms. Try to split into single precision Varnodes.
        /// \param vn is the given Varnode
        /// \param inVarnodes will contain the split constant Varnodes
        /// \return \b true if the Varnode is an extended precision constant and the split is successful
        private bool generateConstants(Varnode vn, List<Varnode> inVarnodes)
        {
            if (vn->loneDescend() == (PcodeOp*)0) return false;
            if (!vn->isWritten()) return false;
            PcodeOp* op = vn->getDef();
            OpCode opc = op->code();
            if (opc == CPUI_INT_ZEXT)
            {
                if (!op->getIn(0)->isConstant()) return false;
            }
            else if (opc == CPUI_PIECE)
            {
                if (!op->getIn(0)->isConstant() || !op->getIn(1)->isConstant())
                    return false;
            }
            else
                return false;
            uintb lo, hi;
            int4 losize;
            int4 fullsize = vn->getSize();
            bool isBigEndian = vn->getSpace()->isBigEndian();
            if (opc == CPUI_INT_ZEXT)
            {
                hi = 0;
                lo = op->getIn(0)->getOffset();
                losize = op->getIn(0)->getSize();
            }
            else
            {
                hi = op->getIn(0)->getOffset();
                lo = op->getIn(1)->getOffset();
                losize = op->getIn(1)->getSize();
            }
            for (int4 i = 0; i < dataTypePieces.size(); ++i)
            {
                Datatype* dt = dataTypePieces[i].inType;
                if (dt->getSize() > sizeof(uintb))
                {
                    inVarnodes.clear();
                    return false;
                }
                int4 sa;
                if (isBigEndian)
                    sa = fullsize - (dataTypePieces[i].offset + dt->getSize());
                else
                    sa = dataTypePieces[i].offset;
                uintb val;
                if (sa >= losize)
                    val = hi >> (sa - losize);
                else
                {
                    val = lo >> sa * 8;
                    if (sa + dt->getSize() > losize)
                        val |= hi << (losize - sa) * 8;
                }
                val &= calc_mask(dt->getSize());
                Varnode* outVn = data.newConstant(dt->getSize(), val);
                inVarnodes.push_back(outVn);
                outVn->updateType(dt, false, false);
            }
            data.opDestroy(op);
            return true;
        }

        /// \brief Assuming the input is a constant, build split constants
        ///
        /// Build constant input Varnodes, extracting the constant value from the given root constant
        /// based on the input offsets in \b dataTypePieces.
        /// \param rootVn is the given root constant
        /// \param inVarnodes is the container for the new Varnodes
        private void buildInConstants(Varnode rootVn, List<Varnode> inVarnodes)
        {
            uintb baseVal = rootVn->getOffset();
            bool bigEndian = rootVn->getSpace()->isBigEndian();
            for (int4 i = 0; i < dataTypePieces.size(); ++i)
            {
                Datatype* dt = dataTypePieces[i].inType;
                int4 off = dataTypePieces[i].offset;
                if (bigEndian)
                    off = rootVn->getSize() - off - dt->getSize();
                uintb val = (baseVal >> (8 * off)) & calc_mask(dt->getSize());
                Varnode* outVn = data.newConstant(dt->getSize(), val);
                inVarnodes.push_back(outVn);
                outVn->updateType(dt, false, false);
            }
        }

        /// \brief Build input Varnodes by extracting SUBPIECEs from the root
        ///
        /// Extract different pieces from the given root based on the offsets and
        /// input data-types in \b dataTypePieces.
        /// \param rootVn is the given root Varnode
        /// \param followOp is the point at which the SUBPIECEs should be inserted (before)
        /// \param inVarnodes is the container for the new Varnodes
        private void buildInSubpieces(Varnode rootVn, PcodeOp followOp, List<Varnode> inVarnodes)
        {
            if (generateConstants(rootVn, inVarnodes))
                return;
            Address baseAddr = rootVn->getAddr();
            for (int4 i = 0; i < dataTypePieces.size(); ++i)
            {
                Datatype* dt = dataTypePieces[i].inType;
                int4 off = dataTypePieces[i].offset;
                Address addr = baseAddr + off;
                addr.renormalize(dt->getSize());
                if (addr.isBigEndian())
                    off = rootVn->getSize() - off - dt->getSize();
                PcodeOp* subpiece = data.newOp(2, followOp->getAddr());
                data.opSetOpcode(subpiece, CPUI_SUBPIECE);
                data.opSetInput(subpiece, rootVn, 0);
                data.opSetInput(subpiece, data.newConstant(4, off), 1);
                Varnode* outVn = data.newVarnodeOut(dt->getSize(), addr, subpiece);
                inVarnodes.push_back(outVn);
                outVn->updateType(dt, false, false);
                data.opInsertBefore(subpiece, followOp);
            }
        }

        /// \brief Build output Varnodes with storage based on the given root
        ///
        /// Extract different pieces from the given root based on the offsets and
        /// output data-types in \b dataTypePieces.
        /// \param rootVn is the given root Varnode
        /// \param inVarnodes is the container for the new Varnodes
        private void buildOutVarnodes(Varnode rootVn, List<Varnode> outVarnodes)
        {
            Address baseAddr = rootVn->getAddr();
            for (int4 i = 0; i < dataTypePieces.size(); ++i)
            {
                Datatype* dt = dataTypePieces[i].outType;
                int4 off = dataTypePieces[i].offset;
                Address addr = baseAddr + off;
                addr.renormalize(dt->getSize());
                Varnode* outVn = data.newVarnode(dt->getSize(), addr, dt);
                outVarnodes.push_back(outVn);
            }
        }

        /// \brief Concatenate output Varnodes into given root Varnode
        ///
        /// Insert PIECE operators concatenating all output Varnodes from most significant to least significant
        /// producing the root Varnode as the final result.
        /// \param rootVn is the given root Varnode
        /// \param previousOp is the point at which to insert (after)
        /// \param outVarnodes is the list of output Varnodes
        private void buildOutConcats(Varnode rootVn, PcodeOp previousOp, List<Varnode> outVarnodes)
        {
            if (rootVn->hasNoDescend())
                return;             // Don't need to produce concatenation if its unused
            Address baseAddr = rootVn->getAddr();
            Varnode* vn;
            PcodeOp* concatOp;
            PcodeOp* preOp = previousOp;
            bool addressTied = rootVn->isAddrTied();
            // We are creating a CONCAT stack, mark varnodes appropriately
            for (int4 i = 0; i < outVarnodes.size(); ++i)
            {
                if (!addressTied)
                    outVarnodes[i]->setProtoPartial();
            }
            if (baseAddr.isBigEndian())
            {
                vn = outVarnodes[0];
                for (int4 i = 1; ; ++i)
                {               // Traverse most to least significant
                    concatOp = data.newOp(2, previousOp->getAddr());
                    data.opSetOpcode(concatOp, CPUI_PIECE);
                    data.opSetInput(concatOp, vn, 0);           // Most significant
                    data.opSetInput(concatOp, outVarnodes[i], 1);   // Least significant
                    data.opInsertAfter(concatOp, preOp);
                    if (i + 1 >= outVarnodes.size()) break;
                    preOp = concatOp;
                    int4 sz = vn->getSize() + outVarnodes[i]->getSize();
                    Address addr = baseAddr;
                    addr.renormalize(sz);
                    vn = data.newVarnodeOut(sz, addr, concatOp);
                    if (!addressTied)
                        vn->setProtoPartial();
                }
            }
            else
            {
                vn = outVarnodes[outVarnodes.size() - 1];
                for (int4 i = outVarnodes.size() - 2; ; --i)
                {       // Traverse most to least significant
                    concatOp = data.newOp(2, previousOp->getAddr());
                    data.opSetOpcode(concatOp, CPUI_PIECE);
                    data.opSetInput(concatOp, vn, 0);           // Most significant
                    data.opSetInput(concatOp, outVarnodes[i], 1);   // Least significant
                    data.opInsertAfter(concatOp, preOp);
                    if (i <= 0) break;
                    preOp = concatOp;
                    int4 sz = vn->getSize() + outVarnodes[i]->getSize();
                    Address addr = outVarnodes[i]->getAddr();
                    addr.renormalize(sz);
                    vn = data.newVarnodeOut(sz, addr, concatOp);
                    if (!addressTied)
                        vn->setProtoPartial();
                }
            }
            concatOp->setPartialRoot();
            data.opSetOutput(concatOp, rootVn);
            if (!addressTied)
                data.getMerge().registerProtoPartialRoot(rootVn);
        }

        /// \brief Build a a series of PTRSUB ops at different offsets, given a root pointer
        ///
        /// Offsets and data-types are based on \b dataTypePieces, taking input data-types if \b isInput is \b true,
        /// output data-types otherwise.  The data-types, relative to the root pointer, are assumed to start at
        /// the given base offset.
        /// \param rootVn is the root pointer
        /// \param ptrType is the pointer data-type associated with the root
        /// \param baseOffset is the given base offset
        /// \param followOp is the point at which the new PTRSUB ops are inserted (before)
        /// \param ptrVarnodes is the container for the new pointer Varnodes
        /// \param isInput specifies either input (\b true) or output (\b false) data-types
        private void buildPointers(Varnode rootVn, TypePointer ptrType, int4 baseOffset, PcodeOp followOp,
                   List<Varnode> ptrVarnodes, bool isInput)
        {
            Datatype* baseType = ptrType->getPtrTo();
            for (int4 i = 0; i < dataTypePieces.size(); ++i)
            {
                Datatype* matchType = isInput ? dataTypePieces[i].inType : dataTypePieces[i].outType;
                int4 byteOffset = baseOffset + dataTypePieces[i].offset;
                Datatype* tmpType = baseType;
                uintb curOff = byteOffset;
                Varnode* inPtr = rootVn;
                do
                {
                    uintb newOff;
                    PcodeOp* newOp;
                    Datatype* newType;
                    if (curOff >= tmpType->getSize())
                    {   // An offset bigger than current data-type indicates an array
                        newType = tmpType;          // The new data-type will be the same as current data-type
                        intb sNewOff = (intb)curOff % tmpType->getSize();   // But new offset will be old offset modulo data-type size
                        newOff = (sNewOff < 0) ? (sNewOff + tmpType->getSize()) : sNewOff;

                    }
                    else
                    {
                        newType = tmpType->getSubType(curOff, &newOff);
                        if (newType == (Datatype*)0)
                        {
                            // Null should only be returned for a hole in a structure, in which case use precomputed data-type
                            newType = matchType;
                            newOff = 0;
                        }
                    }
                    if (tmpType == newType || tmpType->getMetatype() == TYPE_ARRAY)
                    {
                        int4 finalOffset = (int4)curOff - (int4)newOff;
                        int4 sz = newType->getSize();       // Element size in bytes
                        finalOffset = finalOffset / sz;     // Number of elements
                        sz = AddrSpace::byteToAddressInt(sz, ptrType->getWordSize());
                        newOp = data.newOp(3, followOp->getAddr());
                        data.opSetOpcode(newOp, CPUI_PTRADD);
                        data.opSetInput(newOp, inPtr, 0);
                        Varnode* indexVn = data.newConstant(inPtr->getSize(), finalOffset);
                        data.opSetInput(newOp, indexVn, 1);
                        data.opSetInput(newOp, data.newConstant(inPtr->getSize(), sz), 2);
                        Datatype* indexType = types->getBase(indexVn->getSize(), TYPE_INT);
                        indexVn->updateType(indexType, false, false);
                    }
                    else
                    {
                        int4 finalOffset = AddrSpace::byteToAddressInt((int4)curOff - (int4)newOff, ptrType->getWordSize());
                        newOp = data.newOp(2, followOp->getAddr());
                        data.opSetOpcode(newOp, CPUI_PTRSUB);
                        data.opSetInput(newOp, inPtr, 0);
                        data.opSetInput(newOp, data.newConstant(inPtr->getSize(), finalOffset), 1);
                    }
                    inPtr = data.newUniqueOut(inPtr->getSize(), newOp);
                    Datatype* tmpPtr = types->getTypePointerStripArray(ptrType->getSize(), newType, ptrType->getWordSize());
                    inPtr->updateType(tmpPtr, false, false);
                    data.opInsertBefore(newOp, followOp);
                    tmpType = newType;
                    curOff = newOff;
                } while (tmpType->getSize() > matchType->getSize());
                ptrVarnodes.push_back(inPtr);
            }
        }

        /// Is \b this the input to an arithmetic operation
        /// Iterate through descendants of the given Varnode, looking for arithmetic ops.
        /// \param vn is the given Varnode
        /// \return \b true if the Varnode has an arithmetic op as a descendant
        private static bool isArithmeticInput(Varnode vn)
        {
            list<PcodeOp*>::const_iterator iter = vn->beginDescend();
            while (iter != vn->endDescend())
            {
                PcodeOp* op = *iter;
                if (op->getOpcode()->isArithmeticOp())
                    return true;
                ++iter;
            }
            return false;
        }

        /// Is \b this defined by an arithmetic operation
        /// Check if the defining PcodeOp is arithmetic.
        /// \param vn is the given Varnode
        /// \return \b true if the defining op is arithemetic
        private static bool isArithmeticOutput(Varnode vn)
        {
            if (!vn->isWritten())
                return false;
            return vn->getDef()->getOpcode()->isArithmeticOp();
        }

        public SplitDatatype(Funcdata func)
        {
            data = new Funcdata(func);
            Architecture glb = func.getArch();
            types = glb->types;
            splitStructures = (glb->split_datatype_config & OptionSplitDatatypes::option_struct) != 0;
            splitArrays = (glb->split_datatype_config & OptionSplitDatatypes::option_array) != 0;
        }

        /// Split a COPY operation
        /// Based on the input and output data-types, determine if and how the given COPY operation
        /// should be split into pieces. Then if possible, perform the split.
        /// \param copyOp is the given COPY
        /// \param inType is the data-type of the COPY input
        /// \param outType is the data-type of the COPY output
        /// \return \b true if the split was performed
        public bool splitCopy(PcodeOp copyOp, Datatype inType, Datatype outType)
        {
            if (!testCopyConstraints(copyOp))
                return false;
            Varnode* inVn = copyOp->getIn(0);
            if (!testDatatypeCompatibility(inType, outType, inVn->isConstant()))
                return false;
            if (isArithmeticOutput(inVn))       // Sanity check on input
                return false;
            Varnode* outVn = copyOp->getOut();
            if (isArithmeticInput(outVn))   // Sanity check on output
                return false;
            vector<Varnode*> inVarnodes;
            vector<Varnode*> outVarnodes;
            if (inVn->isConstant())
                buildInConstants(inVn, inVarnodes);
            else
                buildInSubpieces(inVn, copyOp, inVarnodes);
            buildOutVarnodes(outVn, outVarnodes);
            buildOutConcats(outVn, copyOp, outVarnodes);
            for (int4 i = 0; i < inVarnodes.size(); ++i)
            {
                PcodeOp* newCopyOp = data.newOp(1, copyOp->getAddr());
                data.opSetOpcode(newCopyOp, CPUI_COPY);
                data.opSetInput(newCopyOp, inVarnodes[i], 0);
                data.opSetOutput(newCopyOp, outVarnodes[i]);
                data.opInsertBefore(newCopyOp, copyOp);
            }
            data.opDestroy(copyOp);
            return true;
        }

        /// Split a LOAD operation
        /// Based on the LOAD data-type, determine if the given LOAD can be split into smaller LOADs.
        /// Then, if possible, perform the split.  The input data-type describes the size and composition of
        /// the value being loaded. Check for the special case where, the LOAD output is a lone input to a COPY,
        /// and split the outputs of the COPY as well.
        /// \param loadOp is the given LOAD to split
        /// \param inType is the data-type associated with the value being loaded
        /// \return \b true if the split was performed
        public bool splitLoad(PcodeOp loadOp, Datatype inType)
        {
            Varnode* outVn = loadOp->getOut();
            PcodeOp* copyOp = (PcodeOp*)0;
            if (!outVn->isAddrTied())
                copyOp = outVn->loneDescend();
            if (copyOp != (PcodeOp*)0)
            {
                OpCode opc = copyOp->code();
                if (opc == CPUI_STORE) return false;    // Handled by RuleSplitStore
                if (opc != CPUI_COPY)
                    copyOp = (PcodeOp*)0;
            }
            if (copyOp != (PcodeOp*)0)
                outVn = copyOp->getOut();
            Datatype* outType = outVn->getTypeDefFacing();
            if (!testDatatypeCompatibility(inType, outType, false))
                return false;
            if (isArithmeticInput(outVn))           // Sanity check on output
                return false;
            RootPointer root;
            if (!root.find(loadOp, inType))
                return false;
            vector<Varnode*> ptrVarnodes;
            vector<Varnode*> outVarnodes;
            PcodeOp* insertPoint = (copyOp == (PcodeOp*)0) ? loadOp : copyOp;
            buildPointers(root.pointer, root.ptrType, root.baseOffset, loadOp, ptrVarnodes, true);
            buildOutVarnodes(outVn, outVarnodes);
            buildOutConcats(outVn, insertPoint, outVarnodes);
            AddrSpace* spc = loadOp->getIn(0)->getSpaceFromConst();
            for (int4 i = 0; i < ptrVarnodes.size(); ++i)
            {
                PcodeOp* newLoadOp = data.newOp(2, insertPoint->getAddr());
                data.opSetOpcode(newLoadOp, CPUI_LOAD);
                data.opSetInput(newLoadOp, data.newVarnodeSpace(spc), 0);
                data.opSetInput(newLoadOp, ptrVarnodes[i], 1);
                data.opSetOutput(newLoadOp, outVarnodes[i]);
                data.opInsertBefore(newLoadOp, insertPoint);
            }
            if (copyOp != (PcodeOp*)0)
                data.opDestroy(copyOp);
            data.opDestroy(loadOp);
            root.freePointerChain(data);
            return true;
        }

        /// Split a STORE operation
        /// Based on the STORE data-type, determine if the given STORE can be split into smaller STOREs.
        /// Then, if possible, perform the split.  The output data-type describes the size and composition of
        /// the value being stored.
        /// \param storeOp is the given STORE to split
        /// \param outType is the data-type associated with the value being stored
        /// \return \b true if the split was performed
        public bool splitStore(PcodeOp storeOp, Datatype outType)
        {
            Varnode* inVn = storeOp->getIn(2);
            PcodeOp* loadOp = (PcodeOp*)0;
            Datatype* inType = (Datatype*)0;
            if (inVn->isWritten() && inVn->getDef()->code() == CPUI_LOAD && inVn->loneDescend() == storeOp)
            {
                loadOp = inVn->getDef();
                inType = getValueDatatype(loadOp, inVn->getSize(), data.getArch()->types);
                if (inType == (Datatype*)0)
                    loadOp = (PcodeOp*)0;
            }
            if (inType == (Datatype*)0)
            {
                inType = inVn->getTypeReadFacing(storeOp);
            }
            if (!testDatatypeCompatibility(inType, outType, inVn->isConstant()))
            {
                if (loadOp != (PcodeOp*)0)
                {
                    // If not compatible while considering the LOAD, check again, but without the LOAD
                    loadOp = (PcodeOp*)0;
                    inType = inVn->getTypeReadFacing(storeOp);
                    dataTypePieces.clear();
                    if (!testDatatypeCompatibility(inType, outType, inVn->isConstant()))
                        return false;
                }
                else
                    return false;
            }

            if (isArithmeticOutput(inVn))       // Sanity check
                return false;

            RootPointer storeRoot;
            if (!storeRoot.find(storeOp, outType))
                return false;

            RootPointer loadRoot;
            if (loadOp != (PcodeOp*)0)
            {
                if (!loadRoot.find(loadOp, inType))
                    return false;
            }

            vector<Varnode*> inVarnodes;
            if (inVn->isConstant())
                buildInConstants(inVn, inVarnodes);
            else if (loadOp != (PcodeOp*)0)
            {
                vector<Varnode*> loadPtrs;
                buildPointers(loadRoot.pointer, loadRoot.ptrType, loadRoot.baseOffset, loadOp, loadPtrs, true);
                AddrSpace* loadSpace = loadOp->getIn(0)->getSpaceFromConst();
                for (int4 i = 0; i < loadPtrs.size(); ++i)
                {
                    PcodeOp* newLoadOp = data.newOp(2, loadOp->getAddr());
                    data.opSetOpcode(newLoadOp, CPUI_LOAD);
                    data.opSetInput(newLoadOp, data.newVarnodeSpace(loadSpace), 0);
                    data.opSetInput(newLoadOp, loadPtrs[i], 1);
                    Datatype* dt = dataTypePieces[i].inType;
                    Varnode* vn = data.newUniqueOut(dt->getSize(), newLoadOp);
                    vn->updateType(dt, false, false);
                    inVarnodes.push_back(vn);
                    data.opInsertBefore(newLoadOp, loadOp);
                }
            }
            else
                buildInSubpieces(inVn, storeOp, inVarnodes);

            vector<Varnode*> storePtrs;
            buildPointers(storeRoot.pointer, storeRoot.ptrType, storeRoot.baseOffset, storeOp, storePtrs, false);
            AddrSpace* storeSpace = storeOp->getIn(0)->getSpaceFromConst();
            // Preserve original STORE object, so that INDIRECT references are still valid
            // but convert it into the first of the smaller STOREs
            data.opSetInput(storeOp, storePtrs[0], 1);
            data.opSetInput(storeOp, inVarnodes[0], 2);
            PcodeOp* lastStore = storeOp;
            for (int4 i = 1; i < storePtrs.size(); ++i)
            {
                PcodeOp* newStoreOp = data.newOp(3, storeOp->getAddr());
                data.opSetOpcode(newStoreOp, CPUI_STORE);
                data.opSetInput(newStoreOp, data.newVarnodeSpace(storeSpace), 0);
                data.opSetInput(newStoreOp, storePtrs[i], 1);
                data.opSetInput(newStoreOp, inVarnodes[i], 2);
                data.opInsertAfter(newStoreOp, lastStore);
                lastStore = newStoreOp;
            }

            if (loadOp != (PcodeOp*)0)
            {
                data.opDestroy(loadOp);
                loadRoot.freePointerChain(data);
            }
            storeRoot.freePointerChain(data);
            return true;
        }

        /// \brief Get a data-type description of the value being pointed at by the given LOAD or STORE
        ///
        /// Take the data-type of the pointer and construct the data-type of the thing being pointed at
        /// so that it matches a specific size.  This takes into account TypePointerRel and can produce
        /// TypePartialStruct in order to match the size.  If no interpretation of the value as a
        /// splittable data-type is possible, null is returned.
        /// \param loadStore is the given LOAD or STORE
        /// \param size is the number of bytes in the value being pointed at
        /// \param tlst is the TypeFactory for constructing partial data-types if necessary
        /// \return the data-type description of the value or null
        public static Datatype getValueDatatype(PcodeOp loadStore, int4 size, TypeFactory tlst)
        {
            Datatype* resType;
            Datatype* ptrType = loadStore->getIn(1)->getTypeReadFacing(loadStore);
            if (ptrType->getMetatype() != TYPE_PTR)
                return (Datatype*)0;
            int4 baseOffset;
            if (ptrType->isPointerRel())
            {
                TypePointerRel* ptrRel = (TypePointerRel*)ptrType;
                resType = ptrRel->getParent();
                baseOffset = ptrRel->getPointerOffset();
                baseOffset = AddrSpace::addressToByteInt(baseOffset, ptrRel->getWordSize());
            }
            else
            {
                resType = ((TypePointer*)ptrType)->getPtrTo();
                baseOffset = 0;
            }
            type_metatype metain = resType->getMetatype();
            if (metain != TYPE_STRUCT && metain == TYPE_ARRAY)
                return (Datatype*)0;
            return tlst->getExactPiece(resType, baseOffset, size);
        }
    }
}
