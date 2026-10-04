using System.Collections;
using System.Text;
using Alchemy;

namespace NST
{
    /// <summary>
    /// Command line tools for the Nintendo Switch version of Crash NST.
    ///
    ///   NST.exe --switch layout   [report.txt]
    ///       Compares the computed Switch object sizes with the real ones (assets/switch_sizes.json).
    ///
    ///   NST.exe --switch verifica &lt;pc.pak&gt; &lt;switch_dump_folder&gt; [report.txt] [--max N]
    ///       For every .igz of the PC archive that also exists in the Switch game, reads the PC file
    ///       with the PC layout and the Switch file with the computed Switch layout, then compares
    ///       all the values. Also rewrites the PC file with the Switch layout and reads it back.
    ///
    ///   NST.exe --switch converti &lt;pc.pak&gt; &lt;switch_dump_folder&gt; &lt;output.pak&gt; [report.txt]
    ///       Builds a Switch archive: shared assets are taken from the Switch game, the files of
    ///       the level are converted from the PC version.
    /// </summary>
    public static class SwitchTools
    {
        private static readonly HashSet<string> _ignoredFields = ["__referenceCount", "__objectType", "_dynamicFieldMemory"];

        // Object types that hold platform-specific graphics data (not converted)
        private static readonly HashSet<string> _graphicsTypes =
        [
            "igImage2", "igGraphicsTexture", "igGraphicsVertexBuffer", "igGraphicsIndexBuffer",
            "igVertexBuffer", "igIndexBuffer", "igVertexFormat", "igMemoryCommandStream",
            "igGraphicsMaterial", "igModelData", "igModelDrawCallData", "igGraphicsObjectSet",
        ];

        public static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            try
            {
                string command = args[0].ToLowerInvariant();
                List<string> rest = args.Skip(1).ToList();

                int max = int.MaxValue;
                int maxIndex = rest.IndexOf("--max");
                if (maxIndex != -1 && maxIndex + 1 < rest.Count)
                {
                    max = int.Parse(rest[maxIndex + 1]);
                    rest.RemoveRange(maxIndex, 2);
                }

                switch (command)
                {
                    case "layout":
                        return Layout(rest.Count > 0 ? rest[0] : "switch_layout.txt");
                    case "verifica":
                        if (rest.Count < 2) break;
                        return Verify(rest[0], rest[1], rest.Count > 2 ? rest[2] : "switch_verifica.txt", max);
                    case "converti":
                        if (rest.Count < 3) break;
                        return Convert(rest[0], rest[1], rest[2], rest.Count > 3 ? rest[3] : "switch_converti.txt");
                }

                PrintUsage();
                return 1;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Errore: {e.Message}\n{e.StackTrace}");
                return 2;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Uso:");
            Console.WriteLine("  NST.exe --switch layout [report.txt]");
            Console.WriteLine("  NST.exe --switch verifica <file_pc.pak> <cartella_dump_switch> [report.txt] [--max N]");
            Console.WriteLine("  NST.exe --switch converti <file_pc.pak> <cartella_dump_switch> <output.pak> [report.txt]");
        }

        // ------------------------------------------------------------------ layout

        private static int Layout(string reportPath)
        {
            var report = new StringBuilder();
            int known = SwitchLayout.KnownSizeCount();
            report.AppendLine($"Dimensioni Switch note: {known}");

            var counts = new Dictionary<string, int>();
            var wrong = new List<string>();
            int missing = 0;

            foreach (string typeName in KnownTypeNames())
            {
                Type? type = FindType(typeName);
                if (type == null) { missing++; continue; }

                SwitchLayout.Layout? layout = SwitchLayout.Get(type);
                if (layout == null)
                {
                    Increment(counts, "non calcolabile");
                    wrong.Add($"{typeName}: non calcolabile");
                    continue;
                }

                string key = $"{layout.Source}, verifica PC {(layout.PcCheckPassed ? "ok" : "no")}, dimensione {(layout.MatchesKnownSize ? "giusta" : "SBAGLIATA")}";
                Increment(counts, key);

                if (!layout.MatchesKnownSize)
                {
                    wrong.Add($"{typeName}: calcolata {layout.Size}, reale {layout.KnownSize} ({layout.Source})");
                }
            }

            report.AppendLine($"Tipi non presenti nell'editor: {missing}");
            foreach (var (key, count) in counts.OrderByDescending(e => e.Value))
            {
                report.AppendLine($"  {count,6}  {key}");
            }
            report.AppendLine();
            report.AppendLine($"=== TIPI CON DIMENSIONE SBAGLIATA ({wrong.Count}) ===");
            foreach (string line in wrong) report.AppendLine("  " + line);

            File.WriteAllText(reportPath, report.ToString());
            Console.WriteLine(report.ToString());
            Console.WriteLine($"Rapporto scritto in {reportPath}");
            return 0;
        }

