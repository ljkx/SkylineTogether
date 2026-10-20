using System;
using System.Collections.Generic;
using System.Text;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>
    /// The type definitions of a .NET assembly read straight from its file (ECMA-335 II.22 and II.24), without loading
    /// anything: each type's full name in reflection form (<c>Namespace.Type</c>, nested <c>Outer+Inner</c>), its base
    /// type's and its interfaces' names, and its enclosing type. <see cref="ReferenceCheck.TypesOf"/> uses it when some types
    /// of the mod do not load: Mono's <see cref="TypeLoadException.TypeName"/> then names the missing game type (the base
    /// class or interface a game update removed), not the mod's type, so the mod's types that did not load are found here
    /// by name, and the game type that broke them by their <c>extends</c> and <c>implements</c> rows. Only the tables needed
    /// are read; a malformed image throws <see cref="BadImageFormatException"/>. Pure.
    /// </summary>
    public sealed class TypeDefTable
    {
        /// <summary>One row of the TypeDef table.</summary>
        public sealed class Row
        {
            /// <summary>The row's metadata token (<c>0x02000001</c> is <c>&lt;Module&gt;</c>).</summary>
            public int Token;
            /// <summary>Reflection form: <c>Namespace.Type</c>, nested <c>Outer+Inner</c>, generic arity as <c>`1</c>.</summary>
            public string FullName;
            /// <summary>The name of the base type (a TypeRef's or TypeDef's full name); null for none or a generic instantiation.</summary>
            public string BaseName;
            /// <summary>The names of the directly implemented interfaces (generic instantiations left out).</summary>
            public readonly List<string> InterfaceNames = new List<string>();
            /// <summary>The enclosing type's row, or null.</summary>
            public Row Enclosing;
            internal int NameIndex, NamespaceIndex, Extends;
        }

        /// <summary>The rows in table order.</summary>
        public IReadOnlyList<Row> Types => _types;

        private readonly List<Row> _types = new List<Row>();

        // table numbers (ECMA-335 II.22)
        private const int Module = 0x00, TypeRef = 0x01, TypeDef = 0x02, FieldPtr = 0x03, Field = 0x04, MethodPtr = 0x05, MethodDef = 0x06,
            ParamPtr = 0x07, Param = 0x08, InterfaceImpl = 0x09, MemberRef = 0x0A, Constant = 0x0B, CustomAttribute = 0x0C,
            FieldMarshal = 0x0D, DeclSecurity = 0x0E, ClassLayout = 0x0F, FieldLayout = 0x10, StandAloneSig = 0x11, EventMap = 0x12,
            EventPtr = 0x13, Event = 0x14, PropertyMap = 0x15, PropertyPtr = 0x16, Property = 0x17, MethodSemantics = 0x18,
            MethodImpl = 0x19, ModuleRef = 0x1A, TypeSpec = 0x1B, ImplMap = 0x1C, FieldRva = 0x1D, EncLog = 0x1E, EncMap = 0x1F,
            Assembly = 0x20, AssemblyProcessor = 0x21, AssemblyOs = 0x22, AssemblyRef = 0x23, AssemblyRefProcessor = 0x24,
            AssemblyRefOs = 0x25, File = 0x26, ExportedType = 0x27, ManifestResource = 0x28, NestedClass = 0x29,
            GenericParam = 0x2A, MethodSpec = 0x2B, GenericParamConstraint = 0x2C;

        private const int NotUsed = -1;

        /// <summary>Reads the TypeDef table of the assembly image <paramref name="image"/> (the bytes of the DLL file).</summary>
        public static TypeDefTable Read(byte[] image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            try
            {
                return new TypeDefTable(image);
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException)
            {
                throw new BadImageFormatException("the metadata tables cannot be read: " + e.Message, e);
            }
        }

        private readonly byte[] _image;
        private int _strings;
        private int _stringsSize;

        private TypeDefTable(byte[] image)
        {
            _image = image;
            // PE: DOS header -> PE signature -> COFF header -> optional header -> data directory 14 (CLI header)
            int pe = I32(0x3C);
            if (pe <= 0 || I32(pe) != 0x00004550) throw new BadImageFormatException("no PE signature");
            int coff = pe + 4;
            int sections = U16(coff + 2);
            int optionalSize = U16(coff + 16);
            int optional = coff + 20;
            int magic = U16(optional);
            int directories = magic == 0x20B ? optional + 112 : magic == 0x10B ? optional + 96 : throw new BadImageFormatException("unknown optional header");
            int cliRva = I32(directories + 14 * 8);
            if (cliRva == 0) throw new BadImageFormatException("not a .NET assembly");
            int sectionTable = optional + optionalSize;
            int Offset(int rva)
            {
                for (int i = 0; i < sections; i++)
                {
                    int s = sectionTable + i * 40;
                    int virtualAddress = I32(s + 12), virtualSize = Math.Max(I32(s + 8), I32(s + 16)), raw = I32(s + 20);
                    if (rva >= virtualAddress && rva < virtualAddress + virtualSize) return rva - virtualAddress + raw;
                }
                throw new BadImageFormatException("RVA 0x" + rva.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + " is in no section");
            }

            int cli = Offset(cliRva);
            int metadata = Offset(I32(cli + 8));
            if (I32(metadata) != 0x424A5342) throw new BadImageFormatException("no metadata signature");
            int versionLength = I32(metadata + 12);
            int at = metadata + 16 + versionLength;
            int streams = U16(at + 2);
            at += 4;
            int tables = -1;
            for (int i = 0; i < streams; i++)
            {
                int offset = I32(at), size = I32(at + 4);
                at += 8;
                int nameStart = at;
                while (_image[at] != 0) at++;
                string name = Encoding.ASCII.GetString(_image, nameStart, at - nameStart);
                at = nameStart + ((at - nameStart + 4) & ~3);
                if (name == "#~" || name == "#-") tables = metadata + offset;
                else if (name == "#Strings")
                {
                    _strings = metadata + offset;
                    _stringsSize = size;
                }
            }
            if (tables < 0 || _stringsSize == 0) throw new BadImageFormatException("no #~ or #Strings stream");
            ReadTables(tables);
        }

        private void ReadTables(int start)
        {
            int heapSizes = _image[start + 6];
            long valid = I64(start + 8);
            var rows = new int[64];
            int at = start + 24;
            for (int t = 0; t < 64; t++)
            {
                if ((valid & (1L << t)) == 0) continue;
                rows[t] = I32(at);
                at += 4;
            }
            int str = (heapSizes & 1) != 0 ? 4 : 2, guid = (heapSizes & 2) != 0 ? 4 : 2, blob = (heapSizes & 4) != 0 ? 4 : 2;
            int Idx(int table) => rows[table] < 0x10000 ? 2 : 4;
            int Coded(int bits, params int[] of)
            {
                int max = 0;
                foreach (int t in of)
                {
                    if (t != NotUsed) max = Math.Max(max, rows[t]);
                }
                return max < (1 << (16 - bits)) ? 2 : 4;
            }
            int typeDefOrRef = Coded(2, TypeDef, TypeRef, TypeSpec);
            int resolutionScope = Coded(2, Module, ModuleRef, AssemblyRef, TypeRef);
            int memberRefParent = Coded(3, TypeDef, TypeRef, ModuleRef, MethodDef, TypeSpec);
            int hasConstant = Coded(2, Field, Param, Property);
            int hasCustomAttribute = Coded(5, MethodDef, Field, TypeRef, TypeDef, Param, InterfaceImpl, MemberRef, Module, DeclSecurity, Property, Event,
                StandAloneSig, ModuleRef, TypeSpec, Assembly, AssemblyRef, File, ExportedType, ManifestResource, GenericParam, GenericParamConstraint, MethodSpec);
            int customAttributeType = Coded(3, NotUsed, NotUsed, MethodDef, MemberRef, NotUsed);
            int hasFieldMarshal = Coded(1, Field, Param);
            int hasDeclSecurity = Coded(2, TypeDef, MethodDef, Assembly);
            int hasSemantics = Coded(1, Event, Property);
            int methodDefOrRef = Coded(1, MethodDef, MemberRef);
            int memberForwarded = Coded(1, Field, MethodDef);
            int implementation = Coded(2, File, AssemblyRef, ExportedType);

            // row sizes of every table up to NestedClass (the tables are stored one after another in this order)
            var size = new int[NestedClass + 1];
            size[Module] = 2 + str + guid * 3;
            size[TypeRef] = resolutionScope + str * 2;
            size[TypeDef] = 4 + str * 2 + typeDefOrRef + Idx(Field) + Idx(MethodDef);
            size[FieldPtr] = Idx(Field);
            size[Field] = 2 + str + blob;
            size[MethodPtr] = Idx(MethodDef);
            size[MethodDef] = 4 + 2 + 2 + str + blob + Idx(Param);
            size[ParamPtr] = Idx(Param);
            size[Param] = 2 + 2 + str;
            size[InterfaceImpl] = Idx(TypeDef) + typeDefOrRef;
            size[MemberRef] = memberRefParent + str + blob;
            size[Constant] = 2 + hasConstant + blob;
            size[CustomAttribute] = hasCustomAttribute + customAttributeType + blob;
            size[FieldMarshal] = hasFieldMarshal + blob;
            size[DeclSecurity] = 2 + hasDeclSecurity + blob;
            size[ClassLayout] = 2 + 4 + Idx(TypeDef);
            size[FieldLayout] = 4 + Idx(Field);
            size[StandAloneSig] = blob;
            size[EventMap] = Idx(TypeDef) + Idx(Event);
            size[EventPtr] = Idx(Event);
            size[Event] = 2 + str + typeDefOrRef;
            size[PropertyMap] = Idx(TypeDef) + Idx(Property);
            size[PropertyPtr] = Idx(Property);
            size[Property] = 2 + str + blob;
            size[MethodSemantics] = 2 + Idx(MethodDef) + hasSemantics;
            size[MethodImpl] = Idx(TypeDef) + methodDefOrRef * 2;
            size[ModuleRef] = str;
            size[TypeSpec] = blob;
            size[ImplMap] = 2 + memberForwarded + str + Idx(ModuleRef);
            size[FieldRva] = 4 + Idx(Field);
            size[EncLog] = 8;
            size[EncMap] = 4;
            size[Assembly] = 4 + 2 * 4 + 4 + blob + str * 2;
            size[AssemblyProcessor] = 4;
            size[AssemblyOs] = 12;
            size[AssemblyRef] = 2 * 4 + 4 + blob + str * 2 + blob;
            size[AssemblyRefProcessor] = 4 + Idx(AssemblyRef);
            size[AssemblyRefOs] = 12 + Idx(AssemblyRef);
            size[File] = 4 + str + blob;
            size[ExportedType] = 4 + 4 + str * 2 + implementation;
            size[ManifestResource] = 4 + 4 + str + implementation;
            size[NestedClass] = Idx(TypeDef) * 2;
            var offset = new int[NestedClass + 1];
            for (int t = 0; t <= NestedClass; t++)
            {
                offset[t] = at;
                at += size[t] * rows[t];
            }
            if (at > _image.Length) throw new BadImageFormatException("the metadata tables are truncated");

            // TypeRef: names (nested TypeRefs have a TypeRef as their resolution scope)
            var refNames = new string[rows[TypeRef] + 1];
            var refScopes = new int[rows[TypeRef] + 1];
            for (int r = 1; r <= rows[TypeRef]; r++)
            {
                int p = offset[TypeRef] + (r - 1) * size[TypeRef];
                int scope = Read(p, resolutionScope);
                refScopes[r] = (scope & 3) == 3 ? scope >> 2 : 0;
                refNames[r] = Join(String(Read(p + resolutionScope + str, str)), String(Read(p + resolutionScope, str)));
            }
            string RefName(int r, int depth)
            {
                if (r <= 0 || r >= refNames.Length) return null;
                int outer = refScopes[r];
                return outer > 0 && depth < 16 ? RefName(outer, depth + 1) + "+" + refNames[r] : refNames[r];
            }

            for (int r = 1; r <= rows[TypeDef]; r++)
            {
                int p = offset[TypeDef] + (r - 1) * size[TypeDef];
                _types.Add(new Row
                {
                    Token = 0x02000000 | r,
                    NameIndex = Read(p + 4, str),
                    NamespaceIndex = Read(p + 4 + str, str),
                    Extends = Read(p + 4 + str * 2, typeDefOrRef),
                });
            }
            for (int r = 1; r <= rows[NestedClass]; r++)
            {
                int p = offset[NestedClass] + (r - 1) * size[NestedClass];
                int nested = Read(p, Idx(TypeDef)), enclosing = Read(p + Idx(TypeDef), Idx(TypeDef));
                if (nested >= 1 && nested <= _types.Count && enclosing >= 1 && enclosing <= _types.Count) _types[nested - 1].Enclosing = _types[enclosing - 1];
            }
            foreach (Row row in _types) row.FullName = DefName(row, 0);

            string TypeName(int coded)
            {
                int index = coded >> 2;
                switch (coded & 3)
                {
                    case 0: return index >= 1 && index <= _types.Count ? _types[index - 1].FullName : null;
                    case 1: return RefName(index, 0);
                    default: return null; // a TypeSpec: a generic instantiation
                }
            }
            foreach (Row row in _types) row.BaseName = row.Extends == 0 ? null : TypeName(row.Extends);
            for (int r = 1; r <= rows[InterfaceImpl]; r++)
            {
                int p = offset[InterfaceImpl] + (r - 1) * size[InterfaceImpl];
                int type = Read(p, Idx(TypeDef));
                string name = TypeName(Read(p + Idx(TypeDef), typeDefOrRef));
                if (name != null && type >= 1 && type <= _types.Count) _types[type - 1].InterfaceNames.Add(name);
            }
        }

        private string DefName(Row row, int depth)
        {
            string name = String(row.NameIndex);
            if (row.Enclosing != null && depth < 16) return DefName(row.Enclosing, depth + 1) + "+" + name;
            return Join(String(row.NamespaceIndex), name);
        }

        private static string Join(string ns, string name) => string.IsNullOrEmpty(ns) ? name : ns + "." + name;

        private string String(int index)
        {
            if (index <= 0 || index >= _stringsSize) return "";
            int start = _strings + index, end = start;
            while (_image[end] != 0) end++;
            return Encoding.UTF8.GetString(_image, start, end - start);
        }

        private int Read(int at, int width) => width == 2 ? U16(at) : I32(at);

        private int U16(int at) => _image[at] | (_image[at + 1] << 8);

        private int I32(int at) => _image[at] | (_image[at + 1] << 8) | (_image[at + 2] << 16) | (_image[at + 3] << 24);

        private long I64(int at) => (uint)I32(at) | ((long)I32(at + 4) << 32);
    }
}
