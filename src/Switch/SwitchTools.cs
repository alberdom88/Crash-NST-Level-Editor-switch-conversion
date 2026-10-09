using System.Collections;
using System.Reflection;
using System.Text;
using Alchemy;

namespace NST
{
    /// <summary>
    /// Command line tools for the Nintendo Switch version of Crash NST.
    ///
    ///   NST.exe --switch layout [report.txt]
    ///       Compares the computed Switch object sizes with the real ones (assets/switch_sizes.json)
    ///       and lists the fields that only exist on PC.
    ///
    ///   NST.exe --switch struttura &lt;switch_dump_folder&gt; [report.txt] [--pak name] [--max N]
    ///       Reads the Switch game files with the computed layout and lists the object bytes that
    ///       are not covered by any known field (missing or misplaced fields).
    ///
    ///   NST.exe --switch verifica &lt;pc.pak&gt; &lt;switch_dump_folder&gt; [report.txt] [--max N]
    ///       For every .igz of the PC archive that also exists in the Switch game, reads the PC file
    ///       with the PC layout and the Switch file with the computed Switch layout, then compares
    ///       all the values. Also rewrites the PC file with the Switch layout and reads it back.
    ///
    ///   NST.exe --switch riscrivi &lt;switch.pak&gt; &lt;output.pak&gt; [report.txt] [--igz nessuno|maps|tutti]
    ///       Rewrites a Switch archive with the editor, optionally rewriting its igz files
    ///       (test of the archive and igz writers in the game).
    ///
    ///   NST.exe --switch converti &lt;pc.pak&gt; &lt;switch_dump_folder&gt; &lt;output.pak&gt; [report.txt]
    ///           [--come-originale] [--pc-originali &lt;pc_archives_folder&gt;]
    ///       Builds a Switch archive: unmodified assets are taken from the Switch game, the files of
    ///       the level are converted from the PC version. --come-originale gives the level back the
    ///       name of the original level, so that the archive can replace it.
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

        private class Options
        {
            public int Max = int.MaxValue;
            public string? PakFilter;
            public string? PcOriginals;
            public bool RenameBack;
            public string IgzMode = "maps";
            public string? ReplaceLevel;
            public bool WithoutBase;
            public bool NewLevel;
            public string? BaseLevel;
            // File di altri livelli copiati dall'editor: "converti" (copie PC convertite, come fino alla v12),
            // "originali" (originali Switch), "originali+dipendenze" (originali Switch con tutte le loro dipendenze)
            public string OtherLevels = "converti";
            // --nuovo: livello originale Switch da cui prendere la zone info (cambia solo il nome); "pc" = quella
            // dell'editor convertita (non funziona: il gioco si blocca). Vuoto = scelta automatica
            public string? ZoneInfoFrom;
            // --nuovo: "proprio" = voce di salvataggio nuova con il nome del livello (provato con Level 3);
            // "originale" = la voce di salvataggio del livello da cui viene la zone info
            public string SaveMode = "proprio";
            // converti: file del PC da non mettere nell'archivio (testo contenuto nel percorso, per esempio Octane)
            public List<string> Exclude = [];
            // converti: dump di Crash Team Racing Nitro-Fueled per Switch, da cui prendere la grafica che manca (prova)
            public string? CtrDump;
            // converti: toglie l'intro dei livelli di Crash 3 (Crash che esce dal portale), aggiunta dall'editor
            public bool WithoutIntro;
            // converti: aggiunge l'intro dei livelli di Crash 3 se il livello non ce l'ha (presa da Gone Tomorrow,
            // come fa l'editor); automatica con --gioco crash3 per un livello di un altro gioco
            public bool AddIntro;
            // --nuovo: archivio in cui registrare il livello: "update" (update.pak, come l'editor PC) o
            // "chunkinfos" (copia di chunkInfos.pak, dove il gioco tiene le zone info dei suoi livelli)
            public string RegisterIn = "update";
            // --nuovo: gioco del livello ("crash1", "crash2", "crash3") al posto di quello scelto nell'editor (Crash Mode)
            public string? Game;
            // --nuovo: memoria del livello nella zone info: "modello" (quella del livello originale da cui viene la
            // zone info), "max" (la piu' grande tra i livelli originali) o il nome di un livello originale
            public string Memory = "modello";
        }

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
                List<string> rest = args.Skip(1).Select(CleanPath).ToList();
                Options options = ParseOptions(rest);

                switch (command)
                {
                    case "layout":
                        return LayoutReport(rest.Count > 0 ? rest[0] : "switch_layout.txt");
                    case "struttura":
                        if (rest.Count < 1) break;
                        return Structure(rest[0], rest.Count > 1 ? rest[1] : "switch_struttura.txt", options);
                    case "verifica":
                        if (rest.Count < 2) break;
                        return Verify(rest[0], rest[1], rest.Count > 2 ? rest[2] : "switch_verifica.txt", options.Max);
                    case "riscrivi":
                        if (rest.Count < 2) break;
                        return Rewrite(rest[0], rest[1], rest.Count > 2 ? rest[2] : "switch_riscrivi.txt", options);
                    case "converti":
                        if (rest.Count < 3) break;
                        return Convert(rest[0], rest[1], rest[2], rest.Count > 3 ? rest[3] : "switch_converti.txt", options);
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

        private static string CleanPath(string value) => value.Trim().Trim('"');

        private static Options ParseOptions(List<string> rest)
        {
            var options = new Options();
            int i = 0;
            while (i < rest.Count)
            {
                string name = rest[i].ToLowerInvariant();
                bool hasValue = i + 1 < rest.Count;

                if (name == "--max" && hasValue) { options.Max = int.Parse(rest[i + 1]); rest.RemoveRange(i, 2); }
                else if (name == "--pak" && hasValue) { options.PakFilter = rest[i + 1]; rest.RemoveRange(i, 2); }
                else if (name == "--pc-originali" && hasValue) { options.PcOriginals = rest[i + 1]; rest.RemoveRange(i, 2); }
                else if (name == "--come-originale") { options.RenameBack = true; rest.RemoveAt(i); }
                else if (name == "--igz" && hasValue) { options.IgzMode = rest[i + 1]; rest.RemoveRange(i, 2); }
                else if (name == "--sostituisci" && hasValue) { options.ReplaceLevel = rest[i + 1]; rest.RemoveRange(i, 2); }
                else if (name == "--senza-base") { options.WithoutBase = true; rest.RemoveAt(i); }
                else if (name == "--nuovo") { options.NewLevel = true; rest.RemoveAt(i); }
                else if (name == "--base" && hasValue) { options.BaseLevel = rest[i + 1]; rest.RemoveRange(i, 2); }
                else if (name == "--altri-livelli" && hasValue) { options.OtherLevels = rest[i + 1].ToLowerInvariant(); rest.RemoveRange(i, 2); }
                else if (name == "--zoneinfo-da" && hasValue) { options.ZoneInfoFrom = rest[i + 1]; rest.RemoveRange(i, 2); }
                else if (name == "--registra-in" && hasValue) { options.RegisterIn = rest[i + 1].ToLowerInvariant(); rest.RemoveRange(i, 2); }
                else if (name == "--salvataggio" && hasValue) { options.SaveMode = rest[i + 1].ToLowerInvariant(); rest.RemoveRange(i, 2); }
                else if (name == "--escludi" && hasValue) { options.Exclude.Add(rest[i + 1]); rest.RemoveRange(i, 2); }
                else if (name == "--ctr" && hasValue) { options.CtrDump = rest[i + 1]; rest.RemoveRange(i, 2); }
                else if (name == "--senza-intro") { options.WithoutIntro = true; rest.RemoveAt(i); }
                else if (name == "--aggiungi-intro") { options.AddIntro = true; rest.RemoveAt(i); }
                else if (name == "--gioco" && hasValue) { options.Game = NormalizeGame(rest[i + 1]); rest.RemoveRange(i, 2); }
                else if (name == "--memoria" && hasValue) { options.Memory = rest[i + 1]; rest.RemoveRange(i, 2); }
                else i++;
            }
            return options;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Uso:");
            Console.WriteLine("  NST.exe --switch layout [report.txt]");
            Console.WriteLine("  NST.exe --switch struttura <cartella_dump_switch> [report.txt] [--pak nome] [--max N]");
            Console.WriteLine("  NST.exe --switch verifica <file_pc.pak> <cartella_dump_switch> [report.txt] [--max N]");
            Console.WriteLine("  NST.exe --switch riscrivi <archivio_switch.pak> <output.pak> [report.txt] [--igz nessuno|maps|tutti]");
            Console.WriteLine("  NST.exe --switch converti <file_pc.pak> <cartella_dump_switch> <output.pak> [report.txt] [--come-originale | --sostituisci <livello> | --nuovo] [--base <livello>] [--senza-base] [--altri-livelli converti|originali|originali+dipendenze] [--zoneinfo-da <livello>|pc] [--salvataggio originale|proprio] [--escludi <testo>]... [--ctr <cartella_dump_ctr_switch>] [--senza-intro | --aggiungi-intro] [--registra-in update|chunkinfos] [--gioco crash1|crash2|crash3] [--memoria modello|max|<livello>] [--pc-originali <cartella_archives_pc>]");
        }

        private static void Increment(Dictionary<string, int> counters, string key)
        {
            counters[key] = counters.GetValueOrDefault(key) + 1;
        }

        private static bool IsIgnoredField(CachedFieldAttr field)
        {
            return _ignoredFields.Contains(field.GetName()) || SwitchLayout.IsRemoved(field.GetFieldInfo());
        }

        /// <summary>
        /// Size of a field in the Switch layout (the reference count of igObject is 32-bit)
        /// </summary>
        private static int FieldBytes(CachedFieldAttr field, Type owner)
        {
            FieldInfo info = field.GetFieldInfo();
            if (info.DeclaringType == typeof(igObject) && info.Name == "__referenceCount") return 4;
            return Math.Max(SwitchLayout.FieldSize(info, owner), 0);
        }

        // ------------------------------------------------------------------ layout

        private static int LayoutReport(string reportPath)
        {
            var report = new StringBuilder();
            IReadOnlyDictionary<string, int> known = SwitchLayout.KnownSizes();
            report.AppendLine($"Dimensioni Switch note: {known.Count}");

            var counts = new Dictionary<string, int>();
            var wrong = new List<string>();
            var ranges = new List<string>();
            int missing = 0;

            foreach (string typeName in known.Keys.OrderBy(k => k))
            {
                Type? type = SwitchLayout.FindType(typeName);
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
                    string padded = layout.Padded ? ", completata con byte vuoti" : "";
                    wrong.Add($"{typeName}: calcolata {layout.ComputedSize}, reale {layout.KnownSize} ({layout.Source}{padded})");
                }

                try
                {
                    string? problem = CheckFieldRanges(type, layout.Size);
                    if (problem != null) ranges.Add($"{typeName}: {problem}");
                }
                catch (Exception e)
                {
                    ranges.Add($"{typeName}: errore {e.Message}");
                }
            }

            report.AppendLine($"Tipi non presenti nell'editor: {missing}");
            foreach (var (key, count) in counts.OrderByDescending(e => e.Value))
            {
                report.AppendLine($"  {count,6}  {key}");
            }

            List<string> removed = SwitchLayout.GetRemovalLog();
            report.AppendLine();
            report.AppendLine($"=== CAMPI SOLO PC (tolti nella versione Switch): {removed.Count} gruppi ===");
            foreach (string line in removed) report.AppendLine("  " + line);

            report.AppendLine();
            report.AppendLine($"=== TIPI CON DIMENSIONE SBAGLIATA ({wrong.Count}) ===");
            foreach (string line in wrong) report.AppendLine("  " + line);

            report.AppendLine();
            report.AppendLine($"=== CAMPI SOVRAPPOSTI O FUORI DALL'OGGETTO ({ranges.Count}) ===");
            foreach (string line in ranges.Take(300)) report.AppendLine("  " + line);

            List<string> messages = SwitchLayout.GetMessages();
            report.AppendLine();
            report.AppendLine($"=== MESSAGGI ({messages.Count}) ===");
            foreach (string line in messages.Take(200)) report.AppendLine("  " + line);

            File.WriteAllText(reportPath, report.ToString());
            Console.WriteLine(report.ToString());
            Console.WriteLine($"Rapporto scritto in {reportPath}");
            return 0;
        }