        private static IEnumerable<string> KnownTypeNames()
        {
            using Stream? stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("NST.assets.switch_sizes.json");
            if (stream == null) return [];
            var sizes = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(stream);
            return sizes?.Keys.OrderBy(k => k).ToList() ?? [];
        }

        // ------------------------------------------------------------------ verifica

        private class Stats
        {
            public Dictionary<string, int[]> Fields = [];          // "Type.field" -> [uguali, diversi]
            public Dictionary<string, string> Examples = [];       // "Type.field" -> esempio di differenza
            public Dictionary<string, int[]> Objects = [];         // tipo -> [confrontati, con differenze]
            public Dictionary<string, int[]> Sizes = [];           // tipo -> [dimensione calcolata, dimensione nel file Switch]
            public Dictionary<string, int[]> Roundtrip = [];       // "Type.field" -> [uguali, diversi] dopo riscrittura
            public Dictionary<string, string> RoundtripExamples = [];
            public Dictionary<string, int> Counters = [];
            public List<string> Errors = [];
            public List<string> CountMismatches = [];
        }

        private static int Verify(string pcPath, string switchDir, string reportPath, int max)
        {
            var stats = new Stats();

            Console.WriteLine("Indicizzo gli archivi Switch...");
            Dictionary<string, IgArchiveFile> index = IndexSwitch(switchDir, stats);
            Console.WriteLine($"  {index.Count} file");

            IgArchive pc = IgArchive.Open(pcPath);
            if (pc.GameVersion != GameVersion.NST)
            {
                Console.WriteLine($"Attenzione: {pcPath} non e' un archivio PC (versione {pc.GameVersion})");
            }

            (string from, string to)? rename = DetectRename(pc, index);
            int done = 0;

            foreach (IgArchiveFile file in pc.Files)
            {
                if (!file.IsIGZ()) continue;
                if (done >= max) break;

                IgArchiveFile? swFile = FindCounterpart(file.Path, index, rename);
                if (swFile == null)
                {
                    Increment(stats.Counters, "igz senza corrispondente Switch");
                    continue;
                }

                done++;
                if (done % 50 == 0) Console.WriteLine($"  {done} file...");

                try
                {
                    byte[] swData = swFile.Uncompress();
                    IgzFile pcIgz = file.ToIgzFile();
                    IgzFile swIgz = new IgzFile(swFile.Path, swData, GameVersion.NSX);

                    CompareSizes(swData, stats);
                    CompareFiles(file.Path, pcIgz, swIgz, stats.Fields, stats.Examples, stats.Objects, stats);
                    Increment(stats.Counters, "igz confrontati");
                }
                catch (Exception e)
                {
                    stats.Errors.Add($"{file.Path}: {e.Message}");
                    Increment(stats.Counters, "igz con errori di lettura");
                    continue;
                }

                // Rewrite the PC file with the Switch layout and read it back
                try
                {
                    IgzFile original = file.ToIgzFile();
                    IgzFile toConvert = file.ToIgzFile();
                    toConvert.GameVersion = GameVersion.NSX;
                    byte[] converted = toConvert.Save();
                    IgzFile back = new IgzFile(file.Path, converted, GameVersion.NSX);

                    CompareFiles(file.Path, original, back, stats.Roundtrip, stats.RoundtripExamples, null, null);
                    Increment(stats.Counters, "igz riscritti e riletti");
                }
                catch (Exception e)
                {
                    stats.Errors.Add($"{file.Path} (riscrittura): {e.Message}");
                    Increment(stats.Counters, "igz con errori di riscrittura");
                }
            }

            WriteVerifyReport(reportPath, pcPath, switchDir, rename, stats);
            return 0;
        }

