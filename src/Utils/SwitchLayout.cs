using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    /// The Switch layout is computed from the PC (NST) metadata:
    /// - data that the PC version holds after the fields known by the editor ("hidden" data,
    ///   ObjectAttr size larger than the fields) is kept, and fields that the editor redeclares
    ///   inside that data (e.g. igBoolMetaFieldInstance._default) are moved with it;
    /// - some fields only exist on PC (Steam, mouse, PC graphics options...). They are found by
    ///   comparing with the real Switch sizes (assets/switch_sizes.json, extracted from the game
    ///   files): a field is removed only if it fixes a size without breaking any other one;
    /// - the CTR (PS4, also Clang) offsets and the PC offsets are used as fallbacks.
    /// </summary>
    public static class SwitchLayout
    {
        public enum Source { Computed, CTR, NST }

        public class Layout
        {
            public int DataSize;          // End of the data, without tail padding (where the fields of a derived class start)
            public int Size;              // Padded size (sizeof)
            public int ComputedSize;      // Size before padding to the real Switch size
            public int Alignment;
            public Source Source;
            public bool PcCheckPassed;    // The same rules rebuild the PC layout exactly
            public bool Padded;           // Size increased to the real Switch size (data unknown to the editor)
            public int? KnownSize;        // Real Switch size, if known
            public Dictionary<string, int> Offsets = [];
            public List<HiddenData> Hidden = [];

            public bool MatchesKnownSize => KnownSize == null || KnownSize == ComputedSize;
        }

        /// <summary>
        /// Data of a class that the editor doesn't describe (PC offset -> Switch offset)
        /// </summary>
        public record HiddenData(int PcOffset, int SwitchOffset, int Size);

        private class Candidate
        {
            public int DataSize;
            public int RawEnd;            // End of the declared fields
            public int Size;
            public int Alignment;
            public Dictionary<string, int> Offsets = [];
            public List<HiddenData> Hidden = [];
        }

        private static readonly object _lock = new();
        private static Calculator? _calculator;
        private static bool _solving;
        private static HashSet<string> _removed = new(StringComparer.Ordinal);
        private static readonly List<string> _removalLog = [];
        private static readonly List<string> _messages = [];
        private static readonly HashSet<string> _loggedMessages = [];

        private static Dictionary<string, int>? _knownSizes;
        private static Dictionary<string, Type>? _typesByName;

        private static readonly Dictionary<(Type, bool), List<(FieldInfo info, int nst, int? ctr)>> _fieldsCache = [];
        private static readonly Dictionary<(FieldInfo, Type), int> _fieldSizeCache = [];
        private static readonly Dictionary<Type, int> _alignCache = [];
        private static readonly HashSet<Type> _alignBusy = [];
        private static readonly Dictionary<Type, object?> _instances = [];

        // Embedded structures that are 16-byte aligned (SIMD vectors and curves built on them)
        private static readonly string[] _align16Names = ["Vec4f", "Matrix44f", "Quaternionf", "Aligned", "RgbCurve", "ColorCurve", "Rgba"];

        // Names of fields that are likely PC-only
        private static readonly Regex _pcOnlyNames = new("steam|hover|mouse|keyboard|cursor|windows|dx11|d3d", RegexOptions.IgnoreCase);

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
        /// Editor class of a TMET type name (igObject types only)
        /// </summary>
        public static Type? FindType(string typeName)
        {
            lock (_lock)
            {
                if (_typesByName == null)
                {
                    _typesByName = [];
                    IEnumerable<Type> types = typeof(igObject).Assembly.GetTypes()
                        .Where(t => t.Namespace == "Alchemy" && !t.ContainsGenericParameters && (t == typeof(igObject) || t.IsSubclassOf(typeof(igObject))))
                        .OrderBy(t => t.IsNested ? 1 : 0);

                    foreach (Type type in types)
                    {
                        _typesByName.TryAdd(TypeName(type), type);
                    }
                }
                return _typesByName.TryGetValue(typeName, out Type? result) ? result : null;
            }
        }

        /// <summary>
        /// Real Switch size of a type (from the game files), or null if unknown
        /// </summary>
        public static int? GetKnownSize(Type type)
        {
            return KnownSizes().TryGetValue(TypeName(type), out int size) ? size : null;
        }

        public static int KnownSizeCount() => KnownSizes().Count;

        public static IReadOnlyDictionary<string, int> KnownSizes()
        {
            lock (_lock)
            {
                if (_knownSizes != null) return _knownSizes;
                _knownSizes = [];
                try
                {
                    using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NST.assets.switch_sizes.json");
                    if (stream != null)
                    {
                        var sizes = JsonSerializer.Deserialize<Dictionary<string, int>>(stream);
                        if (sizes != null) _knownSizes = sizes;
                    }
                }
                catch (Exception e)
                {
                    Log($"Could not load switch_sizes.json: {e.Message}");
                }
                return _knownSizes;
            }
        }

        /// <summary>
        /// Fields that don't exist in the Switch version (found by comparing with the real sizes)
        /// </summary>
        public static bool IsRemoved(FieldInfo field)
        {
            lock (_lock)
            {
                Calc();
                return _removed.Contains(FieldKey(field));
            }
        }

        public static List<string> GetRemovalLog()
        {
            lock (_lock)
            {
                Calc();
                return _removalLog.ToList();
            }
        }

        public static List<string> GetMessages()
        {
            lock (_lock) return _messages.ToList();
        }

        private static void Log(string message)
        {
            if (!_loggedMessages.Add(message)) return;
            if (_messages.Count < 500) _messages.Add(message);
            Console.WriteLine($"[SwitchLayout] {message}");
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
                return Calc().Get(type);
            }
        }

        private static Calculator Calc()
        {
            if (_calculator != null) return _calculator;
            if (_solving) return new Calculator(_removed);

            _solving = true;
            try
            {
                Solve();
            }
            catch (Exception e)
            {
                Log($"Search for PC-only fields failed: {e.Message}");
            }
            finally
            {
                _solving = false;
            }

            _calculator = new Calculator(_removed);
            return _calculator;
        }

        // ------------------------------------------------------------------ PC-only fields

        private static int Depth(Type type)
        {
            int depth = 0;
            for (Type? t = type; t != null; t = t.BaseType) depth++;
            return depth;
        }

        private static List<(Type type, int size)> KnownTypes()
        {
            List<(Type, int)> types = [];
            foreach (var (name, size) in KnownSizes())
            {
                Type? type = FindType(name);
                if (type == null || type == typeof(igObject)) continue;
                types.Add((type, size));
            }
            return types.OrderBy(t => Depth(t.Item1)).ThenBy(t => t.Item1.Name).ToList();
        }

        private static HashSet<Type> Matching(HashSet<string> removed, List<(Type type, int size)> known)
        {
            var calculator = new Calculator(removed);
            HashSet<Type> matching = [];
            foreach (var (type, size) in known)
            {
                if (calculator.Get(type)?.ComputedSize == size) matching.Add(type);
            }
            return matching;
        }

        /// <summary>
        /// Find the fields that only exist on PC: for every type larger than its real Switch size,
        /// try to remove up to 3 candidate fields. A removal is kept only if it gives the right size
        /// and doesn't break the size of any other type.
        /// </summary>
        private static void Solve()
        {
            List<(Type type, int size)> known = KnownTypes();
            var removed = new HashSet<string>(StringComparer.Ordinal);
            HashSet<Type> matching = Matching(removed, known);

            for (int pass = 0; pass < 3; pass++)
            {
                bool changed = false;

                foreach (var (type, size) in known)
                {
                    if (matching.Contains(type)) continue;

                    Layout? current = new Calculator(removed).Get(type);
                    if (current == null || current.ComputedSize <= size) continue;

                    List<FieldInfo> candidates = RemovalCandidates(type, removed);
                    if (candidates.Count == 0) continue;

                    var options = new List<(List<FieldInfo> fields, int ctrScore, int keywords)>();
                    foreach (List<FieldInfo> subset in Subsets(candidates, 3))
                    {
                        var trial = new HashSet<string>(removed, StringComparer.Ordinal);
                        foreach (FieldInfo field in subset) trial.Add(FieldKey(field));

                        Layout? layout = new Calculator(trial).Get(type);
                        if (layout == null || layout.ComputedSize != size) continue;

                        options.Add((subset, CtrScore(type, layout, trial), subset.Count(f => _pcOnlyNames.IsMatch(f.Name))));
                    }

                    var ordered = options
                        .OrderByDescending(o => o.ctrScore)
                        .ThenBy(o => o.fields.Count)
                        .ThenByDescending(o => o.keywords);

                    foreach (var option in ordered)
                    {
                        var trial = new HashSet<string>(removed, StringComparer.Ordinal);
                        foreach (FieldInfo field in option.fields) trial.Add(FieldKey(field));

                        HashSet<Type> trialMatching = Matching(trial, known);
                        if (!matching.IsSubsetOf(trialMatching)) continue;

                        int fixedCount = trialMatching.Count - matching.Count;
                        removed = trial;
                        matching = trialMatching;
                        changed = true;
                        _removalLog.Add($"{TypeName(type)}: {string.Join(", ", option.fields.Select(f => f.DeclaringType?.Name + "." + f.Name))} (tipi corretti: +{fixedCount})");
                        break;
                    }
                }

                if (!changed) break;
            }

            _removed = removed;
        }

        /// <summary>
        /// Fields of a type and its bases that may be PC-only: fields without a CTR offset in classes
        /// that also exist in CTR (another Clang build of the same engine), or with a PC-specific name
        /// </summary>
        private static List<FieldInfo> RemovalCandidates(Type type, HashSet<string> removed)
        {
            var candidates = new List<(FieldInfo info, bool keyword)>();

            for (Type? t = type; t != null && t != typeof(igObject) && t.IsSubclassOf(typeof(igObject)); t = t.BaseType)
            {
                ObjectAttr? attr = GetObjectAttr(t);
                foreach (var (info, nst, ctr) in GetLayoutFields(t, true))
                {
                    if (ctr != null || removed.Contains(FieldKey(info))) continue;

                    bool keyword = _pcOnlyNames.IsMatch(info.Name);
                    if (keyword || attr?.size_ctr != null) candidates.Add((info, keyword));
                }
            }

            return candidates.OrderByDescending(c => c.keyword).Select(c => c.info).Take(14).ToList();
        }

        /// <summary>
        /// Number of fields placed at their CTR offset (CTR is built with the same ABI as the Switch)
        /// </summary>
        private static int CtrScore(Type type, Layout layout, HashSet<string> removed)
        {
            int score = 0;
            foreach (var (info, nst, ctr) in GetLayoutFields(type, false))
            {
                if (ctr == null || info.DeclaringType == typeof(igObject)) continue;
                string key = FieldKey(info);
                if (removed.Contains(key)) continue;
                if (layout.Offsets.TryGetValue(key, out int offset) && offset == ctr.Value) score++;
            }
            return score;
        }

        private static IEnumerable<List<FieldInfo>> Subsets(List<FieldInfo> items, int maxCount)
        {
            for (int count = 1; count <= maxCount && count <= items.Count; count++)
            {
                int[] indices = Enumerable.Range(0, count).ToArray();
                while (true)
                {
                    yield return indices.Select(i => items[i]).ToList();

                    int k = count - 1;
                    while (k >= 0 && indices[k] == items.Count - count + k) k--;
                    if (k < 0) break;

                    indices[k]++;
                    for (int j = k + 1; j < count; j++) indices[j] = indices[j - 1] + 1;
                }
            }
        }

        // ------------------------------------------------------------------ layout computation

        private static ObjectAttr? GetObjectAttr(Type type) => type.GetCustomAttribute<ObjectAttr>(false);

        /// <summary>
        /// Fields of a type that have a PC offset, in PC order
        /// </summary>
        private static List<(FieldInfo info, int nst, int? ctr)> GetLayoutFields(Type type, bool declaredOnly)
        {
            if (_fieldsCache.TryGetValue((type, declaredOnly), out var cached)) return cached;

            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            if (declaredOnly) flags |= BindingFlags.DeclaredOnly;

            List<(FieldInfo, int, int?)> fields = [];
            foreach (FieldInfo field in type.GetFields(flags))
            {
                FieldAttr? attr = field.GetCustomAttribute<FieldAttr>(false);
                if (attr?.offset_nst == null) continue;
                fields.Add((field, attr.offset_nst.Value, attr.offset_ctr));
            }

            List<(FieldInfo info, int nst, int? ctr)> result = fields.OrderBy(f => f.Item2).ToList();
            _fieldsCache[(type, declaredOnly)] = result;
            return result;
        }

        private sealed class Calculator
        {
            private readonly HashSet<string> _removedFields;
            private readonly Dictionary<Type, Layout?> _cache = [];
            private readonly Dictionary<Type, Candidate?> _pcCache = [];

            public Calculator(HashSet<string> removedFields) => _removedFields = removedFields;

            public Layout? Get(Type type)
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
                    Log($"{type}: {e.Message}");
                }

                _cache[type] = layout;
                return layout;
            }

            private Layout? Build(Type type)
            {
                int? known = GetKnownSize(type);

                if (type == typeof(igObject))
                {
                    var root = new Layout { DataSize = 12, Size = 16, ComputedSize = 16, Alignment = 8, Source = Source.Computed, PcCheckPassed = true, KnownSize = known };
                    foreach (var (info, nst, _) in GetLayoutFields(type, true))
                    {
                        root.Offsets[FieldKey(info)] = nst;
                    }
                    return root;
                }

                if (!type.IsSubclassOf(typeof(igObject))) return null;

                ObjectAttr? attr = GetObjectAttr(type);

                Candidate? pc = ComputePC(type);
                Candidate? computed = ComputeSwitch(type, attr, pc);
                bool pcOk = pc != null && CheckPC(type, pc, attr);
                Candidate? ctr = FromAttributes(type, attr, useCTR: true);
                Candidate? nstCandidate = FromAttributes(type, attr, useCTR: false);

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

                int size = chosen.Size;
                int dataSize = chosen.DataSize;
                bool padded = false;

                // The Switch object is larger: it holds data unknown to the editor
                if (known != null && known.Value > size)
                {
                    size = known.Value;
                    dataSize = Math.Max(dataSize, known.Value);
                    padded = true;
                }

                return new Layout
                {
                    DataSize = dataSize,
                    Size = size,
                    ComputedSize = chosen.Size,
                    Alignment = chosen.Alignment,
                    Offsets = chosen.Offsets,
                    Hidden = chosen.Hidden,
                    Source = source,
                    PcCheckPassed = pcOk,
                    Padded = padded,
                    KnownSize = known,
                };
            }

            /// <summary>
            /// Itanium (Clang) layout: derived fields start at the base data size.
            /// Dynamic objects (MetaObjects) store their fields after the padded size of their base.
            /// </summary>
            private Candidate? ComputeSwitch(Type type, ObjectAttr? attr, Candidate? pcSelf)
            {
                Type? baseType = type.BaseType;
                if (baseType == null) return null;

                Layout? baseLayout = Get(baseType);
                if (baseLayout == null) return null;

                Candidate? basePc = ComputePC(baseType);
                bool dynamic = attr?.baseMetaType != null;
                int start = dynamic ? baseLayout.Size : baseLayout.DataSize;

                Candidate? result = Place(type, false, start, baseLayout.Alignment, basePc?.Size ?? 0, baseLayout.Hidden, new Dictionary<string, int>(baseLayout.Offsets), attr);
                if (result == null) return null;

                // Data after the fields known by the editor (the PC size is larger than its fields)
                if (pcSelf != null)
                {
                    int pcHiddenStart = AlignUp(pcSelf.RawEnd, 8);
                    int hiddenSize = pcSelf.Size - pcHiddenStart;

                    if (pcSelf.Size > AlignUp(pcSelf.RawEnd, pcSelf.Alignment) && hiddenSize > 0)
                    {
                        int switchHiddenStart = AlignUp(result.DataSize, 8);
                        result.Hidden.Add(new HiddenData(pcHiddenStart, switchHiddenStart, hiddenSize));
                        result.DataSize = switchHiddenStart + hiddenSize;
                        result.Size = AlignUp(result.DataSize, result.Alignment);
                    }
                }

                return result;
            }

            /// <summary>
            /// MSVC layout rebuilt with the same rules, to check the field sizes and alignments
            /// </summary>
            private Candidate? ComputePC(Type type)
            {
                if (_pcCache.TryGetValue(type, out Candidate? cached)) return cached;
                _pcCache[type] = null;

                Candidate? result = null;
                if (type == typeof(igObject))
                {
                    result = new Candidate { DataSize = 16, RawEnd = 16, Size = 16, Alignment = 8 };
                    foreach (var (info, nst, _) in GetLayoutFields(type, true)) result.Offsets[FieldKey(info)] = nst;
                }
                else if (type.IsSubclassOf(typeof(igObject)))
                {
                    Type? baseType = type.BaseType;
                    Candidate? basePc = baseType == null ? null : ComputePC(baseType);
                    ObjectAttr? attr = GetObjectAttr(type);

                    if (basePc != null)
                    {
                        result = Place(type, true, basePc.Size, basePc.Alignment, basePc.Size, null, new Dictionary<string, int>(basePc.Offsets), attr);

                        // Fields unknown to the editor at the end of the object
                        if (result != null && attr?.size_nst != null && attr.size_nst.Value > result.Size)
                        {
                            result.Size = attr.size_nst.Value;
                        }

                        if (result != null) result.DataSize = result.Size;
                    }
                }

                _pcCache[type] = result;
                return result;
            }

            private bool CheckPC(Type type, Candidate pc, ObjectAttr? attr)
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
            private Candidate? Place(Type type, bool pcMode, int start, int baseAlignment, int basePcSize, List<HiddenData>? baseHidden, Dictionary<string, int> offsets, ObjectAttr? attr)
            {
                var declared = GetLayoutFields(type, true);
                if (!pcMode) declared = declared.Where(f => !_removedFields.Contains(FieldKey(f.info))).ToList();

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
                    int offset = -1;

                    if (nst < basePcSize)
                    {
                        // Field redeclared by the editor inside the data of a base class
                        if (pcMode)
                        {
                            offset = nst;
                        }
                        else
                        {
                            HiddenData? hidden = baseHidden?.FindLast(h => nst >= h.PcOffset && nst < h.PcOffset + h.Size);
                            offset = hidden != null ? hidden.SwitchOffset + (nst - hidden.PcOffset) : start - (basePcSize - nst);
                            if (offset < 12) offset = -1;
                        }
                    }

                    if (offset < 0) offset = AlignUp(cursor, align);

                    offsets[FieldKey(info)] = offset;
                    cursor = Math.Max(cursor, offset + size);
                    alignment = Math.Max(alignment, align);
                }

                if (attr != null) alignment = Math.Max(alignment, attr.alignment);

                int dataSize = Math.Max(cursor, start);

                return new Candidate
                {
                    DataSize = dataSize,
                    RawEnd = dataSize,
                    Size = AlignUp(dataSize, alignment),
                    Alignment = alignment,
                    Offsets = offsets,
                    Hidden = baseHidden == null ? new List<HiddenData>() : baseHidden.ToList(),
                };
            }

            /// <summary>
            /// Layout taken directly from the CTR or NST offsets declared in the attributes
            /// </summary>
            private Candidate? FromAttributes(Type type, ObjectAttr? attr, bool useCTR)
            {
                int? size = useCTR ? attr?.size_ctr : attr?.size_nst;
                if (size == null) return null;

                var candidate = new Candidate { Size = size.Value, Alignment = Math.Max(8, attr!.alignment) };
                int dataSize = 12;

                foreach (var (info, nst, ctr) in GetLayoutFields(type, false))
                {
                    if (useCTR && _removedFields.Contains(FieldKey(info))) continue;

                    int? offset = useCTR ? ctr : nst;
                    if (offset == null) return null;

                    candidate.Offsets[FieldKey(info)] = offset.Value;
                    if (info.DeclaringType == typeof(igObject)) continue;

                    int fieldSize = Math.Max(FieldSize(info, info.DeclaringType ?? type), 0);
                    dataSize = Math.Max(dataSize, offset.Value + fieldSize);
                }

                candidate.DataSize = dataSize;
                candidate.RawEnd = dataSize;
                return candidate;
            }
        }

        // ------------------------------------------------------------------ field sizes and alignments

        private static int AlignUp(int value, int alignment) => alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

        /// <summary>
        /// Size of a field in bytes, or -1 for arrays of unknown length
        /// </summary>
        public static int FieldSize(FieldInfo field, Type owner)
        {
            lock (_lock)
            {
                if (_fieldSizeCache.TryGetValue((field, owner), out int cached)) return cached;

                Type type = field.FieldType;
                int size;

                if (type.IsArray)
                {
                    int length = ArrayLength(field, owner);
                    size = length < 0 ? -1 : length * ValueSize(type.GetElementType()!);
                }
                else
                {
                    size = ValueSize(type);
                }

                _fieldSizeCache[(field, owner)] = size;
                return size;
            }
        }

        private static int ValueSize(Type type)
        {
            if (type.IsEnum) return 4;
            if (type == typeof(Half)) return 2;
            if (type.IsAssignableTo(typeof(igObject)) || type.IsAssignableTo(typeof(Havok.hkReferencedObject))) return 8;
            if (type.IsAssignableTo(typeof(igMetaField)) || type.IsAssignableTo(typeof(Havok.hkObject))) return StructSize(type);
            return AttributeUtils.GetFieldSize(type, GameVersion.NST);
        }

        /// <summary>
        /// Size of an embedded structure. Classes without their own ObjectAttr
        /// (e.g. InlinedMemoryRef&lt;T&gt;) use the size of their base class.
        /// </summary>
        private static int StructSize(Type type)
        {
            for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                ObjectAttr? attr = GetObjectAttr(t);
                if (attr?.size_nst != null) return attr.size_nst.Value;
            }
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
            lock (_lock)
            {
                if (_alignCache.TryGetValue(type, out int cached)) return cached;

                string name = type.IsGenericType ? type.GetGenericTypeDefinition().Name : type.Name;
                if (_align16Names.Any(n => name.Contains(n)))
                {
                    _alignCache[type] = 16;
                    return 16;
                }

                int size = 0;
                try { size = StructSize(type); }
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
}
