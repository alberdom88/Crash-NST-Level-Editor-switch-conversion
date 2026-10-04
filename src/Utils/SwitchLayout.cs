using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Alchemy
{
    /// <summary>
    /// Memory layout of Alchemy objects in the Nintendo Switch version of Crash NST (GameVersion.NSX).
    ///
    /// The PC build is compiled with MSVC, the Switch build with Clang (Itanium C++ ABI).
    /// Clang reuses the tail padding of a base class: the fields of a derived class start right
    /// after the data of the base class (dsize) instead of after its padded size (sizeof).
    /// igObject holds 12 bytes of data (vtable + 32-bit reference count) padded to 16 bytes,
    /// so on Switch the first field of a derived class can start at offset 12 instead of 16.
    ///
    /// The Switch layout is computed from the PC (NST) metadata. When the real Switch size of a
    /// type is known (assets/switch_sizes.json, extracted from the game files) the result is
    /// checked against it, with the CTR (PS4, also Clang) offsets and the PC offsets as fallbacks.
    /// </summary>
    public static class SwitchLayout
    {
        public enum Source { Computed, CTR, NST }

        public class Layout
        {
            public int DataSize;          // End of the last field, without tail padding
            public int Size;              // Padded size (sizeof)
            public int Alignment;
            public Source Source;
            public bool PcCheckPassed;    // The same rules rebuild the PC layout exactly
            public int? KnownSize;        // Real Switch size, if known
            public Dictionary<string, int> Offsets = [];

            public bool MatchesKnownSize => KnownSize == null || KnownSize == Size;
        }

        private class Candidate
        {
            public int DataSize;
            public int Size;
            public int Alignment;
            public Dictionary<string, int> Offsets = [];
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<Type, Layout?> _cache = [];
        private static readonly Dictionary<Type, Candidate?> _pcCache = [];
        private static readonly Dictionary<Type, int> _alignCache = [];
        private static readonly HashSet<Type> _alignBusy = [];
        private static readonly Dictionary<Type, object?> _instances = [];
        private static Dictionary<string, int>? _knownSizes;

        // Embedded structures that are 16-byte aligned (SIMD vectors and curves built on them)
        private static readonly string[] _align16Names = ["Vec4f", "Matrix44f", "Quaternionf", "Aligned", "RgbCurve", "ColorCurve", "Rgba"];

        public static string FieldKey(FieldInfo field) => field.DeclaringType + "::" + field.Name;

        /// <summary>
        /// Name of a type as stored in the TMET fixup
        /// </summary>
        public static string TypeName(Type type)
        {
            string name = type.Name;
            if (name.EndsWith("MetaFieldInstance")) name = name.Replace("MetaFieldInstance", "MetaField");
            return name;
        }

        /// <summary>
        /// Real Switch size of a type (from the game files), or null if unknown
        /// </summary>
        public static int? GetKnownSize(Type type)
        {
            LoadKnownSizes();
            return _knownSizes!.TryGetValue(TypeName(type), out int size) ? size : null;
        }

        public static int KnownSizeCount()
        {
            LoadKnownSizes();
            return _knownSizes!.Count;
        }

        private static void LoadKnownSizes()
        {
            if (_knownSizes != null) return;
            _knownSizes = [];
            try
            {
                using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NST.assets.switch_sizes.json");
                if (stream == null) return;
                var sizes = JsonSerializer.Deserialize<Dictionary<string, int>>(stream);
                if (sizes != null) _knownSizes = sizes;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[SwitchLayout] Could not load switch_sizes.json: {e.Message}");
            }
        }

        /// <summary>
        /// Switch offset of a field. Fields of embedded structures (igMetaField) keep their PC offset.
        /// </summary>
        public static int GetOffset(Type owner, FieldInfo field, int nstOffset)
        {
            if (!owner.IsSubclassOf(typeof(igObject)) && owner != typeof(igObject)) return nstOffset;

            Layout? layout = Get(owner);
            if (layout != null && layout.Offsets.TryGetValue(FieldKey(field), out int offset)) return offset;

            return nstOffset;
        }

        /// <summary>
        /// Switch size of an object, or null if it can't be computed
        /// </summary>
        public static int? GetSize(Type type)
        {
            if (!type.IsSubclassOf(typeof(igObject)) && type != typeof(igObject)) return null;
            return Get(type)?.Size;
        }

        /// <summary>
        /// Switch layout of an igObject type (cached)
        /// </summary>
        public static Layout? Get(Type type)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(type, out Layout? cached)) return cached;
                _cache[type] = null; // Guard against recursion
                Layout? layout = null;
                try
                {
                    layout = Build(type);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[SwitchLayout] {type}: {e.Message}");
                }
                _cache[type] = layout;
                return layout;
            }
        }

        private static ObjectAttr? GetObjectAttr(Type type) => type.GetCustomAttribute<ObjectAttr>(false);

        /// <summary>
        /// Fields of a type that have a PC offset, in PC order
        /// </summary>
        private static List<(FieldInfo info, int nst, int? ctr)> GetLayoutFields(Type type, bool declaredOnly)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            if (declaredOnly) flags |= BindingFlags.DeclaredOnly;

            List<(FieldInfo, int, int?)> fields = [];
            foreach (FieldInfo field in type.GetFields(flags))
            {
                FieldAttr? attr = field.GetCustomAttribute<FieldAttr>(false);
                if (attr?.offset_nst == null) continue;
                fields.Add((field, attr.offset_nst.Value, attr.offset_ctr));
            }
            return fields.OrderBy(f => f.Item2).ToList();
        }

        private static Layout? Build(Type type)
        {
            int? known = GetKnownSize(type);

            if (type == typeof(igObject))
            {
                var layout = new Layout { DataSize = 12, Size = 16, Alignment = 8, Source = Source.Computed, PcCheckPassed = true, KnownSize = known };
                foreach (var (info, nst, _) in GetLayoutFields(type, true))
                {
                    layout.Offsets[FieldKey(info)] = nst;
                }
                return layout;
            }

            if (!type.IsSubclassOf(typeof(igObject))) return null;

            ObjectAttr? attr = GetObjectAttr(type);

            Candidate? computed = ComputeSwitch(type);
            Candidate? pc = ComputePC(type);
            bool pcOk = pc != null && CheckPC(type, pc, attr);
            Candidate? ctr = BuildFromAttributes(type, attr, useCTR: true);
            Candidate? nstCandidate = BuildFromAttributes(type, attr, useCTR: false);

            Candidate? chosen = null;
            Source source = Source.Computed;

            if (known != null)
            {
                if (computed != null && computed.Size == known) { chosen = computed; source = Source.Computed; }
                else if (ctr != null && ctr.Size == known) { chosen = ctr; source = Source.CTR; }
                else if (nstCandidate != null && nstCandidate.Size == known) { chosen = nstCandidate; source = Source.NST; }
            }

            if (chosen == null)
            {
                if (computed != null && (pcOk || ctr == null)) { chosen = computed; source = Source.Computed; }
                else if (ctr != null) { chosen = ctr; source = Source.CTR; }
                else if (computed != null) { chosen = computed; source = Source.Computed; }
                else if (nstCandidate != null) { chosen = nstCandidate; source = Source.NST; }
            }

            if (chosen == null) return null;

            return new Layout
            {
                DataSize = chosen.DataSize,
                Size = chosen.Size,
                Alignment = chosen.Alignment,
                Offsets = chosen.Offsets,
                Source = source,
                PcCheckPassed = pcOk,
                KnownSize = known,
            };
        }

        /// <summary>
        /// Itanium (Clang) layout: derived fields start at the base data size.
        /// Dynamic objects (MetaObjects) store their fields after the padded size of their base.
        /// </summary>
        private static Candidate? ComputeSwitch(Type type)
        {
            Type? baseType = type.BaseType;
            if (baseType == null) return null;

            Layout? baseLayout = Get(baseType);
            if (baseLayout == null) return null;

            ObjectAttr? attr = GetObjectAttr(type);
            bool dynamic = attr?.baseMetaType != null;
            int start = dynamic ? baseLayout.Size : baseLayout.DataSize;

            return Place(type, start, baseLayout.Alignment, new Dictionary<string, int>(baseLayout.Offsets), attr);
        }

        /// <summary>
        /// MSVC layout rebuilt with the same rules, to check the field sizes and alignments
        /// </summary>
        private static Candidate? ComputePC(Type type)
        {
            if (_pcCache.TryGetValue(type, out Candidate? cached)) return cached;
            _pcCache[type] = null;

            Candidate? result;
            if (type == typeof(igObject))
            {
                result = new Candidate { DataSize = 16, Size = 16, Alignment = 8 };
                foreach (var (info, nst, _) in GetLayoutFields(type, true)) result.Offsets[FieldKey(info)] = nst;
            }
            else
            {
                Type? baseType = type.BaseType;
                Candidate? basePc = baseType == null ? null : ComputePC(baseType);
                result = basePc == null ? null : Place(type, basePc.Size, basePc.Alignment, new Dictionary<string, int>(basePc.Offsets), GetObjectAttr(type));
            }

            _pcCache[type] = result;
            return result;
        }

        private static bool CheckPC(Type type, Candidate pc, ObjectAttr? attr)
        {
            if (attr?.size_nst != null && attr.size_nst.Value != pc.Size) return false;

            foreach (var (info, nst, _) in GetLayoutFields(type, true))
            {
                if (!pc.Offsets.TryGetValue(FieldKey(info), out int offset) || offset != nst) return false;
            }
            return true;
        }

        /// <summary>
        /// Place the fields declared by a type after a given offset, with natural alignment
        /// </summary>
        private static Candidate? Place(Type type, int start, int baseAlignment, Dictionary<string, int> offsets, ObjectAttr? attr)
        {
            var declared = GetLayoutFields(type, true);
            int cursor = start;
            int alignment = Math.Max(8, baseAlignment);

            for (int i = 0; i < declared.Count; i++)
            {
                var (info, nst, _) = declared[i];

                int size = FieldSize(info, type);
                if (size < 0)
                {
                    // Unknown array length: use the gap until the next PC field
                    int next = i + 1 < declared.Count ? declared[i + 1].nst : attr?.size_nst ?? -1;
                    if (next <= nst) return null;
                    size = next - nst;
                }

                int align = FieldAlign(info.FieldType);
                int offset = AlignUp(cursor, align);

                offsets[FieldKey(info)] = offset;
                cursor = offset + size;
                alignment = Math.Max(alignment, align);
            }

            if (attr != null) alignment = Math.Max(alignment, attr.alignment);

            int dataSize = Math.Max(cursor, start);

            return new Candidate
            {
                DataSize = dataSize,
                Size = AlignUp(dataSize, alignment),
                Alignment = alignment,
                Offsets = offsets,
            };
        }

        /// <summary>
        /// Layout taken directly from the CTR or NST offsets declared in the attributes
        /// </summary>
        private static Candidate? BuildFromAttributes(Type type, ObjectAttr? attr, bool useCTR)
        {
            int? size = useCTR ? attr?.size_ctr : attr?.size_nst;
            if (size == null) return null;

            var candidate = new Candidate { Size = size.Value, Alignment = Math.Max(8, attr!.alignment) };
            int dataSize = 0;

            foreach (var (info, nst, ctr) in GetLayoutFields(type, false))
            {
                int? offset = useCTR ? ctr : nst;
                if (offset == null) return null;

                candidate.Offsets[FieldKey(info)] = offset.Value;
                int fieldSize = Math.Max(FieldSize(info, info.DeclaringType ?? type), 0);
                dataSize = Math.Max(dataSize, offset.Value + fieldSize);
            }

            candidate.DataSize = dataSize;
            return candidate;
        }

        private static int AlignUp(int value, int alignment) => alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

        /// <summary>
        /// Size of a field in bytes, or -1 for arrays of unknown length
        /// </summary>
        private static int FieldSize(FieldInfo field, Type owner)
        {
            Type type = field.FieldType;

            if (type.IsArray)
            {
                int length = ArrayLength(field, owner);
                if (length < 0) return -1;
                return length * ValueSize(type.GetElementType()!);
            }

            return ValueSize(type);
        }

        private static int ValueSize(Type type)
        {
            if (type.IsEnum) return 4;
            if (type == typeof(Half)) return 2;
            return AttributeUtils.GetFieldSize(type, GameVersion.NST);
        }

        private static int ArrayLength(FieldInfo field, Type owner)
        {
            if (owner.IsAbstract || owner.ContainsGenericParameters) return -1;

            if (!_instances.TryGetValue(owner, out object? instance))
            {
                try { instance = Activator.CreateInstance(owner); }
                catch { instance = null; }
                _instances[owner] = instance;
            }

            if (instance == null) return -1;
            return field.GetValue(instance) is Array array ? array.Length : -1;
        }

        /// <summary>
        /// Natural alignment of a field type
        /// </summary>
        public static int FieldAlign(Type type)
        {
            if (type.IsArray) return FieldAlign(type.GetElementType()!);
            if (type.IsEnum) return 4;
            if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte)) return 1;
            if (type == typeof(Half)) return 2;
            if (type == typeof(string)) return 8;
            if (type.IsPrimitive) return Marshal.SizeOf(type);
            if (type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Matrix4x4) || type == typeof(Matrix3x4)) return 16;
            if (type.IsAssignableTo(typeof(igObject)) || type.IsAssignableTo(typeof(Havok.hkReferencedObject))) return 8;
            if (type.IsAssignableTo(typeof(igMetaField)) || type.IsAssignableTo(typeof(Havok.hkObject))) return StructAlign(type);
            return 8;
        }

        /// <summary>
        /// Alignment of an embedded structure: the largest alignment of its fields
        /// </summary>
        private static int StructAlign(Type type)
        {
            if (_alignCache.TryGetValue(type, out int cached)) return cached;

            string name = type.IsGenericType ? type.GetGenericTypeDefinition().Name : type.Name;
            if (_align16Names.Any(n => name.Contains(n)))
            {
                _alignCache[type] = 16;
                return 16;
            }

            int size = 0;
            try { size = AttributeUtils.GetFieldSize(type, GameVersion.NST); }
            catch { size = 0; }

            int alignment = 1;
            if (!type.IsAssignableTo(typeof(igBitFieldMetaField)) && _alignBusy.Add(type))
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (field.GetCustomAttribute<FieldAttr>(false) == null) continue;
                    alignment = Math.Max(alignment, FieldAlign(field.FieldType));
                }
                _alignBusy.Remove(type);
            }

            if (alignment <= 1)
            {
                alignment = 1;
                while (alignment < 8 && size > 0 && size % (alignment * 2) == 0) alignment *= 2;
            }

            _alignCache[type] = alignment;
            return alignment;
        }
    }
}