        private static Dictionary<string, IgArchiveFile> IndexSwitch(string switchDir, Stats? stats)
        {
            var index = new Dictionary<string, IgArchiveFile>();

            IEnumerable<string> paks = File.Exists(switchDir)
                ? new string[] { switchDir }
                : Directory.EnumerateFiles(switchDir, "*.pak", SearchOption.AllDirectories);

            foreach (string pakPath in paks.OrderBy(p => p))
            {
                try
                {
                    IgArchive archive = IgArchive.Open(pakPath);
                    foreach (IgArchiveFile file in archive.Files)
                    {
                        index.TryAdd(file.Path.ToLowerInvariant(), file);
                    }
                }
                catch (Exception e)
                {
                    stats?.Errors.Add($"Archivio Switch {Path.GetFileName(pakPath)}: {e.Message}");
                }
            }

            return index;
        }

        /// <summary>
        /// Levels declared in an archive (packages/generated/maps/[game]/[level]/[level]_pkg.igz)
        /// </summary>
        private static List<string> LevelNames(IEnumerable<string> paths)
        {
            List<string> levels = [];
            const string prefix = "packages/generated/maps/";
            const string suffix = "_pkg.igz";

            foreach (string path in paths)
            {
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

                string[] parts = path.Substring(prefix.Length).Split('/');
                if (parts.Length != 3) continue;

                levels.Add(parts[2].Substring(0, parts[2].Length - suffix.Length));
            }

            return levels;
        }

