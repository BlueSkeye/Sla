﻿using ghidra;
using Sla.DECCORE;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sla.DECCORE
{
    /// \brief The base datatype class for the decompiler.
    ///
    /// Used for symbols, function prototypes, type propagation etc.
    internal abstract class Datatype
    {
        protected static sub_metatype[] base2sub = new sub_metatype[15];

        /// Boolean properties of datatypes
        [Flags()]
        protected enum Properties
        {
            /// This is a basic type which will never be redefined
            coretype = 1,
            /// ASCII character data
            chartype = 2,
            /// An enumeration type (as well as an integer)
            enumtype = 4,
            /// An enumeration type where all values are of 2^^n form
            poweroftwo = 8,
            /// 16-bit wide chars in unicode UTF16
            utf16 = 16,
            /// 32-bit wide chars in unicode UTF32
            utf32 = 32,
            /// Structure that should be treated as a string
            opaque_string = 64,
            /// May be other structures with same name different lengths
            variable_length = 128,
            /// Datatype has a stripped form for formal declarations
            has_stripped = 0x100,
            /// Datatype is a TypePointerRel
            is_ptrrel = 0x200,
            /// Set if \b this (recursive) data-type has not been fully defined yet
            type_incomplete = 0x400,
            /// Datatype (union, pointer to union) needs resolution before propagation
            needs_resolution = 0x800,
            /// 3-bits encoding display format, 0=none, 1=hex, 2=dec, 3=oct, 4=bin, 5=char
            force_format = 0x7000,
        }

        // friend class TypeFactory;
        // friend struct DatatypeCompare;
        /// A unique id for the type (or 0 if an id is not assigned)
        private uint8 id;
        /// Size (of variable holding a value of this type)
        private int4 size;
        /// Boolean properties of the type
        private uint4 flags;
        /// Name of type
        private string name;
        /// Name to display in output
        private string displayName;
        /// Meta-type - type disregarding size
        private type_metatype metatype;
        /// Sub-type of of the meta-type, for comparisons
        private sub_metatype submeta;
        /// The immediate data-type being typedefed by \e this
        private Datatype typedefImm;

        /// Recover basic data-type properties
        /// Restore the basic properties (name,size,id) of a data-type from an XML element
        /// Properties are read from the attributes of the element
        /// \param decoder is the stream decoder
        private void decodeBasic(Decoder decoder)
        {
            size = -1;
            metatype = TYPE_VOID;
            id = 0;
            for (; ; )
            {
                uint4 attrib = decoder.getNextAttributeId();
                if (attrib == 0) break;
                if (attrib == ATTRIB_NAME)
                {
                    name = decoder.readString();
                }
                else if (attrib == ATTRIB_SIZE)
                {
                    size = decoder.readSignedInteger();
                }
                else if (attrib == ATTRIB_METATYPE)
                {
                    metatype = string2metatype(decoder.readString());
                }
                else if (attrib == ATTRIB_CORE)
                {
                    if (decoder.readBool())
                        flags |= coretype;
                }
                else if (attrib == ATTRIB_ID)
                {
                    id = decoder.readUnsignedInteger();
                }
                else if (attrib == ATTRIB_VARLENGTH)
                {
                    if (decoder.readBool())
                        flags |= variable_length;
                }
                else if (attrib == ATTRIB_OPAQUESTRING)
                {
                    if (decoder.readBool())
                        flags |= opaque_string;
                }
                else if (attrib == ATTRIB_FORMAT)
                {
                    uint4 val = encodeIntegerFormat(decoder.readString());
                    setDisplayFormat(val);
                }
                else if (attrib == ATTRIB_LABEL)
                {
                    displayName = decoder.readString();
                }
            }
            if (size < 0)
                throw new LowlevelError("Bad size for type " + name);
            submeta = base2sub[metatype];
            if ((id == 0) && (name.size() > 0)) // If there is a type name
                id = hashName(name);    // There must be some kind of id
            if (isVariableLength())
            {
                // Id needs to be unique compared to another data-type with the same name
                id = hashSize(id, size);
            }
            if (displayName.empty())
                displayName = name;
        }

        /// Encode basic data-type properties
        /// Encode basic data-type properties (name,size,id) as attributes.
        /// This routine presumes the initial element is already written to the stream.
        /// \param meta is the metatype attribute
        /// \param encoder is the stream encoder
        private void encodeBasic(type_metatype meta, Encoder encoder)
        {
            encoder.writeString(ATTRIB_NAME, name);
            uint8 saveId;
            if (isVariableLength())
                saveId = hashSize(id, size);
            else
                saveId = id;
            if (saveId != 0)
            {
                encoder.writeUnsignedInteger(ATTRIB_ID, saveId);
            }
            encoder.writeSignedInteger(ATTRIB_SIZE, size);
            string metastring;
            metatype2string(meta, metastring);
            encoder.writeString(ATTRIB_METATYPE, metastring);
            if ((flags & coretype) != 0)
                encoder.writeBool(ATTRIB_CORE, true);
            if (isVariableLength())
                encoder.writeBool(ATTRIB_VARLENGTH, true);
            if ((flags & opaque_string) != 0)
                encoder.writeBool(ATTRIB_OPAQUESTRING, true);
            uint4 format = getDisplayFormat();
            if (format != 0)
                encoder.writeString(ATTRIB_FORMAT, decodeIntegerFormat(format));
        }

        /// Encode \b this as a \e typedef element to a stream
        /// Called only if the \b typedefImm field is non-null.  Encode the data-type to the
        /// stream as a simple \<typedef> element including only the names and ids of \b this and
        /// the data-type it typedefs.
        /// \param encoder is the stream encoder
        private void encodeTypedef(Encoder encoder)
        {
            encoder.openElement(ELEM_DEF);
            encoder.writeString(ATTRIB_NAME, name);
            encoder.writeUnsignedInteger(ATTRIB_ID, id);
            uint4 format = getDisplayFormat();
            if (format != 0)
                encoder.writeString(ATTRIB_FORMAT, Datatype::decodeIntegerFormat(format));
            typedefImm->encodeRef(encoder);
            encoder.closeElement(ELEM_DEF);
        }

        /// Mark \b this data-type as completely defined
        private void markComplete()
        {
            flags &= ~(uint4)type_incomplete;
        }

        /// Set a specific display format
        /// The display format for the data-type is changed based on the given format.  A value of
        /// zero clears any preexisting format.  Otherwise the value can be one of:
        /// 1=\b hex, 2=\b dec, 3=\b oct, 4=\b bin, 5=\b char
        /// \param format is the given format
        internal void setDisplayFormat(uint4 format)
        {
            flags &= ~(uint4)force_format;  // Clear preexisting
            flags |= (format << 12);
        }

        /// Clone the data-type
        internal abstract Datatype clone();

        /// Produce a data-type id by hashing the type name
        /// If a type id is explicitly provided for a data-type, this routine is used
        /// to produce an id based on a hash of the name.  IDs produced this way will
        /// have their sign-bit set to distinguish it from other IDs.
        /// \param nm is the type name to be hashed
        private static uint8 hashName(string nm)
        {
            uint8 res = 123;
            for (uint4 i = 0; i < nm.size(); ++i)
            {
                res = (res << 8) | (res >> 56);
                res += (uint8)nm[i];
                if ((res & 1) == 0)
                    res ^= 0xfeabfeab;  // Some kind of feedback
            }
            uint8 tmp = 1;
            tmp <<= 63;
            res |= tmp; // Make sure the hash is negative (to distinguish it from database id's)
            return res;
        }

        /// Reversibly hash size into id
        /// This allows IDs for variable length structures to be uniquefied based on size.
        /// A base ID is given and a size of the specific instance. A unique ID is returned.
        /// The hashing is reversible by feeding the output ID back into this function with the same size.
        /// \param id is the given ID to (de)uniquify
        /// \param size is the instance size of the structure
        /// \return the (de)uniquified id
        private static uint8 hashSize(uint8 id, int4 size)
        {
            uint8 sizeHash = size;
            sizeHash *= 0x98251033aecbabaf; // Hash the size
            id ^= sizeHash;
            return id;
        }

        /// Construct the base data-type copying low-level properties of another
        public Datatype(Datatype op)
        {
            size = op.size;
            name = op.name;
            displayName = op.displayName;
            metatype = op.metatype;
            submeta = op.submeta;
            flags = op.flags;
            id = op.id;
            typedefImm = op.typedefImm;
        }

        /// Construct the base data-type providing size and meta-type
        public Datatype(int4 s, type_metatype m)
        {
            size = s;
            metatype = m;
            submeta = base2sub[m];
            flags = 0;
            id = 0;
            typedefImm = (Datatype*)0;
        }
    
        ~Datatype()
        {
        }

        /// Is this a core data-type
        public bool isCoreType() => ((flags&coretype)!= 0);

        /// Does this print as a 'char'
        public bool isCharPrint() => ((flags&(chartype|utf16|utf32|opaque_string))!= 0);

        /// Is this an enumerated type
        public bool isEnumType() => ((flags&enumtype)!= 0);

        /// Is this a flag-based enumeration
        public bool isPowerOfTwo() => ((flags&poweroftwo)!= 0);

        /// Does this print as an ASCII 'char'
        public bool isASCII() => ((flags&chartype)!= 0);

        /// Does this print as UTF16 'wchar'
        public bool isUTF16() => ((flags&utf16)!= 0);

        /// Does this print as UTF32 'wchar'
        public bool isUTF32() => ((flags&utf32)!= 0);

        ///< Is \b this a variable length structure
        public bool isVariableLength() => ((flags&variable_length)!= 0);

        /// Are these the same variable length data-type
        /// If \b this and the other given data-type are both variable length and come from the
        /// the same base data-type, return \b true.
        /// \param ct is the other given data-type to compare with \b this
        /// \return \b true if they are the same variable length data-type.
        public bool hasSameVariableBase(Datatype ct)
        {
            if (!isVariableLength()) return false;
            if (!ct->isVariableLength()) return false;
            uint8 thisId = hashSize(id, size);
            uint8 themId = hashSize(ct->id, ct->size);
            return (thisId == themId);
        }

        /// Is \b this an opaquely encoded string
        public bool isOpaqueString() => ((flags&opaque_string)!= 0);

        /// Is \b this a TypePointerRel
        public bool isPointerRel() => ((flags & is_ptrrel)!= 0);

        /// Is \b this a non-ephemeral TypePointerRel
        public bool isFormalPointerRel() => (flags & (is_ptrrel | has_stripped))== is_ptrrel;

        /// Return \b true if \b this has a stripped form
        public bool hasStripped() => (flags & has_stripped)!= 0;

        /// Is \b this an incompletely defined data-type
        public bool isIncomplete() => (flags & type_incomplete)!= 0;

        /// Is \b this a union or a pointer to union
        public bool needsResolution() => (flags & needs_resolution)!= 0;

        /// Get properties pointers inherit
        public uint4 getInheritable() => (flags & coretype);

        /// Get the display format for constants with \b this data-type
        /// A non-zero result indicates the type of formatting that is forced on the constant.
        /// One of the following values is returned.
        ///   - 1 for hexadecimal
        ///   - 2 for decimal
        ///   - 3 for octal
        ///   - 4 for binary
        ///   - 5 for char
        ///
        public uint4 getDisplayFormat() => (flags & force_format) >> 12;

        /// Get the type \b meta-type
        public type_metatype getMetatype() => metatype;

        /// Get the \b sub-metatype
        public sub_metatype getSubMeta() => submeta;

        /// Get the type id
        public uint8 getId() => id;

        /// Get the type size
        public int4 getSize() => size;

        /// Get the type name
        public string getName() => name;

        /// Get string to use in display
        public string getDisplayName() => displayName;

        /// Get the data-type immediately typedefed by \e this (or null)
        public Datatype getTypedef() => typedefImm;

        /// Print a description of the type to stream
        /// Print a raw description of the type to stream. Intended for debugging.
        /// Not intended to produce parsable C.
        /// \param s is the output stream
        public virtual void printRaw(TextWriter s)
        {
            if (name.size() > 0)
                s << name;
            else
                s << "unkbyte" << dec << size;
        }

        /// \brief Find an immediate subfield of \b this data-type
        ///
        /// Given a byte range within \b this data-type, determine the field it is contained in
        /// and pass back the renormalized offset. This method applies to TYPE_STRUCT, TYPE_UNION, and
        /// TYPE_PARTIALUNION, data-types that have field components. For TYPE_UNION and TYPE_PARTIALUNION, the
        /// field may depend on the p-code op extracting or writing the value.
        /// \param off is the byte offset into \b this
        /// \param sz is the size of the byte range
        /// \param op is the PcodeOp reading/writing the data-type
        /// \param slot is the index of the Varnode being accessed, -1 for the output, >=0 for an input
        /// \param newoff points to the renormalized offset to pass back
        /// \return the containing field or NULL if the range is not contained
        public virtual TypeField findTruncation(int4 off, int4 sz, PcodeOp op, int4 slot, int4 newoff)
        {
            return (const TypeField*)0;
        }

        /// Recover component data-type one-level down
        /// Given an offset into \b this data-type, return the component data-type at that offset.
        /// Also, pass back a "renormalized" offset suitable for recursize getSubType() calls:
        /// i.e. if the original offset hits the exact start of the sub-type, 0 is passed back.
        /// If there is no valid component data-type at the offset,
        /// return NULL and pass back the original offset
        /// \param off is the offset into \b this data-type
        /// \param newoff is a pointer to the passed-back offset
        /// \return a pointer to the component data-type or NULL
        public virtual Datatype getSubType(uintb off, uintb newoff)
        {               // There is no subtype
            *newoff = off;
            return (Datatype*)0;
        }

        /// Find the first component data-type after the given offset that is (or contains)
        /// an array, and pass back the difference between the component's start and the given offset.
        /// Return the component data-type or null if no array is found.
        /// \param off is the given offset into \b this data-type
        /// \param newoff is used to pass back the offset difference
        /// \param elSize is used to pass back the array element size
        /// \return the component data-type or null
        public virtual Datatype nearestArrayedComponentForward(uintb off, uintb newoff, int4 elSize)
        {
            return (TypeArray*)0;
        }

        /// Find the first component data-type before the given offset that is (or contains)
        /// an array, and pass back the difference between the component's start and the given offset.
        /// Return the component data-type or null if no array is found.
        /// \param off is the given offset into \b this data-type
        /// \param newoff is used to pass back the offset difference
        /// \param elSize is used to pass back the array element size
        /// \return the component data-type or null
        public virtual Datatype nearestArrayedComponentBackward(uintb off, uintb newoff, int4 elSize)
        {
            return (TypeArray*)0;
        }

        /// Get number of bytes at the given offset that are padding
        public virtual int4 getHoleSize(int4 off) => 0;

        /// Return number of component sub-types
        public virtual int4 numDepend() => 0;

        /// Return the i-th component sub-type
        public virtual Datatype getDepend(int4 index) => (Datatype *)0;

        /// Print name as short prefix
        public virtual void printNameBase(TextWriter s) 
        {
            if (!name.empty()) s << name[0];
        }

        /// Order types for propagation
        /// Order \b this with another data-type, in a way suitable for the type propagation algorithm.
        /// Bigger types come earlier. More specific types come earlier.
        /// \param op is the data-type to compare with \b this
        /// \param level is maximum level to descend when recursively comparing
        /// \return negative, 0, positive depending on ordering of types
        public virtual int4 compare(Datatype op, int4 level)
        {
            if (size != op.size) return (op.size - size);
            if (submeta != op.submeta) return (submeta < op.submeta) ? -1 : 1;
            return 0;
        }

        /// Compare for storage in tree structure
        /// Sort data-types for the main TypeFactory container.  The sort needs to be based on
        /// the data-type structure so that an example data-type, constructed outside the factory,
        /// can be used to find the equivalent object inside the factory.  This means the
        /// comparison should not examine the data-type id. In practice, the comparison only needs
        /// to go down one level in the component structure before just comparing component pointers.
        /// \param op is the data-type to compare with \b this
        /// \return negative, 0, positive depending on ordering of types
        public virtual int4 compareDependency(Datatype op)
        {
            if (submeta != op.submeta) return (submeta < op.submeta) ? -1 : 1;
            if (size != op.size) return (op.size - size);
            return 0;
        }

        /// Encode the data-type to a stream
        /// Encode a formal description of the data-type as a \<type> element.
        /// For composite data-types, the description goes down one level, describing
        /// the component types only by reference.
        /// \param encoder is the stream encoder
        public virtual void encode(Encoder encoder)
        {
            encoder.openElement(ELEM_TYPE);
            encodeBasic(metatype, encoder);
            encoder.closeElement(ELEM_TYPE);
        }

        /// Is this data-type suitable as input to a CPUI_PTRSUB op
        /// A CPUI_PTRSUB must act on a pointer data-type where the given offset addresses a component.
        /// Perform this check.
        /// \param off is the given offset
        /// \return \b true if \b this is a suitable PTRSUB data-type
        public virtual bool isPtrsubMatching(uintb off) => false;

        /// Get a stripped version of \b this for formal use in formal declarations
        /// Some data-types are ephemeral, and, in the final decompiler output, get replaced with a formal version
        /// that is a stripped down version of the original.  This method returns this stripped down version, if it
        /// exists, or null otherwise.  A non-null return should correspond with hasStripped returning \b true.
        /// \return the stripped version or null
        public virtual Datatype? getStripped() => null;

        /// Tailor data-type propagation based on Varnode use
        /// For certain data-types, particularly \e union, variables of that data-type are transformed into a subtype
        /// depending on the particular use.  Each read or write of the variable may use a different subtype.
        /// This method returns the particular subtype required based on a specific PcodeOp. A slot index >=0
        /// indicates which operand \e reads the variable, or if the index is -1, the variable is \e written.
        /// \param op is the specific PcodeOp
        /// \param slot indicates the input operand, or the output
        /// \return the resolved sub-type
        public virtual Datatype resolveInFlow(PcodeOp op, int4 slot)
        {
            return this;
        }

        /// Find a previously resolved sub-type
        /// This is the constant version of resolveInFlow.  If a resulting subtype has already been calculated,
        /// for the particular read (\b slot >= 0) or write (\b slot == -1), then return it.
        /// Otherwise return the original data-type.
        /// \param op is the PcodeOp using the Varnode assigned with \b this data-type
        /// \param slot is the slot reading or writing the Varnode
        /// \return the resolved subtype or the original data-type
        public virtual Datatype findResolve(PcodeOp op, int4 slot)
        {
            return this;
        }

        /// Find a resolution compatible with the given data-type
        /// If \b this data-type has an alternate data-type form that matches the given data-type,
        /// return an index indicating this form, otherwise return -1.
        /// \param ct is the given data-type
        /// \return the index of the matching form or -1
        public virtual int4 findCompatibleResolve(Datatype ct)
        {
            return -1;
        }

        /// \brief Resolve which union field is being used for a given PcodeOp when a truncation is involved
        ///
        /// This method applies to the TYPE_UNION and TYPE_PARTIALUNION data-types, when a Varnode is backed
        /// by a larger Symbol with a union data-type, or if the Varnode is produced by a CPUI_SUBPIECE where
        /// the input Varnode has a union data-type.
        /// Scoring is done to compute the best field and the result is cached with the function.
        /// The record of the best field is returned or null if there is no appropriate field
        /// \param offset is the byte offset into the union we are truncating to
        /// \param op is either the PcodeOp reading the truncated Varnode or the CPUI_SUBPIECE doing the truncation
        /// \param slot is either the input slot of the reading PcodeOp or the artificial SUBPIECE slot: 1
        /// \param newoff is used to pass back how much offset is left to resolve
        /// \return the field of the union best associated with the truncation or null
        public virtual TypeField? resolveTruncation(int4 offset, PcodeOp op, int4 slot, int4 newoff)
        {
            return (const TypeField*)0;
        }

        /// Order this with -op- datatype
        public int4 typeOrder(Datatype op)
        {
            if (this==&op) return 0;
            return compare(op, 10);
        }

        /// Order \b this with -op-, treating \e bool data-type as special
        /// Order data-types, with special handling of the \e bool data-type. Data-types are compared
        /// using the normal ordering, but \e bool is ordered after all other data-types. A return value
        /// of 0 indicates the data-types are the same, -1 indicates that \b this is prefered (ordered earlier),
        /// and 1 indicates \b this is ordered later.
        /// \param op is the other data-type to compare with \b this
        /// \return -1, 0, or 1
        public int4 typeOrderBool(Datatype op)
        {
            if (this == &op) return 0;
            if (metatype == TYPE_BOOL) return 1;        // Never prefer bool over other data-types
            if (op.metatype == TYPE_BOOL) return -1;
            return compare(op, 10);
        }

        /// Encode a reference of \b this to a stream
        /// Encode a simple reference to \b this data-type as a \<typeref> element,
        /// including only the name and id.
        /// \param encoder is the stream encoder
        public void encodeRef(Encoder encoder)
        {               // Save just a name reference if possible
            if ((id != 0) && (metatype != TYPE_VOID))
            {
                encoder.openElement(ELEM_TYPEREF);
                encoder.writeString(ATTRIB_NAME, name);
                if (isVariableLength())
                {           // For a type with a "variable length" base
                    encoder.writeUnsignedInteger(ATTRIB_ID, hashSize(id, size));    // Emit the size independent version of the id
                    encoder.writeSignedInteger(ATTRIB_SIZE, size);          // but also emit size of this instance
                }
                else
                {
                    encoder.writeUnsignedInteger(ATTRIB_ID, id);
                }
                encoder.closeElement(ELEM_TYPEREF);
            }
            else
                encode(encoder);
        }

        /// Does \b this data-type consist of separate pieces?
        /// If a value with \b this data-type is put together from multiple pieces, is it better to display
        /// this construction as a sequence of separate assignments or as a single concatenation.
        /// Generally a TYPE_STRUCT or TYPE_ARRAY should be represented with separate assignments.
        /// \return \b true if the data-type is put together with multiple assignments
        public bool isPieceStructured()
        {
            //  if (metatype == TYPE_STRUCT || metatype == TYPE_ARRAY || metatype == TYPE_UNION ||
            //      metatype == TYPE_PARTIALUNION || metatype == TYPE_PARTIALSTRUCT)
            return (metatype <= TYPE_ARRAY);
        }

        /// \brief Encode the \b format attribute from an XML element
        ///
        /// Possible values are:
        ///   - 1  - \b hex
        ///   - 2  - \b dec
        ///   - 3  - \b oct
        ///   - 4  - \b bin
        ///   - 5 - \b char
        ///
        /// \param val is the string to encode
        /// \return the encoded value
        public static uint4 encodeIntegerFormat(string val)
        {
            if (val == "hex")
                return 1;
            else if (val == "dec")
                return 2;
            else if (val == "oct")
                return 3;
            else if (val == "bin")
                return 4;
            else if (val == "char")
                return 5;
            throw new LowlevelError("Unrecognized integer format: " + val);
        }

        /// \brief Decode the given format value into an XML attribute string
        ///
        /// Possible encoded values are 1-5 corresponding to "hex", "dec", "oct", "bin", "char"
        /// \param val is the value to decode
        /// \return the decoded string
        public static string decodeIntegerFormat(uint4 val)
        {
            if (val == 1)
                return "hex";
            else if (val == 2)
                return "dec";
            else if (val == 3)
                return "oct";
            else if (val == 4)
                return "bin";
            else if (val == 5)
                return "char";
            throw new LowlevelError("Unrecognized integer format encoding");
        }
    }
}