        /// <summary>
        /// Check that the Switch fields of a type don't overlap and fit in the object
        /// </summary>
        private static string? CheckFieldRanges(Type type, int size)
        {
            var fields = new List<(string name, int start, int end)>();
            foreach (CachedFieldAttr field in AttributeUtils.GetAttributes(type).GetFields(GameVersion.NSX))
            {
                int length = FieldBytes(field, type);
                if (length <= 0) continue;
                int start = field.GetOffset(GameVersion.NSX);
                fields.Add((field.GetName(), start, start + length));
            }

            fields.Sort((a, b) => a.start.CompareTo(b.start));

            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].end > size) return $"{fields[i].name} [{fields[i].start}-{fields[i].end}) oltre la dimensione {size}";
                if (i > 0 && fields[i].start < fields[i - 1].end) return $"{fields[i - 1].name} [{fields[i - 1].start}-{fields[i - 1].end}) sovrapposto a {fields[i].name} [{fields[i].start}-{fields[i].end})";
            }
            return null;
        }

        // ------------------------------------------------------------------ struttura

        private class Coverage
        {
            public string EditorType = "";
            public int RealSize;
            public int ComputedSize;
            public int Objects;
            public int WithUncovered;
            public int WithOutside;
            public string? Example;
            public Dictionary<int, int> UncoveredOffsets = [];
        }

        private static IEnumerable<string> PakFiles(string folder)
        {
            if (File.Exists(folder)) return [folder];
            return Directory.EnumerateFiles(folder, "*.pak", SearchOption.AllDirectories).OrderBy(p => p);
        }

        private static int Structure(string switchDir, string reportPath, Options options)
        {
            FieldInfo? objectsField = typeof(IgzReader).GetField("_objects", BindingFlags.NonPublic | BindingFlags.Instance);
            if (objectsField == null)
            {
                Console.WriteLine("Errore: IgzReader._objects non trovato");
                return 2;
            }

            var types = new Dictionary<string, Coverage>();
            var counters = new Dictionary<string, int>();
            var errors = new List<string>();
            var seen = new HashSet<string>();
            int done = 0;

            Console.WriteLine("Leggo i file della Switch...");

            foreach (string pakPath in PakFiles(switchDir))
            {
                if (options.PakFilter != null && !Path.GetFileName(pakPath).Contains(options.PakFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (done >= options.Max) break;

                IgArchive archive;
                try
                {
                    archive = IgArchive.Open(pakPath);
                }
                catch (Exception e)
                {
                    errors.Add($"Archivio {Path.GetFileName(pakPath)}: {e.Message}");
                    continue;
                }

                Increment(counters, "archivi");

                foreach (IgArchiveFile file in archive.Files)
                {
                    if (!file.IsIGZ() || !seen.Add(file.Path.ToLowerInvariant())) continue;
                    if (done >= options.Max) break;

                    done++;
                    if (done % 250 == 0) Console.WriteLine($"  {done} file...");

                    try
                    {
                        CheckCoverage(file.Path, file.Uncompress(), objectsField, types);
                        Increment(counters, "igz letti");
                    }
                    catch (Exception e)
                    {
                        if (errors.Count < 1000) errors.Add($"{file.Path}: {e.GetType().Name}: {e.Message}");
                        Increment(counters, "igz con errori di lettura");
                    }
                }
            }

            WriteStructureReport(reportPath, switchDir, counters, types, errors);
            return 0;
        }

        private static void CheckCoverage(string path, byte[] data, FieldInfo objectsField, Dictionary<string, Coverage> types)
        {
            var reader = new IgzReader(new MemoryStream(data), GameVersion.NSX);
            var objects = (Dictionary<int, igObject>)objectsField.GetValue(reader)!;
            List<(string, int)> typeSizes = ReadTypeSizes(data);

            foreach ((int offset, igObject obj) in objects)
            {
                if (offset < 0 || offset + 4 > data.Length) continue;

                int typeIndex = BitConverter.ToInt32(data, offset);
                if (typeIndex < 0 || typeIndex >= typeSizes.Count) continue;

                var (typeName, realSize) = typeSizes[typeIndex];
                if (realSize <= 0) continue;

                Type objectType = obj.GetType();
                if (!types.TryGetValue(typeName, out Coverage? coverage))
                {
                    coverage = new Coverage
                    {
                        EditorType = objectType.Name,
                        RealSize = realSize,
                        ComputedSize = AttributeUtils.GetObjectSize(objectType, GameVersion.NSX),
                    };
                    types[typeName] = coverage;
                }

                coverage.Objects++;

                bool[] covered = new bool[realSize];
                bool outside = false;

                foreach (CachedFieldAttr field in AttributeUtils.GetAttributes(objectType).GetFields(GameVersion.NSX))
                {
                    int start = field.GetOffset(GameVersion.NSX);
                    int length = FieldBytes(field, objectType);

                    for (int i = start; i < start + length; i++)
                    {
                        if (i < 0 || i >= realSize) { outside = true; break; }
                        covered[i] = true;
                    }
                }

                // Dynamic objects (VSC data...) store their own fields after the base object
                bool dynamicType = SwitchLayout.TypeName(objectType) != typeName;
                if (outside && !dynamicType) coverage.WithOutside++;

                bool uncovered = false;
                for (int i = 0; i < realSize && offset + i < data.Length; i++)
                {
                    if (covered[i] || data[offset + i] == 0) continue;
                    uncovered = true;
                    coverage.UncoveredOffsets[i] = coverage.UncoveredOffsets.GetValueOrDefault(i) + 1;
                }

                if (uncovered)
                {
                    coverage.WithUncovered++;
                    coverage.Example ??= $"{path} @0x{offset:X}";
                }
            }
        }

        /// <summary>
        /// Group consecutive offsets: "0x10-0x13 (x25)"
        /// </summary>
        private static string FormatRanges(Dictionary<int, int> offsets, int maxRanges)
        {
            var groups = new List<(int start, int end, int count)>();
            foreach (int offset in offsets.Keys.OrderBy(o => o))
            {
                int count = offsets[offset];
                if (groups.Count > 0 && groups[^1].end == offset - 1)
                {
                    var last = groups[^1];
                    groups[^1] = (last.start, offset, Math.Max(last.count, count));
                }
                else
                {
                    groups.Add((offset, offset, count));
                }
            }

            return string.Join(", ", groups
                .OrderByDescending(g => g.count)
                .Take(maxRanges)
                .OrderBy(g => g.start)
                .Select(g => g.start == g.end ? $"{g.start} (x{g.count})" : $"{g.start}-{g.end} (x{g.count})"));
        }

        private static void WriteStructureReport(string reportPath, string switchDir, Dictionary<string, int> counters, Dictionary<string, Coverage> types, List<string> errors)
        {
            var report = new StringBuilder();
            report.AppendLine("STRUTTURA DEGLI OGGETTI SWITCH (byte non coperti dai campi conosciuti)");
            report.AppendLine($"dump Switch: {switchDir}");
            report.AppendLine();

            foreach (var (key, count) in counters.OrderBy(e => e.Key)) report.AppendLine($"  {key}: {count}");

            int objects = types.Values.Sum(t => t.Objects);
            var withData = types.Where(e => e.Value.WithUncovered > 0).OrderByDescending(e => e.Value.WithUncovered).ToList();
            var outside = types.Where(e => e.Value.WithOutside > 0).OrderByDescending(e => e.Value.WithOutside).ToList();
            var sizes = types.Where(e => e.Value.ComputedSize != e.Value.RealSize).OrderBy(e => e.Key).ToList();

            report.AppendLine();
            report.AppendLine($"tipi: {types.Count}, oggetti: {objects}");
            report.AppendLine($"tipi completamente coperti: {types.Count - withData.Count}");
            report.AppendLine($"tipi con byte non coperti: {withData.Count} (oggetti: {withData.Sum(e => e.Value.WithUncovered)})");
            report.AppendLine($"tipi con campi fuori dall'oggetto: {outside.Count}");
            report.AppendLine($"tipi con dimensione calcolata diversa: {sizes.Count}");

            report.AppendLine();
            report.AppendLine("=== TIPI CON BYTE NON COPERTI (tipo [classe editor] dimensione: oggetti con byte non coperti / oggetti, posizioni) ===");
            foreach (var (name, coverage) in withData.Take(600))
            {
                string editor = coverage.EditorType == name ? "" : $" [{coverage.EditorType}]";
                report.AppendLine($"  {name}{editor} {coverage.RealSize}: {coverage.WithUncovered}/{coverage.Objects}, byte {FormatRanges(coverage.UncoveredOffsets, 6)}   es. {coverage.Example}");
            }

            report.AppendLine();
            report.AppendLine("=== TIPI CON CAMPI FUORI DALL'OGGETTO ===");
            foreach (var (name, coverage) in outside.Take(300))
            {
                report.AppendLine($"  {name}: {coverage.WithOutside}/{coverage.Objects} (dimensione reale {coverage.RealSize}, calcolata {coverage.ComputedSize})");
            }

            report.AppendLine();
            report.AppendLine("=== TIPI CON DIMENSIONE CALCOLATA DIVERSA ===");
            foreach (var (name, coverage) in sizes.Take(300))
            {
                report.AppendLine($"  {name}: calcolata {coverage.ComputedSize}, reale {coverage.RealSize} ({coverage.Objects} oggetti)");
            }

            report.AppendLine();
            report.AppendLine($"=== ERRORI ({errors.Count}) ===");
            foreach (string line in errors.Take(300)) report.AppendLine("  " + line);

            File.WriteAllText(reportPath, report.ToString());
            Console.WriteLine($"Tipi completamente coperti: {types.Count - withData.Count}/{types.Count}  (rapporto completo: {reportPath})");
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
            Dictionary<string, IgArchiveFile> index = IndexArchives(switchDir, stats.Errors);
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

        /// <summary>
        /// All the files of the archives of a folder (or of a single archive), by lowercase path
        /// </summary>
        private static Dictionary<string, IgArchiveFile> IndexArchives(string folder, List<string>? errors)
        {
            var index = new Dictionary<string, IgArchiveFile>();

            foreach (string pakPath in PakFiles(folder))
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
                    errors?.Add($"Archivio {Path.GetFileName(pakPath)}: {e.Message}");
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
        /// A new level created from an original one (L112_RoadToNowhere_Custom from L112_RoadToNowhere).
        /// Both names keep the case used in the PC archive.
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

            if (original == null) return null;
            return (level, level.Substring(0, original.Length));
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

                Type? type = SwitchLayout.FindType(typeName);
                int computed = type == null ? -1 : AttributeUtils.GetObjectSize(type, GameVersion.NSX);
                stats.Sizes[typeName] = [computed, realSize];
            }
        }

        /// <summary>
        /// Object types and sizes declared in an IGZ file (TMET and MTSZ fixups), in TMET order
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
                    if (IsIgnoredField(field)) continue;

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
                    if (IsIgnoredField(field)) continue;
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

        // ------------------------------------------------------------------ indice degli archivi Switch

        /// <summary>
        /// Files of the Switch game: by path, by archive, and by name inside each archive
        /// </summary>
        private class ArchiveIndex
        {
            public Dictionary<string, IgArchiveFile> ByPath = [];                       // lowercase path -> first file
            public Dictionary<string, List<IgArchiveFile>> ByArchive = [];              // lowercase archive name -> files
            public Dictionary<IgArchiveFile, string> ArchiveOf = [];                    // file -> lowercase archive name
            private readonly Dictionary<string, Dictionary<string, List<IgArchiveFile>>> _names = [];

            public static ArchiveIndex Build(string folder, List<string>? errors)
            {
                var index = new ArchiveIndex();

                foreach (string pakPath in PakFiles(folder))
                {
                    try
                    {
                        IgArchive archive = IgArchive.Open(pakPath);
                        string key = Path.GetFileNameWithoutExtension(pakPath).ToLowerInvariant();
                        if (!index.ByArchive.TryGetValue(key, out List<IgArchiveFile>? list))
                        {
                            list = [];
                            index.ByArchive[key] = list;
                        }

                        foreach (IgArchiveFile file in archive.Files)
                        {
                            index.ByPath.TryAdd(file.Path.ToLowerInvariant(), file);
                            index.ArchiveOf[file] = key;
                            list.Add(file);
                        }
                    }
                    catch (Exception e)
                    {
                        errors?.Add($"Archivio {Path.GetFileName(pakPath)}: {e.Message}");
                    }
                }

                return index;
            }

            /// <summary>
            /// Files of an archive by namespace (file name without extension, lowercase)
            /// </summary>
            public Dictionary<string, List<IgArchiveFile>> Names(string archive)
            {
                if (_names.TryGetValue(archive, out var cached)) return cached;

                var names = new Dictionary<string, List<IgArchiveFile>>();
                foreach (IgArchiveFile file in ByArchive.GetValueOrDefault(archive) ?? [])
                {
                    string lower = file.Path.ToLowerInvariant();
                    if (lower.StartsWith("maps/") || lower.StartsWith("packages/") || lower.StartsWith("update/") || lower.Contains("staticcollision")) continue;

                    foreach (string name in NameAliases(NamespaceUtils.GetFileName(file.Path, false).ToLowerInvariant()))
                    {
                        if (!names.TryGetValue(name, out List<IgArchiveFile>? list))
                        {
                            list = [];
                            names[name] = list;
                        }
                        list.Add(file);
                    }
                }

                _names[archive] = names;
                return names;
            }
        }

        /// <summary>
        /// Names under which a file can be referenced (same rules as the editor)
        /// </summary>
        private static IEnumerable<string> NameAliases(string name)
        {
            yield return name;

            string alias = name;
            if (alias.StartsWith("shared")) alias = alias.Substring(6);
            if (alias.EndsWith("_character")) alias = alias.Substring(0, alias.Length - 10);
            else if (alias.EndsWith("_behavior")) alias = alias.Substring(0, alias.Length - 9);
            else if (alias.EndsWith("_script")) alias = alias.Substring(0, alias.Length - 7);

            if (alias != name && alias.Length > 0) yield return alias;
        }

        /// <summary>
        /// Namespaces referenced by an igz file (TDEP dependencies and named references),
        /// read from the fixups only: it doesn't depend on the object layouts
        /// </summary>
        private static HashSet<string> RawDependencies(byte[] data)
        {
            var names = new HashSet<string>();
            if (data.Length < 0x24 || BitConverter.ToUInt32(data, 0) != 0x49475A01) return names;

            using var reader = new BinaryReader(new MemoryStream(data));
            reader.BaseStream.Position = BitConverter.ToInt32(data, 0x18);
            FixupCollection fixups = FixupCollection.Parse(reader);

            void Add(string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                names.Add(value.ToLowerInvariant());
                string fileName = NamespaceUtils.GetFileName(value, false).ToLowerInvariant();
                if (fileName.Length > 0) names.Add(fileName);
            }

            foreach (TDEP_Fixup.TDEP_Item item in fixups.TDEP)
            {
                Add(item.name);
                Add(item.path);
            }

            foreach (NamedReference reference in fixups.handleReferences) Add(reference.namespaceName);
            foreach (NamedReference reference in fixups.objectReferences) Add(reference.namespaceName);
            foreach (NamedReference reference in fixups.exidReferences) Add(reference.namespaceName);

            return names;
        }

        // ------------------------------------------------------------------ riscrivi

        /// <summary>
        /// Rewrite a Switch archive with the editor (test of the archive and igz writers)
        /// </summary>
        private static int Rewrite(string inputPath, string outputPath, string reportPath, Options options)
        {
            string mode = options.IgzMode.ToLowerInvariant();
            var report = new StringBuilder();
            report.AppendLine("RISCRITTURA DI UN ARCHIVIO SWITCH");
            report.AppendLine($"archivio: {inputPath}");
            report.AppendLine($"output: {outputPath}");
            report.AppendLine($"igz riscritti: {mode} (nessuno = solo archivio, maps = file del livello, tutti = tutti tranne le texture)");
            report.AppendLine();

            IgArchive input = IgArchive.Open(inputPath);
            IgArchive output = new IgArchive(outputPath, input.GameVersion);
            var counters = new Dictionary<string, int>();
            var lines = new List<string>();

            foreach (IgArchiveFile file in input.Files)
            {
                string lower = file.Path.ToLowerInvariant();

                // Havok files of the level (static collision)
                if (file.IsHKX() && mode != "nessuno" && (lower.Contains("staticcollision") || mode == "tutti"))
                {
                    try
                    {
                        byte[] original = file.Uncompress();
                        byte[] data = new Havok.HavokFile(original, input.GameVersion).Save();

                        IgArchiveFile copy = file.Clone();
                        copy.SetData(data);
                        output.AddFile(copy);

                        bool same = data.AsSpan().SequenceEqual(original);
                        Increment(counters, same ? "havok riscritti identici all'originale" : "havok riscritti diversi");
                        if (!same) lines.Add($"havok diverso: {file.Path} ({original.Length} -> {data.Length} byte, primo byte diverso 0x{FirstDifference(original, data):X})");
                    }
                    catch (Exception e)
                    {
                        output.AddFile(file.Clone());
                        Increment(counters, "havok non riscrivibili (copiati)");
                        lines.Add($"ERRORE havok {file.Path}: {e.GetType().Name}: {e.Message}");
                    }
                    continue;
                }

                bool rewrite = file.IsIGZ() && (
                    (mode == "maps" && (lower.StartsWith("maps/") || lower.StartsWith("packages/"))) ||
                    (mode == "tutti" && !lower.StartsWith("textures/")));

                if (!rewrite)
                {
                    output.AddFile(file.Clone());
                    Increment(counters, "copiati senza modifiche");
                    continue;
                }

                try
                {
                    byte[] original = file.Uncompress();
                    IgzFile igz = new IgzFile(file.Path, original, input.GameVersion);
                    byte[] data = igz.Save(null, true);

                    IgArchiveFile copy = file.Clone();
                    copy.SetData(data);
                    output.AddFile(copy);

                    if (data.AsSpan().SequenceEqual(original))
                    {
                        Increment(counters, "igz riscritti identici all'originale");
                    }
                    else
                    {
                        Increment(counters, data.Length == original.Length ? "igz riscritti diversi (stessa dimensione)" : "igz riscritti diversi (dimensione diversa)");
                        int first = FirstDifference(original, data);
                        lines.Add($"diverso: {file.Path} ({original.Length} -> {data.Length} byte, primo byte diverso 0x{first:X})");
                    }
                }
                catch (Exception e)
                {
                    output.AddFile(file.Clone());
                    Increment(counters, "igz non riscrivibili (copiati)");
                    lines.Add($"ERRORE {file.Path}: {e.GetType().Name}: {e.Message}");
                }
            }

            output.Save(outputPath);

            foreach (var (key, count) in counters.OrderBy(e => e.Key)) report.AppendLine($"  {key}: {count}");
            report.AppendLine();
            foreach (string line in lines) report.AppendLine("  " + line);

            File.WriteAllText(reportPath, report.ToString());
            foreach (var (key, count) in counters.OrderBy(e => e.Key)) Console.WriteLine($"  {key}: {count}");
            Console.WriteLine($"Archivio scritto in {outputPath} (dettagli in {reportPath})");
            return 0;
        }

        private static int FirstDifference(byte[] a, byte[] b)
        {
            int length = Math.Min(a.Length, b.Length);
            for (int i = 0; i < length; i++)
            {
                if (a[i] != b[i]) return i;
            }
            return length;
        }

        // ------------------------------------------------------------------ havok

        /// <summary>
        /// Convert a Havok file to the Switch layout (same as CTR: reusePaddingOptimization)
        /// </summary>
        private static byte[] HavokToSwitch(byte[] data, GameVersion from)
        {
            var hkx = new Havok.HavokFile(data, from);
            hkx.GameVersion = GameVersion.NSX;
            hkx.GetHeader().reusePaddingOptimization = 1;
            return hkx.Save();
        }

        /// <summary>
        /// Read and rewrite a Switch Havok file: tells if the Switch layout is right
        /// </summary>
        private static string HavokRoundTrip(byte[] original)
        {
            byte[] data = new Havok.HavokFile(original, GameVersion.NSX).Save();
            if (data.AsSpan().SequenceEqual(original)) return "identico all'originale";
            return $"diverso ({original.Length} -> {data.Length} byte, primo byte diverso 0x{FirstDifference(original, data):X})";
        }

        /// <summary>
        /// Rename the namespaces referenced by the handles and external objects of a file
        /// </summary>
        private static int RenameNamespaces(IgzFile igz, Dictionary<string, string> renamed)
        {
            // References stored as hashes (EXID) whose name is unknown keep the hash as their name
            var byHash = new Dictionary<string, string>();
            foreach (var (from, to) in renamed) byHash.TryAdd(NamespaceUtils.ComputeHash(from).ToString(), to);

            string? NewName(string name) => renamed.TryGetValue(name, out string? found) ? found : byHash.GetValueOrDefault(name);

            int count = 0;
            foreach (igObject obj in igz.Objects)
            {
                foreach (NamedReference handle in obj.GetHandles(igz.GameVersion))
                {
                    string? newName = NewName(handle.namespaceName);
                    if (newName == null) continue;
                    handle.SetNamespace(newName);
                    count++;
                }

                string? referenceName = obj.Reference == null ? null : NewName(obj.Reference.namespaceName);
                if (obj.Reference != null && referenceName != null)
                {
                    obj.Reference.SetNamespace(referenceName);
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// The static collision links its shapes to the objects with a hash of their file name
        /// (namespace) and object name: update the hashes of the renamed files
        /// </summary>
        private static int RenameCollisionKeys(IgzFile igz, Dictionary<string, string> renamed)
        {
            var hashes = new Dictionary<u32, u32>();
            foreach (var (from, to) in renamed) hashes.TryAdd(NamespaceUtils.ComputeHash(from), NamespaceUtils.ComputeHash(to));

            int count = 0;
            foreach (CStaticCollisionHashInstanceIdHashTable table in igz.Objects.OfType<CStaticCollisionHashInstanceIdHashTable>())
            {
                var entries = table.Dict.ToList();
                int changed = 0;

                table.Dict.Clear();
                foreach (var (key, value) in entries)
                {
                    u64 newKey = key;
                    if (hashes.TryGetValue((u32)(key >> 32), out u32 newHash))
                    {
                        newKey = ((u64)newHash << 32) | (key & 0xFFFFFFFFUL);
                        changed++;
                    }
                    table.Dict[newKey] = value;
                }

                if (changed > 0) table.RebuildDict = true;
                count += changed;
            }
            return count;
        }

        // ------------------------------------------------------------------ sostituzione di un livello

        /// <summary>
        /// Renaming of a level: folder (Crash1/Custom_Level -> Crash3/L301_ToadVillage) and name
        /// </summary>
        private class LevelRename
        {
            public string FromName = "";
            public string ToName = "";
            public string? FromDir;
            public string? ToDir;

            public string Apply(string path)
            {
                string result = path;
                if (FromDir != null && ToDir != null && !FromDir.Equals(ToDir, StringComparison.OrdinalIgnoreCase))
                {
                    result = ReplaceIgnoreCase(result, "/" + FromDir + "/", "/" + ToDir + "/");
                }
                return ReplaceIgnoreCase(result, FromName, ToName);
            }

            public override string ToString()
            {
                string dirs = FromDir != null && ToDir != null && !FromDir.Equals(ToDir, StringComparison.OrdinalIgnoreCase) ? $" (cartella {FromDir} -> {ToDir})" : "";
                return $"{FromName} -> {ToName}{dirs}";
            }
        }

        /// <summary>
        /// Folder ("Crash1/L112_RoadToNowhere") and level name of a package file path
        /// (packages/generated/maps/[game]/[folder]/[level]_pkg.igz)
        /// </summary>
        private static (string dir, string name)? PackageInfo(string path)
        {
            const string prefix = "packages/generated/maps/";
            const string suffix = "_pkg.igz";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;

            string[] parts = path.Substring(prefix.Length).Split('/');
            if (parts.Length != 3) return null;

            return (parts[0] + "/" + parts[1], parts[2].Substring(0, parts[2].Length - suffix.Length));
        }

        /// <summary>
        /// Nome del livello ricavato dal nome del file di uscita: solo lettere, cifre e _
        /// (gli altri caratteri diventano _), per esempio "Mio livello 2.pak" -> "Mio_livello_2"
        /// </summary>
        private static string LevelNameFromFile(string path)
        {
            var name = new StringBuilder();
            foreach (char c in Path.GetFileNameWithoutExtension(path))
            {
                bool valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                char next = valid ? c : '_';
                if (next == '_' && (name.Length == 0 || name[name.Length - 1] == '_')) continue;
                name.Append(next);
            }
            string result = name.ToString().TrimEnd('_');
            // Un nome che inizia con una cifra potrebbe essere scambiato per un hash: "3livello" -> "L3livello"
            if (result.Length > 0 && result[0] >= '0' && result[0] <= '9') result = "L" + result;
            return result;
        }

        /// <summary>
        /// The level of the PC archive takes the place of an original Switch level
        /// </summary>
        private static LevelRename? FindReplacement(IgArchive pc, ArchiveIndex sw, string levelName, out IgArchiveFile? switchPackage)
        {
            switchPackage = null;

            var pcPackage = pc.Files.Select(f => PackageInfo(f.Path)).FirstOrDefault(p => p != null);
            if (pcPackage == null) return null;

            foreach (IgArchiveFile file in sw.ByPath.Values)
            {
                var info = PackageInfo(file.Path);
                if (info == null || !info.Value.name.Equals(levelName, StringComparison.OrdinalIgnoreCase)) continue;

                switchPackage = file;
                return new LevelRename
                {
                    FromName = pcPackage.Value.name,
                    ToName = info.Value.name,
                    FromDir = pcPackage.Value.dir,
                    ToDir = info.Value.dir,
                };
            }

            return null;
        }

        // ------------------------------------------------------------------ converti

        /// <summary>
        /// File del livello stesso: quelli nella sua cartella (maps/Crash1/L112_RoadToNowhere/...,
        /// anche per un livello creato dall'editor da uno originale, L112_RoadToNowhere_Custom, i cui file
        /// modificati tengono i nomi dell'originale) o con il suo nome nel percorso (pacchetto, collisione,
        /// zone info). I file di altri livelli che l'editor copia nell'archivio quando usi i loro oggetti
        /// (per esempio maps/Crash3/L309_TombTime/L309_TombTime.igz) non lo sono: se esistono nel gioco
        /// Switch si usano gli originali, senza convertire la copia PC.
        /// </summary>
        private static bool IsLevelContentAll(string path, List<string> levels)
        {
            // Regola fino alla v12: tutto quello che sta in maps/ e packages/ si converte dal PC
            string lower = path.ToLowerInvariant();
            if (lower.StartsWith("maps/") || lower.StartsWith("packages/") || lower.StartsWith("update/")) return true;
            return levels.Any(l => lower.Contains(l.ToLowerInvariant()));
        }

        private static bool IsLevelContent(string path, List<string> levels, List<string> levelDirs)
        {
            string lower = "/" + path.ToLowerInvariant();
            if (lower.StartsWith("/update/")) return true;
            if (levelDirs.Any(d => lower.Contains("/" + d.ToLowerInvariant().Trim('/') + "/"))) return true;
            return levels.Any(l => lower.Contains(l.ToLowerInvariant()));
        }

        private static bool SameData(IgArchiveFile a, IgArchiveFile b)
        {
            return a.Uncompress().AsSpan().SequenceEqual(b.Uncompress());
        }

        private static int Convert(string pcPath, string switchDir, string outputPath, string reportPath, Options options)
        {
            // Cartelle di uscita (archivio e rapporto) create se mancano
            foreach (string path in new[] { outputPath, reportPath })
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }

            var report = new StringBuilder();
            report.AppendLine("CONVERSIONE PC -> SWITCH");
            report.AppendLine($"archivio PC: {pcPath}");
            report.AppendLine($"output: {outputPath}");

            Console.WriteLine("Indicizzo gli archivi Switch...");
            ArchiveIndex sw = ArchiveIndex.Build(switchDir, null);
            Dictionary<string, IgArchiveFile> index = sw.ByPath;

            // --ctr: grafica che il gioco Switch non ha presa da Crash Team Racing Nitro-Fueled per Switch (prova)
            ArchiveIndex? ctr = null;
            if (options.CtrDump != null)
            {
                Console.WriteLine("Indicizzo gli archivi di Crash Team Racing...");
                var ctrErrors = new List<string>();
                ctr = ArchiveIndex.Build(options.CtrDump, ctrErrors);
                report.AppendLine($"dump CTR: {options.CtrDump} ({ctr.ByArchive.Count} archivi, {ctr.ByPath.Count} file)");
                foreach (string error in ctrErrors.Take(20)) report.AppendLine("  ERRORE " + error);
                if (ctrErrors.Count > 20) report.AppendLine($"  ... e altri {ctrErrors.Count - 20} errori");
            }

            Dictionary<string, IgArchiveFile>? pcIndex = null;
            if (options.PcOriginals != null)
            {
                Console.WriteLine("Indicizzo gli archivi originali PC...");
                pcIndex = ArchiveIndex.Build(options.PcOriginals, null).ByPath;
                report.AppendLine($"originali PC: {options.PcOriginals} ({pcIndex.Count} file)");
            }

            IgArchive pc = IgArchive.Open(pcPath);
            List<string> levels = LevelNames(pc.Files.Select(f => f.Path));
            List<string> levelDirs = pc.Files.Select(f => PackageInfo(f.Path)).Where(p => p != null).Select(p => p!.Value.dir).Distinct().ToList();
            string otherLevels = options.OtherLevels;
            if (otherLevels != "converti" && otherLevels != "originali" && otherLevels != "originali+dipendenze")
            {
                Console.WriteLine($"Errore: --altri-livelli {otherLevels}: valori possibili converti, originali, originali+dipendenze");
                return 1;
            }
            bool otherLevelsFromSwitch = otherLevels != "converti";
            report.AppendLine($"file di altri livelli copiati dall'editor: {otherLevels}");
            report.AppendLine($"livelli nel file: {string.Join(", ", levels)}");

            LevelRename? rename = null;
            string? baseArchive = null;

            if (options.NewLevel && (options.ReplaceLevel != null || options.RenameBack))
            {
                Console.WriteLine("Errore: --nuovo non si usa insieme a --sostituisci o --come-originale");
                return 1;
            }
            if (options.Game == "")
            {
                Console.WriteLine("Errore: --gioco vuole crash1, crash2 o crash3");
                return 1;
            }
            if (!options.NewLevel && (options.Game != null || !options.Memory.Equals("modello", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("Errore: --gioco e --memoria si usano solo con --nuovo (cambiano la zone info del livello nuovo)");
                return 1;
            }

            // Livello nuovo: prende il nome dell'archivio di uscita (il gioco apre archives/<livello>.pak,
            // quindi file e livello devono avere lo stesso nome)
            (string dir, string name)? newPackage = null;
            if (options.NewLevel)
            {
                newPackage = pc.Files.Select(f => PackageInfo(f.Path)).FirstOrDefault(p => p != null);
                if (newPackage == null)
                {
                    Console.WriteLine("Errore: --nuovo: nel file non c'e' un livello (packages/generated/maps/...)");
                    return 1;
                }
                List<string> switchLevels = LevelNames(index.Keys);
                string pcName = newPackage.Value.name;
                string requested = LevelNameFromFile(outputPath);
                if (requested.Length == 0) requested = pcName;
                if (!requested.Equals(Path.GetFileNameWithoutExtension(outputPath), StringComparison.OrdinalIgnoreCase))
                {
                    report.AppendLine($"--nuovo: '{Path.GetFileNameWithoutExtension(outputPath)}' non e' un nome di livello valido (solo lettere, cifre e _): uso {requested}");
                }

                if (!requested.Equals(pcName, StringComparison.OrdinalIgnoreCase))
                {
                    if (switchLevels.Any(l => l.Equals(requested, StringComparison.OrdinalIgnoreCase)))
                    {
                        report.AppendLine($"--nuovo: {requested} e' il nome di un livello del gioco: per sostituirlo usa --sostituisci {requested}");
                        File.WriteAllText(reportPath, report.ToString());
                        Console.WriteLine($"Errore: {requested} e' gia' un livello del gioco. Scegli un altro nome di uscita oppure usa --sostituisci {requested}");
                        return 1;
                    }
                    if (switchLevels.Any(l => l.Equals(pcName, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Gli asset del livello originale hanno lo stesso nome: rinominarli li staccherebbe dagli originali Switch
                        report.AppendLine($"--nuovo: il livello si chiama come l'originale {pcName} e non si puo' rinominare in {requested}");
                        File.WriteAllText(reportPath, report.ToString());
                        Console.WriteLine($"Errore: il livello si chiama {pcName}, come un livello del gioco: non si puo' rinominare. " +
                                          $"Usa --come-originale o --sostituisci, oppure salvalo nell'editor con un nome nuovo.");
                        return 1;
                    }
                    string game = newPackage.Value.dir.Split('/')[0];
                    rename = new LevelRename { FromName = pcName, ToName = requested, FromDir = newPackage.Value.dir, ToDir = game + "/" + requested };
                    newPackage = (game + "/" + requested, requested);
                    report.AppendLine($"--nuovo: il livello prende il nome indicato: {rename}");
                }
                else if (switchLevels.Any(l => l.Equals(pcName, StringComparison.OrdinalIgnoreCase)))
                {
                    report.AppendLine($"ATTENZIONE: {pcName} esiste gia' nel gioco: per sostituirlo usa --come-originale o --sostituisci");
                }

                // Sulla Switch gli archivi dei livelli sono in minuscolo (l112_roadtonowhere.pak) e il gioco
                // apre archives/<livello in minuscolo>.pak: la romfs distingue le maiuscole
                string wanted = newPackage.Value.name.ToLowerInvariant() + ".pak";
                if (!Path.GetFileName(outputPath).Equals(wanted, StringComparison.Ordinal))
                {
                    outputPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".", wanted);
                    report.AppendLine($"--nuovo: archivio scritto come {wanted} (in minuscolo, come lo cerca il gioco)");
                }
                report.AppendLine($"--nuovo: livello {newPackage.Value.dir}/{newPackage.Value.name}, avvio diretto: {(newPackage.Value.dir + "/" + newPackage.Value.name).ToLowerInvariant()}");
            }

            if (options.ReplaceLevel != null)
            {
                // The level replaces the chosen original level
                rename = FindReplacement(pc, sw, options.ReplaceLevel, out IgArchiveFile? switchPackage);
                if (rename == null || switchPackage == null)
                {
                    report.AppendLine($"--sostituisci: livello {options.ReplaceLevel} non trovato nel gioco Switch");
                    Console.WriteLine($"Errore: livello {options.ReplaceLevel} non trovato nel gioco Switch (usa il nome del file .pak, per esempio L101_NSanityBeach)");
                    File.WriteAllText(reportPath, report.ToString());
                    return 1;
                }
                baseArchive = sw.ArchiveOf.GetValueOrDefault(switchPackage);
                report.AppendLine($"il livello sostituisce {rename.ToName}: {rename}");
            }
            else if (options.RenameBack)
            {
                // Level created from an original one (..._Custom): it takes back the original name
                var detected = DetectRename(pc, index);
                if (detected != null) rename = new LevelRename { FromName = detected.Value.from, ToName = detected.Value.to };
                report.AppendLine(rename != null
                    ? $"il livello prende il nome dell'originale: {rename}"
                    : "--come-originale: livello originale non trovato, nomi lasciati invariati (per un livello nuovo usa --sostituisci <livello>)");
            }

            // Archive of the original level in the Switch game (base of the new archive)
            if (baseArchive == null && options.BaseLevel != null)
            {
                if (sw.ByArchive.ContainsKey(options.BaseLevel.ToLowerInvariant())) baseArchive = options.BaseLevel.ToLowerInvariant();
                else report.AppendLine($"--base: archivio {options.BaseLevel}.pak non trovato nel gioco Switch");
            }
            if (baseArchive == null && options.NewLevel)
            {
                // Livello creato da uno originale (..._Custom): gli asset di partenza sono quelli dell'originale
                var detected = DetectRename(pc, index);
                if (detected != null && sw.ByArchive.ContainsKey(detected.Value.to.ToLowerInvariant())) baseArchive = detected.Value.to.ToLowerInvariant();
            }
            if (baseArchive == null)
            {
                // Con --nuovo il nome di arrivo e' nuovo: la base resta quella del livello PC
                string? baseLevel = (options.NewLevel ? null : rename?.ToName) ?? levels.FirstOrDefault();
                if (baseLevel != null && sw.ByArchive.ContainsKey(baseLevel.ToLowerInvariant())) baseArchive = baseLevel.ToLowerInvariant();
            }
            if (options.WithoutBase) baseArchive = null;

            report.AppendLine(baseArchive != null ? $"archivio Switch di partenza: {baseArchive}.pak" : "archivio Switch di partenza: nessuno");
            report.AppendLine();

            // Namespaces renamed with the level (package and collision files)
            var renamedNamespaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (rename != null)
            {
                foreach (IgArchiveFile file in pc.Files)
                {
                    string target = rename.Apply(file.Path);
                    if (target == file.Path) continue;
                    renamedNamespaces[NamespaceUtils.GetFileName(file.Path, false)] = NamespaceUtils.GetFileName(target, false);
                }

                // The level must not use files of the level it replaces: they would get the same names
                var newNames = new HashSet<string>(renamedNamespaces.Values, StringComparer.OrdinalIgnoreCase);
                List<string> conflicts = pc.Files
                    .Where(f => !f.Path.StartsWith("update/", StringComparison.OrdinalIgnoreCase) && rename.Apply(f.Path) == f.Path && newNames.Contains(NamespaceUtils.GetFileName(f.Path, false)))
                    .Select(f => f.Path)
                    .ToList();

                if (conflicts.Count > 0)
                {
                    string choose = options.NewLevel ? "scegli un altro nome" : "scegli un altro livello da sostituire";
                    report.AppendLine($"ERRORE: il livello usa file chiamati {rename.ToName}, che avrebbero lo stesso nome dei suoi:");
                    foreach (string conflict in conflicts) report.AppendLine("  " + conflict);
                    report.AppendLine(char.ToUpper(choose[0]) + choose.Substring(1) + ".");
                    File.WriteAllText(reportPath, report.ToString());
                    Console.WriteLine($"Errore: il livello usa file di {rename.ToName} ({string.Join(", ", conflicts.Select(c => NamespaceUtils.GetFileName(c)))}): {choose}.");
                    return 1;
                }
            }

            IgArchive output = new IgArchive(outputPath, GameVersion.NSX);
            var included = new HashSet<string>();
            var fromSwitch = new List<IgArchiveFile>();
            var counters = new Dictionary<string, int>();
            var lines = new List<string>();
            var registration = new List<IgArchiveFile>();
            var otherLevelOriginals = new HashSet<IgArchiveFile>();
            var entityLines = new List<string>();
            bool levelHasC3Intro = false;
            int done = 0;

            // Intro di Crash 3 da aggiungere se manca: con --aggiungi-intro, oppure con --gioco crash3 per un livello che
            // nell'editor era di un altro gioco (l'editor la aggiunge quando passi un livello a Crash 3)
            EGameYear? editorYear = PcZoneInfoYear(pc.Files.ToList());
            bool addIntro = !options.WithoutIntro &&
                            (options.AddIntro || (options.Game == "crash3" && editorYear != EGameYear.eGY_2017_Crash3));
            if (options.WithoutIntro && options.AddIntro)
                report.AppendLine("--aggiungi-intro ignorata: c'e' anche --senza-intro");

            void AddOriginal(IgArchiveFile file, string counter)
            {
                if (!included.Add(file.Path.ToLowerInvariant())) return;
                output.AddFile(file.Clone());
                fromSwitch.Add(file);
                Increment(counters, counter);
            }

            // File di CTR Switch: copiati non compressi (CTR potrebbe usare un'altra compressione)
            var fromCtr = new List<IgArchiveFile>();
            bool AddCtr(IgArchiveFile file, string counter)
            {
                string key = file.Path.ToLowerInvariant();
                if (included.Contains(key)) return true;
                try
                {
                    IgArchiveFile copy = new IgArchiveFile(file.Path, GameVersion.NSX);
                    copy.SetData(file.Uncompress());
                    output.AddFile(copy);
                    included.Add(key);
                    fromCtr.Add(file);
                    Increment(counters, counter);
                    return true;
                }
                catch (Exception e)
                {
                    lines.Add($"ERRORE file CTR {file.Path}: {e.Message}");
                    return false;
                }
            }

            // Stesso percorso, oppure (materiali) stesso nome con un suffisso diverso dopo l'ultima virgola
            IgArchiveFile? FindCtr(string target)
            {
                if (ctr == null) return null;
                string key = target.ToLowerInvariant();
                if (ctr.ByPath.TryGetValue(key, out IgArchiveFile? exact)) return exact;
                int comma = key.LastIndexOf(',');
                if (comma > 0)
                {
                    string prefix = key.Substring(0, comma + 1);
                    IgArchiveFile? byPrefix = ctr.ByPath.Where(e => e.Key.StartsWith(prefix)).Select(e => e.Value).FirstOrDefault();
                    if (byPrefix != null) return byPrefix;
                }
                // Stesso nome di file in un'altra cartella (per i materiali: stesso nome fino all'ultima virgola)
                string fileName = key.Substring(key.LastIndexOf('/') + 1);
                int nameComma = fileName.LastIndexOf(',');
                string namePrefix = nameComma > 0 ? fileName.Substring(0, nameComma + 1) : fileName;
                return ctr.ByPath.Where(e => e.Key.Substring(e.Key.LastIndexOf('/') + 1).StartsWith(namePrefix))
                                 .Where(e => nameComma > 0 || e.Key.EndsWith("/" + fileName))
                                 .Select(e => e.Value).FirstOrDefault();
            }

            foreach (IgArchiveFile file in pc.Files)
            {
                string path = file.Path;
                done++;
                if (done % 250 == 0) Console.WriteLine($"  {done}/{pc.Files.Count} file...");

                try
                {
                    // --escludi: file lasciati fuori dall'archivio (e quindi dal pacchetto del livello)
                    if (!path.StartsWith("update/", StringComparison.OrdinalIgnoreCase) &&
                        options.Exclude.Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase)))
                    {
                        Increment(counters, "file esclusi (--escludi)");
                        lines.Add($"escluso: {path}");
                        continue;
                    }

                    // Livello nuovo: la zone info e gli altri file update/ vanno in update.pak
                    if (options.NewLevel && path.StartsWith("update/", StringComparison.OrdinalIgnoreCase))
                    {
                        registration.Add(file);
                        Increment(counters, "file update/ spostati in update.pak");
                        continue;
                    }

                    // The zone info of a new level is not needed when it replaces the original one
                    if (rename != null && path.StartsWith("update/", StringComparison.OrdinalIgnoreCase))
                    {
                        Increment(counters, "file update/ tolti (il livello sostituisce l'originale)");
                        lines.Add($"tolto: {path}");
                        continue;
                    }

                    string target = rename != null ? rename.Apply(path) : path;
                    if (included.Contains(target.ToLowerInvariant())) continue;

                    index.TryGetValue(target.ToLowerInvariant(), out IgArchiveFile? original);

                    bool? unmodified = null;
                    if (pcIndex != null && pcIndex.TryGetValue(path.ToLowerInvariant(), out IgArchiveFile? pcOriginal))
                    {
                        unmodified = SameData(file, pcOriginal);
                    }

                    bool levelContent = otherLevelsFromSwitch ? IsLevelContent(path, levels, levelDirs) : IsLevelContentAll(path, levels);
                    bool collision = target.Contains("staticcollision", StringComparison.OrdinalIgnoreCase);

                    if (original != null)
                    {
                        if (unmodified == true) { AddOriginal(original, "non modificati: presi dagli originali Switch"); continue; }
                        if (!file.IsIGZ() && !(collision && file.IsHKX())) { AddOriginal(original, "file non igz presi dagli originali Switch"); continue; }
                        // (fino alla v12 le collisioni si convertivano sempre: con "converti" resta cosi')
                        if (unmodified == null && !levelContent && (otherLevelsFromSwitch || !collision))
                        {
                            bool otherLevel = otherLevelsFromSwitch &&
                                              (target.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) || target.StartsWith("packages/", StringComparison.OrdinalIgnoreCase) ||
                                               target.StartsWith("models/maps/", StringComparison.OrdinalIgnoreCase));
                            AddOriginal(original, otherLevel ? "file di altri livelli presi dagli originali Switch" : "asset presi dagli originali Switch");
                            if (otherLevel) otherLevelOriginals.Add(original);
                            if (otherLevel) lines.Add($"originale Switch (file di un altro livello): {target}");
                            continue;
                        }
                    }

                    // Havok file (static collision of the level): converted to the Switch layout
                    if (file.IsHKX())
                    {
                        try
                        {
                            if (original != null)
                            {
                                lines.Add($"prova: collisione originale Switch riletta e riscritta: {HavokRoundTrip(original.Uncompress())}");
                            }

                            IgArchiveFile hkx = new IgArchiveFile(target, GameVersion.NSX);
                            hkx.SetData(HavokToSwitch(file.Uncompress(), file.GameVersion));
                            output.AddFile(hkx);
                            included.Add(target.ToLowerInvariant());
                            Increment(counters, "havok convertiti");
                            lines.Add($"havok convertito: {path} -> {target}");
                        }
                        catch (Exception e)
                        {
                            if (original != null)
                            {
                                AddOriginal(original, "havok non convertibili: presi dagli originali Switch");
                            }
                            else
                            {
                                IgArchiveFile raw = new IgArchiveFile(target, GameVersion.NSX);
                                raw.SetData(file.Uncompress());
                                output.AddFile(raw);
                                included.Add(target.ToLowerInvariant());
                                Increment(counters, "havok non convertibili copiati dal PC");
                            }
                            lines.Add($"ERRORE conversione havok {path}: {e.GetType().Name}: {e.Message}");
                        }
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
                                AddOriginal(original, "grafica presa dagli originali Switch");
                                lines.Add($"grafica, originale Switch: {target}");
                            }
                            else if (FindCtr(target) is IgArchiveFile ctrFile && AddCtr(ctrFile, "grafica presa da CTR Switch"))
                            {
                                lines.Add($"grafica da CTR Switch: {ctrFile.Path} (al posto di {path})");
                            }
                            else if (path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
                            {
                                Increment(counters, "texture PC senza equivalente con lo stesso nome (saltate)");
                            }
                            else
                            {
                                Increment(counters, "grafica NON convertibile (file saltato)");
                                lines.Add($"SALTATO (grafica PC, {graphics}): {path}");
                            }
                            continue;
                        }

                        // Diagnosi: entita' del file principale del livello (nome, tipo, componenti)
                        if (levels.Any(l => NamespaceUtils.GetFileName(path, false).Equals(l, StringComparison.OrdinalIgnoreCase)))
                        {
                            foreach (igEntity entity in igz.Objects.OfType<igEntity>())
                            {
                                string components = string.Join(", ", entity.GetComponents().Select(c => c.GetType().Name));
                                entityLines.Add($"  {entity.ObjectName ?? "?"} [{entity.GetType().Name}] {components}");
                            }
                        }

                        // L'intro dei livelli di Crash 3 (IntroCutsceneSequencePlayer, Crash che esce dal portale) fa comparire
                        // Crash: la zone info di un livello di Crash 3 la aspetta (vedi BuildRegistration)
                        bool hasIntro = igz.Objects.OfType<igEntity>().Any(e => e.GetComponent<common_C3_IntroSequenceData>() != null);
                        if (hasIntro && !options.WithoutIntro) levelHasC3Intro = true;

                        // --senza-intro: toglie quell'intro
                        if (options.WithoutIntro && hasIntro)
                        {
                            HashSet<igObject> intros = igz.Objects.OfType<igEntity>()
                                .Where(e => e.GetComponent<common_C3_IntroSequenceData>() != null)
                                .Cast<igObject>()
                                .ToHashSet();
                            if (intros.Count > 0)
                            {
                                HashSet<igObject> removedObjects = igz.Remove(intros);
                                Increment(counters, "intro di Crash 3 tolte (--senza-intro)");
                                lines.Add($"intro di Crash 3 tolta da {target} ({removedObjects.Count} oggetti)");
                            }
                        }

                        igz.GameVersion = GameVersion.NSX;
                        string? newNamespace = target != path ? NamespaceUtils.GetFileName(target, false) : null;

                        // Intro di Crash 3 aggiunta al file principale del livello (copiata da Gone Tomorrow della Switch)
                        bool mainFile = levels.Any(l => NamespaceUtils.GetFileName(path, false).Equals(l, StringComparison.OrdinalIgnoreCase));
                        if (addIntro && mainFile && !hasIntro && !levelHasC3Intro)
                        {
                            try
                            {
                                List<IgArchiveFile> introFiles = AddC3Intro(igz, switchDir, output, out string introInfo);
                                levelHasC3Intro = true;
                                Increment(counters, "intro di Crash 3 aggiunta");
                                lines.Add($"intro di Crash 3 aggiunta a {target}: {introInfo}");
                                foreach (IgArchiveFile dependency in introFiles)
                                {
                                    // un file che c'e' anche nell'archivio PC si converte dal PC, come gli altri
                                    if (pc.Files.Any(f => f.Path.Equals(dependency.Path, StringComparison.OrdinalIgnoreCase))) continue;
                                    AddOriginal(dependency, "file per l'intro di Crash 3 (da L321_GoneTomorrow Switch)");
                                    lines.Add($"per l'intro di Crash 3: {dependency.Path}");
                                }
                            }
                            catch (Exception e)
                            {
                                Increment(counters, "errori");
                                lines.Add($"ERRORE aggiungendo l'intro di Crash 3: {e.GetType().Name}: {e.Message}");
                            }
                        }

                        if (rename != null)
                        {
                            int renamedCount = RenameNamespaces(igz, renamedNamespaces);
                            renamedCount += RenameCollisionKeys(igz, renamedNamespaces);

                            // Name of the static collision, stored as text in its name list
                            if (collision)
                            {
                                foreach (igNameList list in igz.Objects.OfType<igNameList>())
                                {
                                    foreach (igNameMetaField entry in list._data)
                                    {
                                        if (entry._name == null || !entry._name.Contains(rename.FromName, StringComparison.OrdinalIgnoreCase)) continue;
                                        entry._name = ReplaceIgnoreCase(entry._name, rename.FromName, rename.ToName);
                                        renamedCount++;
                                    }
                                }
                            }

                            // Files listed by the package (keeping their type)
                            if (PackageInfo(target) != null)
                            {
                                foreach (igStreamingChunkInfo info in igz.Objects.OfType<igStreamingChunkInfo>())
                                {
                                    foreach (ChunkFileInfoMetaField entry in info._required._data)
                                    {
                                        if (entry._name == null) continue;
                                        string renamedName = rename.Apply(entry._name);
                                        if (renamedName == entry._name) continue;
                                        entry._name = renamedName.ToLowerInvariant();
                                        renamedCount++;
                                    }
                                }
                            }

                            if (renamedCount > 0) lines.Add($"riferimenti al livello rinominati: {renamedCount} in {target}");
                        }

                        IgArchiveFile converted = new IgArchiveFile(target, GameVersion.NSX);
                        converted.SetData(igz.Save(newNamespace));
                        output.AddFile(converted);
                        included.Add(target.ToLowerInvariant());

                        Increment(counters, unmodified == false ? "igz modificati convertiti" : "igz convertiti");
                        lines.Add(target != path ? $"convertito e rinominato: {path} -> {target}" : $"convertito: {path}");
                        continue;
                    }

                    IgArchiveFile copy = new IgArchiveFile(target, GameVersion.NSX);
                    copy.SetData(file.Uncompress());
                    output.AddFile(copy);
                    included.Add(target.ToLowerInvariant());
                    Increment(counters, file.IsHKX() ? "havok PC copiati senza conversione (probabilmente non funzionano)" : "altri file copiati senza conversione");
                    lines.Add($"copiato senza conversione: {target}");
                }
                catch (Exception e)
                {
                    Increment(counters, "errori");
                    lines.Add($"ERRORE {path}: {e.Message}");
                }
            }

            // Everything else of the original level (its own assets with their Switch names)
            if (baseArchive != null)
            {
                foreach (IgArchiveFile file in sw.ByArchive[baseArchive])
                {
                    // The placement files of the level come only from the PC archive
                    string lower = file.Path.ToLowerInvariant();
                    if (lower.StartsWith("maps/") || lower.StartsWith("packages/") || lower.StartsWith("update/")) continue;
                    AddOriginal(file, "file del livello originale Switch aggiunti");
                }
            }

            // Dependencies of the Switch files (e.g. the Switch textures of the Switch materials),
            // searched in the same archive the file comes from
            Console.WriteLine("Cerco le dipendenze dei file Switch...");
            for (int i = 0; i < fromSwitch.Count; i++)
            {
                IgArchiveFile file = fromSwitch[i];
                if (!file.IsIGZ() || file.Path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!sw.ArchiveOf.TryGetValue(file, out string? archive)) continue;
                // I file di altri livelli si portano dietro tutto il loro livello: le dipendenze solo se richieste
                if (otherLevelOriginals.Contains(file) && otherLevels != "originali+dipendenze") continue;

                HashSet<string> dependencies;
                try
                {
                    dependencies = RawDependencies(file.Uncompress());
                }
                catch (Exception e)
                {
                    lines.Add($"dipendenze non lette: {file.Path} ({e.Message})");
                    continue;
                }

                Dictionary<string, List<IgArchiveFile>> names = sw.Names(archive);
                foreach (string name in dependencies)
                {
                    if (!names.TryGetValue(name, out List<IgArchiveFile>? found)) continue;
                    foreach (IgArchiveFile dependency in found)
                    {
                        if (included.Contains(dependency.Path.ToLowerInvariant())) continue;
                        AddOriginal(dependency, "dipendenze Switch aggiunte");
                        lines.Add($"dipendenza Switch: {dependency.Path} (da {file.Path})");
                    }
                }
            }

            // Dipendenze dei file presi da CTR (texture dei materiali, materiali dei modelli...), dallo stesso archivio CTR
            if (ctr != null)
            {
                for (int i = 0; i < fromCtr.Count; i++)
                {
                    IgArchiveFile file = fromCtr[i];
                    if (!file.IsIGZ() || file.Path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ctr.ArchiveOf.TryGetValue(file, out string? archive)) continue;
                    HashSet<string> dependencies;
                    try
                    {
                        dependencies = RawDependencies(file.Uncompress());
                    }
                    catch (Exception e)
                    {
                        lines.Add($"dipendenze CTR non lette: {file.Path} ({e.Message})");
                        continue;
                    }
                    Dictionary<string, List<IgArchiveFile>> names = ctr.Names(archive);
                    foreach (string name in dependencies)
                    {
                        if (!names.TryGetValue(name, out List<IgArchiveFile>? found)) continue;
                        foreach (IgArchiveFile dependency in found)
                        {
                            if (included.Contains(dependency.Path.ToLowerInvariant())) continue;
                            // Un file che il gioco Crash ha gia' con lo stesso percorso resta il suo
                            if (index.ContainsKey(dependency.Path.ToLowerInvariant())) continue;
                            if (AddCtr(dependency, "dipendenze CTR aggiunte")) lines.Add($"dipendenza CTR: {dependency.Path} (da {file.Path})");
                        }
                    }
                }
                if (fromCtr.Count > 0)
                {
                    try
                    {
                        byte[] sample = fromCtr.First(f => f.IsIGZ()).Uncompress();
                        report.AppendLine($"igz di CTR: versione {BitConverter.ToUInt32(sample, 4)}, piattaforma {BitConverter.ToUInt32(sample, 12)}, " +
                                          $"hash dei campi 0x{BitConverter.ToUInt32(sample, 8):X8} (Crash Switch: versione 10, piattaforma 2)");
                    }
                    catch (Exception e)
                    {
                        report.AppendLine($"igz di CTR non leggibili: {e.Message}");
                    }
                }
                else
                {
                    report.AppendLine("nessun file preso da CTR (la grafica che manca non e' nel dump CTR con lo stesso percorso)");
                }
            }

            // The package file must list exactly the files of the archive
            try
            {
                if (output.FindPackageFile() == null)
                {
                    lines.Add("file del pacchetto (_pkg.igz) non trovato o non unico: non aggiornato");
                }
                else
                {
                    output.RebuildPackageFile(output.Files.Where(f => !f.Path.StartsWith("update/", StringComparison.OrdinalIgnoreCase)).ToList(), out _);
                    Increment(counters, "file del pacchetto aggiornato");
                }
            }
            catch (Exception e)
            {
                Increment(counters, "errori");
                lines.Add($"ERRORE aggiornando il file del pacchetto: {e.Message}");
            }

            string? updatePath = null;
            string? sizeReference = rename?.ToName ?? options.ReplaceLevel;  // livello originale con cui confrontare la memoria
            if (options.NewLevel && newPackage != null)
            {
                // Archivio della registrazione: update.pak oppure chunkInfos.pak (con il nome che ha nel dump)
                string registerIn = options.RegisterIn == "chunkinfos" ? "chunkinfos" : "update";
                string registerFile = "update.pak";
                if (registerIn == "chunkinfos")
                {
                    string? chunkInfosOriginal = PakFiles(switchDir).FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals("chunkinfos", StringComparison.OrdinalIgnoreCase));
                    registerFile = chunkInfosOriginal != null ? Path.GetFileName(chunkInfosOriginal) : "chunkInfos.pak";
                }
                updatePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".", registerFile);
                string levelId = (newPackage.Value.dir + "/" + newPackage.Value.name).ToLowerInvariant();
                var embedded = new List<(string path, byte[] data)>();

                // Zone info: quella di un livello originale Switch (la zone info dell'editor convertita blocca il gioco
                // all'avvio diretto). Per un livello creato da uno originale (..._Custom) quell'originale, altrimenti
                // un livello dello stesso gioco (come fa l'editor per un livello nuovo da zero)
                string? zoneTemplate = options.ZoneInfoFrom;
                if (zoneTemplate == null)
                {
                    var detectedBase = DetectRename(pc, index);
                    string game = newPackage.Value.dir.Split('/')[0].ToLowerInvariant();
                    // Il gioco del livello e' quello scelto nell'editor ("Crash Mode", l'anno della zone info del PC), che
                    // puo' essere diverso dalla cartella: un livello in crash3 impostato come Crash 1 non ha l'intro di
                    // Crash 3 e con la zone info di un livello di Crash 3 Crash non compare
                    EGameYear? pcYear = PcZoneInfoYear(registration);
                    string yearGame = options.Game
                                    ?? (pcYear == EGameYear.eGY_2017_Crash1 ? "crash1"
                                      : pcYear == EGameYear.eGY_2017_Crash2 ? "crash2"
                                      : pcYear == EGameYear.eGY_2017_Crash3 ? "crash3" : game);
                    string gameDefault = yearGame == "crash2" ? "L201_TurtleWoods" : yearGame == "crash3" ? "L301_ToadVillage" : "L101_NSanityBeach";
                    zoneTemplate = detectedBase != null && FindZoneInfo(sw, detectedBase.Value.to) != null ? detectedBase.Value.to : gameDefault;
                    report.AppendLine($"--nuovo: zone info presa da {zoneTemplate} (scelta automatica; per cambiarla --zoneinfo-da <livello>)");
                    if (options.Game != null) report.AppendLine($"  gioco scelto con --gioco: {options.Game}, cartella del livello: {game}");
                    else if (pcYear != null) report.AppendLine($"  gioco impostato nell'editor (Crash Mode): {yearGame}, cartella del livello: {game}");
                    if (yearGame != game && zoneTemplate == gameDefault)
                        report.AppendLine($"  il livello e' nella cartella {game} ma nell'editor e' impostato come {yearGame}: zone info di {yearGame}");
                }
                else if (zoneTemplate.Equals("pc", StringComparison.OrdinalIgnoreCase))
                {
                    zoneTemplate = null;
                    report.AppendLine("--nuovo: zone info dell'editor convertita dal PC (--zoneinfo-da pc): il gioco potrebbe bloccarsi all'avvio diretto");
                }
                sizeReference = zoneTemplate;

                // Memoria del livello (--memoria): dimensioni delle aree di memoria da un'altra zone info originale
                CZoneInfo? poolSource = null;
                string? poolFrom = null;
                if (options.Memory.Equals("max", StringComparison.OrdinalIgnoreCase))
                {
                    (poolSource, poolFrom) = LargestPoolZoneInfo(sw);
                    if (poolSource == null) report.AppendLine("--memoria max: nessuna zone info originale leggibile nel dump");
                }
                else if (!options.Memory.Equals("modello", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        poolSource = FindZoneInfo(sw, options.Memory)?.ToIgzFile().FindObject<CZoneInfo>();
                    }
                    catch (Exception e)
                    {
                        report.AppendLine($"--memoria {options.Memory}: zone info non leggibile ({e.Message})");
                    }
                    poolFrom = options.Memory;
                    if (poolSource == null)
                    {
                        Console.WriteLine($"Errore: --memoria {options.Memory}: zone info di quel livello non trovata nel dump (usa modello, max o il nome di un livello originale)");
                        return 1;
                    }
                }

                EGameYear? forcedYear = options.Game == null ? null : LevelBuilder.GetGameYear(options.Game);
                if (!BuildRegistration(levelId, registration, sw, updatePath, report, embedded, rename, renamedNamespaces, zoneTemplate, registerIn,
                                       options.SaveMode != "originale", levelHasC3Intro, forcedYear, poolSource, poolFrom))
                {
                    updatePath = null;
                    Increment(counters, "errori");
                }
                else
                {
                    // Gli stessi file di registrazione restano anche nell'archivio del livello, in update/
                    // (non compressi): NST Pak Manager li unisce da solo all'update.pak originale
                    foreach (var (path, data) in embedded)
                    {
                        IgArchiveFile copy = new IgArchiveFile("update/" + path, GameVersion.NSX);
                        copy.SetData(data);
                        output.AddFile(copy);
                    }
                    report.AppendLine($"Installa insieme {Path.GetFileName(outputPath)} e {Path.GetFileName(updatePath)} (sostituisce l'originale); avvio diretto: {levelId}");
                    report.AppendLine("Con NST Pak Manager 1.7 basta il livello: update.pak lo crea l'app dai file update/ del livello");
                }
                report.AppendLine();
            }

            // File che i file convertiti dal PC usano ma che il gioco Switch non ha da nessuna parte (per esempio
            // asset di Crash Team Racing importati nell'editor, o grafica PC saltata): il livello li cerchera'
            // durante il caricamento e potrebbe bloccarsi
            var missingReferences = FindMissingReferences(output, fromSwitch, index.Keys);
            if (missingReferences.Count > 0)
            {
                report.AppendLine($"=== FILE USATI DAL LIVELLO CHE IL GIOCO SWITCH NON HA ({missingReferences.Count}) ===");
                report.AppendLine("Il livello usa oggetti di questi file, che non sono nell'archivio ne' nel gioco Switch: il");
                report.AppendLine("caricamento puo' bloccarsi. Di solito sono asset PC non convertibili (grafica, modelli) o");
                report.AppendLine("importati da altri giochi (Octane = Crash Team Racing).");
                foreach (var (name, users) in missingReferences)
                    report.AppendLine($"  {name}  (usato da {string.Join(", ", users.Take(3).Select(u => NamespaceUtils.GetFileName(u)))}{(users.Count > 3 ? $" e altri {users.Count - 3}" : "")})");
                report.AppendLine();
            }

            // Windows tiene la grafia di un file gia' esistente: un vecchio Custom_Level.pak resterebbe con
            // le maiuscole anche scrivendo custom_level.pak, quindi si toglie prima
            string? outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (outputDir != null && File.Exists(outputPath) &&
                !Path.GetFullPath(outputPath).Equals(Path.GetFullPath(pcPath), StringComparison.OrdinalIgnoreCase))
            {
                string? existing = Directory.EnumerateFiles(outputDir)
                    .FirstOrDefault(f => Path.GetFileName(f).Equals(Path.GetFileName(outputPath), StringComparison.OrdinalIgnoreCase));
                if (existing != null && Path.GetFileName(existing) != Path.GetFileName(outputPath)) File.Delete(existing);
            }

            AppendMemoryReport(report, output, sw, sizeReference);

            output.Save(outputPath);

            if (entityLines.Count > 0)
            {
                report.AppendLine($"=== ENTITA' DEL FILE PRINCIPALE DEL LIVELLO ({entityLines.Count}) ===");
                foreach (string line in entityLines.Take(200)) report.AppendLine(line);
                if (entityLines.Count > 200) report.AppendLine($"  ... e altre {entityLines.Count - 200}");
                report.AppendLine();
            }

            // Nome del file: il gioco Switch cerca archives/<livello in minuscolo>.pak
            string? nameWarning = null;
            var outputPackage = output.Files.Select(f => PackageInfo(f.Path)).FirstOrDefault(p => p != null);
            if (outputPackage != null)
            {
                string expected = outputPackage.Value.name.ToLowerInvariant() + ".pak";
                if (!Path.GetFileName(outputPath).Equals(expected, StringComparison.Ordinal))
                    nameWarning = $"ATTENZIONE: per Eden il file deve chiamarsi {expected} (il gioco cerca il livello in minuscolo); NST Pak Manager lo rinomina da solo";
            }
            if (nameWarning != null) report.AppendLine(nameWarning);

            report.AppendLine($"file nell'archivio: {output.Files.Count}");
            foreach (var (key, count) in counters.OrderBy(e => e.Key)) report.AppendLine($"  {key}: {count}");
            report.AppendLine();
            foreach (string line in lines) report.AppendLine("  " + line);

            File.WriteAllText(reportPath, report.ToString());
            foreach (var (key, count) in counters.OrderBy(e => e.Key)) Console.WriteLine($"  {key}: {count}");
            Console.WriteLine($"Archivio Switch scritto in {outputPath} ({output.Files.Count} file, dettagli in {reportPath})");
            if (updatePath != null) Console.WriteLine($"Registrazione del livello scritta in {updatePath}: installala insieme al livello");
            else if (options.NewLevel) Console.WriteLine("ERRORE: update.pak con la registrazione del livello non creato (vedi il rapporto)");
            if (nameWarning != null) Console.WriteLine(nameWarning);
            if (missingReferences.Count > 0)
            {
                Console.WriteLine($"ATTENZIONE: il livello usa {missingReferences.Count} file che il gioco Switch non ha " +
                                  $"(per esempio {string.Join(", ", missingReferences.Keys.Take(3))}): il caricamento puo' bloccarsi. Elenco nel rapporto.");
            }
            return 0;
        }

        /// <summary>
        /// Namespace usati dai file convertiti dal PC (riferimenti negli oggetti e dipendenze) che non
        /// corrispondono a nessun file dell'archivio di uscita ne' del gioco Switch. Chiave: nome, valore:
        /// file che lo usano.
        /// </summary>
        private static SortedDictionary<string, SortedSet<string>> FindMissingReferences(IgArchive output, List<IgArchiveFile> fromSwitch,
                                                                                         IEnumerable<string> switchPaths)
        {
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddName(string path)
            {
                foreach (string name in NameAliases(NamespaceUtils.GetFileName(path, false).ToLowerInvariant())) available.Add(name);
            }
            foreach (string path in switchPaths) AddName(path);
            foreach (IgArchiveFile file in output.Files) AddName(file.Path);

            var originals = new HashSet<string>(fromSwitch.Select(f => f.Path.ToLowerInvariant()));
            var missing = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (IgArchiveFile file in output.Files)
            {
                string lower = file.Path.ToLowerInvariant();
                if (!file.IsIGZ() || originals.Contains(lower) || lower.StartsWith("update/")) continue;

                HashSet<string> references;
                try
                {
                    references = RawDependencies(file.Uncompress());
                }
                catch
                {
                    continue;
                }

                foreach (string name in references)
                {
                    // Solo nomi di file (i percorsi completi sono presenti anche come nome); i numeri sono hash
                    if (name.Length == 0 || name.Contains('/') || name.Contains(':') || name.All(char.IsDigit)) continue;
                    if (name.StartsWith("meta")) continue;  // metaobject, metafield, ...: tipi del motore, non file
                    if (available.Contains(name)) continue;
                    if (!missing.TryGetValue(name, out SortedSet<string>? users))
                    {
                        users = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        missing[name] = users;
                    }
                    users.Add(file.Path);
                }
            }
            return missing;
        }

        // ------------------------------------------------------------------ livello nuovo

        private static void ReplaceFile(IgArchive archive, string path, byte[] data)
        {
            foreach (IgArchiveFile old in archive.Files.Where(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                archive.RemoveFile(old);
            }
            IgArchiveFile file = new IgArchiveFile(path, GameVersion.NSX);
            file.SetData(data);
            archive.AddFile(file);
        }

        /// <summary>
        /// Come il tasto Play dell'editor per un livello nuovo: update.pak (quello originale Switch) con
        /// la zone info del livello e il file del pacchetto di chunkInfos che la elenca tra i file
        /// da caricare all'avvio. Cosi' il gioco conosce il livello anche se non ha il nome di uno originale.
        /// </summary>
        private static bool BuildRegistration(string levelId, List<IgArchiveFile> updateFiles, ArchiveIndex sw, string updatePath, StringBuilder report,
                                              List<(string path, byte[] data)> embedded, LevelRename? rename,
                                              Dictionary<string, string> renamedNamespaces, string? zoneInfoTemplate, string registerIn,
                                              bool ownSave, bool levelHasC3Intro, EGameYear? forcedYear,
                                              CZoneInfo? poolSource, string? poolFrom)
        {
            string zoneInfoPath = $"maps/{levelId}_zoneinfo.igz";
            report.AppendLine($"REGISTRAZIONE DEL LIVELLO ({Path.GetFileName(updatePath)})");

            // --zoneinfo-da: zone info di un livello originale Switch al posto di quella del PC
            IgArchiveFile? template = null;
            if (zoneInfoTemplate != null)
            {
                template = FindZoneInfo(sw, zoneInfoTemplate);
                if (template == null)
                {
                    report.AppendLine($"  ERRORE: --zoneinfo-da: zone info di {zoneInfoTemplate} non trovata nel dump");
                    return false;
                }
            }

            IgArchive update = new IgArchive(updatePath, GameVersion.NSX);
            if (sw.ByArchive.TryGetValue(registerIn, out List<IgArchiveFile>? originals))
            {
                foreach (IgArchiveFile file in originals) update.AddFile(file.Clone());
                long size = originals.Sum(f => (long)f.GetData().Length);
                report.AppendLine($"  {registerIn}.pak originale Switch: {originals.Count} file ({size / 1024} KB)");
            }
            else if (registerIn == "chunkinfos")
            {
                report.AppendLine("  ERRORE: chunkInfos.pak non trovato nel dump");
                return false;
            }
            else
            {
                report.AppendLine("  update.pak originale non trovato nel dump: il nuovo contiene solo la registrazione");
            }

            // Zone info e altri file update/ del livello, convertiti
            var zoneInfos = new List<string>();
            foreach (IgArchiveFile file in updateFiles)
            {
                string path = file.Path.Substring("update/".Length);
                // Livello rinominato (nome dell'archivio di uscita): anche zone info e riferimenti
                string newPath = rename != null ? rename.Apply(path) : path;
                bool renamed = newPath != path;
                string? newNamespace = renamed ? NamespaceUtils.GetFileName(newPath, false) : null;
                bool isZoneInfo = path.EndsWith("_zoneinfo.igz", StringComparison.OrdinalIgnoreCase);
                if (isZoneInfo && template != null)
                {
                    report.AppendLine($"  zone info del PC non usata ({path}): --zoneinfo-da {zoneInfoTemplate}");
                    try
                    {
                        if (file.ToIgzFile().FindObject<CZoneInfo>() is CZoneInfo pcZone) report.AppendLine("    PC: " + DescribeZoneInfo(pcZone));
                    }
                    catch (Exception e)
                    {
                        report.AppendLine($"    PC: non leggibile ({e.Message})");
                    }
                    continue;
                }
                // La zone info del livello si registra con il percorso usato dal gioco (minuscolo)
                string renamedPath = isZoneInfo && newPath.Equals(zoneInfoPath, StringComparison.OrdinalIgnoreCase) ? zoneInfoPath : newPath;
                try
                {
                    byte[] data;
                    if (file.IsIGZ())
                    {
                        IgzFile igz = file.ToIgzFile();
                        if (rename != null)
                        {
                            int count = RenameNamespaces(igz, renamedNamespaces);
                            if (count > 0) report.AppendLine($"  riferimenti al livello rinominati: {count} in {renamedPath}");
                        }
                        if (isZoneInfo)
                        {
                            zoneInfos.Add(renamedPath);
                            if (rename != null) RenameZoneInfo(igz, rename, levelId, report);
                            AdjustZoneInfo(igz, report);
                        }
                        igz.GameVersion = GameVersion.NSX;
                        data = newNamespace != null ? igz.Save(newNamespace) : igz.Save();
                    }
                    else
                    {
                        data = file.Uncompress();
                    }
                    ReplaceFile(update, renamedPath, data);
                    embedded.Add((renamedPath, data));
                    report.AppendLine(renamed ? $"  aggiunto: {path} -> {renamedPath}" : $"  aggiunto: {path}");
                }
                catch (Exception e)
                {
                    report.AppendLine($"  ERRORE {file.Path}: {e.Message}");
                }
            }

            if (template != null)
            {
                IgzFile igz = template.ToIgzFile();
                CZoneInfo? zoneInfo = igz.FindObject<CZoneInfo>();
                if (zoneInfo == null)
                {
                    report.AppendLine($"  ERRORE: {template.Path} non contiene CZoneInfo");
                    return false;
                }
                report.AppendLine($"    {template.Path}: " + DescribeZoneInfo(zoneInfo));
                string? oldName = zoneInfo._name;
                zoneInfo._name = levelId;
                if (ownSave) zoneInfo._saveName = levelId.Substring(levelId.LastIndexOf('/') + 1);
                // --gioco: come "Crash Mode" nell'editor, cambia solo il gioco (anno) della zone info
                if (forcedYear != null && zoneInfo._year != forcedYear.Value)
                {
                    report.AppendLine($"  gioco cambiato con --gioco: {zoneInfo._year} -> {forcedYear.Value}");
                    zoneInfo._year = forcedYear.Value;
                }
                // --memoria: aree di memoria del livello prese da un'altra zone info originale
                if (poolSource != null)
                {
                    zoneInfo._levelPoolSize ??= new igSizeTypeMetaField();
                    zoneInfo._globalChunkPoolSize ??= new igSizeTypeMetaField();
                    report.AppendLine($"  memoria del livello da {poolFrom} (--memoria): livello {Mb(zoneInfo._levelPoolSize._size)} -> " +
                                      $"{Mb(poolSource._levelPoolSize?._size ?? 0)}, globale {Mb(zoneInfo._globalChunkPoolSize._size)} -> " +
                                      $"{Mb(poolSource._globalChunkPoolSize?._size ?? 0)}");
                    if (poolSource._levelPoolSize != null) zoneInfo._levelPoolSize._size = poolSource._levelPoolSize._size;
                    if (poolSource._globalChunkPoolSize != null) zoneInfo._globalChunkPoolSize._size = poolSource._globalChunkPoolSize._size;
                }
                // Zone info di Crash 3 presa da un livello originale con l'intro accesa: Crash non compariva (Oichi, v24);
                // spenta, Crash compare e l'intro del livello (Crash che esce dal portale) parte lo stesso (v25)
                if (zoneInfo._year == EGameYear.eGY_2017_Crash3 && zoneInfo._flags._magicMomentIntro)
                {
                    zoneInfo._flags._magicMomentIntro = false;
                    report.AppendLine("  intro della zone info spenta (magic moment); intro di Crash 3 nel livello: " +
                                      (levelHasC3Intro ? "si'" : "no, Crash compare senza (--aggiungi-intro per aggiungerla)"));
                }
                igz.GameVersion = GameVersion.NSX;
                byte[] zoneInfoData = igz.Save(NamespaceUtils.GetFileName(zoneInfoPath, false));
                ReplaceFile(update, zoneInfoPath, zoneInfoData);
                embedded.Add((zoneInfoPath, zoneInfoData));
                zoneInfos.Add(zoneInfoPath);
                report.AppendLine($"  zone info presa dall'originale Switch {template.Path}: nome {oldName} -> {levelId}, " +
                                  $"nome mostrato '{zoneInfo._displayName}' (come l'originale)");
                report.AppendLine(ownSave
                    ? $"  voce di salvataggio propria: '{zoneInfo._saveName}'"
                    : $"  voce di salvataggio: '{zoneInfo._saveName}', la stessa del livello originale (gemme e tempi finiscono li')");
            }

            if (!zoneInfos.Any(z => z.Equals(zoneInfoPath, StringComparison.OrdinalIgnoreCase)))
            {
                string game = levelId.Substring(0, levelId.IndexOf('/'));
                (CZoneInfo zoneInfo, igLocalizedInfo localizedInfo) = ObjectFactory.CreateZoneInfo(levelId, LevelBuilder.GetGameYear(game));
                zoneInfo._zoneVehicle = null;
                IgzFile igz = new IgzFile(zoneInfoPath, [zoneInfo, localizedInfo], GameVersion.NSX);
                byte[] zoneInfoData = igz.Save();
                ReplaceFile(update, zoneInfoPath, zoneInfoData);
                embedded.Add((zoneInfoPath, zoneInfoData));
                zoneInfos.Add(zoneInfoPath);
                report.AppendLine($"  zone info creata (il livello non ne aveva una): {zoneInfoPath}");
            }

            // File del pacchetto di chunkInfos: la versione di update.pak, se c'e', altrimenti quella di chunkInfos.pak
            IgArchiveFile? chunkPackage = null;
            if (sw.ByArchive.TryGetValue("chunkinfos", out List<IgArchiveFile>? chunkFiles))
            {
                List<IgArchiveFile> packages = chunkFiles.Where(f => f.Path.EndsWith("_pkg.igz", StringComparison.OrdinalIgnoreCase)).ToList();
                if (packages.Count == 1) chunkPackage = packages[0];
            }
            if (chunkPackage == null)
            {
                report.AppendLine("  ERRORE: chunkInfos.pak (o il suo file _pkg.igz) non trovato nel dump");
                return false;
            }
            IgArchiveFile source = update.Files.FirstOrDefault(f => f.Path.Equals(chunkPackage.Path, StringComparison.OrdinalIgnoreCase)) ?? chunkPackage;
            IgzFile packageIgz = source.ToIgzFile();
            igStreamingChunkInfo? chunkInfo = packageIgz.FindObject<igStreamingChunkInfo>();
            if (chunkInfo == null)
            {
                report.AppendLine($"  ERRORE: {chunkPackage.Path} non contiene igStreamingChunkInfo");
                return false;
            }
            foreach (string zoneInfo in zoneInfos)
            {
                if (chunkInfo._required._data.Any(e => e._name != null && e._name.Equals(zoneInfo, StringComparison.OrdinalIgnoreCase))) continue;
                chunkInfo._required._data.Add(new ChunkFileInfoMetaField() { _type = "igx_file", _name = zoneInfo.ToLowerInvariant() });
                report.AppendLine($"  registrata in {chunkPackage.Path}: {zoneInfo}");
            }
            packageIgz.GameVersion = GameVersion.NSX;
            byte[] packageData = packageIgz.Save();
            ReplaceFile(update, chunkPackage.Path, packageData);
            embedded.Add((chunkPackage.Path, packageData));

            update.Save(updatePath);
            report.AppendLine($"  scritto {updatePath} ({update.Files.Count} file)");
            return true;
        }

        /// <summary>
        /// Copia nel file principale del livello l'intro dei livelli di Crash 3 (IntroCutsceneSequencePlayer di
        /// L321_GoneTomorrow, dal dump Switch) con le stesse impostazioni che usa l'editor per un livello nuovo.
        /// Restituisce i file di Gone Tomorrow che servono all'intro (script, comportamenti, ...).
        /// </summary>
        private static List<IgArchiveFile> AddC3Intro(IgzFile destIgz, string switchDir, IgArchive output, out string info)
        {
            string pakPath = PakFiles(switchDir).FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals("l321_gonetomorrow", StringComparison.OrdinalIgnoreCase))
                             ?? throw new FileNotFoundException("l321_gonetomorrow.pak non trovato nel dump Switch");
            IgArchive source = IgArchive.Open(pakPath);
            IgArchiveFile sourceFile = source.Files.FirstOrDefault(f => NamespaceUtils.GetFileName(f.Path, false).Equals("L321_GoneTomorrow", StringComparison.OrdinalIgnoreCase) &&
                                                                        f.Path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
                                       ?? throw new FileNotFoundException("file principale di Gone Tomorrow non trovato in " + Path.GetFileName(pakPath));
            IgzFile sourceIgz = sourceFile.ToIgzFile();
            CEntity intro = sourceIgz.FindObject<CEntity>("IntroCutsceneSequencePlayer")
                            ?? throw new InvalidDataException("IntroCutsceneSequencePlayer non trovato in " + sourceFile.Path);
            common_C3_IntroSequenceData introData = intro.GetComponent<common_C3_IntroSequenceData>()
                            ?? throw new InvalidDataException("IntroCutsceneSequencePlayer senza common_C3_IntroSequenceData");

            // Come LevelBuilder.CreateNewLevel per un livello nuovo di Crash 3
            introData._BehaviorEventCrashIntro = "Cutscene_Crash2_Portal_Exit_Victory";
            introData._Float_0x30 = 1.0f;
            introData._Float_0x34 = 1.0f;
            introData._Float_0x40 = 2.45f;
            introData._Float_0x4c = 300;
            CEntityHandleList? shots = sourceIgz.FindObject<CEntityHandleList>("IntroCutsceneSequencePlayer_entityData_componentData_CommonCutsceneSequencePlayer_CutsceneSequenceShotList001");
            if (shots != null) shots._data.Clear();
            else if (intro.GetComponent<common_CutsceneSequencePlayerData>() is common_CutsceneSequencePlayerData player)
                player._CutsceneSequenceShotList.Reference = null;

            IgzFile.Clone(intro, source, output, sourceIgz, destIgz, out List<IgArchiveFile> dependencies);
            // il file principale di Gone Tomorrow no: porterebbe nel livello tutte le sue entita'
            dependencies.RemoveAll(d => d.Path.Equals(sourceFile.Path, StringComparison.OrdinalIgnoreCase));
            info = $"da {sourceFile.Path}, {dependencies.Count} file collegati";
            return dependencies;
        }

        private static string Mb(ulong bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";

        /// <summary>
        /// --gioco: crash1/crash2/crash3 (anche 1, 2, 3 o c3); "" se non valido
        /// </summary>
        private static string NormalizeGame(string value)
        {
            string v = value.Trim().ToLowerInvariant().Replace(" ", "");
            if (v.StartsWith("crash")) v = v.Substring(5);
            else if (v.StartsWith("c")) v = v.Substring(1);
            return v is "1" or "2" or "3" ? "crash" + v : "";
        }

        /// <summary>
        /// Zone info originale con l'area di memoria del livello piu' grande (--memoria max)
        /// </summary>
        private static (CZoneInfo? zone, string? level) LargestPoolZoneInfo(ArchiveIndex sw)
        {
            CZoneInfo? best = null;
            string? bestLevel = null;
            foreach (var (path, file) in sw.ByPath)
            {
                if (!path.StartsWith("maps/") || !path.EndsWith("_zoneinfo.igz")) continue;
                try
                {
                    if (file.ToIgzFile().FindObject<CZoneInfo>() is not CZoneInfo zone || zone._levelPoolSize == null) continue;
                    if (best == null || zone._levelPoolSize._size > best._levelPoolSize!._size)
                    {
                        best = zone;
                        bestLevel = NamespaceUtils.GetFileName(path, false);
                    }
                }
                catch
                {
                    // zone info non leggibile: si salta
                }
            }
            if (bestLevel != null && bestLevel.EndsWith("_zoneinfo")) bestLevel = bestLevel.Substring(0, bestLevel.Length - "_zoneinfo".Length);
            return (best, bestLevel);
        }

        /// <summary>
        /// Quanto pesa il livello (file che il gioco carica, non compressi), diviso per provenienza, confrontato con il
        /// livello originale da cui viene la zone info (o quello sostituito) e con i livelli originali piu' grandi
        /// </summary>
        private static void AppendMemoryReport(StringBuilder report, IgArchive output, ArchiveIndex sw, string? reference)
        {
            var files = output.Files.Where(f => !f.Path.StartsWith("update/", StringComparison.OrdinalIgnoreCase)).ToList();
            long total = files.Sum(f => (long)f.UncompressedSize);
            string? ownDir = output.Files.Select(f => PackageInfo(f.Path)).FirstOrDefault(p => p != null)?.dir.ToLowerInvariant();

            report.AppendLine("=== MEMORIA ===");
            report.AppendLine($"Il livello carica {Mb((ulong)total)} di file ({files.Count} file, non compressi).");
            var groups = new Dictionary<string, long>();
            foreach (IgArchiveFile f in files)
            {
                string[] parts = f.Path.ToLowerInvariant().Split('/');
                string group = parts.Length >= 4 && parts[0] == "maps"
                    ? (parts[1] + "/" + parts[2] == ownDir ? "cartella del livello" : "altro livello: " + parts[2])
                    : parts.Length >= 2 ? parts[0] + "/" : "altro";
                groups[group] = groups.GetValueOrDefault(group) + f.UncompressedSize;
            }
            foreach (var (group, size) in groups.OrderByDescending(g => g.Value).Take(15))
                report.AppendLine($"  {Mb((ulong)size),10}  {group}");
            int otherLevels = groups.Keys.Count(k => k.StartsWith("altro livello: "));
            if (otherLevels > 0)
                report.AppendLine($"  ({otherLevels} altri livelli: l'editor copia per intero i file da cui prendi anche un solo oggetto)");
            report.AppendLine("File piu' grandi:");
            foreach (IgArchiveFile f in files.OrderByDescending(x => x.UncompressedSize).Take(10))
                report.AppendLine($"  {Mb((ulong)f.UncompressedSize),10}  {f.Path}");

            // Livelli originali per confronto (archivi Lxxx_/Bxxx_ del dump)
            var originals = sw.ByArchive
                .Where(a => a.Key.Length > 5 && (a.Key[0] == 'l' || a.Key[0] == 'b') && char.IsDigit(a.Key[1]) && a.Key[4] == '_')
                .Select(a => (name: a.Key, size: a.Value.Sum(f => (long)f.UncompressedSize)))
                .OrderByDescending(a => a.size)
                .ToList();
            if (originals.Count > 0)
            {
                string? refKey = reference?.ToLowerInvariant();
                var refLevel = originals.FirstOrDefault(o => o.name == refKey);
                if (refKey != null && refLevel.name == refKey)
                {
                    report.AppendLine($"Livello originale di riferimento {reference}: {Mb((ulong)refLevel.size)}.");
                    if (total > refLevel.size)
                        report.AppendLine($"ATTENZIONE: il livello e' {(double)total / refLevel.size:0.0} volte {reference}: con la memoria di quel livello " +
                                          "potrebbe rallentare o chiudersi sulla Switch. Prova --memoria max e togli oggetti presi da altri livelli.");
                }
                report.AppendLine("Livelli originali piu' grandi: " +
                                  string.Join(", ", originals.Take(3).Select(o => $"{o.name} {Mb((ulong)o.size)}")));
                if (total > originals[0].size)
                    report.AppendLine("ATTENZIONE: il livello e' piu' grande di tutti i livelli originali.");
            }
            report.AppendLine();
        }

        /// <summary>
        /// Anno (gioco) della zone info del livello PC tra i file update/, null se manca o non si legge
        /// </summary>
        private static EGameYear? PcZoneInfoYear(List<IgArchiveFile> updateFiles)
        {
            foreach (IgArchiveFile file in updateFiles)
            {
                if (!file.Path.EndsWith("_zoneinfo.igz", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (file.ToIgzFile().FindObject<CZoneInfo>() is CZoneInfo zone) return zone._year;
                }
                catch
                {
                    // zone info non leggibile: si decide dalla cartella
                }
            }
            return null;
        }

        private static string DescribeZoneInfo(CZoneInfo zone)
        {
            return $"nome '{zone._name}', anno {zone._year}, build '{zone._build}', personaggio '{zone._overrideCharacter}', " +
                   $"veicolo '{zone._zoneVehicle}', intro {zone._flags._magicMomentIntro}, boss {zone._flags._isBoss}, menu {zone._flags._isMenu}, " +
                   $"salvataggio '{zone._saveName}', caricamento '{zone._loadScreenName}'/'{zone._loadMovieName}', " +
                   $"memoria livello {Mb(zone._levelPoolSize?._size ?? 0)}, globale {Mb(zone._globalChunkPoolSize?._size ?? 0)}";
        }

        /// <summary>
        /// Zone info di un livello originale Switch (maps/.../<livello>_zoneinfo.igz), null se manca
        /// </summary>
        private static IgArchiveFile? FindZoneInfo(ArchiveIndex sw, string level)
        {
            string suffix = "/" + level.ToLowerInvariant() + "_zoneinfo.igz";
            return sw.ByPath.Where(e => e.Key.StartsWith("maps/") && e.Key.EndsWith(suffix)).Select(e => e.Value).FirstOrDefault();
        }

        /// <summary>
        /// Zone info di un livello rinominato: il gioco la cerca per nome (findZoneInfoByName con
        /// l'identificativo del livello), quindi nome, nome del salvataggio e voce del menu di debug
        /// prendono il nome nuovo
        /// </summary>
        private static void RenameZoneInfo(IgzFile igz, LevelRename rename, string levelId, StringBuilder report)
        {
            CZoneInfo? zoneInfo = igz.FindObject<CZoneInfo>();
            if (zoneInfo == null) return;
            string? oldName = zoneInfo._name;
            zoneInfo._name = levelId;
            if (zoneInfo._saveName != null) zoneInfo._saveName = ReplaceIgnoreCase(zoneInfo._saveName, rename.FromName, rename.ToName).ToLowerInvariant();
            if (zoneInfo._debugMenuName != null) zoneInfo._debugMenuName = ReplaceIgnoreCase(zoneInfo._debugMenuName, rename.FromName, rename.ToName);
            report.AppendLine($"  zone info rinominata: {oldName} -> {levelId}");
        }

        /// <summary>
        /// Le stesse correzioni dell'editor alla zone info di un livello nuovo (veicolo dalle opzioni speciali)
        /// </summary>
        private static void AdjustZoneInfo(IgzFile igz, StringBuilder report)
        {
            CZoneInfo? zoneInfo = igz.FindObject<CZoneInfo>();
            if (zoneInfo == null) return;

            List<string> options = GameplayModeManager.GetSpecialZoneInfoOptions(zoneInfo._build);
            zoneInfo._zoneVehicle = options.Contains("jetski") ? "CocoJetski" : options.Contains("plane") ? "CrashCocoPlane" : null;
            if (options.Count > 0)
            {
                report.AppendLine($"  ATTENZIONE: opzioni speciali del livello ({string.Join(", ", options)}): i dati del personaggio/hub " +
                                  "che l'editor crea per queste opzioni non vengono ancora convertiti");
            }
        }

        private static bool IsGraphicsType(string typeName) => _graphicsTypes.Contains(typeName) || typeName.EndsWith("Material");
    }
}