        /// <summary>
        /// A new level created from an original one (L112_RoadToNowhere_Custom from L112_RoadToNowhere)
        /// </summary>
        private static (string from, string to)? DetectRename(IgArchive pc, Dictionary<string, IgArchiveFile> index)
        {
            List<string> pcLevels = LevelNames(pc.Files.Select(f => f.Path));
            if (pcLevels.Count == 0) return null;

            string level = pcLevels[0];
            List<string> switchLevels = LevelNames(index.Keys);

            if (switchLevels.Any(l => l.Equals(level, StringComparison.OrdinalIgnoreCase))) return null;

            string? original = switchLevels
                .Where(l => level.StartsWith(l, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => l.Length)
                .FirstOrDefault();

            return original == null ? null : (level, original);
        }

        private static IgArchiveFile? FindCounterpart(string path, Dictionary<string, IgArchiveFile> index, (string from, string to)? rename)
        {
            List<string> candidates = [path];
            if (path.StartsWith("update/", StringComparison.OrdinalIgnoreCase)) candidates.Add(path.Substring(7));

            foreach (string candidate in candidates)
            {
                if (index.TryGetValue(candidate.ToLowerInvariant(), out IgArchiveFile? file)) return file;
            }

            if (rename != null)
            {
                foreach (string candidate in candidates)
                {
                    string renamed = ReplaceIgnoreCase(candidate, rename.Value.from, rename.Value.to);
                    if (index.TryGetValue(renamed.ToLowerInvariant(), out IgArchiveFile? file)) return file;
                }
            }

            return null;
        }

        private static string ReplaceIgnoreCase(string text, string from, string to)
        {
            return System.Text.RegularExpressions.Regex.Replace(text, System.Text.RegularExpressions.Regex.Escape(from), to.Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Compare the computed Switch sizes with the sizes stored in a Switch file (TMET + MTSZ)
        /// </summary>
        private static void CompareSizes(byte[] switchData, Stats stats)
        {
            foreach (var (typeName, realSize) in ReadTypeSizes(switchData))
            {
                if (stats.Sizes.ContainsKey(typeName)) continue;

                Type? type = FindType(typeName);
                int computed = type == null ? -1 : AttributeUtils.GetObjectSize(type, GameVersion.NSX);
                stats.Sizes[typeName] = [computed, realSize];
            }
        }

        /// <summary>
        /// Object types and sizes declared in an IGZ file (TMET and MTSZ fixups)
        /// </summary>
        public static List<(string, int)> ReadTypeSizes(byte[] data)
        {
            List<(string, int)> result = [];
            if (data.Length < 0x24 || BitConverter.ToUInt32(data, 0) != 0x49475A01) return result;

            int position = BitConverter.ToInt32(data, 0x18); // Offset of the fixups chunk
            List<string> types = [];
            List<int> sizes = [];

            for (int k = 0; k < 64 && position >= 0 && position + 16 <= data.Length; k++)
            {
                if (data[position] == 0 || data[position + 1] == 0 || data[position + 2] == 0 || data[position + 3] == 0) break;

                string tag = Encoding.ASCII.GetString(data, position, 4);
                int count = BitConverter.ToInt32(data, position + 4);
                int size = BitConverter.ToInt32(data, position + 8);
                int header = BitConverter.ToInt32(data, position + 12);
                if (size <= 0) break;

                int q = position + header;
                if (tag == "TMET")
                {
                    for (int i = 0; i < count && q < data.Length; i++)
                    {
                        int end = Array.IndexOf(data, (byte)0, q);
                        if (end < 0) break;
                        types.Add(Encoding.ASCII.GetString(data, q, end - q));
                        q = end + 1;
                        if (q % 2 == 1) q++;
                    }
                }
                else if (tag == "MTSZ")
                {
                    for (int i = 0; i < count && q + 4 * i + 4 <= data.Length; i++)
                    {
                        sizes.Add(BitConverter.ToInt32(data, q + 4 * i));
                    }
                }

                position += size;
            }

            for (int i = 0; i < Math.Min(types.Count, sizes.Count); i++) result.Add((types[i], sizes[i]));
            return result;
        }

        /// <summary>
        /// Find the editor class corresponding to a TMET type name
        /// </summary>
        private static Type? FindType(string typeName)
        {
            if (typeName.EndsWith("MetaField"))
            {
                Type? instance = Type.GetType("Alchemy." + typeName + "Instance");
                if (instance != null) return instance;
            }
            return Type.GetType("Alchemy." + typeName);
        }

        /// <summary>
        /// Match the objects of two versions of the same file and compare all their values
        /// </summary>
        private static void CompareFiles(string path, IgzFile a, IgzFile b,
            Dictionary<string, int[]> fields, Dictionary<string, string> examples,
            Dictionary<string, int[]>? objects, Stats? stats)
        {
            var pairs = MatchObjects(a.Objects, b.Objects);

            if (stats != null)
            {
                var countsA = a.Objects.GroupBy(o => o.GetType().Name).ToDictionary(g => g.Key, g => g.Count());
                var countsB = b.Objects.GroupBy(o => o.GetType().Name).ToDictionary(g => g.Key, g => g.Count());
                foreach (string type in countsA.Keys.Union(countsB.Keys))
                {
                    int ca = countsA.GetValueOrDefault(type);
                    int cb = countsB.GetValueOrDefault(type);
                    if (ca != cb && stats.CountMismatches.Count < 200)
                    {
                        stats.CountMismatches.Add($"{path}: {type} PC {ca}, Switch {cb}");
                    }
                }
            }

            foreach (var (objA, objB) in pairs)
            {
                string typeName = objA.GetType().Name;
                bool anyDifference = false;

                foreach (CachedFieldAttr field in AttributeUtils.GetAttributes(objA.GetType()).GetFields(GameVersion.NST))
                {
                    if (_ignoredFields.Contains(field.GetName())) continue;

                    string key = $"{typeName}.{field.GetName()}";
                    if (!fields.TryGetValue(key, out int[]? counter))
                    {
                        counter = [0, 0];
                        fields[key] = counter;
                    }

                    if (ValuesEqual(field.GetValue(objA), field.GetValue(objB), 0, out string why))
                    {
                        counter[0]++;
                    }
                    else
                    {
                        counter[1]++;
                        anyDifference = true;
                        if (!examples.ContainsKey(key))
                        {
                            examples[key] = $"{path} [{objA.ObjectName ?? "?"}]: {why}";
                        }
                    }
                }

                if (objects != null)
                {
                    if (!objects.TryGetValue(typeName, out int[]? counter))
                    {
                        counter = [0, 0];
                        objects[typeName] = counter;
                    }
                    counter[0]++;
                    if (anyDifference) counter[1]++;
                }
            }
        }

        private static List<(igObject, igObject)> MatchObjects(List<igObject> a, List<igObject> b)
        {
            List<(igObject, igObject)> pairs = [];
            var used = new HashSet<igObject>();

            var byName = new Dictionary<(Type, string), Queue<igObject>>();
            foreach (igObject obj in b)
            {
                if (obj.ObjectName == null) continue;
                var key = (obj.GetType(), obj.ObjectName);
                if (!byName.TryGetValue(key, out var queue))
                {
                    queue = new Queue<igObject>();
                    byName[key] = queue;
                }
                queue.Enqueue(obj);
            }

            List<igObject> unnamed = [];
            foreach (igObject obj in a)
            {
                if (obj.ObjectName != null && byName.TryGetValue((obj.GetType(), obj.ObjectName), out var queue) && queue.Count > 0)
                {
                    igObject match = queue.Dequeue();
                    pairs.Add((obj, match));
                    used.Add(match);
                }
                else
                {
                    unnamed.Add(obj);
                }
            }

            var byType = new Dictionary<Type, Queue<igObject>>();
            foreach (igObject obj in b)
            {
                if (used.Contains(obj)) continue;
                if (!byType.TryGetValue(obj.GetType(), out var queue))
                {
                    queue = new Queue<igObject>();
                    byType[obj.GetType()] = queue;
                }
                queue.Enqueue(obj);
            }

            foreach (igObject obj in unnamed)
            {
                if (byType.TryGetValue(obj.GetType(), out var queue) && queue.Count > 0)
                {
                    pairs.Add((obj, queue.Dequeue()));
                }
            }

            return pairs;
        }

        private static bool ValuesEqual(object? a, object? b, int depth, out string why)
        {
            why = "";

            if (a == null && b == null) return true;
            if (a == null || b == null)
            {
                why = $"{Show(a)} / {Show(b)}";
                return false;
            }
            if (depth > 8) return true;

            if (a is float fa && b is float fb)
            {
                if (fa.Equals(fb) || Math.Abs(fa - fb) <= 1e-5f * Math.Max(1f, Math.Abs(fa))) return true;
                why = $"{fa} / {fb}";
                return false;
            }
            if (a is double da && b is double db)
            {
                if (da.Equals(db) || Math.Abs(da - db) <= 1e-9 * Math.Max(1.0, Math.Abs(da))) return true;
                why = $"{da} / {db}";
                return false;
            }
            if (a is string || a.GetType().IsPrimitive || a.GetType().IsEnum || a is Half)
            {
                if (a.Equals(b)) return true;
                why = $"{Show(a)} / {Show(b)}";
                return false;
            }
            if (a is igObject oa && b is igObject ob)
            {
                bool same = oa.GetType() == ob.GetType()
                    && oa.ObjectName == ob.ObjectName
                    && oa.Reference?.ToString() == ob.Reference?.ToString();
                if (!same) why = $"{oa} / {ob}";
                return same;
            }
            if (a is igHandleMetaField ha && b is igHandleMetaField hb)
            {
                bool same = ha.Reference?.ToString() == hb.Reference?.ToString();
                if (!same) why = $"{ha.Reference} / {hb.Reference}";
                return same;
            }
            if (a is igRawRefMetaField) return true;

            if (a is IEnumerable ea && b is IEnumerable eb)
            {
                List<object?> la = ea.Cast<object?>().ToList();
                List<object?> lb = eb.Cast<object?>().ToList();
                if (la.Count != lb.Count)
                {
                    why = $"{la.Count} elementi / {lb.Count} elementi";
                    return false;
                }
                for (int i = 0; i < la.Count; i++)
                {
                    if (!ValuesEqual(la[i], lb[i], depth + 1, out string inner))
                    {
                        why = $"[{i}] {inner}";
                        return false;
                    }
                }
                return true;
            }

            if (a is igObjectBase ma && b is igObjectBase mb)
            {
                if (ma.GetType() != mb.GetType())
                {
                    why = $"{ma.GetType().Name} / {mb.GetType().Name}";
                    return false;
                }
                foreach (CachedFieldAttr field in AttributeUtils.GetAttributes(ma.GetType()).GetFields(GameVersion.NST))
                {
                    if (_ignoredFields.Contains(field.GetName())) continue;
                    if (!ValuesEqual(field.GetValue(ma), field.GetValue(mb), depth + 1, out string inner))
                    {
                        why = $"{field.GetName()}: {inner}";
                        return false;
                    }
                }
                return true;
            }

            if (a.Equals(b)) return true;
            why = $"{Show(a)} / {Show(b)}";
            return false;
        }

        private static string Show(object? value)
        {
            if (value == null) return "null";
            string text = value.ToString() ?? "";
            return text.Length > 60 ? text.Substring(0, 60) + "..." : text;
        }

        private static void WriteVerifyReport(string reportPath, string pcPath, string switchDir, (string from, string to)? rename, Stats stats)
        {
            var report = new StringBuilder();
            report.AppendLine("VERIFICA CONVERSIONE PC -> SWITCH");
            report.AppendLine($"archivio PC: {pcPath}");
            report.AppendLine($"dump Switch: {switchDir}");
            if (rename != null) report.AppendLine($"livello: {rename.Value.from} (originale {rename.Value.to})");
            report.AppendLine();

            foreach (var (key, count) in stats.Counters.OrderBy(e => e.Key)) report.AppendLine($"  {key}: {count}");

            int equal = stats.Fields.Values.Sum(v => v[0]);
            int different = stats.Fields.Values.Sum(v => v[1]);
            double percent = equal + different == 0 ? 0 : 100.0 * equal / (equal + different);
            report.AppendLine();
            report.AppendLine($"=== LETTURA: valori PC (struttura PC) contro valori Switch (struttura calcolata) ===");
            report.AppendLine($"campi uguali {equal}, diversi {different} ({percent:F2}% uguali)");

            int rtEqual = stats.Roundtrip.Values.Sum(v => v[0]);
            int rtDifferent = stats.Roundtrip.Values.Sum(v => v[1]);
            report.AppendLine($"=== RISCRITTURA: file PC riscritto con la struttura Switch e riletto ===");
            report.AppendLine($"campi uguali {rtEqual}, diversi {rtDifferent}");

            var wrongSizes = stats.Sizes.Where(e => e.Value[0] != e.Value[1]).OrderBy(e => e.Key).ToList();
            report.AppendLine($"=== DIMENSIONI: {stats.Sizes.Count} tipi nei file Switch, {wrongSizes.Count} con dimensione calcolata diversa ===");
            foreach (var (type, sizes) in wrongSizes.Take(150))
            {
                report.AppendLine($"  {type}: calcolata {sizes[0]}, reale {sizes[1]}");
            }

            report.AppendLine();
            report.AppendLine("=== CAMPI CON DIFFERENZE IN LETTURA (dal piu' frequente) ===");
            foreach (var (key, counter) in stats.Fields.Where(e => e.Value[1] > 0).OrderByDescending(e => e.Value[1]).Take(300))
            {
                report.AppendLine($"  {key}: uguali {counter[0]}, diversi {counter[1]}   es. {stats.Examples.GetValueOrDefault(key)}");
            }

            report.AppendLine();
            report.AppendLine("=== CAMPI CON DIFFERENZE IN RISCRITTURA ===");
            foreach (var (key, counter) in stats.Roundtrip.Where(e => e.Value[1] > 0).OrderByDescending(e => e.Value[1]).Take(150))
            {
                report.AppendLine($"  {key}: uguali {counter[0]}, diversi {counter[1]}   es. {stats.RoundtripExamples.GetValueOrDefault(key)}");
            }

            report.AppendLine();
            report.AppendLine("=== OGGETTI PER TIPO (confrontati / con differenze) ===");
            foreach (var (type, counter) in stats.Objects.OrderByDescending(e => e.Value[1]).ThenBy(e => e.Key).Take(400))
            {
                report.AppendLine($"  {type}: {counter[0]} / {counter[1]}");
            }

            report.AppendLine();
            report.AppendLine($"=== NUMERO DI OGGETTI DIVERSO ({stats.CountMismatches.Count}) ===");
            foreach (string line in stats.CountMismatches.Take(200)) report.AppendLine("  " + line);

            report.AppendLine();
            report.AppendLine($"=== ERRORI ({stats.Errors.Count}) ===");
            foreach (string line in stats.Errors.Take(300)) report.AppendLine("  " + line);

            File.WriteAllText(reportPath, report.ToString());
            Console.WriteLine($"Campi uguali in lettura: {percent:F2}%  (rapporto completo: {reportPath})");
        }

        // ------------------------------------------------------------------ converti

        private static int Convert(string pcPath, string switchDir, string outputPath, string reportPath)
        {
            var report = new StringBuilder();
            report.AppendLine("CONVERSIONE PC -> SWITCH");
            report.AppendLine($"archivio PC: {pcPath}");
            report.AppendLine($"output: {outputPath}");

            Console.WriteLine("Indicizzo gli archivi Switch...");
            Dictionary<string, IgArchiveFile> index = IndexSwitch(switchDir, null);

            IgArchive pc = IgArchive.Open(pcPath);
            List<string> levels = LevelNames(pc.Files.Select(f => f.Path));
            report.AppendLine($"livelli nel file: {string.Join(", ", levels)}");
            report.AppendLine();

            IgArchive output = new IgArchive(outputPath, GameVersion.NSX);
            var counters = new Dictionary<string, int>();
            var lines = new List<string>();

            foreach (IgArchiveFile file in pc.Files)
            {
                string path = file.Path;
                bool levelFile = levels.Any(l => path.Contains(l, StringComparison.OrdinalIgnoreCase));
                index.TryGetValue(path.ToLowerInvariant(), out IgArchiveFile? original);

                try
                {
                    // Shared asset that exists in the Switch game: use the Switch version
                    if (!levelFile && original != null)
                    {
                        output.AddFile(original.Clone());
                        Increment(counters, "presi dagli originali Switch");
                        continue;
                    }

                    if (file.IsIGZ())
                    {
                        IgzFile igz = file.ToIgzFile();
                        string? graphics = igz.Objects.Select(o => o.GetType().Name).FirstOrDefault(IsGraphicsType);

                        if (graphics != null)
                        {
                            if (original != null)
                            {
                                output.AddFile(original.Clone());
                                Increment(counters, "grafica del livello presa dagli originali Switch");
                                lines.Add($"grafica, originale Switch: {path}");
                            }
                            else
                            {
                                Increment(counters, "grafica NON convertibile (file saltato)");
                                lines.Add($"SALTATO (grafica PC, {graphics}): {path}");
                            }
                            continue;
                        }

                        igz.GameVersion = GameVersion.NSX;
                        IgArchiveFile converted = new IgArchiveFile(path, GameVersion.NSX);
                        converted.SetData(igz.Save());
                        output.AddFile(converted);
                        Increment(counters, "igz convertiti");
                        continue;
                    }

                    // Other files (Havok collisions, sounds...): not converted yet
                    if (original != null && file.IsHKX())
                    {
                        output.AddFile(original.Clone());
                        Increment(counters, "collisioni prese dagli originali Switch");
                        lines.Add($"collisione originale Switch (modifiche PC perse): {path}");
                        continue;
                    }

                    IgArchiveFile copy = new IgArchiveFile(path, GameVersion.NSX);
                    copy.SetData(file.Uncompress());
                    output.AddFile(copy);
                    Increment(counters, file.IsHKX() ? "collisioni PC copiate senza conversione" : "altri file copiati senza conversione");
                    lines.Add($"copiato senza conversione: {path}");
                }
                catch (Exception e)
                {
                    Increment(counters, "errori");
                    lines.Add($"ERRORE {path}: {e.Message}");
                }
            }

            output.Save(outputPath);

            foreach (var (key, count) in counters.OrderBy(e => e.Key)) report.AppendLine($"  {key}: {count}");
            report.AppendLine();
            foreach (string line in lines) report.AppendLine("  " + line);

            File.WriteAllText(reportPath, report.ToString());
            Console.WriteLine(report.ToString());
            Console.WriteLine($"Archivio Switch scritto in {outputPath}");
            return 0;
        }

        private static bool IsGraphicsType(string typeName) => _graphicsTypes.Contains(typeName) || typeName.EndsWith("Material");

        private static void Increment(Dictionary<string, int> counters, string key)
        {
            counters[key] = counters.GetValueOrDefault(key) + 1;
        }
    }
}
