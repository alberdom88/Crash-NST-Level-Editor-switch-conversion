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
    ///   NST.exe --switch converti &lt;pc.pak&gt; &lt;switch_dump_folder&gt; &lt;output.pak&gt; [report.txt] [opzioni]
    ///       Builds a Switch archive: unmodified assets are taken from the Switch game, the files of
    ///       the level are converted from the PC version. --nuovo registers it as a new level (update.pak),
    ///       --come-originale / --sostituisci put it in place of an original level. All the options are in
    ///       PrintUsage and in README_SWITCH.md.
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
            // --nuovo: non riportare nella zone info presa da un originale la modalita' scelta nell'editor
            // (personaggio, veicolo, opzioni speciali come {boulder} e i dati del personaggio che le attivano)
            public bool WithoutMode;
            // converti: dei file di altri livelli tiene solo gli oggetti che il livello usa, e toglie la grafica
            // (modelli, materiali, texture) che dopo non usa piu' nessuno (prova)
            public bool OnlyUsed;
            // converti: le dipendenze della grafica che mancano si prendono anche dall'archivio di un altro livello quando
            // l'archivio Switch del file che le usa non le ha (come fino alla v59; dalla v60 no: Level 7, schermo nero)
            public bool DependenciesFromOtherLevels;
            // --nuovo: archivio in cui registrare il livello: "update" (update.pak, come l'editor PC) o
            // "chunkinfos" (copia di chunkInfos.pak, dove il gioco tiene le zone info dei suoi livelli)
            public string RegisterIn = "update";
            // --nuovo: gioco del livello ("crash1", "crash2", "crash3") al posto di quello scelto nell'editor (Crash Mode)
            public string? Game;
            // --nuovo senza --gioco: Game e' il gioco impostato nell'editor (Crash Mode), diverso dalla cartella del livello
            public bool GameFromEditor;
            // --nuovo: memoria del livello nella zone info: "modello" (quella del livello originale da cui viene la
            // zone info), "max" (la piu' grande tra i livelli originali), il nome di un livello originale o i MB (dalla v56)
            public string Memory = "modello";
            // converti: MB di texture degli archivi dei livelli originali, che nessun file usa, aggiunte al pacchetto (prova, dalla
            // v57): con le stesse entita' il livello carica piu' texture, per capire se lo schermo nero viene dalla memoria
            public int? TestTexturesMb;
            // converti: file dell'archivio (testo nel percorso) di cui il rapporto descrive gli oggetti, tipo, nome e campi (dalla v59)
            public List<string> Describe = [];
            // converti: entita' da togliere dai file di altri livelli copiati dall'editor, "[file:]testo" (testo nel
            // nome, nel tipo, nei componenti o nei file che usa; * = tutte), per trovare quelle che bloccano il gioco
            public List<string> RemoveEntities = [];
            // converti: file di altri livelli (testo nel percorso) presi dall'originale Switch invece di convertire
            // la copia del PC, con le loro dipendenze dallo stesso archivio Switch
            public List<string> SwitchOriginals = [];
            // converti: non togliere le entita' con un comportamento prese da livelli con una modalita' che il livello
            // non ha (sub, tigre, orso, moto d'acqua...) e dai boss (dalla v39 si tolgono da sole)
            public bool KeepModeEntities;
            // converti: non spegnere le occlusioni (COcclusionTrigger, COcclusionBox) dei file convertiti: sulla console nascondono
            // decorazioni, pensate per il livello da cui vengono (dalla v49 si spengono da sole)
            public bool KeepOcclusion;
            // --nuovo: lascia com'e' il gestore delle tessere dei bonus di Crash 1 (Crash1_BonusRoundManager), che fuori da Crash 1
            // si toglie: senza, per un livello con le tessere di un altro gioco va in update.pak una copia che resta (dalla v51)
            public bool TokensOnlyCrash1;
            // --nuovo: con le tessere fuori da Crash 1 non cambiare l'HUD di Crash (CrashBandicoot_HUD): le tessere si contano ma
            // in alto non si vedono (dalla v52)
            public bool TokensNoHud;
            // converti: l'intro di Crash 3 aggiunta dal convertitore come la aggiunge l'editor quando passi un livello a Crash 3
            // (valori di Gone Tomorrow, ritardo 0.5) invece che come in un livello nuovo dell'editor (dalla v52)
            public bool IntroLikeEditor;
            // converti: "Spawn offset" (_Float_0x4c) dell'intro di Crash 3 del livello, aggiunta o gia' nel livello (dalla v52)
            public float? IntroSpawnOffset;
            // converti: l'intro di Crash 3 aggiunta dal convertitore resta dov'e' in Gone Tomorrow. Dalla v55 e' il comportamento
            // normale (resta valida per compatibilita'): vedi IntroMoveToStart
            public bool IntroKeepPosition;
            // converti: l'intro di Crash 3 aggiunta si sposta alla partenza del livello (v54; nella v52 e v53 il bolt la lasciava
            // ferma). Con il Level 7 spostata (v54) il caricamento si e' bloccato, e la posizione dell'intro non conta per lo script,
            // che fa comparire Crash dove sta Crash: dalla v55 solo con --intro-alla-partenza
            public bool IntroMoveToStart;
            // converti: intro "come in Crash 2" (prova, dalla v53): l'intro di Crash 3, che con la zone info di Crash 3 serve a far
            // comparire Crash, senza il portale visibile, con Spawn offset a 0 e con l'animazione di comparsa di Crash 2 presa dai dati
            // di Crash (Crash_RespawnAnimationManager), se si riconosce
            public bool IntroCrash2;
            // converti: come IntroCrash2 ma con l'animazione di comparsa di Crash 1 ('SpawnIn') (dalla v54)
            public bool IntroCrash1;
            // converti: evento di Crash dell'intro di Crash 3 (_BehaviorEventCrashIntro, l'animazione con cui compare) (dalla v53)
            public string? IntroEvent;
            // converti: l'intro di Crash 3 senza il portale visibile: l'effetto resta, uguale, ma lontanissimo (Animation offset,
            // come --intro-crash1/2 nella v55: con il Level 7 e Crash 3 il caricamento si e' bloccato)
            public bool IntroNoPortal;
            // converti: portale nascosto come nella v54 (Animation size 0.01, ai piedi di Crash), per le prove (dalla v55)
            public bool IntroTinyPortal;
            // converti: l'intro di Crash 3 senza l'effetto del portale (_Vfx_Effect vuoto, dalla v54). Nella v53 e dalla v56 e'
            // anche quello che fanno --intro-crash1/2: con il Level 7 e' l'unico modo in cui il caricamento e' finito (lo schermo
            // nero dopo il caricamento c'e' anche con --gioco crash1, senza intro: e' del livello)
            public bool IntroRemovePortal;
            // --nuovo: riflessi planari nella zone info ("auto": accesi se il livello ha i file dei riflessi di un livello con il
            // ghiaccio che riflette, come Snow Go; "si"; "no")
            public string Reflections = "auto";
            // converti: modello da mostrare sui blocchi di collisione (CDynamicClipEntity) dei file di altri livelli,
            // "<testo nel percorso>=<modello>" (per esempio L218=Levels\Crash2\...\Modello)
            public List<(string file, string model)> ClipModels = [];
            // converti: sui blocchi di collisione dei file con il testo nel percorso si mette una copia di una decorazione del
            // livello (un pavimento), grande come il blocco: "<testo>" (decorazione scelta da sola) o "<testo>=<testo nel nome
            // della decorazione da copiare>"
            public List<(string file, string template)> FloorBlocks = [];
            // Errori nella riga di comando (opzioni sconosciute, valori mancanti o non validi)
            public List<string> Errors = [];
        }

        // Opzioni che vogliono un valore
        private static readonly HashSet<string> _valueOptions =
        [
            "--max", "--pak", "--pc-originali", "--igz", "--sostituisci", "--base", "--altri-livelli", "--zoneinfo-da",
            "--registra-in", "--salvataggio", "--escludi", "--ctr", "--gioco", "--memoria", "--togli-entita", "--originale-switch",
            "--riflessi", "--modello-blocchi", "--pavimento-blocchi", "--intro-scostamento", "--intro-evento", "--prova-texture", "--descrivi",
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
                List<string> rest = args.Skip(1).Select(CleanPath).ToList();
                Options options = ParseOptions(rest);
                if (options.Errors.Count > 0)
                {
                    foreach (string error in options.Errors) Console.WriteLine("Errore: " + error);
                    PrintUsage();
                    return 1;
                }

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

                // un'opzione che vuole un valore non prende l'opzione successiva (--escludi --solo-usati)
                if (_valueOptions.Contains(name) && (!hasValue || rest[i + 1].StartsWith("--")))
                {
                    options.Errors.Add($"{rest[i]} vuole un valore");
                    rest.RemoveAt(i);
                    continue;
                }

                if (name == "--max" && hasValue)
                {
                    if (int.TryParse(rest[i + 1], out int max) && max > 0) options.Max = max;
                    else options.Errors.Add($"--max {rest[i + 1]}: serve un numero");
                    rest.RemoveRange(i, 2);
                }
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
                else if (name == "--senza-modalita") { options.WithoutMode = true; rest.RemoveAt(i); }
                else if (name == "--solo-usati") { options.OnlyUsed = true; rest.RemoveAt(i); }
                else if (name == "--dipendenze-altri-livelli") { options.DependenciesFromOtherLevels = true; rest.RemoveAt(i); }
                else if (name == "--tieni-entita-modalita") { options.KeepModeEntities = true; rest.RemoveAt(i); }
                else if (name == "--tieni-occlusioni") { options.KeepOcclusion = true; rest.RemoveAt(i); }
                else if (name == "--tessere-solo-crash1") { options.TokensOnlyCrash1 = true; rest.RemoveAt(i); }
                else if (name == "--tessere-senza-hud") { options.TokensNoHud = true; rest.RemoveAt(i); }
                else if (name == "--intro-come-editor") { options.IntroLikeEditor = true; rest.RemoveAt(i); }
                else if (name == "--intro-posizione-originale") { options.IntroKeepPosition = true; rest.RemoveAt(i); }
                else if (name == "--intro-alla-partenza") { options.IntroMoveToStart = true; rest.RemoveAt(i); }
                else if (name == "--intro-portale-piccolo") { options.IntroTinyPortal = true; rest.RemoveAt(i); }
                else if (name == "--intro-crash2") { options.IntroCrash2 = true; rest.RemoveAt(i); }
                else if (name == "--intro-crash1") { options.IntroCrash1 = true; rest.RemoveAt(i); }
                else if (name == "--intro-senza-portale") { options.IntroNoPortal = true; rest.RemoveAt(i); }
                else if (name == "--intro-togli-portale") { options.IntroRemovePortal = true; rest.RemoveAt(i); }
                else if (name == "--intro-evento" && hasValue) { options.IntroEvent = rest[i + 1]; rest.RemoveRange(i, 2); }
                else if (name == "--intro-scostamento" && hasValue)
                {
                    if (float.TryParse(rest[i + 1].Replace(',', '.'), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out float offset))
                        options.IntroSpawnOffset = offset;
                    else
                        options.Errors.Add($"--intro-scostamento {rest[i + 1]}: serve un numero (per esempio 0 o 300)");
                    rest.RemoveRange(i, 2);
                }
                else if (name == "--riflessi" && hasValue) { options.Reflections = rest[i + 1].ToLowerInvariant(); rest.RemoveRange(i, 2); }
                else if (name == "--pavimento-blocchi" && hasValue)
                {
                    string value = rest[i + 1];
                    int equals = value.IndexOf('=');
                    if (equals == 0 || (equals > 0 && equals == value.Length - 1)) options.Errors.Add($"--pavimento-blocchi {value}: serve <testo del file> o <testo del file>=<nome della decorazione>");
                    else if (equals < 0) options.FloorBlocks.Add((value.Trim(), ""));
                    else options.FloorBlocks.Add((value.Substring(0, equals).Trim(), value.Substring(equals + 1).Trim()));
                    rest.RemoveRange(i, 2);
                }
                else if (name == "--modello-blocchi" && hasValue)
                {
                    string value = rest[i + 1];
                    int equals = value.IndexOf('=');
                    if (equals <= 0 || equals == value.Length - 1) options.Errors.Add($"--modello-blocchi {value}: serve <testo del file>=<modello>");
                    else options.ClipModels.Add((value.Substring(0, equals).Trim(), value.Substring(equals + 1).Trim()));
                    rest.RemoveRange(i, 2);
                }
                else if (name == "--gioco" && hasValue) { options.Game = NormalizeGame(rest[i + 1]); rest.RemoveRange(i, 2); }
                else if (name == "--memoria" && hasValue) { options.Memory = rest[i + 1]; rest.RemoveRange(i, 2); }
                else if (name == "--prova-texture" && hasValue)
                {
                    if (int.TryParse(rest[i + 1], out int megabytes) && megabytes >= 1 && megabytes <= 1000) options.TestTexturesMb = megabytes;
                    else options.Errors.Add($"--prova-texture {rest[i + 1]}: servono i MB, da 1 a 1000 (per esempio 50)");
                    rest.RemoveRange(i, 2);
                }
                else if (name == "--togli-entita" && hasValue) { options.RemoveEntities.Add(rest[i + 1]); rest.RemoveRange(i, 2); }
                else if (name == "--descrivi" && hasValue) { options.Describe.Add(rest[i + 1]); rest.RemoveRange(i, 2); }
                else if (name == "--originale-switch" && hasValue) { options.SwitchOriginals.Add(rest[i + 1]); rest.RemoveRange(i, 2); }
                else i++;
            }

            // Un'opzione scritta male finirebbe tra i file (per esempio come nome del rapporto) e verrebbe ignorata
            foreach (string unknown in rest.Where(a => a.StartsWith("--"))) options.Errors.Add($"opzione sconosciuta: {unknown}");
            rest.RemoveAll(a => a.StartsWith("--"));

            if (options.RegisterIn != "update" && options.RegisterIn != "chunkinfos")
                options.Errors.Add($"--registra-in {options.RegisterIn}: valori possibili update, chunkinfos");
            if (options.Reflections != "auto" && options.Reflections != "si" && options.Reflections != "no")
                options.Errors.Add($"--riflessi {options.Reflections}: valori possibili auto, si, no");
            if (options.SaveMode != "proprio" && options.SaveMode != "originale")
                options.Errors.Add($"--salvataggio {options.SaveMode}: valori possibili proprio, originale");
            if (options.IntroCrash1 && options.IntroCrash2)
                options.Errors.Add("--intro-crash1 e --intro-crash2 insieme: scegline una");
            if (options.IntroMoveToStart && options.IntroKeepPosition)
                options.Errors.Add("--intro-alla-partenza e --intro-posizione-originale insieme: scegline una");
            if (options.IntroTinyPortal && options.IntroRemovePortal)
                options.Errors.Add("--intro-portale-piccolo e --intro-togli-portale insieme: scegline una");
            if (options.IntroNoPortal && (options.IntroTinyPortal || options.IntroRemovePortal))
                options.Errors.Add($"--intro-senza-portale e {(options.IntroTinyPortal ? "--intro-portale-piccolo" : "--intro-togli-portale")} insieme: scegline una");
            string igzMode = options.IgzMode.ToLowerInvariant();
            if (igzMode != "nessuno" && igzMode != "maps" && igzMode != "tutti")
                options.Errors.Add($"--igz {options.IgzMode}: valori possibili nessuno, maps, tutti");
            return options;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Uso:");
            Console.WriteLine("  NST.exe --switch layout [report.txt]");
            Console.WriteLine("  NST.exe --switch struttura <cartella_dump_switch> [report.txt] [--pak nome] [--max N]");
            Console.WriteLine("  NST.exe --switch verifica <file_pc.pak> <cartella_dump_switch> [report.txt] [--max N]");
            Console.WriteLine("  NST.exe --switch riscrivi <archivio_switch.pak> <output.pak> [report.txt] [--igz nessuno|maps|tutti]");
            Console.WriteLine("  NST.exe --switch converti <file_pc.pak> <cartella_dump_switch> <output.pak> [report.txt] [--come-originale | --sostituisci <livello> | --nuovo] [--base <livello>] [--senza-base] [--altri-livelli converti|originali|originali+dipendenze] [--zoneinfo-da <livello>|pc] [--salvataggio originale|proprio] [--escludi <testo>]... [--solo-usati] [--dipendenze-altri-livelli] [--tieni-entita-modalita] [--tieni-occlusioni] [--tessere-solo-crash1] [--tessere-senza-hud] [--intro-crash1 | --intro-crash2] [--intro-come-editor] [--intro-scostamento <numero>] [--intro-evento <nome>] [--intro-senza-portale] [--intro-togli-portale | --intro-portale-piccolo] [--intro-alla-partenza] [--togli-entita [file:]testo]... [--originale-switch <testo>]... [--ctr <cartella_dump_ctr_switch>] [--senza-intro | --aggiungi-intro] [--registra-in update|chunkinfos] [--gioco crash1|crash2|crash3] [--senza-modalita] [--riflessi auto|si|no] [--modello-blocchi <file>=<modello>]... [--pavimento-blocchi <file>[=<decorazione>]]... [--memoria modello|max|<livello>|<MB>[/<MB>]] [--prova-texture <MB>] [--descrivi <testo>]... [--pc-originali <cartella_archives_pc>]");
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
        /// Fixup di un igz (dipendenze TDEP, riferimenti per nome), letti senza leggere gli oggetti; null se non si leggono
        /// </summary>
        private static FixupCollection? RawFixups(IgArchiveFile file)
        {
            try
            {
                byte[] data = file.Uncompress();
                if (data.Length < 0x24 || BitConverter.ToUInt32(data, 0) != 0x49475A01) return null;
                using var reader = new BinaryReader(new MemoryStream(data));
                reader.BaseStream.Position = BitConverter.ToInt32(data, 0x18);
                return FixupCollection.Parse(reader);
            }
            catch
            {
                return null;
            }
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
                // tutti i riferimenti, anche dentro liste, memorie e strutture (GetHandles dell'editor non li vede tutti)
                foreach (NamedReference handle in ObjectReferences(obj, igz.GameVersion))
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
        /// Riferimenti per nome di un oggetto (handle e oggetti di altri file) nei suoi campi, anche dentro liste, memorie,
        /// tabelle e strutture. Non entra negli altri oggetti del file (nell'igz ci sono tutti, si visitano a parte). Serve
        /// perche' GetHandles dell'editor non guarda gli oggetti esterni nelle memorie e, se un campo e' un array di numeri,
        /// non restituisce niente per tutto l'oggetto
        /// </summary>
        private static List<NamedReference> ObjectReferences(igObject obj, GameVersion version)
        {
            var result = new List<NamedReference>();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

            void Collect(object? value, int depth)
            {
                if (value == null || depth > 64) return;
                switch (value)
                {
                    case igHandleMetaField handle:
                        if (handle.Reference != null) result.Add(handle.Reference);
                        return;
                    case igObject other:
                        // oggetto di un altro file (EXNM/EXID); quelli del file si visitano da soli
                        if (other.Reference != null) result.Add(other.Reference);
                        return;
                    case string:
                        return;
                }
                if (!value.GetType().IsClass || !visited.Add(value)) return;
                if (value is IMemoryRef && value is IEnumerable elements)
                {
                    foreach (object? element in elements) Collect(element, depth + 1);
                }
                else if (value is igObjectBase meta)
                {
                    foreach (CachedFieldAttr field in meta.GetFields(version)) Collect(field.GetValue(meta), depth + 1);
                }
                else if (value is Array array && !array.GetType().GetElementType()!.IsPrimitive)
                {
                    foreach (object? element in array) Collect(element, depth + 1);
                }
            }

            foreach (CachedFieldAttr field in obj.GetFields(version)) Collect(field.GetValue(obj), 0);
            return result;
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
            // Cartelle dello stesso livello in un altro gioco (Crash2/Custom_Level accanto a Crash1/Custom_Level): l'editor
            // ci lascia una parte dei file quando si cambia il Crash Mode. Vanno anche loro in ToDir
            public List<string> ExtraFromDirs = [];
            // File di quelle cartelle che restano dove sono (stesso nome di un file della cartella del livello)
            public HashSet<string> KeepFolder = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Cartella e nome cambiano solo nei percorsi dei file dei livelli (maps/, models/maps/, packages/generated/maps/,
            /// anche sotto update/): gli asset del livello originale (models/Levels/..., vsc/...) tengono il loro percorso,
            /// cosi' si trovano tra gli originali Switch. Il nome cambia solo nel nome del file e solo come parola intera
            /// (Custom_Level.igz, Custom_Level_Art.igz, StaticCollision_Custom_Level.hkx), in un solo passaggio: fino alla
            /// v36 Custom_Level -> Custom_Level2 dava la cartella Custom_Level22
            /// </summary>
            public string Apply(string path)
            {
                string[] parts = path.Split('/');
                int start = LevelFolderStart(parts, 0);
                if (start < 0) return path;
                if (FromDir != null && ToDir != null)
                {
                    string[] to = ToDir.Split('/');
                    bool Matches(string dir)
                    {
                        string[] from = dir.Split('/');
                        return from.Length == 2 && start + 2 < parts.Length &&
                               parts[start].Equals(from[0], StringComparison.OrdinalIgnoreCase) &&
                               parts[start + 1].Equals(from[1], StringComparison.OrdinalIgnoreCase);
                    }
                    bool primary = !FromDir.Equals(ToDir, StringComparison.OrdinalIgnoreCase) && Matches(FromDir);
                    bool extra = !primary && !KeepFolder.Contains(path) && ExtraFromDirs.Any(Matches);
                    if (to.Length == 2 && (primary || extra))
                    {
                        parts[start] = to[0];
                        parts[start + 1] = to[1];
                    }
                }
                parts[parts.Length - 1] = ReplaceNameToken(parts[parts.Length - 1], FromName, ToName);
                return string.Join("/", parts);
            }

            /// <summary>
            /// Cartella del livello ("Crash1/Custom_Level") di un percorso di un file dei livelli, null per gli altri percorsi
            /// </summary>
            public static string? DirOf(string path)
            {
                string[] parts = path.Split('/');
                int start = LevelFolderStart(parts, 0);
                return start >= 0 && start + 2 < parts.Length ? parts[start] + "/" + parts[start + 1] : null;
            }

            public override string ToString()
            {
                string dirs = FromDir != null && ToDir != null && !FromDir.Equals(ToDir, StringComparison.OrdinalIgnoreCase) ? $" (cartella {FromDir} -> {ToDir})" : "";
                return $"{FromName} -> {ToName}{dirs}";
            }
        }

        /// <summary>
        /// Posizione della cartella del gioco (Crash1) nei percorsi dei file dei livelli: maps/Crash1/..., models/maps/Crash1/...,
        /// packages/generated/maps/Crash1/..., anche sotto update/. -1 per gli altri percorsi
        /// </summary>
        private static int LevelFolderStart(string[] parts, int offset)
        {
            bool Is(int i, string text) => i < parts.Length && parts[i].Equals(text, StringComparison.OrdinalIgnoreCase);
            if (Is(offset, "update")) return LevelFolderStart(parts, offset + 1);
            if (Is(offset, "maps")) return offset + 1;
            if (Is(offset, "models") && Is(offset + 1, "maps")) return offset + 2;
            if (Is(offset, "packages") && Is(offset + 1, "generated") && Is(offset + 2, "maps")) return offset + 3;
            return -1;
        }

        /// <summary>
        /// Il nome come parola intera: all'inizio o dopo _ / e alla fine o prima di _ . / ,
        /// </summary>
        private static bool IsNameToken(string text, int start, int length)
        {
            int end = start + length;
            bool before = start == 0 || text[start - 1] == '_' || text[start - 1] == '/';
            bool after = end == text.Length || text[end] == '_' || text[end] == '.' || text[end] == '/' || text[end] == ',';
            return before && after;
        }

        /// <summary>
        /// Sostituisce il nome (maiuscole e minuscole indifferenti) solo dove e' una parola intera, in un solo passaggio
        /// </summary>
        private static string ReplaceNameToken(string text, string from, string to)
        {
            if (from.Length == 0 || from.Equals(to, StringComparison.Ordinal)) return text;
            var result = new StringBuilder();
            int position = 0;
            while (position < text.Length)
            {
                int found = text.IndexOf(from, position, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                result.Append(text, position, found - position);
                if (IsNameToken(text, found, from.Length))
                {
                    result.Append(to);
                    position = found + from.Length;
                }
                else
                {
                    result.Append(text[found]);
                    position = found + 1;
                }
            }
            result.Append(text, position, text.Length - position);
            return result.ToString();
        }

        /// <summary>
        /// Il percorso contiene il nome come parola intera (Custom_Level in maps/.../Custom_Level_Art.igz, non in Custom_Level2)
        /// </summary>
        private static bool HasNameToken(string text, string name)
        {
            if (name.Length == 0) return false;
            int position = 0;
            while (position < text.Length)
            {
                int found = text.IndexOf(name, position, StringComparison.OrdinalIgnoreCase);
                if (found < 0) return false;
                if (IsNameToken(text, found, name.Length)) return true;
                position = found + 1;
            }
            return false;
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
            return levels.Any(l => HasNameToken(path, l));
        }

        private static bool IsLevelContent(string path, List<string> levels, List<string> levelDirs)
        {
            string lower = "/" + path.ToLowerInvariant();
            if (lower.StartsWith("/update/")) return true;
            if (levelDirs.Any(d => lower.Contains("/" + d.ToLowerInvariant().Trim('/') + "/"))) return true;
            return levels.Any(l => HasNameToken(path, l));
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

            // L'uscita non va nella cartella del dump: Save svuota il file di arrivo e la conversione successiva
            // prenderebbe il livello convertito (e il suo update.pak) per un file del gioco
            string dumpFolder = Path.GetFullPath(switchDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string outputFolder = (Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                  Path.DirectorySeparatorChar;
            if (Directory.Exists(switchDir) && outputFolder.StartsWith(dumpFolder, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Errore: l'archivio di uscita e' nella cartella del dump Switch ({dumpFolder}): scegli un'altra cartella");
                return 1;
            }

            Console.WriteLine("Indicizzo gli archivi Switch...");
            var indexErrors = new List<string>();
            ArchiveIndex sw = ArchiveIndex.Build(switchDir, indexErrors);
            Dictionary<string, IgArchiveFile> index = sw.ByPath;
            if (sw.ByArchive.Count == 0)
            {
                Console.WriteLine($"Errore: nessun archivio .pak leggibile in {switchDir} (serve la cartella del dump Switch che contiene gli archivi)");
                return 1;
            }
            report.AppendLine($"dump Switch: {sw.ByArchive.Count} archivi, {index.Count} file");
            foreach (string error in indexErrors.Take(10)) report.AppendLine("  ERRORE " + error);
            if (indexErrors.Count > 10) report.AppendLine($"  ... e altri {indexErrors.Count - 10} errori");

            // --memoria <livello>: controllato subito, non dopo la conversione (--memoria <MB>[/<MB>] non cerca niente)
            if (!options.Memory.Equals("modello", StringComparison.OrdinalIgnoreCase) && !options.Memory.Equals("max", StringComparison.OrdinalIgnoreCase) &&
                !TryParseMemoryMb(options.Memory, out _, out _, out _) && FindZoneInfo(sw, options.Memory) == null)
            {
                Console.WriteLine($"Errore: --memoria {options.Memory}: zone info di quel livello non trovata nel dump (usa modello, max, il nome di un livello " +
                                  "originale o i MB, per esempio 300 o 300/40)");
                return 1;
            }
            if (TryParseMemoryMb(options.Memory, out _, out _, out string? memoryError) && memoryError != null)
            {
                Console.WriteLine($"Errore: --memoria {options.Memory}: {memoryError}");
                return 1;
            }

            // --ctr: grafica che il gioco Switch non ha presa da Crash Team Racing Nitro-Fueled per Switch (prova)
            ArchiveIndex? ctr = null;
            if (options.CtrDump != null)
            {
                Console.WriteLine("ATTENZIONE: --ctr nelle prove blocca il gioco (i modelli di CTR usano file che Crash non ha): non usarlo per i livelli da giocare");
                report.AppendLine("ATTENZIONE: --ctr nelle prove blocca il gioco al caricamento (i modelli di CTR usano vertexformat/indexformats, che Crash non ha)");
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
            // File del livello in una sua seconda cartella che hanno lo stesso nome di uno della cartella del livello e
            // nessuna entita' (file principale vuoto lasciato dall'editor): fuori dall'archivio
            var duplicateLevelFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                // Un livello nuovo con il nome di un livello del gioco prenderebbe il posto del suo archivio (e della sua voce
                // di salvataggio); con il nome di un altro archivio del gioco (update, chunkinfos, crash1...) lo sostituirebbe
                if (switchLevels.Any(l => l.Equals(requested, StringComparison.OrdinalIgnoreCase)))
                {
                    report.AppendLine($"--nuovo: {requested} e' il nome di un livello del gioco: per sostituirlo usa --sostituisci {requested} (o --come-originale)");
                    File.WriteAllText(reportPath, report.ToString());
                    Console.WriteLine($"Errore: {requested} e' gia' un livello del gioco. Scegli un altro nome di uscita oppure usa --sostituisci {requested}");
                    return 1;
                }
                if (sw.ByArchive.ContainsKey(requested.ToLowerInvariant()) || requested.Equals("update", StringComparison.OrdinalIgnoreCase) ||
                    requested.Equals("chunkinfos", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"Errore: {requested}.pak e' un archivio del gioco: scegli un altro nome per il file di uscita");
                    return 1;
                }

                // Il gioco del livello e' quello impostato nell'editor (Crash Mode, l'anno della sua zone info). Se il livello
                // e' nella cartella di un altro gioco (Crash Mode cambiato dopo averlo creato) passa nella cartella di quel
                // gioco, come con --gioco: cartella e zone info di giochi diversi (fino alla v37 la zone info del Crash Mode
                // in un livello lasciato nella sua cartella) e Level 10 e 11 restavano su LOADING; con la zone info di Road
                // to Nowhere passata a Crash 3 nella cartella di Crash 1 il gioco si fermava sul titolo (v26)
                if (options.Game == null && options.ZoneInfoFrom == null)
                {
                    List<IgArchiveFile> pcUpdate = pc.Files.Where(f => f.Path.StartsWith("update/", StringComparison.OrdinalIgnoreCase)).ToList();
                    string? editorGame = GameOfYear(PcZoneInfoYear(pcUpdate.Count > 0 ? pcUpdate : pc.Files.ToList()));
                    string folderGame = newPackage.Value.dir.Split('/')[0].ToLowerInvariant();
                    if (editorGame != null && editorGame != folderGame)
                    {
                        options.Game = editorGame;
                        options.GameFromEditor = true;
                        report.AppendLine($"--nuovo: nell'editor il livello e' impostato come {editorGame} (Crash Mode) ma e' nella cartella " +
                                          $"{newPackage.Value.dir.Split('/')[0]}: passa nella cartella {GameFolder(editorGame)}, perche' cartella e zone " +
                                          $"info devono essere dello stesso gioco (con --gioco {folderGame} resta dov'e', con le regole di {folderGame})");
                    }
                }

                if (!requested.Equals(pcName, StringComparison.OrdinalIgnoreCase))
                {
                    if (switchLevels.Any(l => l.Equals(pcName, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Gli asset del livello originale hanno lo stesso nome: rinominarli li staccherebbe dagli originali Switch
                        report.AppendLine($"--nuovo: il livello si chiama come l'originale {pcName} e non si puo' rinominare in {requested}");
                        File.WriteAllText(reportPath, report.ToString());
                        Console.WriteLine($"Errore: il livello si chiama {pcName}, come un livello del gioco: non si puo' rinominare. " +
                                          $"Usa --come-originale o --sostituisci, oppure salvalo nell'editor con un nome nuovo.");
                        return 1;
                    }
                    string game = GameFolder(options.Game) ?? newPackage.Value.dir.Split('/')[0];
                    rename = new LevelRename { FromName = pcName, ToName = requested, FromDir = newPackage.Value.dir, ToDir = game + "/" + requested };
                    newPackage = (game + "/" + requested, requested);
                    report.AppendLine($"--nuovo: il livello prende il nome indicato: {rename}");
                }
                else if (GameFolder(options.Game) is string gameFolder &&
                         !newPackage.Value.dir.Split('/')[0].Equals(gameFolder, StringComparison.OrdinalIgnoreCase))
                {
                    // --gioco (o gioco dell'editor): stesso nome, cartella del gioco scelto
                    string toDir = gameFolder + "/" + newPackage.Value.dir.Split('/')[1];
                    rename = new LevelRename { FromName = pcName, ToName = pcName, FromDir = newPackage.Value.dir, ToDir = toDir };
                    newPackage = (toDir, pcName);
                    report.AppendLine($"{(options.GameFromEditor ? "gioco dell'editor" : "--gioco")}: il livello passa nella cartella del gioco: {rename}");
                }

                // Parte dei file del livello nella cartella di un altro gioco (Crash2/Custom_Level accanto a Crash1/Custom_Level,
                // lasciati dall'editor quando si cambia il Crash Mode): finiscono anche loro nella cartella del livello. Un file
                // con lo stesso nome di uno della cartella del livello resta dov'e' (se e' senza entita', come il file principale
                // vuoto lasciato dall'editor, si toglie: con lo stesso nome del file principale ne prenderebbe il posto)
                if (rename != null && rename.FromDir != null && rename.ToDir != null)
                {
                    LevelRename levelRename = rename;
                    string fromDir = rename.FromDir;
                    string fromFolder = fromDir.Split('/')[1];
                    List<string> extra = pc.Files.Select(f => LevelRename.DirOf(f.Path)).OfType<string>()
                        .Where(d => !d.Equals(fromDir, StringComparison.OrdinalIgnoreCase) &&
                                    d.Split('/')[1].Equals(fromFolder, StringComparison.OrdinalIgnoreCase) &&
                                    NormalizeGame(d.Split('/')[0]) != "")
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (extra.Count > 0)
                    {
                        // destinazioni dei file della cartella del livello (prima di aggiungere le altre cartelle)
                        var primaryTargets = new HashSet<string>(
                            pc.Files.Where(f => fromDir.Equals(LevelRename.DirOf(f.Path), StringComparison.OrdinalIgnoreCase))
                                    .Select(f => levelRename.Apply(f.Path)), StringComparer.OrdinalIgnoreCase);
                        levelRename.ExtraFromDirs = extra;
                        int moved = 0;
                        foreach (IgArchiveFile levelFile in pc.Files.Where(p => extra.Any(d => d.Equals(LevelRename.DirOf(p.Path), StringComparison.OrdinalIgnoreCase))))
                        {
                            if (!primaryTargets.Contains(levelRename.Apply(levelFile.Path)))
                            {
                                moved++;
                                continue;
                            }
                            levelRename.KeepFolder.Add(levelFile.Path);
                            int entities = -1;
                            try
                            {
                                if (levelFile.IsIGZ()) entities = levelFile.ToIgzFile().Objects.OfType<igEntity>().Count();
                            }
                            catch
                            {
                                // non leggibile: resta
                            }
                            if (entities == 0)
                            {
                                duplicateLevelFiles.Add(levelFile.Path);
                                report.AppendLine($"--nuovo: {levelFile.Path} (senza entita') ha lo stesso nome di un file del livello: tolto");
                            }
                            else
                            {
                                report.AppendLine($"--nuovo: ATTENZIONE: {levelFile.Path} ha lo stesso nome di un file del livello: resta nella sua cartella " +
                                                  "(i due file hanno lo stesso nome interno, il gioco potrebbe usarne uno solo)");
                            }
                        }
                        report.AppendLine($"--nuovo: file del livello anche in {string.Join(", ", extra)} (lasciati dall'editor cambiando il " +
                                          $"Crash Mode): {moved} spostati in {levelRename.ToDir}");
                    }
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
            // Cartelle del livello, comprese quelle in un altro gioco (vedi ExtraFromDirs): i loro file non sono di altri livelli
            List<string> levelDirsAll = rename != null ? levelDirs.Concat(rename.ExtraFromDirs).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : levelDirs;

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

            // File elencati nel pacchetto del livello PC (con i nomi rinominati). L'editor, quando prendi un oggetto da
            // un altro livello, copia nell'archivio l'intero file di quel livello (maps/...) ma NON lo mette nel
            // pacchetto: il gioco lo usa solo per gli oggetti presi, senza caricare tutto quello che contiene
            var pcPackageList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pcPackageOriginal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // con i nomi del PC
            if (pc.Files.FirstOrDefault(f => PackageInfo(f.Path) != null) is IgArchiveFile pcPackage)
            {
                try
                {
                    foreach (igStreamingChunkInfo info in pcPackage.ToIgzFile().Objects.OfType<igStreamingChunkInfo>())
                        foreach (ChunkFileInfoMetaField entry in info._required._data)
                        {
                            if (entry._name == null) continue;
                            pcPackageOriginal.Add(entry._name);
                            pcPackageList.Add(rename != null ? rename.Apply(entry._name) : entry._name);
                        }
                }
                catch (Exception e)
                {
                    report.AppendLine($"pacchetto del livello PC non leggibile ({e.Message}): tutti i file finiscono nel pacchetto");
                    pcPackageList.Clear();
                    pcPackageOriginal.Clear();
                }
            }

            // File di altri livelli copiati dall'editor: contenuto (per il rapporto) ed entita' tolte
            var otherContent = new StringBuilder();
            var removedEntities = new List<string>();
            var usedFloorFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // --pavimento-blocchi usati
            var unreadableFiles = new List<string>();  // file convertiti che non si rileggono
            var lightVolumes = new List<LightVolume>();  // luci, volumi di colore e box visivi (rapporto)
            var occlusionInfos = new List<OcclusionInfo>();  // occlusioni dei file convertiti, prima di spegnerle (rapporto)
            var placedEntities = new List<PlacedEntity>();  // decorazioni, blocchi e oggetti con un modello (rapporto)
            (float x, float y, float z)? playerStart = null;  // partenza del livello (rapporto)
            var introLines = new List<string>();               // intro di Crash 3: aggiunta, cambiata (rapporto)
            bool introAdded = false;
            // --intro-crash1, --intro-crash2: animazione di comparsa di Crash 1 o 2 dai dati di Crash (se non c'e' --intro-evento)
            string? introGameEvent = null;
            if ((options.IntroCrash1 || options.IntroCrash2) && !options.WithoutIntro && options.IntroEvent == null)
            {
                string optionName = options.IntroCrash1 ? "--intro-crash1" : "--intro-crash2";
                try
                {
                    var found = options.IntroCrash1 ? C1RespawnEvent(sw) : C2RespawnEvent(sw);
                    introGameEvent = found.eventName;
                    introLines.Add($"{optionName}: {found.reason}");
                }
                catch (Exception e)
                {
                    introLines.Add($"{optionName}: animazione di comparsa non cercata ({e.GetType().Name}: {e.Message}), resta quella dell'intro");
                }
            }
            var switchOriginalUsed = new List<string>();
            var usedFilters = new HashSet<string>();
            var modifiedOtherFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matchedSwitchOriginals = new HashSet<string>();

            // File di altri livelli nel pacchetto del PC: il gioco carica tutte le loro entita', fanno parte del livello.
            // Quelli fuori dal pacchetto li usa solo per gli oggetti a cui il livello fa riferimento
            bool InPcPackage(string pcFilePath) => pcPackageOriginal.Count == 0 || pcPackageOriginal.Contains(pcFilePath);
            var otherLevelTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Nomi dei file che il livello puo' usare: quelli del gioco Switch e quelli dell'archivio PC che finiranno
            // nell'archivio (non quelli esclusi con --escludi e non la grafica PC che la Switch non ha, che si salta)
            var availableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in index.Keys)
                foreach (string alias in NameAliases(NamespaceUtils.GetFileName(name, false).ToLowerInvariant()))
                    availableNames.Add(alias);
            foreach (IgArchiveFile pcFile in pc.Files)
            {
                string pcLower = pcFile.Path.ToLowerInvariant();
                if (options.Exclude.Any(x => pcFile.Path.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                if (ctr == null && IsCtrAsset(pcFile.Path)) continue;
                bool pcGraphics = (pcLower.StartsWith("models/") && !pcLower.StartsWith("models/maps/")) || pcLower.StartsWith("textures/") ||
                                  pcLower.StartsWith("materialinstances/") || pcLower.StartsWith("loosetextures/");
                if (pcGraphics && ctr == null && !index.ContainsKey(pcLower)) continue;
                foreach (string alias in NameAliases(NamespaceUtils.GetFileName(pcFile.Path, false).ToLowerInvariant()))
                    availableNames.Add(alias);
            }

            // Intro di Crash 3 gia' nel livello: nei suoi file e in quelli di altri livelli nel pacchetto del PC (il gioco ne
            // carica tutte le entita'); le copie fuori dal pacchetto non contano. Si guarda prima di convertire, perche'
            // nell'archivio i file non sono in ordine di cartella (fino alla v36 contava anche una copia fuori dal pacchetto
            // e, se veniva prima del file principale, l'intro non si aggiungeva)
            bool levelHasC3Intro = !options.WithoutIntro && pc.Files.Any(f =>
                f.IsIGZ() && f.Path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) &&
                !options.Exclude.Any(x => f.Path.Contains(x, StringComparison.OrdinalIgnoreCase)) &&
                (!IsOtherLevelMapFile(f.Path, levelDirsAll) || InPcPackage(f.Path)) &&
                HasC3Intro(f));
            int done = 0;

            // Intro di Crash 3 da aggiungere se manca: con --aggiungi-intro, oppure con --gioco crash3 per un livello che
            // nell'editor era di un altro gioco (l'editor la aggiunge quando passi un livello a Crash 3)
            // zone info dell'editor: quella della registrazione (update/), non quelle di altri livelli copiate nell'archivio
            List<IgArchiveFile> pcZoneFiles = pc.Files.Where(f => f.Path.StartsWith("update/", StringComparison.OrdinalIgnoreCase)).ToList();
            if (pcZoneFiles.Count == 0) pcZoneFiles = pc.Files.ToList();
            EGameYear? editorYear = PcZoneInfoYear(pcZoneFiles);
            bool editorJetski = PcZoneInfoUsesJetski(pcZoneFiles);
            // Modalita' del livello: quelle scelte nell'editor ({bear}, {boulder}, {swim}... e il veicolo), che la
            // registrazione riporta nella zone info (con --nuovo --senza-modalita nessuna)
            HashSet<string> levelModes = options.NewLevel && options.WithoutMode
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : PcZoneInfoModes(pcZoneFiles);
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

                    // Asset di Crash Team Racing (Octane) importati nell'editor: la Switch non li ha e la loro grafica non si
                    // converte, quindi i loro oggetti resterebbero senza modello. Senza --ctr restano fuori (come --escludi Octane)
                    if (ctr == null && !path.StartsWith("update/", StringComparison.OrdinalIgnoreCase) && IsCtrAsset(path))
                    {
                        Increment(counters, "asset di Crash Team Racing esclusi (senza --ctr)");
                        lines.Add($"escluso (Crash Team Racing): {path}");
                        continue;
                    }

                    // File vuoto del livello rimasto in una sua seconda cartella con lo stesso nome di uno della cartella del livello
                    if (duplicateLevelFiles.Contains(path))
                    {
                        Increment(counters, "file vuoti del livello lasciati dall'editor in un'altra cartella (tolti)");
                        lines.Add($"tolto (file vuoto lasciato dall'editor): {path}");
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

                    bool otherLevelFile = IsOtherLevelMapFile(path, levelDirsAll);
                    if (otherLevelFile) otherLevelTargets.Add(target);

                    index.TryGetValue(target.ToLowerInvariant(), out IgArchiveFile? original);

                    bool? unmodified = null;
                    if (pcIndex != null && pcIndex.TryGetValue(path.ToLowerInvariant(), out IgArchiveFile? pcOriginal))
                    {
                        unmodified = SameData(file, pcOriginal);
                    }

                    bool levelContent = otherLevelsFromSwitch ? IsLevelContent(path, levels, levelDirsAll) : IsLevelContentAll(path, levels);
                    bool collision = target.Contains("staticcollision", StringComparison.OrdinalIgnoreCase);

                    // --originale-switch: file di un altro livello preso dall'originale Switch (le sue dipendenze si
                    // aggiungono dopo, dallo stesso archivio Switch)
                    if (otherLevelFile && options.SwitchOriginals.Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase)))
                    {
                        foreach (string pattern in options.SwitchOriginals.Where(x => path.Contains(x, StringComparison.OrdinalIgnoreCase)))
                            matchedSwitchOriginals.Add(pattern);
                        if (original != null)
                        {
                            AddOriginal(original, "file di altri livelli presi dall'originale Switch (--originale-switch)");
                            switchOriginalUsed.Add($"  {target}: originale Switch ({sw.ArchiveOf.GetValueOrDefault(original) ?? "?"}.pak) al posto della copia PC");
                            continue;
                        }
                        switchOriginalUsed.Add($"  {target}: non c'e' nel gioco Switch, convertito dal PC");
                    }

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
                                // solo una prova: se non riesce, la conversione del file del PC va avanti lo stesso
                                try
                                {
                                    lines.Add($"prova: collisione originale Switch riletta e riscritta: {HavokRoundTrip(original.Uncompress())}");
                                }
                                catch (Exception roundTripError)
                                {
                                    lines.Add($"prova: collisione originale Switch non riletta ({roundTripError.Message})");
                                }
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
                        bool mainFile = IsMainLevelFile(path, levels, levelDirs);
                        if (mainFile)
                        {
                            foreach (igEntity entity in igz.Objects.OfType<igEntity>())
                            {
                                string components = string.Join(", ", entity.GetComponents().Select(c => c.GetType().Name));
                                entityLines.Add($"  {entity.ObjectName ?? "?"} [{entity.GetType().Name}] {components}");
                            }
                        }

                        // L'intro dei livelli di Crash 3 (IntroCutsceneSequencePlayer, Crash che esce dal portale) fa comparire
                        // Crash: la zone info di un livello di Crash 3 la aspetta (vedi BuildRegistration). Se il livello ce
                        // l'ha gia' lo dice levelHasC3Intro, calcolato prima della conversione
                        bool hasIntro = igz.Objects.OfType<igEntity>().Any(e => e.GetComponent<common_C3_IntroSequenceData>() != null);

                        // --intro-crash1/2, --intro-scostamento, --intro-evento, --intro-senza-portale, --intro-togli-portale: anche per
                        // l'intro che il livello ha gia'
                        if (hasIntro && !options.WithoutIntro)
                        {
                            foreach (common_C3_IntroSequenceData data in igz.Objects.OfType<common_C3_IntroSequenceData>())
                            {
                                string? changed = ApplyIntroOptions(data, options, introGameEvent);
                                if (changed != null) introLines.Add($"intro gia' nel livello ({target}): {changed}");
                            }
                        }

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

                        // File di un altro livello copiato dall'editor. Se il pacchetto del PC lo elenca il gioco carica tutte le
                        // sue entita', che fanno parte del livello; altrimenti usa solo gli oggetti a cui il livello fa
                        // riferimento. Contenuto nel rapporto, --togli-entita e, solo per quelli nel pacchetto, --solo-usati
                        if (otherLevelFile)
                        {
                            bool inPcPackage = InPcPackage(path);
                            var references = new Dictionary<igEntity, HashSet<string>>();
                            HashSet<string> Refs(igEntity entity)
                            {
                                if (!references.TryGetValue(entity, out HashSet<string>? found)) references[entity] = found = EntityReferences(entity, igz);
                                return found;
                            }
                            // metaobject, metafield...: tipi del motore, non file (fino alla v35 contavano come file mancanti e
                            // --solo-usati toglieva tutti i nemici, i massi e le staccionate)
                            List<string> MissingRefs(igEntity entity) =>
                                Refs(entity).Where(n => !n.All(char.IsDigit) && !n.StartsWith("meta") && !availableNames.Contains(n)).OrderBy(n => n).ToList();
                            bool modified = false;

                            // Modalita' del livello da cui viene il file (masso, orso, sub...), per il rapporto e per le entita' di
                            // livelli con un'altra modalita' (sotto)
                            var sourceLevel = SourceLevelModes(path);
                            string? modeInfo = sourceLevel == null ? null
                                : $"    {sourceLevel.Value.level} e' un livello con {(sourceLevel.Value.modes.Count > 1 ? "le modalita'" : "la modalita'")} " +
                                  $"{string.Join("/", sourceLevel.Value.modes.Select(ModeDescription))}: " +
                                  (sourceLevel.Value.modes.Overlaps(levelModes) ? "ce l'ha anche il livello convertito" : "il livello convertito non ce l'ha");
                            DescribeOtherLevelFile(otherContent, path, igz, original, inPcPackage, Refs, MissingRefs, modeInfo);

                            var chosen = new HashSet<igObject>();
                            // posizione delle entita' nel file, per le parti di --togli-entita (file:#k/n)
                            List<igEntity> filterEntities = igz.Objects.OfType<igEntity>().ToList();
                            for (int entityIndex = 0; entityIndex < filterEntities.Count; entityIndex++)
                            {
                                igEntity candidate = filterEntities[entityIndex];
                                foreach (string filter in options.RemoveEntities)
                                    if (EntityFilterMatches(filter, path, candidate, Refs(candidate), entityIndex, filterEntities.Count))
                                    {
                                        chosen.Add(candidate);
                                        usedFilters.Add(filter);
                                    }
                            }
                            if (chosen.Count > 0)
                            {
                                string names = string.Join(", ", chosen.Take(12).Select(e => e.ObjectName ?? "?")) + (chosen.Count > 12 ? $" e altre {chosen.Count - 12}" : "");
                                HashSet<igObject> removedObjects = igz.Remove(chosen);
                                modified = true;
                                counters["entita' tolte (--togli-entita)"] = counters.GetValueOrDefault("entita' tolte (--togli-entita)") + chosen.Count;
                                removedEntities.Add($"  --togli-entita, {path}: {chosen.Count} entita' ({removedObjects.Count} oggetti): {names}");
                            }

                            // Entita' con un comportamento (personaggi, nemici, spline delle corsie, oggetti che si muovono) di
                            // livelli con una modalita' che questo livello non ha (sub, tigre, orso, moto d'acqua...) e dei boss:
                            // aspettano quella modalita' e il gioco resta su LOADING (Level 11 con i nemici di Under Pressure e
                            // le corsie di Orient Express e Midnight Run). Solo nei file nel pacchetto, di cui il gioco carica
                            // tutte le entita'. Le decorazioni restano; --tieni-entita-modalita le lascia tutte
                            if (inPcPackage && !options.KeepModeEntities && sourceLevel != null && !sourceLevel.Value.modes.Overlaps(levelModes))
                            {
                                var modeEntities = igz.Objects.OfType<igEntity>()
                                    .Where(e => Refs(e).Any(r => r.Contains("metaobject")))
                                    .Cast<igObject>()
                                    .ToHashSet();
                                if (modeEntities.Count > 0)
                                {
                                    string names = string.Join(", ", modeEntities.Take(12).Select(e => e.ObjectName ?? "?")) + (modeEntities.Count > 12 ? $" e altre {modeEntities.Count - 12}" : "");
                                    string modeText = string.Join("/", sourceLevel.Value.modes.Select(ModeDescription));
                                    HashSet<igObject> removedObjects = igz.Remove(modeEntities);
                                    modified = true;
                                    counters["entita' di livelli con un'altra modalita' o di boss tolte"] =
                                        counters.GetValueOrDefault("entita' di livelli con un'altra modalita' o di boss tolte") + modeEntities.Count;
                                    removedEntities.Add($"  modalita', {path}: {modeEntities.Count} entita' ({removedObjects.Count} oggetti) di {sourceLevel.Value.level} " +
                                                        $"({modeText}, che il livello non ha): {names}");
                                }
                            }

                            // Entita' che bloccano il caricamento, trovate con le prove (vedi _knownBlockingEntities): i pinguini fermi
                            // di Snow Go anche quando il livello ha la loro modalita', i cancelli elettrici di Crash Dash se il livello
                            // non ha il masso. Solo nei file nel pacchetto; --tieni-entita-modalita le lascia
                            if (inPcPackage && !options.KeepModeEntities)
                            {
                                var blocking = new Dictionary<igObject, string>();
                                foreach (igEntity candidate in igz.Objects.OfType<igEntity>())
                                    if (KnownBlockingEntity(candidate, Refs(candidate), levelModes) is string description) blocking[candidate] = description;
                                if (blocking.Count > 0)
                                {
                                    string names = string.Join(", ", blocking.Keys.Take(12).Select(e => e.ObjectName ?? "?")) + (blocking.Count > 12 ? $" e altre {blocking.Count - 12}" : "");
                                    string reasons = string.Join(", ", blocking.Values.Distinct());
                                    HashSet<igObject> removedObjects = igz.Remove(blocking.Keys.ToHashSet());
                                    modified = true;
                                    counters["entita' che bloccano il caricamento tolte"] =
                                        counters.GetValueOrDefault("entita' che bloccano il caricamento tolte") + blocking.Count;
                                    removedEntities.Add($"  blocca il caricamento, {path}: {blocking.Count} entita' ({removedObjects.Count} oggetti, {reasons}): {names}");
                                }
                            }

                            // Blocchi di collisione (CDynamicClipEntity) senza modello: si cammina sopra ma non si vedono. Con
                            // --modello-blocchi prendono il modello indicato; altrimenti quello dei blocchi dell'originale Switch,
                            // se lo hanno
                            List<CDynamicClipEntityData> clipData = igz.Objects.OfType<CDynamicClipEntityData>().ToList();
                            if (clipData.Count > 0)
                            {
                                string? clipModel = options.ClipModels.Where(c => path.Contains(c.file, StringComparison.OrdinalIgnoreCase))
                                                                      .Select(c => c.model).LastOrDefault();
                                string clipSource = "--modello-blocchi";
                                if (clipModel == null && original != null && clipData.Any(d => string.IsNullOrWhiteSpace(d._modelName)))
                                {
                                    try
                                    {
                                        List<string> switchModels = original.ToIgzFile().Objects.OfType<CDynamicClipEntityData>()
                                            .Select(d => d._modelName ?? "").Where(m => m.Trim().Length > 0).Distinct().ToList();
                                        if (switchModels.Count == 1)
                                        {
                                            clipModel = switchModels[0];
                                            clipSource = "come nell'originale Switch";
                                        }
                                    }
                                    catch
                                    {
                                        // originale non leggibile: i blocchi restano come sono
                                    }
                                }
                                if (clipModel != null)
                                {
                                    var changedData = clipData.Where(d => options.ClipModels.Count > 0 && clipSource == "--modello-blocchi"
                                                                              ? d._modelName != clipModel
                                                                              : string.IsNullOrWhiteSpace(d._modelName)).ToList();
                                    foreach (CDynamicClipEntityData data in changedData) data._modelName = clipModel;
                                    if (changedData.Count > 0)
                                    {
                                        modified = true;
                                        int blocks = igz.Objects.OfType<CDynamicClipEntity>().Count(c => changedData.Contains(c._entityData as CDynamicClipEntityData));
                                        Increment(counters, "blocchi di collisione con un modello");
                                        removedEntities.Add($"  modello dei blocchi, {path}: {blocks} blocchi mostrano {clipModel} ({clipSource})");
                                    }
                                }
                            }

                            if (options.OnlyUsed && inPcPackage)
                            {
                                // Decorazioni (solo modello e collisione) che usano modelli che la Switch non ha: sulla Switch non si
                                // vedrebbero comunque. I prefab (..._prefab, prefab_...) mancano anche nei livelli che funzionano: non
                                // contano. Le entita' con altri componenti (nemici, piattaforme...) restano
                                var broken = new Dictionary<igObject, string>();
                                foreach (igEntity candidate in igz.Objects.OfType<igEntity>())
                                {
                                    List<string> missingFiles = MissingRefs(candidate).Where(n => !n.EndsWith("_prefab") && !n.StartsWith("prefab_")).ToList();
                                    if (missingFiles.Count > 0 && IsStaticDecoration(candidate)) broken[candidate] = "manca " + string.Join(", ", missingFiles);
                                    // Nemici dei livelli con la moto d'acqua (personaggi e quello che fanno comparire) in un livello
                                    // senza moto d'acqua: con quelli di Tell No Tales Tropical Escape resta su LOADING
                                    else if (!editorJetski && IsJetskiEnemy(candidate, Refs(candidate))) broken[candidate] = "nemico della moto d'acqua";
                                }
                                if (broken.Count > 0)
                                {
                                    string names = string.Join(", ", broken.Take(12).Select(b => $"{b.Key.ObjectName ?? "?"} ({b.Value})")) +
                                                   (broken.Count > 12 ? $" e altre {broken.Count - 12}" : "");
                                    HashSet<igObject> removedObjects = igz.Remove(broken.Keys.ToHashSet());
                                    modified = true;
                                    counters["entita' tolte dai file di altri livelli (--solo-usati)"] =
                                        counters.GetValueOrDefault("entita' tolte dai file di altri livelli (--solo-usati)") + broken.Count;
                                    removedEntities.Add($"  --solo-usati, {path}: {broken.Count} entita' ({removedObjects.Count} oggetti): {names}");
                                }
                            }
                            if (modified) modifiedOtherFiles.Add(target);
                        }

                        // --pavimento-blocchi: sui blocchi di collisione del file (si cammina sopra ma non si vedono) una copia di
                        // una decorazione del livello, grande come il blocco e senza collisione
                        var floorRequests = options.FloorBlocks.Where(f => path.Contains(f.file, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (floorRequests.Count > 0 && igz.Objects.OfType<CDynamicClipEntity>().Any())
                        {
                            foreach (var request in floorRequests) usedFloorFilters.Add(request.file);
                            try
                            {
                                removedEntities.AddRange(AddFloorOverClips(igz, path, floorRequests[^1].template, pc, sw));
                            }
                            catch (Exception floorError)
                            {
                                removedEntities.Add($"  ERRORE --pavimento-blocchi, {path}: {floorError.GetType().Name}: {floorError.Message}");
                            }
                            if (otherLevelFile) modifiedOtherFiles.Add(target);
                        }

                        // --togli-entita con il file indicato (file:testo) vale anche per i file del livello stesso (il file
                        // principale, l'illuminazione...): serve a provare a togliere un'entita' senza l'editor. Senza il file
                        // solo i file di altri livelli, come prima
                        if (!otherLevelFile && path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
                        {
                            List<string> ownFilters = options.RemoveEntities.Where(f => FilterNamesFile(f) && FilterFileMatches(f, path, target)).ToList();
                            var ownChosen = new HashSet<igObject>();
                            if (ownFilters.Count > 0)
                            {
                                List<igEntity> ownEntities = igz.Objects.OfType<igEntity>().ToList();
                                for (int ownIndex = 0; ownIndex < ownEntities.Count; ownIndex++)
                                {
                                    igEntity candidate = ownEntities[ownIndex];
                                    HashSet<string> candidateRefs = EntityReferences(candidate, igz);
                                    foreach (string filter in ownFilters)
                                    {
                                        if (!EntityFilterMatches(filter, path, candidate, candidateRefs, ownIndex, ownEntities.Count) &&
                                            !EntityFilterMatches(filter, target, candidate, candidateRefs, ownIndex, ownEntities.Count)) continue;
                                        ownChosen.Add(candidate);
                                        usedFilters.Add(filter);
                                    }
                                }
                            }
                            if (ownChosen.Count > 0)
                            {
                                string ownNames = string.Join(", ", ownChosen.Take(12).Select(e => e.ObjectName ?? "?")) + (ownChosen.Count > 12 ? $" e altre {ownChosen.Count - 12}" : "");
                                HashSet<igObject> ownRemoved = igz.Remove(ownChosen);
                                counters["entita' tolte (--togli-entita)"] = counters.GetValueOrDefault("entita' tolte (--togli-entita)") + ownChosen.Count;
                                removedEntities.Add($"  --togli-entita, {target} (file del livello): {ownChosen.Count} entita' ({ownRemoved.Count} oggetti): {ownNames}");
                            }
                        }

                        // Occlusioni (COcclusionTrigger, COcclusionBox): sulla console nascondono le parti del livello che non si vedono
                        // da dove sta Crash, ma quelle copiate da un altro livello (Snow Go) sono fatte per quel livello e nel livello
                        // nuovo nascondono decorazioni a caso (il pavimento del Level 10). Descritte per il rapporto e spente, tranne
                        // con --tieni-occlusioni (vedi NeutralizeOcclusion)
                        if (path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) && igz.Objects.Any(o => o is COcclusionTrigger || o is COcclusionBox))
                        {
                            try
                            {
                                occlusionInfos.AddRange(DescribeOcclusion(igz, target));
                                if (!options.KeepOcclusion)
                                {
                                    var (triggerCount, boxCount) = NeutralizeOcclusion(igz);
                                    Increment(counters, "file con le occlusioni spente");
                                    removedEntities.Add($"  occlusioni spente, {target}: {triggerCount} zone e {boxCount} gruppi (sulla console nascondevano decorazioni)");
                                    if (otherLevelFile) modifiedOtherFiles.Add(target);
                                }
                            }
                            catch (Exception occlusionError)
                            {
                                lines.Add($"occlusioni non lette in {target}: {occlusionError.GetType().Name}: {occlusionError.Message}");
                            }
                        }

                        // Diagnosi: luci, volumi di colore e box visivi, con la partenza del livello, e le entita' con un modello (rapporto)
                        if (path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                (float x, float y, float z)? foundStart = CollectLightVolumes(igz, target, !otherLevelFile || InPcPackage(path), lightVolumes);
                                if (foundStart != null && (playerStart == null || mainFile)) playerStart = foundStart;
                                CollectPlacedEntities(igz, target, !otherLevelFile || InPcPackage(path), placedEntities);
                            }
                            catch (Exception lightError)
                            {
                                lines.Add($"luci non lette in {target}: {lightError.GetType().Name}: {lightError.Message}");
                            }
                        }

                        igz.GameVersion = GameVersion.NSX;
                        string? newNamespace = target != path ? NamespaceUtils.GetFileName(target, false) : null;

                        // Intro di Crash 3 aggiunta al file principale del livello (copiata da Gone Tomorrow della Switch)
                        if (addIntro && mainFile && !hasIntro && !levelHasC3Intro)
                        {
                            try
                            {
                                var introDetails = new List<string>();
                                List<IgArchiveFile> introFiles = AddC3Intro(igz, switchDir, output, options.IntroLikeEditor, options.IntroMoveToStart,
                                                                            data => ApplyIntroOptions(data, options, introGameEvent),
                                                                            out string introInfo, introDetails);
                                levelHasC3Intro = true;
                                introAdded = true;
                                Increment(counters, "intro di Crash 3 aggiunta");
                                lines.Add($"intro di Crash 3 aggiunta a {target}: {introInfo}");
                                introLines.Add($"intro aggiunta a {target} (copiata da Gone Tomorrow, L321_GoneTomorrow Switch):");
                                foreach (string detail in introDetails) introLines.Add("  " + detail);
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
                                introLines.Add($"ERRORE aggiungendo l'intro a {target}: {e.GetType().Name}: {e.Message}");
                            }
                        }

                        // Cartella e nome del livello nei riferimenti, nella collisione e nel pacchetto
                        int RenameLevel(IgzFile x)
                        {
                            if (rename == null) return 0;
                            int count = RenameNamespaces(x, renamedNamespaces);
                            count += RenameCollisionKeys(x, renamedNamespaces);

                            // Name of the static collision, stored as text in its name list
                            if (collision)
                            {
                                foreach (igNameList list in x.Objects.OfType<igNameList>())
                                {
                                    foreach (igNameMetaField entry in list._data)
                                    {
                                        if (entry._name == null || !entry._name.Contains(rename.FromName, StringComparison.OrdinalIgnoreCase)) continue;
                                        entry._name = ReplaceIgnoreCase(entry._name, rename.FromName, rename.ToName);
                                        count++;
                                    }
                                }
                            }

                            // Files listed by the package (keeping their type)
                            if (PackageInfo(target) != null)
                            {
                                foreach (igStreamingChunkInfo info in x.Objects.OfType<igStreamingChunkInfo>())
                                {
                                    foreach (ChunkFileInfoMetaField entry in info._required._data)
                                    {
                                        if (entry._name == null) continue;
                                        string renamedName = rename.Apply(entry._name);
                                        if (renamedName == entry._name) continue;
                                        entry._name = renamedName.ToLowerInvariant();
                                        count++;
                                    }
                                }
                            }
                            return count;
                        }

                        int renamedCount = RenameLevel(igz);
                        if (renamedCount > 0) lines.Add($"riferimenti al livello rinominati: {renamedCount} in {target}");

                        IgArchiveFile converted = new IgArchiveFile(target, GameVersion.NSX);
                        converted.SetData(igz.Save(newNamespace));
                        string? readError = RereadError(converted);

                        // File di altri livelli da cui sono state tolte entita'
                        if (modifiedOtherFiles.Contains(target))
                        {
                            // Non resta nessuna entita' (per esempio tutte decorazioni con modelli che la Switch non ha): il file resta
                            // fuori dall'archivio e dal pacchetto. Scritto con la lista vuota non si rileggeva (L304_BoneYard_Art in
                            // Level 11, che restava su LOADING)
                            if (!igz.Objects.OfType<igEntity>().Any() &&
                                igz.Objects.All(o => o.GetType().Name is "igObjectList" or "igEntityList" or "igNameList"))
                            {
                                Increment(counters, "file di altri livelli tolti (nessuna entita' rimasta)");
                                removedEntities.Add($"  {target}: non resta nessuna entita', il file resta fuori dall'archivio e dal pacchetto");
                                included.Add(target.ToLowerInvariant());
                                continue;
                            }
                            // Non si rilegge: meglio il file com'era, senza togliere entita', che un file rovinato
                            if (readError != null)
                            {
                                IgzFile fresh = file.ToIgzFile();
                                if (options.WithoutIntro)
                                {
                                    HashSet<igObject> freshIntros = fresh.Objects.OfType<igEntity>()
                                        .Where(e => e.GetComponent<common_C3_IntroSequenceData>() != null)
                                        .Cast<igObject>()
                                        .ToHashSet();
                                    if (freshIntros.Count > 0) fresh.Remove(freshIntros);
                                }
                                fresh.GameVersion = GameVersion.NSX;
                                RenameLevel(fresh);
                                converted.SetData(fresh.Save(newNamespace));
                                string? again = RereadError(converted);
                                removedEntities.Add($"  ATTENZIONE: {target} dopo aver tolto le entita' non si rileggeva ({readError}): tenuto com'era, " +
                                                    "senza toglierle" + (again != null ? $" (ma non si rilegge neanche cosi': {again})" : ""));
                                Increment(counters, "file di altri livelli tenuti senza togliere entita' (non si rileggevano)");
                                readError = again;
                            }
                        }
                        if (readError != null) unreadableFiles.Add($"  {target}: {readError}");
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

            foreach (var (floorFile, _) in options.FloorBlocks.Where(f => !usedFloorFilters.Contains(f.file)))
                removedEntities.Add($"  --pavimento-blocchi {floorFile}: nessun file con blocchi di collisione e questo testo nel percorso");

            // Everything else of the original level (its own assets with their Switch names)
            if (baseArchive != null)
            {
                foreach (IgArchiveFile file in sw.ByArchive[baseArchive])
                {
                    // I file di posizionamento del livello vengono solo dall'archivio PC; la collisione statica
                    // (models/maps/...) e' quella del livello PC, quella dell'originale occuperebbe solo memoria
                    string lower = file.Path.ToLowerInvariant();
                    if (lower.StartsWith("maps/") || lower.StartsWith("packages/") || lower.StartsWith("update/") || lower.StartsWith("models/maps/")) continue;
                    AddOriginal(file, "file del livello originale Switch aggiunti");
                }
            }

            // Riferimenti a file che sulla Switch stanno solo negli archivi di altri livelli (banchi dei suoni, script...): il
            // gioco carica l'archivio del livello e quelli comuni, non quelli degli altri livelli, quindi non li troverebbe.
            // Si copiano dall'archivio di un livello che li ha (le loro dipendenze si aggiungono sotto, dallo stesso archivio).
            // Non i file in maps/ (pezzi di altri livelli: telecamere, nemici...), che restano nel rapporto
            var copiedFromLevels = new List<string>();
            {
                // archivi dei livelli: quelli con un pacchetto in packages/generated/maps e quelli con il nome di un livello o di
                // un boss (Lxxx_..., Bxxx_...: i pacchetti dei boss stanno una cartella piu' in basso)
                var levelArchives = LevelNames(sw.ByPath.Keys).Select(l => l.ToLowerInvariant())
                    .Concat(sw.ByArchive.Keys.Where(a => a.Length > 5 && (a[0] == 'l' || a[0] == 'b') && char.IsDigit(a[1]) && a[4] == '_'))
                    .Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();
                var levelArchiveSet = new HashSet<string>(levelArchives);
                var outputNames = new HashSet<string>(output.Files.SelectMany(f => NameAliases(NamespaceUtils.GetFileName(f.Path, false).ToLowerInvariant())));
                var commonNames = new HashSet<string>();
                foreach (var (archiveName, archiveFiles) in sw.ByArchive)
                {
                    if (levelArchiveSet.Contains(archiveName)) continue;
                    foreach (IgArchiveFile archiveFile in archiveFiles)
                        foreach (string alias in NameAliases(NamespaceUtils.GetFileName(archiveFile.Path, false).ToLowerInvariant()))
                            commonNames.Add(alias);
                }
                var originalPaths = new HashSet<string>(fromSwitch.Select(f => f.Path.ToLowerInvariant()));
                var users = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                foreach (IgArchiveFile converted in output.Files.ToList())
                {
                    string lower = converted.Path.ToLowerInvariant();
                    if (!converted.IsIGZ() || originalPaths.Contains(lower) || lower.StartsWith("update/")) continue;
                    FixupCollection? fixups = RawFixups(converted);
                    if (fixups == null) continue;
                    var names = new List<string>();
                    foreach (TDEP_Fixup.TDEP_Item item in fixups.TDEP)
                        if (!string.IsNullOrEmpty(item.path))
                            names.Add(NameAliases(NamespaceUtils.GetFileName(NormalizeFixupPath(item.path), false).ToLowerInvariant()).First());
                    foreach (NamedReference reference in fixups.handleReferences.Concat(fixups.objectReferences).Concat(fixups.exidReferences))
                        if (!string.IsNullOrEmpty(reference.namespaceName)) names.Add(reference.namespaceName.ToLowerInvariant());
                    foreach (string name in names)
                    {
                        if (name.Length == 0 || name.All(char.IsDigit) || name.StartsWith("meta") || outputNames.Contains(name) || commonNames.Contains(name)) continue;
                        if (!users.TryGetValue(name, out SortedSet<string>? usedBy)) users[name] = usedBy = new SortedSet<string>(StringComparer.Ordinal);
                        usedBy.Add(NamespaceUtils.GetFileName(converted.Path));
                    }
                }
                foreach (var (name, usedBy) in users)
                {
                    string? archive = levelArchives.FirstOrDefault(a => sw.Names(a).ContainsKey(name));
                    if (archive == null) continue;
                    string who = string.Join(", ", usedBy.Take(3)) + (usedBy.Count > 3 ? $" e altri {usedBy.Count - 3}" : "");
                    foreach (IgArchiveFile dependency in sw.Names(archive)[name])
                    {
                        if (included.Contains(dependency.Path.ToLowerInvariant())) continue;
                        AddOriginal(dependency, "file presi da archivi di altri livelli (riferimenti)");
                        lines.Add($"preso da {archive}.pak (usato da {who}): {dependency.Path}");
                        copiedFromLevels.Add($"  {dependency.Path} da {archive}.pak (usato da {who})");
                    }
                }
            }

            // Modelli indicati per nome nei dati delle entita' (CGameEntityData._modelName e _skinName: casse, piattaforme,
            // blocchi che si vedono...). Sono testi, che i controlli sui riferimenti non vedevano: quelli che sulla Switch
            // stanno solo negli archivi di altri livelli si copiano (le loro dipendenze si aggiungono sotto). Il rapporto li
            // elenca tutti, insieme ai blocchi di collisione (CDynamicClipEntity) e al modello che mostrano
            var entityModels = new List<string>();
            int modelsInArchive = 0;
            var clipBlocks = new List<string>();
            {
                var originalPaths = new HashSet<string>(fromSwitch.Select(f => f.Path.ToLowerInvariant()));
                var levelArchiveSet = new HashSet<string>(LevelNames(sw.ByPath.Keys).Select(l => l.ToLowerInvariant())
                    .Concat(sw.ByArchive.Keys.Where(a => a.Length > 5 && (a[0] == 'l' || a[0] == 'b') && char.IsDigit(a[1]) && a[4] == '_')));
                var archivesOfPath = new Dictionary<string, List<string>>();
                foreach (var (archiveFile, archiveName) in sw.ArchiveOf)
                {
                    string key = archiveFile.Path.ToLowerInvariant();
                    if (!archivesOfPath.TryGetValue(key, out List<string>? archives)) archivesOfPath[key] = archives = [];
                    if (!archives.Contains(archiveName)) archives.Add(archiveName);
                }
                var outputPaths = new HashSet<string>(output.Files.Select(f => f.Path.ToLowerInvariant()));
                var modelUsers = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                var modelTexts = new Dictionary<string, string>();

                static string ClipModels(IEnumerable<CDynamicClipEntity> clips) => string.Join(", ", clips
                    .Select(c => (c._entityData as CGameEntityData)?._modelName)
                    .Select(m => string.IsNullOrWhiteSpace(m) ? "nessuno (invisibili)" : m)
                    .Distinct());

                foreach (IgArchiveFile converted in output.Files.ToList())
                {
                    string lower = converted.Path.ToLowerInvariant();
                    if (!converted.IsIGZ() || !lower.StartsWith("maps/") || originalPaths.Contains(lower)) continue;
                    IgzFile convertedIgz;
                    try
                    {
                        convertedIgz = converted.ToIgzFile();
                    }
                    catch
                    {
                        continue;
                    }
                    string fileName = NamespaceUtils.GetFileName(converted.Path);
                    void UseModel(string? modelName, string folder)
                    {
                        string? modelPath = ModelPathFromName(modelName, folder);
                        if (modelPath == null) return;
                        if (!modelUsers.TryGetValue(modelPath, out SortedSet<string>? users))
                        {
                            modelUsers[modelPath] = users = new SortedSet<string>(StringComparer.Ordinal);
                            modelTexts[modelPath] = modelName!;
                        }
                        users.Add(fileName);
                    }
                    // Modello delle decorazioni (CModelComponentData._fileName, "models:\Levels\...\Nome.igb"): sul PC il gioco lo
                    // trova in qualunque archivio, e l'editor non lo copia se non sta nell'archivio del livello da cui prendi
                    // l'oggetto (i pavimenti di neve di Cold Hard Crash sono nell'archivio di Snow Biz). Sulla Switch non si vede
                    foreach (CModelComponentData modelComponent in convertedIgz.Objects.OfType<CModelComponentData>())
                        UseModel(modelComponent._fileName, "models");
                    foreach (CGameEntityData data in convertedIgz.Objects.OfType<CGameEntityData>())
                    {
                        // _modelName e' un modello (models/...), _skinName un personaggio (actors/..., come nell'editor)
                        UseModel(data._modelName, "models");
                        UseModel(data._skinName, "actors");
                    }

                    List<CDynamicClipEntity> clips = convertedIgz.Objects.OfType<CDynamicClipEntity>().ToList();
                    if (clips.Count == 0) continue;
                    string switchClips = "";
                    if (sw.ByPath.TryGetValue(lower, out IgArchiveFile? switchVersion))
                    {
                        try
                        {
                            List<CDynamicClipEntity> originalClips = switchVersion.ToIgzFile().Objects.OfType<CDynamicClipEntity>().ToList();
                            switchClips = $"; originale Switch: {originalClips.Count} blocchi, modello {ClipModels(originalClips)}";
                        }
                        catch (Exception e)
                        {
                            switchClips = $"; originale Switch non leggibile ({e.Message})";
                        }
                    }
                    clipBlocks.Add($"  {converted.Path}: {clips.Count} blocchi, modello {ClipModels(clips)}{switchClips}");
                }

                foreach (var (modelPath, users) in modelUsers)
                {
                    string who = string.Join(", ", users.Take(3)) + (users.Count > 3 ? $" e altri {users.Count - 3}" : "");
                    string status;
                    if (outputPaths.Contains(modelPath))
                    {
                        status = "nell'archivio";
                    }
                    else if (archivesOfPath.TryGetValue(modelPath, out List<string>? archives))
                    {
                        if (archives.Any(a => !levelArchiveSet.Contains(a)))
                        {
                            status = "negli archivi comuni del gioco";
                        }
                        else
                        {
                            string archive = archives[0];
                            string name = NamespaceUtils.GetFileName(modelPath, false);
                            var copied = new List<string>();
                            foreach (IgArchiveFile modelFile in sw.Names(archive).GetValueOrDefault(name) ?? new List<IgArchiveFile>())
                            {
                                if (included.Contains(modelFile.Path.ToLowerInvariant())) continue;
                                AddOriginal(modelFile, "modelli delle entita' presi da archivi di altri livelli");
                                copied.Add(NamespaceUtils.GetFileName(modelFile.Path));
                                lines.Add($"modello di un'entita' preso da {archive}.pak (usato da {who}): {modelFile.Path}");
                            }
                            status = $"solo nell'archivio di {archive}: copiato ({string.Join(", ", copied)})";
                        }
                    }
                    else
                    {
                        status = "NON C'E' nel gioco Switch: quelle entita' non si vedono";
                    }
                    if (status == "nell'archivio") modelsInArchive++;
                    else entityModels.Add($"  {modelTexts[modelPath]}: {status} (usato da {who})");
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

            // --solo-usati: modelli, materiali e texture che dopo lo sfoltimento non usa piu' nessun file del livello
            if (options.OnlyUsed)
            {
                // i file tolti si possono rimettere sotto, se qualcosa li usa
                foreach (IgArchiveFile removed in RemoveUnusedFiles(output, report, lines)) included.Remove(removed.Path.ToLowerInvariant());
            }

            // Dipendenze della grafica presa dagli originali Switch (modelli e materiali: i loro materiali e le loro texture)
            // che non sono nell'archivio ne' negli archivi comuni del gioco, comprese quelle indicate per hash (EXID) con
            // nomi che il PC non conosce, che il ciclo delle dipendenze non riconosceva: senza, la grafica resta senza
            // materiale o senza texture. Si aggiungono dall'archivio Switch del file che le usa, o da un altro che le ha
            var graphicsDependencies = new List<string>();
            var excludedDependencies = new List<string>();
            var skippedDependencies = new List<string>();
            var skippedDependencyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            {
                var levelArchiveSet = new HashSet<string>(LevelNames(sw.ByPath.Keys).Select(l => l.ToLowerInvariant())
                    .Concat(sw.ByArchive.Keys.Where(a => a.Length > 5 && (a[0] == 'l' || a[0] == 'b') && char.IsDigit(a[1]) && a[4] == '_')));
                var commonNames = new HashSet<string>();
                var switchByName = new Dictionary<string, List<IgArchiveFile>>();
                var hashNames = new Dictionary<uint, string>();
                foreach (var (archiveName, archiveFiles) in sw.ByArchive)
                {
                    foreach (IgArchiveFile archiveFile in archiveFiles)
                    {
                        string lowerPath = archiveFile.Path.ToLowerInvariant();
                        string name = NamespaceUtils.GetFileName(lowerPath, false);
                        hashNames.TryAdd(NamespaceUtils.ComputeHash(name), name);
                        if (!levelArchiveSet.Contains(archiveName)) commonNames.Add(name);
                        if (lowerPath.StartsWith("maps/") || lowerPath.StartsWith("packages/") || lowerPath.StartsWith("update/") ||
                            lowerPath.Contains("staticcollision")) continue;
                        if (!switchByName.TryGetValue(name, out List<IgArchiveFile>? list)) switchByName[name] = list = [];
                        list.Add(archiveFile);
                    }
                }
                var outputNames = new HashSet<string>(output.Files.Select(f => NamespaceUtils.GetFileName(f.Path, false).ToLowerInvariant()));
                var archiveOfPath = new Dictionary<string, string>();
                foreach (IgArchiveFile original in fromSwitch)
                    if (sw.ArchiveOf.TryGetValue(original, out string? archiveName)) archiveOfPath.TryAdd(original.Path.ToLowerInvariant(), archiveName);

                static bool IsGraphicsPath(string path) =>
                    path.StartsWith("models/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("materialinstances/", StringComparison.OrdinalIgnoreCase);
                var pending = new Queue<IgArchiveFile>(output.Files.Where(f => f.IsIGZ() && IsGraphicsPath(f.Path)));
                while (pending.Count > 0)
                {
                    IgArchiveFile user = pending.Dequeue();
                    FixupCollection? fixups = RawFixups(user);
                    if (fixups == null) continue;
                    var referencedNames = new HashSet<string>();
                    foreach (TDEP_Fixup.TDEP_Item item in fixups.TDEP)
                    {
                        if (!string.IsNullOrEmpty(item.path)) referencedNames.Add(NamespaceUtils.GetFileName(NormalizeFixupPath(item.path), false));
                        if (!string.IsNullOrEmpty(item.name)) referencedNames.Add(NamespaceUtils.GetFileName(item.name, false).ToLowerInvariant());
                    }
                    foreach (NamedReference reference in fixups.handleReferences.Concat(fixups.objectReferences).Concat(fixups.exidReferences))
                        if (!string.IsNullOrEmpty(reference.namespaceName)) referencedNames.Add(reference.namespaceName.ToLowerInvariant());

                    foreach (string referenced in referencedNames)
                    {
                        string name = referenced;
                        if (name.All(char.IsDigit))
                        {
                            if (!uint.TryParse(name, out uint hash) || !hashNames.TryGetValue(hash, out string? resolved)) continue;
                            name = resolved;
                        }
                        if (name.Length == 0 || name.StartsWith("meta") || outputNames.Contains(name) || commonNames.Contains(name)) continue;

                        string? userArchive = archiveOfPath.GetValueOrDefault(user.Path.ToLowerInvariant());
                        List<IgArchiveFile>? candidates = null;
                        if (userArchive != null && sw.Names(userArchive).TryGetValue(name, out List<IgArchiveFile>? inArchive)) candidates = inArchive;
                        else if (userArchive != null && !options.DependenciesFromOtherLevels)
                        {
                            // Il file che la usa viene dall'archivio Switch di un livello che non la ha (ne' negli archivi comuni): in quel
                            // livello il gioco funziona senza, e presa da un altro livello puo' fare danni. Nel Level 7 i cristalli di Koala
                            // Kong (materiali di b103_koalakong) nominano l'insieme C1_KoalaKong_Cave_materials, che ha solo Stormy Ascent:
                            // aggiunto fino alla v59, lo schermo restava nero (dalla v60 no, --dipendenze-altri-livelli per riaverlo)
                            if (switchByName.TryGetValue(name, out List<IgArchiveFile>? elsewhere) && skippedDependencyKeys.Add($"{name}|{userArchive}"))
                                skippedDependencies.Add($"  NON AGGIUNTA: {elsewhere[0].Path} (c'e' in {sw.ArchiveOf.GetValueOrDefault(elsewhere[0]) ?? "?"}.pak), " +
                                                        $"usata da {NamespaceUtils.GetFileName(user.Path)} di {userArchive}, che non la ha: l'originale funziona senza");
                            continue;
                        }
                        else if (switchByName.TryGetValue(name, out List<IgArchiveFile>? anywhere))
                        {
                            candidates = new List<IgArchiveFile> { anywhere[0] };
                            if (sw.ArchiveOf.TryGetValue(anywhere[0], out string? firstArchive) &&
                                sw.Names(firstArchive).TryGetValue(name, out List<IgArchiveFile>? sameArchive)) candidates = sameArchive;
                        }
                        if (candidates == null) continue;  // non c'e' nel gioco Switch: lo dicono le altre sezioni

                        outputNames.Add(name);
                        foreach (IgArchiveFile dependency in candidates)
                        {
                            string key = dependency.Path.ToLowerInvariant();
                            if (included.Contains(key)) continue;
                            string dependencyArchive = sw.ArchiveOf.GetValueOrDefault(dependency) ?? "?";
                            string userText = $"{NamespaceUtils.GetFileName(user.Path)}{(userArchive != null ? $" di {userArchive}" : "")}";
                            // --escludi vale anche qui (dalla v59): per provare il livello senza una dipendenza presa da un altro livello
                            if (options.Exclude.Any(x => dependency.Path.Contains(x, StringComparison.OrdinalIgnoreCase)))
                            {
                                excludedDependencies.Add($"  ESCLUSA (--escludi): {dependency.Path} da {dependencyArchive}.pak (usato da {userText})");
                                lines.Add($"dipendenza della grafica esclusa (--escludi): {dependency.Path} (da {user.Path})");
                                continue;
                            }
                            AddOriginal(dependency, "dipendenze della grafica aggiunte (anche per hash)");
                            archiveOfPath.TryAdd(key, dependencyArchive);
                            graphicsDependencies.Add($"  {dependency.Path} da {dependencyArchive}.pak (usato da {userText})");
                            lines.Add($"dipendenza della grafica: {dependency.Path} (da {user.Path})");
                            if (dependency.IsIGZ()) pending.Enqueue(dependency);
                        }
                    }
                }
            }

            // --prova-texture <MB> (prova): texture degli archivi di livelli originali, che nessun file del livello usa e che non
            // stanno negli archivi comuni, aggiunte al pacchetto: il gioco le carica con il livello. Con un livello che si vede (per
            // esempio con --togli-entita "*") dice se lo schermo nero viene dalla memoria: stesse entita', piu' texture caricate.
            // Prese da tutti i livelli in un ordine misto (hash del percorso), non solo le piu' grandi: numero e misure simili a
            // quelle delle texture vere di un livello (conta anche quante sono, non solo quanto pesano)
            if (options.TestTexturesMb != null)
            {
                bool IsLevelArchive(string name) => name.Length > 5 && (name[0] == 'l' || name[0] == 'b') && char.IsDigit(name[1]) && name[4] == '_';
                var inCommonArchives = new HashSet<string>(sw.ByArchive.Where(a => !IsLevelArchive(a.Key))
                                                             .SelectMany(a => a.Value).Select(f => f.Path.ToLowerInvariant()));
                var usedNames = new HashSet<string>(output.Files.Select(f => NamespaceUtils.GetFileName(f.Path, false).ToLowerInvariant()));
                long wanted = options.TestTexturesMb.Value * 1024L * 1024L, added = 0;
                int count = 0;
                var sources = new SortedSet<string>(StringComparer.Ordinal);
                var candidates = sw.ByArchive
                    .Where(a => IsLevelArchive(a.Key))
                    .SelectMany(a => a.Value.Where(f => f.IsIGZ() && f.UncompressedSize > 0 && f.Path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
                                            .Select(f => (archive: a.Key, file: f)))
                    .OrderBy(c => NamespaceUtils.ComputeHash(c.file.Path))
                    .ThenBy(c => c.file.Path, StringComparer.Ordinal);
                foreach (var (archive, texture) in candidates)
                {
                    if (added >= wanted) break;
                    string key = texture.Path.ToLowerInvariant();
                    if (included.Contains(key) || inCommonArchives.Contains(key) || !usedNames.Add(NamespaceUtils.GetFileName(key, false))) continue;
                    AddOriginal(texture, "texture aggiunte per la prova della memoria (--prova-texture)");
                    added += texture.UncompressedSize;
                    count++;
                    sources.Add(archive);
                }
                report.AppendLine($"=== PROVA DELLA MEMORIA (--prova-texture {options.TestTexturesMb}) ===");
                report.AppendLine($"Aggiunte al pacchetto {count} texture ({Mb((ulong)added)}) di {sources.Count} livelli originali che nessun file del livello usa:");
                report.AppendLine("il gioco le carica con il livello. Le entita' sono le stesse: se con queste lo schermo diventa nero (o il caricamento si");
                report.AppendLine("blocca) il problema e' la memoria delle texture, non un'entita'.");
                report.AppendLine($"  livelli: {string.Join(", ", sources)}");
                report.AppendLine();
            }

            // Il pacchetto elenca i file del livello, come quello del PC: i file di altri livelli (maps/...) che l'editor
            // copia per gli oggetti presi da li' ci sono solo se li elenca il pacchetto del PC (il gioco li carica interi,
            // con tutte le loro entita'); gli altri restano nell'archivio, fuori dal pacchetto
            try
            {
                if (output.FindPackageFile() == null)
                {
                    lines.Add("file del pacchetto (_pkg.igz) non trovato o non unico: non aggiornato");
                }
                else
                {
                    string? ownDir = output.Files.Select(f => PackageInfo(f.Path)).FirstOrDefault(p => p != null)?.dir;
                    bool InPackage(string p)
                    {
                        if (pcPackageList.Count == 0 || !p.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) || pcPackageList.Contains(p)) return true;
                        // una copia di un file di un altro livello resta fuori anche se finisce nella cartella del livello
                        // (--sostituisci con il livello da cui l'editor l'ha copiata)
                        if (otherLevelTargets.Contains(p)) return false;
                        return ownDir != null && p.StartsWith("maps/" + ownDir + "/", StringComparison.OrdinalIgnoreCase);
                    }
                    var levelFiles = output.Files.Where(f => !f.Path.StartsWith("update/", StringComparison.OrdinalIgnoreCase)).ToList();
                    var outside = levelFiles.Where(f => !InPackage(f.Path)).ToList();
                    output.RebuildPackageFile(levelFiles.Where(f => InPackage(f.Path)).ToList(), out _);
                    Increment(counters, "file del pacchetto aggiornato");
                    if (outside.Count > 0)
                    {
                        counters["file di altri livelli fuori dal pacchetto (come nell'editor)"] = outside.Count;
                        foreach (IgArchiveFile f in outside) lines.Add($"fuori dal pacchetto (usato solo per gli oggetti presi): {f.Path}");
                    }
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
                    string? baseTemplate = detectedBase != null && FindZoneInfo(sw, detectedBase.Value.to) != null ? detectedBase.Value.to : null;
                    if (baseTemplate != null && options.Game != null)
                    {
                        // Zone info di un livello di un altro gioco con l'anno cambiato: il gioco si blocca sul titolo
                        // (Level 3, L112 come Crash 3); si usa quella del primo livello del gioco scelto
                        string[] baseParts = FindZoneInfo(sw, baseTemplate)!.Path.Split('/');
                        string baseGame = baseParts.Length > 1 ? baseParts[1].ToLowerInvariant() : "";
                        if (baseGame != options.Game)
                        {
                            report.AppendLine($"{(options.GameFromEditor ? "gioco dell'editor" : "--gioco")} {options.Game}: {baseTemplate} e' di {baseGame}, zone info presa da {gameDefault}");
                            baseTemplate = null;
                        }
                    }
                    zoneTemplate = baseTemplate ?? gameDefault;
                    report.AppendLine($"--nuovo: zone info presa da {zoneTemplate} (scelta automatica; per cambiarla --zoneinfo-da <livello>)");
                    if (options.Game != null && options.GameFromEditor)
                        report.AppendLine($"  gioco impostato nell'editor (Crash Mode): {options.Game}, cartella del livello: {game} (spostata in quella del gioco)");
                    else if (options.Game != null) report.AppendLine($"  gioco scelto con --gioco: {options.Game}, cartella del livello: {game}");
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

                // Memoria del livello (--memoria): dimensioni delle aree di memoria da un'altra zone info originale, oppure in MB
                // (--memoria 300, --memoria 300/40: area del livello e area globale)
                CZoneInfo? poolSource = null;
                string? poolFrom = null;
                TryParseMemoryMb(options.Memory, out ulong? customLevelPool, out ulong? customGlobalPool, out _);
                if (customLevelPool != null)
                {
                    // MB scritti: nessuna zone info da cui prenderle
                }
                else if (options.Memory.Equals("max", StringComparison.OrdinalIgnoreCase))
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
                if (zoneTemplate == null && (forcedYear != null || poolSource != null || customLevelPool != null))
                    report.AppendLine("  ATTENZIONE: con --zoneinfo-da pc --gioco e --memoria non si applicano (cambiano solo la zone info presa da un originale)");
                // Riflessi planari: il ghiaccio che riflette (Snow Go, Cold Hard Crash...) sulla Switch disegna il riflesso che il
                // gioco calcola solo se la zone info li accende; spenti (N. Sanity Beach) il ghiaccio resta nero e sembra che
                // non ci sia. Con "auto" si accendono se il livello ha i file dei riflessi di un altro livello (..._Reflections.igz)
                List<string> reflectionFiles = output.Files
                    .Where(f => f.Path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith("_reflections.igz", StringComparison.OrdinalIgnoreCase))
                    .Select(f => NamespaceUtils.GetFileName(f.Path)).ToList();
                bool? planarReflections = null;
                if (options.Reflections == "si") planarReflections = true;
                else if (options.Reflections == "no") planarReflections = false;
                else if (reflectionFiles.Count > 0) planarReflections = true;
                string reflectionsReason = options.Reflections != "auto" ? $"--riflessi {options.Reflections}"
                    : reflectionFiles.Count > 0 ? $"il livello ha {string.Join(", ", reflectionFiles.Take(3))}" : "";
                // Tessere dei bonus di Crash 1 nel livello (teletrasporti o tessere): fuori da Crash 1 serve il gestore che resta
                bool crash1Tokens = LevelHasCrash1Tokens(output);
                bool registered;
                try
                {
                    registered = BuildRegistration(levelId, registration, sw, updatePath, report, embedded, rename, renamedNamespaces, zoneTemplate, registerIn,
                                                   options.SaveMode != "originale", levelHasC3Intro, forcedYear, poolSource, poolFrom, !options.WithoutMode,
                                                   planarReflections, reflectionsReason, crash1Tokens, options.TokensOnlyCrash1, options.TokensNoHud,
                                                   customLevelPool != null ? (customLevelPool.Value, customGlobalPool, options.Memory) : ((ulong, ulong?, string)?)null);
                }
                catch (Exception registrationError)
                {
                    report.AppendLine($"  ERRORE nella registrazione del livello: {registrationError.GetType().Name}: {registrationError.Message}");
                    registered = false;
                    try
                    {
                        File.Delete(updatePath + ".tmp");
                    }
                    catch
                    {
                        // il file temporaneo resta: non cambia niente
                    }
                }
                if (!registered)
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
                    report.AppendLine("Con NST Pak Manager (1.7 o successivo) basta il livello: update.pak lo crea l'app dai file update/ del livello");
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

            foreach (string pattern in options.SwitchOriginals.Where(x => !matchedSwitchOriginals.Contains(x)))
                switchOriginalUsed.Add($"  --originale-switch {pattern}: nessun file di altri livelli con questo testo nel percorso");
            foreach (string filter in options.RemoveEntities.Where(f => !usedFilters.Contains(f)))
                removedEntities.Add($"  --togli-entita {filter}: nessuna entita' trovata nei file di altri livelli");
            if (removedEntities.Count > 0 || switchOriginalUsed.Count > 0)
            {
                report.AppendLine("=== FILE DI ALTRI LIVELLI CAMBIATI ===");
                foreach (string line in switchOriginalUsed) report.AppendLine(line);
                foreach (string line in removedEntities) report.AppendLine(line);
                report.AppendLine();
            }

            if (copiedFromLevels.Count > 0)
            {
                report.AppendLine($"=== FILE PRESI DAGLI ARCHIVI DI ALTRI LIVELLI ({copiedFromLevels.Count}) ===");
                report.AppendLine("Il livello li usa e sulla Switch stanno solo negli archivi di altri livelli, che il gioco non carica");
                report.AppendLine("con questo: sono stati copiati nell'archivio del livello, con le loro dipendenze.");
                foreach (string line in copiedFromLevels.Take(80)) report.AppendLine(line);
                if (copiedFromLevels.Count > 80) report.AppendLine($"  ... e altri {copiedFromLevels.Count - 80}");
                report.AppendLine();
            }
            report.AppendLine($"=== MODELLI DELLE ENTITA' INDICATI PER NOME ({entityModels.Count + modelsInArchive}) ===");
            report.AppendLine("Modelli scritti come testo nelle entita' (decorazioni: _fileName; casse, piattaforme: _modelName; personaggi:");
            report.AppendLine("_skinName). Sul PC il gioco li trova in qualunque archivio e l'editor non sempre li copia; sulla Switch quelli");
            report.AppendLine("che non sono nell'archivio del livello non si vedrebbero: se stanno nell'archivio di un altro livello vengono copiati.");
            report.AppendLine($"  {modelsInArchive} gia' nell'archivio; gli altri:");
            foreach (string line in entityModels.Take(80)) report.AppendLine(line);
            if (entityModels.Count > 80) report.AppendLine($"  ... e altri {entityModels.Count - 80}");
            report.AppendLine("Blocchi di collisione (CDynamicClipEntity: si cammina sopra, si vede solo il loro modello):");
            if (clipBlocks.Count == 0) report.AppendLine("  nessuno");
            foreach (string line in clipBlocks) report.AppendLine(line);
            report.AppendLine();
            report.AppendLine($"=== DIPENDENZE DELLA GRAFICA AGGIUNTE ALLA FINE ({graphicsDependencies.Count}) ===");
            report.AppendLine("Materiali e texture usati dai modelli e dai materiali presi dagli originali Switch che mancavano");
            report.AppendLine("(anche quelli indicati solo per hash, o tolti per sbaglio da --solo-usati): aggiunti dall'archivio Switch");
            report.AppendLine("del file che li usa. Senza, quella grafica resterebbe senza materiale o senza texture (nera o invisibile).");
            if (graphicsDependencies.Count == 0) report.AppendLine("  nessuna: non ne mancava nessuna");
            foreach (string line in graphicsDependencies.Take(80)) report.AppendLine(line);
            if (graphicsDependencies.Count > 80) report.AppendLine($"  ... e altre {graphicsDependencies.Count - 80}");
            foreach (string line in excludedDependencies.Take(40)) report.AppendLine(line);
            if (excludedDependencies.Count > 40) report.AppendLine($"  ... e altre {excludedDependencies.Count - 40} escluse");
            if (skippedDependencies.Count > 0)
                report.AppendLine("  Non aggiunte (dalla v60): dipendenze che l'archivio Switch del file che le usa non ha, quindi nel suo livello il " +
                                  "gioco funziona senza; prese da un altro livello possono fare danni (Level 7: schermo nero). " +
                                  "--dipendenze-altri-livelli le prende lo stesso, come fino alla v59.");
            foreach (string line in skippedDependencies.Take(40)) report.AppendLine(line);
            if (skippedDependencies.Count > 40) report.AppendLine($"  ... e altre {skippedDependencies.Count - 40} non aggiunte");
            report.AppendLine();
            AppendLightVolumes(report, lightVolumes, playerStart);
            AppendOcclusionAndNearStart(report, occlusionInfos, placedEntities, playerStart, !options.KeepOcclusion);
            // Tessere dei bonus di Crash 1: analisi (con --gioco crash3 non si contano), dettagli in <rapporto>_tessere.txt
            string? tokensPath = null;
            try
            {
                Console.WriteLine("Controllo le tessere dei bonus...");
                tokensPath = AppendBonusTokens(report, output, sw, reportPath, options.Game ?? GameOfYear(editorYear));
            }
            catch (Exception e)
            {
                report.AppendLine("=== TESSERE DEI BONUS DI CRASH 1 (analisi) ===");
                report.AppendLine($"ERRORE: {e.GetType().Name}: {e.Message}");
                report.AppendLine();
            }
            // Intro di Crash 3: dove fa comparire Crash rispetto alla partenza, dettagli in <rapporto>_intro.txt
            string? introPath = null;
            bool introDataOptions = options.IntroSpawnOffset != null || options.IntroCrash1 || options.IntroCrash2 || options.IntroEvent != null ||
                                    options.IntroNoPortal || options.IntroRemovePortal || options.IntroTinyPortal;
            bool introOptions = options.IntroLikeEditor || options.IntroKeepPosition || options.IntroMoveToStart || introDataOptions;
            if (options.WithoutIntro)
            {
                if (introOptions)
                {
                    report.AppendLine("=== INTRO DI CRASH 3 ===");
                    report.AppendLine("  opzioni dell'intro (--intro-...) ignorate: c'e' --senza-intro");
                    report.AppendLine();
                }
            }
            else if (levelHasC3Intro || introOptions)
            {
                if (!introAdded && (options.IntroLikeEditor || options.IntroKeepPosition || options.IntroMoveToStart))
                    introLines.Add("--intro-come-editor, --intro-alla-partenza e --intro-posizione-originale valgono solo per l'intro aggiunta dal convertitore: " +
                                   (levelHasC3Intro ? "il livello ce l'aveva gia', resta com'e'" : "qui non e' stata aggiunta (serve --gioco crash3 o --aggiungi-intro)"));
                if (introDataOptions && !levelHasC3Intro)
                    introLines.Add("--intro-crash1, --intro-crash2, --intro-scostamento, --intro-evento, --intro-senza-portale, --intro-togli-portale e " +
                                   "--intro-portale-piccolo ignorate: il livello non ha l'intro di Crash 3 (con la zone info di Crash 1 o 2 il gioco usa la sua intro)");
                try
                {
                    Console.WriteLine("Controllo l'intro di Crash 3...");
                    introPath = AppendC3IntroAnalysis(report, output, sw, reportPath, introLines);
                }
                catch (Exception e)
                {
                    report.AppendLine("=== INTRO DI CRASH 3 (analisi) ===");
                    foreach (string line in introLines) report.AppendLine("  " + line);
                    report.AppendLine($"ERRORE: {e.GetType().Name}: {e.Message}");
                    report.AppendLine();
                }
            }
            if (unreadableFiles.Count > 0)
            {
                report.AppendLine($"=== FILE CONVERTITI CHE NON SI RILEGGONO ({unreadableFiles.Count}) ===");
                report.AppendLine("Scritti nell'archivio, ma il convertitore non riesce a rileggerli: se il livello si blocca, sono i");
                report.AppendLine("primi sospetti (si possono lasciare fuori con --escludi <testo del percorso>).");
                foreach (string line in unreadableFiles.Take(80)) report.AppendLine(line);
                if (unreadableFiles.Count > 80) report.AppendLine($"  ... e altri {unreadableFiles.Count - 80}");
                report.AppendLine();
            }

            AppendMissingDependencies(report, output, fromSwitch, sw);

            Console.WriteLine("Controllo gli oggetti usati dal livello...");
            AppendMissingObjects(report, output, fromSwitch, sw);

            AppendMemoryReport(report, output, sw, sizeReference);

            // Quante texture si porta dietro ogni file in maps/ (per alleggerire la memoria togliendo le entita' giuste)
            try
            {
                Console.WriteLine("Conto le texture di ogni file...");
                AppendTextureUsers(report, output);
            }
            catch (Exception e)
            {
                report.AppendLine("=== TEXTURE PER FILE ===");
                report.AppendLine($"ERRORE: {e.GetType().Name}: {e.Message}");
                report.AppendLine();
            }

            // Schermo nero: la telecamera della partenza e i valori impossibili (NaN, infiniti, enormi) nei file convertiti
            try
            {
                AppendStartCamera(report, output);
            }
            catch (Exception e)
            {
                report.AppendLine("=== PARTENZA E TELECAMERA ===");
                report.AppendLine($"ERRORE: {e.GetType().Name}: {e.Message}");
                report.AppendLine();
            }
            try
            {
                Console.WriteLine("Controllo i valori dei file convertiti...");
                AppendOddValues(report, output);
            }
            catch (Exception e)
            {
                report.AppendLine("=== VALORI IMPOSSIBILI NEI FILE CONVERTITI ===");
                report.AppendLine($"ERRORE: {e.GetType().Name}: {e.Message}");
                report.AppendLine();
            }
            // --descrivi: gli oggetti dei file indicati, con l'archivio Switch da cui vengono
            if (options.Describe.Count > 0)
            {
                try
                {
                    var describeOrigins = new Dictionary<string, string>();
                    foreach (IgArchiveFile switchFile in fromSwitch)
                        describeOrigins.TryAdd(switchFile.Path.ToLowerInvariant(), "dall'archivio Switch " + (sw.ArchiveOf.GetValueOrDefault(switchFile) ?? "?"));
                    AppendDescribedFiles(report, output, options.Describe, describeOrigins);
                }
                catch (Exception e)
                {
                    report.AppendLine("=== FILE DESCRITTI (--descrivi) ===");
                    report.AppendLine($"ERRORE: {e.GetType().Name}: {e.Message}");
                    report.AppendLine();
                }
            }

            // Scritto prima in un file temporaneo: se qualcosa va storto il vecchio archivio resta com'era
            string? saveError = null;
            try
            {
                output.Save(outputPath, temporaryPath: outputPath + ".tmp");
            }
            catch (Exception e)
            {
                saveError = $"{e.GetType().Name}: {e.Message}";
                report.AppendLine($"ERRORE: archivio non scritto ({saveError})");
                report.AppendLine();
                try
                {
                    File.Delete(outputPath + ".tmp");
                }
                catch
                {
                    // il file temporaneo resta: non cambia niente
                }
            }

            if (entityLines.Count > 0)
            {
                report.AppendLine($"=== ENTITA' DEL FILE PRINCIPALE DEL LIVELLO ({entityLines.Count}) ===");
                foreach (string line in entityLines.Take(200)) report.AppendLine(line);
                if (entityLines.Count > 200) report.AppendLine($"  ... e altre {entityLines.Count - 200}");
                report.AppendLine();
            }

            if (otherContent.Length > 0)
            {
                report.AppendLine("=== CONTENUTO DEI FILE DI ALTRI LIVELLI (prima delle modifiche) ===");
                report.AppendLine("Per ogni file: se e' nel pacchetto (il gioco carica tutte le sue entita') o fuori, entita' raggruppate");
                report.AppendLine("per tipo e componenti (quante, esempi, file che usano), quelle che usano file che il gioco Switch non ha");
                report.AppendLine("(MANCA) e il confronto con l'originale Switch. Per toglierne alcune: --togli-entita <file>:<testo>");
                report.AppendLine("(testo nel nome, nel tipo, nei componenti o nei file usati; * = tutte).");
                report.AppendLine($"Modalita' del livello convertito (scelte nell'editor): " +
                                  $"{(levelModes.Count == 0 ? "nessuna" : string.Join(", ", levelModes.OrderBy(m => m).Select(ModeDescription)))}. " +
                                  "Le entita' dei livelli con un'altra modalita' possono tenere il gioco su LOADING.");
                report.Append(otherContent);
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
            if (saveError != null)
            {
                Console.WriteLine($"ERRORE: archivio {outputPath} non scritto: {saveError} (dettagli in {reportPath})");
                return 2;
            }
            Console.WriteLine($"Archivio Switch scritto in {outputPath} ({output.Files.Count} file, dettagli in {reportPath})");
            if (updatePath != null) Console.WriteLine($"Registrazione del livello scritta in {updatePath}: installala insieme al livello");
            else if (options.NewLevel) Console.WriteLine("ERRORE: update.pak con la registrazione del livello non creato (vedi il rapporto)");
            if (tokensPath != null) Console.WriteLine($"Analisi delle tessere dei bonus scritta in {tokensPath}: allegala insieme al rapporto");
            if (introPath != null) Console.WriteLine($"Analisi dell'intro di Crash 3 scritta in {introPath}: allegala insieme al rapporto");
            if (nameWarning != null) Console.WriteLine(nameWarning);
            if (missingReferences.Count > 0)
            {
                Console.WriteLine($"ATTENZIONE: il livello usa {missingReferences.Count} file che il gioco Switch non ha " +
                                  $"(per esempio {string.Join(", ", missingReferences.Keys.Take(3))}): il caricamento puo' bloccarsi. Elenco nel rapporto.");
            }
            return options.NewLevel && updatePath == null ? 1 : 0;
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
                                              CZoneInfo? poolSource, string? poolFrom, bool editorMode,
                                              bool? planarReflections, string reflectionsReason, bool crash1Tokens, bool tokensOnlyCrash1,
                                              bool tokensNoHud, (ulong level, ulong? global, string text)? customPool = null)
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
                    report.AppendLine($"  ERRORE: zone info di {zoneInfoTemplate} non trovata nel dump");
                    return false;
                }
            }

            IgArchive update = new IgArchive(updatePath, GameVersion.NSX);
            if (sw.ByArchive.TryGetValue(registerIn, out List<IgArchiveFile>? originals))
            {
                foreach (IgArchiveFile file in originals) update.AddFile(file.Clone());
                long size = originals.Sum(f => (long)f.UncompressedSize);
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
            CZoneInfo? pcZoneInfo = null;  // zone info dell'editor, quando si usa quella di un originale
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
                    report.AppendLine($"  zone info del PC non usata ({path}): al suo posto quella di {zoneInfoTemplate}");
                    try
                    {
                        if (file.ToIgzFile().FindObject<CZoneInfo>() is CZoneInfo pcZone)
                        {
                            report.AppendLine("    PC: " + DescribeZoneInfo(pcZone));
                            pcZoneInfo ??= pcZone;  // la prima, come per il gioco del livello (PcZoneInfoYear)
                        }
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
                // nome nel menu di debug: quello del livello, non dell'originale (come per la zone info del PC, RenameZoneInfo)
                if (zoneInfo._debugMenuName != null) zoneInfo._debugMenuName = rename?.ToName ?? levelId.Substring(levelId.LastIndexOf('/') + 1);
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
                // --memoria <MB>[/<MB>]: area di memoria del livello (e globale) scritta a mano. Tutti i livelli originali della
                // Switch hanno 200 MB e 30 MB; quella del PC dell'editor ~910 e ~890 MB. Per la prova di un livello pesante
                if (customPool != null)
                {
                    var (poolLevel, poolGlobal, poolText) = customPool.Value;
                    zoneInfo._levelPoolSize ??= new igSizeTypeMetaField();
                    zoneInfo._globalChunkPoolSize ??= new igSizeTypeMetaField();
                    ulong newGlobal = poolGlobal ?? zoneInfo._globalChunkPoolSize._size;
                    report.AppendLine($"  memoria del livello scritta con --memoria {poolText}: livello {Mb(zoneInfo._levelPoolSize._size)} -> " +
                                      $"{Mb(poolLevel)}, globale {Mb(zoneInfo._globalChunkPoolSize._size)} -> {Mb(newGlobal)} (i livelli originali " +
                                      "della Switch hanno tutti 200 MB e 30 MB: se il gioco non trova tanta memoria potrebbe bloccarsi al caricamento)");
                    zoneInfo._levelPoolSize._size = poolLevel;
                    zoneInfo._globalChunkPoolSize._size = newGlobal;
                }
                // Modalita' scelta nell'editor (personaggio, veicolo, opzioni speciali): l'editor la mette nella sua zone
                // info e, quando premi Play, crea i dati del personaggio che la attivano. Si fa lo stesso qui
                if (editorMode && pcZoneInfo != null)
                    ApplyEditorMode(zoneInfo, pcZoneInfo, levelId, sw, update, embedded, report);

                // Riflessi planari (vedi --riflessi): accesi anche se lo erano nella zone info dell'editor
                bool? reflections = planarReflections;
                if (reflections == null && pcZoneInfo != null && pcZoneInfo._planarReflectionsEnabled)
                {
                    reflections = true;
                    reflectionsReason = "accesi nella zone info dell'editor";
                }
                if (reflections != null && reflections.Value != zoneInfo._planarReflectionsEnabled)
                {
                    zoneInfo._planarReflectionsEnabled = reflections.Value;
                    report.AppendLine($"  riflessi planari {(reflections.Value ? "accesi" : "spenti")} ({reflectionsReason}): " +
                                      (reflections.Value ? "il ghiaccio che riflette si vede" : "come chiesto"));
                }
                else
                {
                    report.AppendLine($"  riflessi planari: {(zoneInfo._planarReflectionsEnabled ? "accesi" : "spenti")}, come nell'originale" +
                                      (reflectionsReason.Length > 0 ? $" ({reflectionsReason})" : ""));
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

            // Tessere dei bonus di Crash 1 in un livello di un altro gioco: il gestore delle tessere (Crash1_BonusRoundManager,
            // componente di Crash e Coco) all'avvio controlla il gioco e fuori da Crash 1 si toglie, quindi le tessere prese non
            // si contano e il teletrasporto del bonus non si accende. In update.pak va una copia dello script che resta
            if (crash1Tokens)
            {
                EGameYear? levelYear = forcedYear;
                try
                {
                    IgArchiveFile? registered = update.Files.FirstOrDefault(f => f.Path.Equals(zoneInfoPath, StringComparison.OrdinalIgnoreCase));
                    if (registered?.ToIgzFile().FindObject<CZoneInfo>() is CZoneInfo finalZone) levelYear = finalZone._year;
                }
                catch
                {
                    // resta il gioco di --gioco
                }
                if (levelYear == EGameYear.eGY_2017_Crash1)
                    report.AppendLine("  tessere dei bonus di Crash 1: livello di Crash 1, il gestore delle tessere resta com'e'");
                else if (tokensOnlyCrash1)
                    report.AppendLine($"  tessere dei bonus di Crash 1: --tessere-solo-crash1, il gestore resta com'e' (nel gioco {levelYear?.ToString() ?? "?"} " +
                                      "si toglie: le tessere non si contano)");
                else if (PatchBonusTokenManager(sw, update, embedded, report, levelYear))
                {
                    if (tokensNoHud) report.AppendLine("  HUD delle tessere: --tessere-senza-hud, le tessere prese non si vedono in alto (si contano)");
                    else PatchBonusTokenHud(sw, update, embedded, report);
                }
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

            // prima in un file temporaneo: se qualcosa va storto quello vecchio resta com'era
            update.Save(updatePath, temporaryPath: updatePath + ".tmp");
            report.AppendLine($"  scritto {updatePath} ({update.Files.Count} file)");
            return true;
        }

        /// <summary>
        /// Copia nel file principale del livello l'intro dei livelli di Crash 3 (IntroCutsceneSequencePlayer di
        /// L321_GoneTomorrow, dal dump Switch). Impostazioni: come l'editor per un livello nuovo di Crash 3 (LevelBuilder),
        /// oppure, con likeEditor, come l'editor quando passi a Crash 3 un livello che c'e' gia' (GameplayModeManager.
        /// AddC3IntroCutscene: valori di Gone Tomorrow, Animation delay 0.5); adjust applica le opzioni dell'intro (--intro-crash1/2,
        /// --intro-scostamento, --intro-evento, --intro-senza-portale, --intro-togli-portale, --intro-portale-piccolo) e dice cosa ha
        /// cambiato. Le entita' copiate (l'intro e quelle del file di Gone Tomorrow a cui rimanda) restano dove sono in Gone Tomorrow,
        /// come fa l'editor: lo script fa comparire Crash dove sta Crash, la posizione dell'intro non conta. Con moveToStart
        /// (--intro-alla-partenza, come la v54) passano dalla partenza di Gone Tomorrow a quella del livello, nella stessa posizione
        /// rispetto alla partenza. Una partenza o un mondo di Gone Tomorrow copiati perche' l'intro li nomina si tolgono e l'intro usa
        /// quelli del livello. Restituisce i file di Gone Tomorrow che servono all'intro (script, comportamenti, ...); in details le
        /// righe per il rapporto
        /// </summary>
        private static List<IgArchiveFile> AddC3Intro(IgzFile destIgz, string switchDir, IgArchive output, bool likeEditor, bool moveToStart,
                                                      Func<common_C3_IntroSequenceData, string?>? adjust, out string info, List<string> details)
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
            common_CutsceneSequencePlayerData? player = intro.GetComponent<common_CutsceneSequencePlayerData>();

            // Partenze prima della copia: quella di Gone Tomorrow piu' vicina all'intro e quella del livello
            CPlayerStartEntity? sourceStart = MainPlayerStart(sourceIgz, intro);
            CPlayerStartEntity? destStart = MainPlayerStart(destIgz, null);
            details.Add($"in Gone Tomorrow: {intro.ObjectName} {PlaceText(intro)}" +
                        (sourceStart != null ? $", rispetto alla partenza {sourceStart.ObjectName}: {RelativeText(sourceStart, intro)}" : ", partenza non trovata"));
            details.Add($"  {IntroValuesText(introData)}");
            details.Add($"  riferimenti: {IntroRefsText(introData)}");
            if (player != null) details.Add($"  sequenza: {CutscenePlayerText(player)}");

            string settings;
            if (likeEditor)
            {
                // Come GameplayModeManager.AddC3IntroCutscene: livello che c'e' gia' passato a Crash 3 nell'editor
                introData._Float_0x30 = 0.5f;
                if (player != null) player._CutsceneSequenceShotList.Reference = null;
                settings = "come l'editor quando passi a Crash 3 un livello che c'e' gia' (--intro-come-editor: valori di Gone Tomorrow, Animation delay 0.5)";
            }
            else
            {
                // Come LevelBuilder.CreateNewLevel per un livello nuovo di Crash 3
                introData._BehaviorEventCrashIntro = "Cutscene_Crash2_Portal_Exit_Victory";
                introData._Float_0x30 = 1.0f;
                introData._Float_0x34 = 1.0f;
                introData._Float_0x40 = 2.45f;
                introData._Float_0x4c = 300;
                CEntityHandleList? shots = sourceIgz.FindObject<CEntityHandleList>("IntroCutsceneSequencePlayer_entityData_componentData_CommonCutsceneSequencePlayer_CutsceneSequenceShotList001");
                if (shots != null) shots._data.Clear();
                else if (player != null) player._CutsceneSequenceShotList.Reference = null;
                settings = "come l'editor per un livello nuovo di Crash 3";
            }
            // opzioni dell'intro prima della copia: senza il portale non si copiano i suoi file
            string? adjusted = adjust?.Invoke(introData);
            if (adjusted != null) settings += "; poi " + adjusted;
            details.Add($"impostazioni: {settings}");
            details.Add($"  {IntroValuesText(introData)}");

            var before = new HashSet<igObject>(destIgz.Objects, ReferenceEqualityComparer.Instance);
            IgzFile.Clone(intro, source, output, sourceIgz, destIgz, out List<IgArchiveFile> dependencies);
            // il file principale di Gone Tomorrow no: porterebbe nel livello tutte le sue entita'
            dependencies.RemoveAll(d => d.Path.Equals(sourceFile.Path, StringComparison.OrdinalIgnoreCase));
            List<igObject> addedObjects = destIgz.Objects.Where(o => !before.Contains(o)).ToList();
            List<igEntity> added = addedObjects.OfType<igEntity>().ToList();
            details.Add($"copiati da Gone Tomorrow: {addedObjects.Count} oggetti, {added.Count} entita'");

            // Partenza o mondo di Gone Tomorrow copiati perche' l'intro li nomina: due partenze o due mondi nello stesso livello
            // non vanno, si usano quelli del livello
            string destNamespace = destIgz.GetName(false);
            var removed = new HashSet<igObject>(ReferenceEqualityComparer.Instance);
            foreach (igEntity copy in added.Where(e => e is CPlayerStartEntity || e is CWorldEntity).ToList())
            {
                igEntity? own = copy is CPlayerStartEntity ? destStart : before.OfType<CWorldEntity>().FirstOrDefault();
                if (own?.ObjectName == null || copy.ObjectName == null)
                {
                    details.Add($"  {EntityText(copy)} di Gone Tomorrow (l'intro la nomina): lasciata, nel livello non ce n'e' una da usare al suo posto");
                    continue;
                }
                int redirected = 0;
                foreach (igObject holder in addedObjects.Where(o => !ReferenceEquals(o, copy) && !removed.Contains(o)))
                {
                    foreach (NamedReference reference in holder.GetHandles(destIgz.GameVersion))
                    {
                        if (reference.objectName != copy.ObjectName || reference.namespaceName == null ||
                            !reference.namespaceName.Equals(destNamespace, StringComparison.OrdinalIgnoreCase)) continue;
                        reference.objectName = own.ObjectName;
                        redirected++;
                    }
                }
                foreach (igObject gone in destIgz.Remove(copy)) removed.Add(gone);
                details.Add($"  {EntityText(copy)} di Gone Tomorrow (l'intro la nomina): tolta, {redirected} riferimenti passati a {own.ObjectName} del livello");
            }
            added.RemoveAll(e => removed.Contains(e));

            // Entita' copiate: restano dove sono in Gone Tomorrow (come fa l'editor e come nelle v51-v53, in cui il bolt fermava lo
            // spostamento); con --intro-alla-partenza dalla partenza di Gone Tomorrow a quella del livello (v54: con il Level 7 il
            // caricamento si e' bloccato)
            if (!moveToStart)
            {
                details.Add("posizione: come in Gone Tomorrow (lo script fa comparire Crash dove sta Crash; --intro-alla-partenza per spostarla " +
                            "vicino alla partenza come nella v54)" +
                            (destStart != null ? $"; partenza del livello {destStart.ObjectName} {PlaceText(destStart)}" : ""));
                foreach (igEntity entity in added) details.Add($"  {EntityText(entity)}: {PlaceText(entity)}");
            }
            else if (sourceStart == null || destStart == null)
            {
                details.Add($"posizione: come in Gone Tomorrow (partenza {(sourceStart == null ? "di Gone Tomorrow" : "del livello")} non trovata nel file principale)");
                foreach (igEntity entity in added) details.Add($"  {EntityText(entity)}: {PlaceText(entity)}");
            }
            else
            {
                details.Add($"posizione: dalla partenza di Gone Tomorrow {sourceStart.ObjectName} ({PlaceText(sourceStart)}) a quella del livello " +
                            $"{destStart.ObjectName} ({PlaceText(destStart)}), stessa posizione rispetto alla partenza");
                float[] relative = MultiplyMatrices(RotationMatrix(destStart._transform?._parentSpaceRotation),
                                                    TransposeMatrix(RotationMatrix(sourceStart._transform?._parentSpaceRotation)));
                igVec3fMetaField sourcePosition = sourceStart._parentSpacePosition, destPosition = destStart._parentSpacePosition;
                foreach (igEntity entity in added)
                {
                    // Attaccata (bolt) a un'entita' del livello: la posizione e' rispetto a quella e resta. Fino alla v53 restava anche
                    // con un bolt senza entita' o con un'entita' che nel livello non c'e' (l'intro di Gone Tomorrow nel Level 7 a 4457
                    // dalla partenza): quella posizione e' nel mondo e si sposta come le altre
                    string boltNote = "";
                    if (entity._bolt != null)
                    {
                        NamedReference? boltTarget = entity._bolt._entityToBoltTo?.Reference;
                        if (boltTarget != null && boltTarget.IsNullOrEmpty()) boltTarget = null;
                        if (boltTarget != null && destIgz.FindObject(boltTarget) is igEntity boltEntity)
                        {
                            details.Add($"  {EntityText(entity)}: attaccata a {boltEntity.ObjectName} ({BoltText(entity._bolt)}), resta {PlaceText(entity)}");
                            continue;
                        }
                        boltNote = boltTarget == null
                            ? $" (bolt senza entita': {BoltText(entity._bolt)})"
                            : $" (attaccata a {boltTarget.namespaceName}::{boltTarget.objectName}, che nel file del livello non c'e': {BoltText(entity._bolt)})";
                    }
                    string old = PlaceText(entity);
                    igVec3fMetaField position = entity._parentSpacePosition;
                    float[] offset = MultiplyVector(relative, [position._x - sourcePosition._x, position._y - sourcePosition._y, position._z - sourcePosition._z]);
                    entity._parentSpacePosition = new igVec3fMetaField(destPosition._x + offset[0], destPosition._y + offset[1], destPosition._z + offset[2]);
                    if (entity._transform != null)
                        entity._transform._parentSpaceRotation = EulerFromMatrix(MultiplyMatrices(relative, RotationMatrix(entity._transform._parentSpaceRotation)));
                    details.Add($"  {EntityText(entity)}: {old} -> {PlaceText(entity)}{boltNote}");
                }
            }

            info = $"da {sourceFile.Path}, {dependencies.Count} file collegati";
            return dependencies;
        }

        // Portale piccolissimo (--intro-portale-piccolo; nella v54 il portale nascosto di --intro-crash1/2 e --intro-senza-portale):
        // "Animation size" (la scala con cui l'intro crea l'effetto; Makin' Waves usa 0.35 per un portale piu' piccolo) all'1%, con
        // Animation offset a 0 ai piedi di Crash
        private const float TinyPortalSize = 0.01f;

        // Portale lontano (--intro-senza-portale; nella v55 anche --intro-crash1/2): l'effetto resta com'e', come nelle intro che
        // funzionano, ma l'intro lo crea lontanissimo. "Animation offset" e' la distanza del portale davanti alla partenza di Crash (340
        // in Gone Tomorrow, -200 in Bone Yard, dietro): a -30000 sta 30 km dietro Crash. Le prove con il Level 7 e Crash 3: effetto tolto
        // (v53) caricamento finito e schermo nero; piccolissimo (v54) e lontano (v55) caricamento bloccato. Lo schermo nero c'e' anche
        // con --gioco crash1, senza intro: e' del livello. Per questo dalla v56 --intro-crash1/2 tolgono l'effetto, come nella v53
        private const float HiddenPortalOffset = -30000f;

        /// <summary>
        /// Opzioni dell'intro di Crash 3 (aggiunta o gia' nel livello): --intro-evento (animazione con cui compare Crash),
        /// --intro-togli-portale (niente effetto del portale), --intro-senza-portale (l'effetto resta com'e', lontanissimo, come
        /// nella v55), --intro-portale-piccolo (piccolissimo ai piedi di Crash, come nella v54), --intro-scostamento (Spawn offset) e
        /// --intro-crash1/--intro-crash2 (per fare un'intro come quella di Crash 1 o 2: animazione di comparsa di quel gioco se
        /// trovata, effetto del portale tolto se non c'e' un'altra opzione del portale, Spawn offset a 0). Dice cosa ha cambiato,
        /// null se niente
        /// </summary>
        private static string? ApplyIntroOptions(common_C3_IntroSequenceData data, Options options, string? gameEvent)
        {
            var changes = new List<string>();
            bool otherGame = options.IntroCrash1 || options.IntroCrash2;
            string gameOption = options.IntroCrash1 ? "--intro-crash1" : "--intro-crash2";
            string? newEvent = options.IntroEvent ?? (otherGame ? gameEvent : null);
            if (newEvent != null && newEvent != data._BehaviorEventCrashIntro)
            {
                changes.Add($"evento '{data._BehaviorEventCrashIntro}' -> '{newEvent}' ({(options.IntroEvent != null ? "--intro-evento" : gameOption)})");
                data._BehaviorEventCrashIntro = newEvent;
            }
            // --intro-crash1/2 senza un'altra opzione del portale: effetto tolto, come nella v53 (con il Level 7 e Crash 3 l'effetto
            // piccolissimo della v54 e quello lontano della v55 hanno bloccato il caricamento, senza effetto e' finito)
            if (options.IntroRemovePortal || (otherGame && !options.IntroNoPortal && !options.IntroTinyPortal))
            {
                if (data._Vfx_Effect.Reference != null)
                {
                    changes.Add($"effetto del portale {HandleText(data._Vfx_Effect)} tolto " +
                                $"({(options.IntroRemovePortal ? "--intro-togli-portale" : gameOption + "; --intro-senza-portale per lasciarlo, lontano")})");
                    data._Vfx_Effect.Reference = null;
                }
            }
            else if (options.IntroTinyPortal)
            {
                // come la v54: piccolissimo ai piedi di Crash (con il Level 7 il caricamento si e' bloccato)
                if (data._Vfx_Effect.Reference != null && data._Float_0x78 > TinyPortalSize)
                {
                    changes.Add($"portale piccolissimo: Animation size {FormatFloat(data._Float_0x78)} -> {FormatFloat(TinyPortalSize)} (--intro-portale-piccolo)");
                    data._Float_0x78 = TinyPortalSize;
                }
                if (data._Vfx_Effect.Reference != null && data._Float_0x48 != 0)
                {
                    changes.Add($"Animation offset {FormatFloat(data._Float_0x48)} -> 0 (--intro-portale-piccolo: ai piedi di Crash)");
                    data._Float_0x48 = 0;
                }
            }
            else if (options.IntroNoPortal && data._Vfx_Effect.Reference != null && data._Float_0x48 != HiddenPortalOffset)
            {
                // l'effetto resta com'e', come nelle intro che funzionano: lo crea lontanissimo dietro Crash (come la v55)
                changes.Add($"portale lontano: Animation offset {FormatFloat(data._Float_0x48)} -> {FormatFloat(HiddenPortalOffset)} " +
                            $"(l'effetto {HandleText(data._Vfx_Effect)} resta com'e', {FormatFloat(-HiddenPortalOffset)} unita' dietro Crash: non si vede) " +
                            "(--intro-senza-portale)");
                data._Float_0x48 = HiddenPortalOffset;
            }
            float? spawnOffset = options.IntroSpawnOffset ?? (otherGame ? 0f : (float?)null);
            if (spawnOffset != null && spawnOffset.Value != data._Float_0x4c)
            {
                changes.Add($"Spawn offset {FormatFloat(data._Float_0x4c)} -> {FormatFloat(spawnOffset.Value)} " +
                            $"({(options.IntroSpawnOffset != null ? "--intro-scostamento" : gameOption)})");
                data._Float_0x4c = spawnOffset.Value;
            }
            return changes.Count == 0 ? null : string.Join(", ", changes);
        }

        /// <summary>
        /// Dati delle animazioni di comparsa di Crash (Crash_RespawnAnimationManagerData in Crash_CharacterData.igz del dump), con il
        /// percorso del file; null se mancano, con il motivo
        /// </summary>
        private static (Crash_RespawnAnimationManagerData? data, string path, string? problem) RespawnAnimationData(ArchiveIndex sw)
        {
            IgArchiveFile? file = (sw.ByArchive.GetValueOrDefault("crash") ?? [])
                                      .FirstOrDefault(f => NamespaceUtils.GetFileName(f.Path).Equals("Crash_CharacterData.igz", StringComparison.OrdinalIgnoreCase))
                                  ?? sw.ByPath.Where(e => e.Key.EndsWith("/crash_characterdata.igz")).Select(e => e.Value).FirstOrDefault();
            if (file == null) return (null, "", "Crash_CharacterData.igz non trovato nel dump");
            Crash_RespawnAnimationManagerData? data = file.ToIgzFile().Objects.OfType<Crash_RespawnAnimationManagerData>().FirstOrDefault();
            if (data == null) return (null, file.Path, $"nessun Crash_RespawnAnimationManagerData in {file.Path}");
            return (data, file.Path, null);
        }

        /// <summary>
        /// Animazione di comparsa di Crash 1 per --intro-crash1: nello script delle animazioni di comparsa
        /// (Crash_RespawnAnimationManager) per Crash 1 c'e' sempre lo stesso testo, il terzo dei dati (_String_0x38, 'SpawnIn');
        /// se e' vuoto 'SpawnIn', il nome che usano anche Crash 2 e 3 per ricomparire dopo una vita persa
        /// </summary>
        private static (string? eventName, string reason) C1RespawnEvent(ArchiveIndex sw)
        {
            var (data, path, problem) = RespawnAnimationData(sw);
            if (data == null) return ("SpawnIn", $"animazione di Crash 1 'SpawnIn' ({problem}: il nome che usano i dati di Crash)");
            if (!string.IsNullOrWhiteSpace(data._String_0x38))
                return (data._String_0x38, $"animazione di Crash 1 '{data._String_0x38}' dai dati di Crash ({path}, il testo che lo script delle " +
                                           "animazioni di comparsa usa per Crash 1)");
            return ("SpawnIn", $"animazione di Crash 1 'SpawnIn' (nei dati di Crash, {path}, il testo di Crash 1 e' vuoto)");
        }

        /// <summary>
        /// Animazione di comparsa di Crash 2 per --intro-crash2: tra i testi dei dati delle animazioni di comparsa di Crash
        /// (Crash_RespawnAnimationManagerData in Crash_CharacterData.igz) quello che nomina Crash 2 ("Crash2", "C2_", "_C2"...);
        /// se sono piu' d'uno, quello che parla di comparsa (spawn, warp, intro, start). Null se non ce n'e', con il motivo
        /// </summary>
        private static (string? eventName, string reason) C2RespawnEvent(ArchiveIndex sw)
        {
            var c2 = new System.Text.RegularExpressions.Regex(@"crash_?2|(^|[^a-z0-9])c2([^0-9]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var appear = new System.Text.RegularExpressions.Regex("spawn|warp|intro|start|appear", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var (data, path, problem) = RespawnAnimationData(sw);
            if (data == null) return (null, $"{problem}: resta l'animazione dell'intro");
            var texts = new List<string>();
            foreach (CachedFieldAttr field in data.GetFields(GameVersion.NSX))
            {
                if (field.GetValue(data) is string text && !string.IsNullOrWhiteSpace(text) && c2.IsMatch(text) && !texts.Contains(text)) texts.Add(text);
            }
            if (texts.Count == 0) return (null, $"nei dati delle animazioni di comparsa di Crash ({path}) nessun testo nomina Crash 2: resta l'animazione dell'intro");
            List<string> preferred = texts.Where(t => appear.IsMatch(t)).ToList();
            string chosen = preferred.Count > 0 ? preferred[0] : texts[0];
            return (chosen, $"animazione di Crash 2 '{chosen}' dai dati di Crash ({path}" +
                            (texts.Count > 1 ? $"; candidate: {string.Join(", ", texts.Select(t => "'" + t + "'"))}" : "") + ")");
        }

        /// <summary>
        /// Campi di un oggetto come testo (nome=valore) per il rapporto: testi, numeri, si'/no, enum e riferimenti
        /// </summary>
        private static string FieldsText(igObjectBase obj, GameVersion version)
        {
            var parts = new List<string>();
            foreach (CachedFieldAttr field in obj.GetFields(version))
            {
                string name = field.GetName();
                if (name is "_meta" or "_bitfield" or "_dynamicFieldMemory" || name.StartsWith("__")) continue;
                object? value;
                try
                {
                    value = field.GetValue(obj);
                }
                catch
                {
                    continue;
                }
                string? text = value switch
                {
                    null => null,
                    string str => $"'{str}'",
                    igHandleMetaField handle => handle.Reference == null ? null : HandleText(handle),
                    float number => FormatFloat(number),
                    bool flag => flag ? "si'" : "no",
                    Enum choice => choice.ToString(),
                    int or uint or short or ushort or byte or sbyte or long or ulong => value.ToString(),
                    _ => null,
                };
                if (text != null) parts.Add($"{name.TrimStart('_')}={text}");
            }
            return parts.Count == 0 ? "-" : string.Join(", ", parts);
        }

        /// <summary>
        /// Partenza del livello in un file: le CPlayerStartEntity con _zoneStart (se nessuna lo ha, tutte); se sono piu' d'una,
        /// la piu' vicina a near, altrimenti la prima
        /// </summary>
        private static CPlayerStartEntity? MainPlayerStart(IgzFile igz, igEntity? near)
        {
            List<CPlayerStartEntity> starts = igz.Objects.OfType<CPlayerStartEntity>().ToList();
            List<CPlayerStartEntity> zoneStarts = starts.Where(s => s._zoneStart).ToList();
            if (zoneStarts.Count > 0) starts = zoneStarts;
            if (starts.Count <= 1 || near == null) return starts.FirstOrDefault();
            return starts.OrderBy(s => EntityDistance(s, near)).First();
        }

        private static float EntityDistance(igEntity a, igEntity b)
        {
            float dx = a._parentSpacePosition._x - b._parentSpacePosition._x;
            float dy = a._parentSpacePosition._y - b._parentSpacePosition._y;
            float dz = a._parentSpacePosition._z - b._parentSpacePosition._z;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Rotazione di un'entita' (_parentSpaceRotation: radianti, ordine ZYX come nell'editor) come matrice 3x3 per righe
        /// </summary>
        private static float[] RotationMatrix(igVec3fMetaField? euler)
        {
            float x = euler?._x ?? 0f, y = euler?._y ?? 0f, z = euler?._z ?? 0f;
            float cx = MathF.Cos(x), sx = MathF.Sin(x), cy = MathF.Cos(y), sy = MathF.Sin(y), cz = MathF.Cos(z), sz = MathF.Sin(z);
            // Rz * Ry * Rx
            return
            [
                cz * cy, cz * sy * sx - sz * cx, cz * sy * cx + sz * sx,
                sz * cy, sz * sy * sx + cz * cx, sz * sy * cx - cz * sx,
                -sy,     cy * sx,                cy * cx,
            ];
        }

        /// <summary>
        /// Angoli (radianti, ordine ZYX come nell'editor) di una matrice di rotazione 3x3 per righe
        /// </summary>
        private static igVec3fMetaField EulerFromMatrix(float[] m)
        {
            float y = MathF.Asin(-Math.Clamp(m[6], -1f, 1f));
            if (MathF.Abs(m[6]) < 0.9999999f) return new igVec3fMetaField(MathF.Atan2(m[7], m[8]), y, MathF.Atan2(m[3], m[0]));
            return new igVec3fMetaField(0f, y, MathF.Atan2(-m[1], m[4]));
        }

        private static float[] MultiplyMatrices(float[] a, float[] b)
        {
            var result = new float[9];
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                    result[row * 3 + column] = a[row * 3] * b[column] + a[row * 3 + 1] * b[3 + column] + a[row * 3 + 2] * b[6 + column];
            return result;
        }

        private static float[] TransposeMatrix(float[] m) => [m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8]];

        private static float[] MultiplyVector(float[] m, float[] v) =>
        [
            m[0] * v[0] + m[1] * v[1] + m[2] * v[2],
            m[3] * v[0] + m[4] * v[1] + m[5] * v[2],
            m[6] * v[0] + m[7] * v[1] + m[8] * v[2],
        ];

        private static string EntityText(igEntity entity) => $"{entity.ObjectName ?? "?"} [{entity.GetType().Name}]";

        /// <summary>
        /// Come un'entita' e' attaccata a un'altra (bolt) per il rapporto: tipo, entita', punto e oggetto del bolt
        /// </summary>
        private static string BoltText(igEntityBolt bolt)
        {
            var parts = new List<string> { bolt.GetType().Name, "entita' " + HandleText(bolt._entityToBoltTo) };
            if (bolt is CEntityBolt entityBolt)
            {
                if (entityBolt._boltSelect != null)
                    parts.Add($"punto '{entityBolt._boltSelect._boltName}' scostamento {FormatFloat(entityBolt._boltSelect._offset._x)} " +
                              $"{FormatFloat(entityBolt._boltSelect._offset._y)} {FormatFloat(entityBolt._boltSelect._offset._z)}");
                if (entityBolt._objectBolt != null)
                    parts.Add($"oggetto {entityBolt._objectBolt._type}, di {HandleText(entityBolt._objectBolt._owner)}");
            }
            return string.Join(", ", parts);
        }

        /// <summary>
        /// Posizione e rotazione (in gradi) di un'entita' per il rapporto
        /// </summary>
        private static string PlaceText(igEntity entity)
        {
            igVec3fMetaField p = entity._parentSpacePosition;
            string text = $"posizione {FormatFloat(p._x)} {FormatFloat(p._y)} {FormatFloat(p._z)}";
            igVec3fMetaField? r = entity._transform?._parentSpaceRotation;
            if (r != null && (r._x != 0 || r._y != 0 || r._z != 0))
                text += $", rotazione {FormatFloat(r._x * 180f / MathF.PI)} {FormatFloat(r._y * 180f / MathF.PI)} {FormatFloat(r._z * 180f / MathF.PI)} gradi";
            return text;
        }

        /// <summary>
        /// Dove sta un'entita' rispetto a una partenza: distanza, posizione nel sistema della partenza (assi della partenza
        /// ruotata), rotazione rispetto alla partenza (gradi)
        /// </summary>
        private static string RelativeText(igEntity start, igEntity entity)
        {
            float[] startRotation = TransposeMatrix(RotationMatrix(start._transform?._parentSpaceRotation));
            igVec3fMetaField a = start._parentSpacePosition, b = entity._parentSpacePosition;
            float[] local = MultiplyVector(startRotation, [b._x - a._x, b._y - a._y, b._z - a._z]);
            igVec3fMetaField rotation = EulerFromMatrix(MultiplyMatrices(startRotation, RotationMatrix(entity._transform?._parentSpaceRotation)));
            return $"distanza {FormatFloat(EntityDistance(start, entity))}, nel sistema della partenza {FormatFloat(local[0])} {FormatFloat(local[1])} {FormatFloat(local[2])}, " +
                   $"rotazione {FormatFloat(rotation._x * 180f / MathF.PI)} {FormatFloat(rotation._y * 180f / MathF.PI)} {FormatFloat(rotation._z * 180f / MathF.PI)} gradi";
        }

        /// <summary>
        /// Valori dell'intro di Crash 3 (common_C3_IntroSequenceData) con i nomi dell'editor
        /// </summary>
        private static string IntroValuesText(common_C3_IntroSequenceData d) =>
            $"evento '{d._BehaviorEventCrashIntro}', Animation delay {FormatFloat(d._Float_0x30)}, Crash spawn delay {FormatFloat(d._Float_0x34)}, " +
            $"Animation duration {FormatFloat(d._Float_0x40)}, Animation offset {FormatFloat(d._Float_0x48)}, Spawn offset {FormatFloat(d._Float_0x4c)}, " +
            $"Animation size {FormatFloat(d._Float_0x78)}, Bool {(d._Bool ? "si'" : "no")}, Float_0x58 {FormatFloat(d._Float_0x58)}, Float_0x5c {FormatFloat(d._Float_0x5c)}";

        private static string IntroRefsText(common_C3_IntroSequenceData d) =>
            $"effetto {HandleText(d._Vfx_Effect)}, Entity_0x50 {HandleText(d._Entity_0x50)}, camera {HandleText(d._Camera_Base)}, " +
            $"variabile {HandleText(d._Game_Int_Variable)}, Entity_0x70 {HandleText(d._Entity_0x70)}";

        private static string CutscenePlayerText(common_CutsceneSequencePlayerData p) =>
            $"parte all'avvio {(p._IsPlayOnStart ? "si'" : "no")}, checkpoint di partenza {HandleText(p._Starting_Checkpoint)} " +
            $"(escluso {(p._ExcludeStartingCheckpoint ? "si'" : "no")}), inquadrature {HandleText(p._CutsceneSequenceShotList)}, " +
            $"preset {HandleText(p._CutscenePreset)}, dissolvenza {HandleText(p._FadeInPreset)}, salto {(p._IsSkipFadeOn ? "si'" : "no")} " +
            $"{HandleText(p._SkipFadePreset)}, uscita della camera {FormatFloat(p._OnCutsceneDoneCameraBlendOutTime)}, suono {HandleText(p._Sound)}, " +
            $"id {HandleText(p._DesignCutsceneIdentifier)}";

        /// <summary>
        /// Riporta nella zone info presa da un originale la modalita' impostata nell'editor: personaggio (Coco), opzioni
        /// speciali di _build ({boulder}, {hog}, {jetski}, ...), veicolo (moto d'acqua, aereo). Per le opzioni che lo
        /// richiedono crea, come l'editor quando premi Play, i dati del personaggio (Crash/Coco_CharacterData.igz,
        /// dal dump Switch) che collegano la modalita' alla zone info del livello, e li mette nella registrazione.
        /// </summary>
        private static void ApplyEditorMode(CZoneInfo zoneInfo, CZoneInfo pcZone, string levelId, ArchiveIndex sw, IgArchive update,
                                            List<(string path, byte[] data)> embedded, StringBuilder report)
        {
            List<string> options = GameplayModeManager.GetSpecialZoneInfoOptions(pcZone._build);
            static string? Special(string? name) => string.IsNullOrEmpty(name) || name == "Crash" ? null : name;
            string? character = Special(pcZone._overrideCharacter);

            // Personaggio: quello scelto nell'editor anche quando l'originale ne usa un altro (un livello ..._Custom
            // di un livello di Coco passato a Crash nell'editor resta di Crash)
            string? templateCharacter = Special(zoneInfo._overrideCharacter);
            if (character != templateCharacter)
            {
                report.AppendLine($"  personaggio dell'editor: {character ?? "Crash"} (l'originale: {templateCharacter ?? "Crash"})");
                zoneInfo._overrideCharacter = character;
            }
            // Veicolo: come il Play dell'editor, solo dalle opzioni speciali ({jetski}, {plane}); senza, nessuno
            string? vehicle = options.Contains("jetski") ? "CocoJetski" : options.Contains("plane") ? "CrashCocoPlane" : null;
            if (vehicle != zoneInfo._zoneVehicle)
            {
                report.AppendLine($"  veicolo: '{zoneInfo._zoneVehicle}' -> '{vehicle}' (come l'editor)");
                zoneInfo._zoneVehicle = vehicle;
            }
            if (options.Count > 0)
            {
                zoneInfo._build = GameplayModeManager.UpdateSpecialZoneInfoOptions(zoneInfo._build, options);
                report.AppendLine($"  modalita' dell'editor: {string.Join(", ", options)} (build '{zoneInfo._build}', veicolo '{zoneInfo._zoneVehicle}')");
            }

            string[] characterOptions = ["hog", "bear", "tiger", "jetpack", "dig", "boulder"];
            List<string> needData = options.Where(o => characterOptions.Contains(o)).ToList();
            if (options.Contains("hub")) report.AppendLine("  ATTENZIONE: l'opzione hub non viene convertita (servono i dati di sistema del gioco)");
            if (needData.Count == 0) return;

            // Come l'editor: <personaggio>_CharacterData.igz dall'archivio del personaggio (crash.pak, coco.pak), se c'e'
            string characterName = character ?? "Crash";
            string name = characterName + "_CharacterData.igz";
            IgArchiveFile? source = (sw.ByArchive.GetValueOrDefault(characterName.ToLowerInvariant()) ?? [])
                                        .FirstOrDefault(f => NamespaceUtils.GetFileName(f.Path).Equals(name, StringComparison.OrdinalIgnoreCase))
                                    ?? sw.ByPath.Where(e => NamespaceUtils.GetFileName(e.Key).Equals(name, StringComparison.OrdinalIgnoreCase))
                                                .Select(e => e.Value).FirstOrDefault();
            if (source == null)
            {
                report.AppendLine($"  ERRORE: {name} non trovato nel dump: la modalita' {string.Join(", ", needData)} non si attiva");
                return;
            }
            try
            {
                IgzFile igz = source.ToIgzFile();
                string zoneInfoName = levelId.Substring(levelId.LastIndexOf('/') + 1) + "_zoneinfo";
                var done = new List<string>();
                foreach (string option in needData)
                {
                    NamedReference? reference = option switch
                    {
                        "hog" => igz.FindObject<Crash_Ride_HogData>()?._Zone_Info_0x78.Reference,
                        "bear" => igz.FindObject<Crash_Ride_BearData>()?._Zone_Info_0xb0.Reference,
                        "tiger" => igz.FindObject<Crash_Ride_TigerData>()?._Zone_Info_0x80.Reference,
                        "jetpack" => igz.FindObjects<Crash_JetPackData>().LastOrDefault()?._Zone_Info_0x138.Reference,
                        "dig" => igz.FindObject<Crash_DiggingData>()?._Zone_Info_0x80.Reference,
                        "boulder" => igz.FindObject<Crash_BoulderData>()?._Zone_Info_0x88.Reference,
                        _ => null
                    };
                    if (reference == null)
                    {
                        report.AppendLine($"  ATTENZIONE: {option}: dati non trovati in {source.Path}");
                        continue;
                    }
                    reference.namespaceName = zoneInfoName;
                    done.Add(option);
                }
                if (done.Count == 0) return;
                igz.GameVersion = GameVersion.NSX;
                byte[] data = igz.Save();
                ReplaceFile(update, source.Path, data);
                embedded.Add((source.Path, data));
                report.AppendLine($"  dati del personaggio per {string.Join(", ", done)}: {source.Path} (collegati a {zoneInfoName})");
            }
            catch (Exception e)
            {
                report.AppendLine($"  ERRORE dati del personaggio ({source.Path}): {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// File di un altro livello copiato dall'editor: maps/<gioco>/<livello>/... fuori dalle cartelle del livello
        /// </summary>
        private static bool IsOtherLevelMapFile(string path, List<string> levelDirs)
        {
            // senza il pacchetto del livello non si sa quali sono i file del livello: nessuno conta come di un altro livello
            if (levelDirs.Count == 0) return false;
            string[] parts = path.Split('/');
            return parts.Length >= 4 && parts[0].Equals("maps", StringComparison.OrdinalIgnoreCase) &&
                   !levelDirs.Any(d => d.Equals(parts[1] + "/" + parts[2], StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Il file contiene l'intro dei livelli di Crash 3 (entita' con common_C3_IntroSequenceData)
        /// </summary>
        private static bool HasC3Intro(IgArchiveFile file)
        {
            try
            {
                return file.ToIgzFile().Objects.OfType<igEntity>().Any(e => e.GetComponent<common_C3_IntroSequenceData>() != null);
            }
            catch
            {
                return false;
            }
        }

        private static readonly HashSet<string> _decorationComponents = ["CModelComponentData", "CStaticComponentData", "CStaticCollisionComponentData"];

        /// <summary>
        /// Decorazione: entita' semplice con solo modello, componente statico e collisione statica (rocce, muri, piante)
        /// </summary>
        private static bool IsStaticDecoration(igEntity entity) =>
            entity.GetType() == typeof(igEntity) && entity.GetComponents().All(c => _decorationComponents.Contains(c.GetType().Name));

        /// <summary>
        /// File (namespace, in minuscolo) usati da un'entita' e dai suoi componenti: modelli, script, prefab...
        /// Non entra nelle altre entita' collegate e non conta il file stesso
        /// </summary>
        private static HashSet<string> EntityReferences(igEntity entity, IgzFile igz)
        {
            GameVersion version = igz.GameVersion;
            string own = igz.GetName(false);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<igObject> { entity };
            var queue = new Queue<igObject>();
            queue.Enqueue(entity);
            while (queue.Count > 0)
            {
                igObject current = queue.Dequeue();
                try
                {
                    foreach (NamedReference handle in ObjectReferences(current, version))
                    {
                        string ns = handle.namespaceName;
                        if (string.IsNullOrEmpty(ns) || ns.Equals(own, StringComparison.OrdinalIgnoreCase)) continue;
                        result.Add(ns.ToLowerInvariant());
                    }
                    // modelli e altri file indicati con il loro percorso (CModelComponentData._fileName, ...)
                    foreach (string text in current.GetStrings(version))
                    {
                        if (text.Contains('/') && (text.EndsWith(".igz", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
                            result.Add(NamespaceUtils.GetFileName(text, false).ToLowerInvariant());
                    }
                    foreach (igObject child in current.GetChildren(version))
                        if (child is not igEntity && visited.Add(child)) queue.Enqueue(child);
                }
                catch
                {
                    // un oggetto che non si esplora non cambia il resto
                }
            }
            return result;
        }

        /// <summary>
        /// Nemico dei livelli con la moto d'acqua (Tell No Tales, Makin' Waves...): personaggio (o oggetto che fa comparire)
        /// con un comportamento (riferimenti ai tipi del motore, metaobject) e "jetski_enemy" nel nome. I cannoni delle
        /// barche (Jetski_Hazard_Cannon) non lo sono: in Tropical Escape non bloccano il gioco
        /// </summary>
        private static bool IsJetskiEnemy(igEntity entity, HashSet<string> references) =>
            (entity.ObjectName ?? "").Contains("jetski_enemy", StringComparison.OrdinalIgnoreCase) &&
            references.Any(r => r.StartsWith("meta"));

        /// <summary>
        /// --togli-entita "[file:]testo": il file (testo nel percorso) e il testo nel nome dell'entita', nel tipo, nei
        /// componenti, nel modello (dalla v58) o nei file che usa. Testo vuoto o * = tutte le entita' del file. "#k/n" (dalla
        /// v58): la parte k di n delle entita' del file, nell'ordine del file ("#k-l/n" dalla parte k alla l), per trovare
        /// un'entita' dimezzando senza conoscerne il nome; index e count sono la posizione dell'entita' e quante sono
        /// </summary>
        private static bool EntityFilterMatches(string filter, string path, igEntity entity, HashSet<string> references, int index = -1, int count = 0)
        {
            string file = "", text = filter.Trim();
            int colon = text.IndexOf(':');
            if (colon >= 0)
            {
                file = text.Substring(0, colon).Trim();
                text = text.Substring(colon + 1).Trim();
            }
            if (file.Length > 0 && !path.Contains(file, StringComparison.OrdinalIgnoreCase)) return false;
            if (text.Length == 0 || text == "*") return true;
            if (TryParseEntityPart(text, out int firstPart, out int lastPart, out int parts))
            {
                if (index < 0 || count <= 0) return false;
                int part = (int)((long)index * parts / count) + 1;
                return part >= firstPart && part <= lastPart;
            }
            if ((entity.ObjectName ?? "").Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            if (entity.GetType().Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            if (entity.GetComponents().Any(c => c.GetType().Name.Contains(text, StringComparison.OrdinalIgnoreCase))) return true;
            if (EntityModelNames(entity).Any(m => m.Contains(text, StringComparison.OrdinalIgnoreCase))) return true;
            return references.Any(r => r.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// "#k/n" o "#k-l/n" di --togli-entita: parti da k a l (da 1) di n. False se il testo non e' cosi'
        /// </summary>
        private static bool TryParseEntityPart(string text, out int first, out int last, out int parts)
        {
            first = last = parts = 0;
            if (!text.StartsWith('#')) return false;
            string[] halves = text.Substring(1).Split('/');
            if (halves.Length != 2 || !int.TryParse(halves[1].Trim(), out parts) || parts < 1 || parts > 100000) return false;
            string[] range = halves[0].Split('-');
            if (range.Length > 2 || !int.TryParse(range[0].Trim(), out first)) return false;
            last = first;
            if (range.Length == 2 && !int.TryParse(range[1].Trim(), out last)) return false;
            return first >= 1 && last >= first && last <= parts;
        }

        /// <summary>
        /// Modelli di un'entita' (CModelComponentData._fileName, per esempio "models:\Levels\Crash1\jungle\C1_Rock04.igb"), senza
        /// cartella ne' estensione
        /// </summary>
        private static List<string> EntityModelNames(igEntity entity) =>
            entity.GetComponents().OfType<CModelComponentData>()
                  .Select(model => model._fileName)
                  .Where(name => !string.IsNullOrWhiteSpace(name))
                  .Select(name => Path.GetFileNameWithoutExtension(name!.Replace('\\', '/')))
                  .Where(name => name.Length > 0)
                  .ToList();

        /// <summary>
        /// Rapporto: contenuto di un file di un altro livello (entita' raggruppate per tipo e componenti, quelle che usano
        /// file che il gioco Switch non ha, altri oggetti) e confronto con l'originale Switch dello stesso file
        /// </summary>
        private static void DescribeOtherLevelFile(StringBuilder sb, string path, IgzFile igz, IgArchiveFile? switchOriginal, bool inPackage,
                                                   Func<igEntity, HashSet<string>> refs, Func<igEntity, List<string>> missingRefs,
                                                   string? modeInfo = null)
        {
            try
            {
                List<igEntity> entities = igz.Objects.OfType<igEntity>().ToList();
                string originalInfo = "non c'e' nel gioco Switch";
                HashSet<string>? originalNames = null;
                if (switchOriginal != null)
                {
                    try
                    {
                        IgzFile original = switchOriginal.ToIgzFile();
                        List<igEntity> originalEntities = original.Objects.OfType<igEntity>().ToList();
                        originalNames = originalEntities.Select(e => e.ObjectName ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
                        originalInfo = $"originale Switch: {originalEntities.Count} entita', {original.Objects.Count} oggetti";
                    }
                    catch (Exception e)
                    {
                        originalInfo = $"originale Switch non leggibile: {e.Message}";
                    }
                }
                sb.AppendLine($"{path}: {entities.Count} entita', {igz.Objects.Count} oggetti ({originalInfo})");
                sb.AppendLine(inPackage
                    ? "    nel pacchetto: il gioco carica tutte le sue entita'"
                    : "    fuori dal pacchetto: il gioco usa solo gli oggetti a cui il livello fa riferimento (--solo-usati non lo tocca)");
                if (modeInfo != null) sb.AppendLine(modeInfo);

                if (originalNames != null)
                {
                    var names = entities.Select(e => e.ObjectName ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
                    List<string> added = entities.Select(e => e.ObjectName ?? "?").Where(n => !originalNames.Contains(n)).ToList();
                    int missing = originalNames.Count(n => !names.Contains(n));
                    sb.AppendLine(added.Count == 0 && missing == 0
                        ? "    stesse entita' dell'originale Switch"
                        : $"    rispetto all'originale Switch: {added.Count} entita' in piu'{(added.Count > 0 ? $" (es. {string.Join(", ", added.Take(5))})" : "")}, {missing} in meno");
                }

                var groups = entities
                    .GroupBy(e => $"[{e.GetType().Name}] {string.Join(", ", e.GetComponents().Select(c => c.GetType().Name).OrderBy(n => n))}")
                    .OrderByDescending(g => g.Count())
                    .ToList();
                foreach (var group in groups.Take(60))
                {
                    string examples = string.Join(", ", group.Take(3).Select(e => e.ObjectName ?? "?"));
                    List<string> used = group.SelectMany(e => refs(e)).Where(n => !n.All(char.IsDigit))
                        .GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(5).ToList();
                    sb.AppendLine($"    {group.Count(),4}x {group.Key}  es. {examples}{(used.Count > 0 ? "  usa: " + string.Join(", ", used) : "")}");
                }
                if (groups.Count > 60) sb.AppendLine($"    ... e altri {groups.Count - 60} gruppi");

                // Modelli delle entita' (CModelComponentData._fileName) e materiali sostituiti (CStaticComponentData._materialOverrides,
                // originale -> sostituto): per togliere un solo tipo di decorazione (--togli-entita <file>:<modello>) e per capire
                // quale modello o materiale da' problemi sulla Switch (dalla v58)
                var models = entities.SelectMany(e => EntityModelNames(e).Distinct(StringComparer.OrdinalIgnoreCase))
                    .GroupBy(m => m, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (models.Count > 0)
                    sb.AppendLine($"    modelli ({models.Count}): {string.Join(", ", models.Take(30).Select(g => $"{g.Key} {g.Count()}"))}" +
                                  (models.Count > 30 ? $" e altri {models.Count - 30}" : ""));
                var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (igEntity entity in entities)
                {
                    foreach (CStaticComponentData staticData in entity.GetComponents().OfType<CStaticComponentData>())
                    {
                        CFxMaterialRedirectTable? table = staticData._materialOverrides;
                        if (table == null) continue;
                        if (table._default.Reference != null)
                        {
                            string defaultKey = $"tutti -> {RefText(table._default.Reference)}";
                            overrides[defaultKey] = overrides.GetValueOrDefault(defaultKey) + 1;
                        }
                        int pairs = Math.Max(table._keys?._data.Count ?? 0, table._values?._data.Count ?? 0);
                        for (int i = 0; i < pairs; i++)
                        {
                            NamedReference? from = table._keys != null && i < table._keys._data.Count ? table._keys._data[i]?.Reference : null;
                            NamedReference? to = table._values != null && i < table._values._data.Count ? table._values._data[i]?.Reference : null;
                            string pairKey = $"{RefText(from)} -> {RefText(to)}";
                            overrides[pairKey] = overrides.GetValueOrDefault(pairKey) + 1;
                        }
                    }
                }
                if (overrides.Count > 0)
                    sb.AppendLine($"    materiali sostituiti ({overrides.Count}): " +
                                  string.Join(", ", overrides.OrderByDescending(o => o.Value).ThenBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
                                                            .Take(30).Select(o => $"{o.Key} ({o.Value})")) +
                                  (overrides.Count > 30 ? $" e altri {overrides.Count - 30}" : ""));

                int shown = 0, total = 0;
                foreach (igEntity entity in entities)
                {
                    List<string> missingNames = missingRefs(entity);
                    if (missingNames.Count == 0) continue;
                    total++;
                    if (shown++ < 30) sb.AppendLine($"    MANCA {string.Join(", ", missingNames)}: {entity.ObjectName ?? "?"} [{entity.GetType().Name}]");
                }
                if (total > 30) sb.AppendLine($"    ... e altre {total - 30} entita' che usano file che mancano");

                var otherTypes = igz.Objects.Where(o => o is not igEntity && o is not igComponentData)
                    .GroupBy(o => o.GetType().Name).OrderByDescending(g => g.Count()).Take(12)
                    .Select(g => $"{g.Key} {g.Count()}").ToList();
                if (otherTypes.Count > 0) sb.AppendLine($"    altri oggetti: {string.Join(", ", otherTypes)}");
            }
            catch (Exception e)
            {
                sb.AppendLine($"{path}: contenuto non leggibile ({e.GetType().Name}: {e.Message})");
            }
        }

        /// <summary>
        /// Rapporto: file che i file convertiti dal PC usano (dipendenze TDEP, che il gioco carica prima del file, e
        /// riferimenti per nome a oggetti di altri file) e che nell'archivio non ci sono, ma sulla Switch sono solo negli
        /// archivi di altri livelli: il gioco carica gli archivi comuni (crash1, common...) ma non quelli degli altri
        /// livelli, quindi non li trova e il caricamento puo' bloccarsi. Quelli che non sono da nessuna parte sono
        /// nella sezione dei file che il gioco Switch non ha
        /// </summary>
        private static void AppendMissingDependencies(StringBuilder report, IgArchive output, List<IgArchiveFile> fromSwitch, ArchiveIndex sw)
        {
            static string Normalize(string value)
            {
                string p = value.Replace('\\', '/').ToLowerInvariant();
                int colon = p.IndexOf(':');
                if (colon >= 0) p = p.Substring(colon + 1);
                return p.TrimStart('/');
            }
            static IEnumerable<string> Names(string path) => NameAliases(NamespaceUtils.GetFileName(path, false).ToLowerInvariant());

            var outputNames = new HashSet<string>(output.Files.SelectMany(f => Names(f.Path)));
            var originals = new HashSet<string>(fromSwitch.Select(f => f.Path.ToLowerInvariant()));
            var levelArchives = new HashSet<string>(LevelNames(sw.ByPath.Keys).Select(l => l.ToLowerInvariant()));
            var archivesByName = new Dictionary<string, SortedSet<string>>();
            foreach (var (file, archive) in sw.ArchiveOf)
            {
                foreach (string name in Names(file.Path))
                {
                    if (!archivesByName.TryGetValue(name, out SortedSet<string>? set)) archivesByName[name] = set = new SortedSet<string>();
                    set.Add(archive);
                }
            }

            // nome (file o namespace) -> (tipo di riferimento, file che lo usano)
            var users = new SortedDictionary<string, (string kind, SortedSet<string> files)>();
            void Use(string name, string kind, IgArchiveFile user)
            {
                if (name.Length == 0 || name.All(char.IsDigit) || name.StartsWith("meta") || outputNames.Contains(name)) return;
                if (!users.TryGetValue(name, out var entry)) users[name] = entry = (kind, new SortedSet<string>());
                entry.files.Add(NamespaceUtils.GetFileName(user.Path));
            }
            foreach (IgArchiveFile file in output.Files)
            {
                string lower = file.Path.ToLowerInvariant();
                if (!file.IsIGZ() || originals.Contains(lower) || lower.StartsWith("update/")) continue;
                FixupCollection? fixups = RawFixups(file);
                if (fixups == null) continue;
                foreach (TDEP_Fixup.TDEP_Item item in fixups.TDEP)
                    if (!string.IsNullOrEmpty(item.path)) Use(Names(Normalize(item.path)).First(), "dipendenza", file);
                foreach (NamedReference reference in fixups.handleReferences.Concat(fixups.objectReferences).Concat(fixups.exidReferences))
                    if (!string.IsNullOrEmpty(reference.namespaceName)) Use(reference.namespaceName.ToLowerInvariant(), "riferimento", file);
            }

            int common = 0, nowhere = 0;
            var problems = new List<string>();
            foreach (var (name, (kind, files)) in users)
            {
                SortedSet<string>? archives = archivesByName.GetValueOrDefault(name);
                if (archives == null || archives.Count == 0) nowhere++;
                else if (archives.All(levelArchives.Contains))
                {
                    string usedBy = string.Join(", ", files.Take(3)) + (files.Count > 3 ? $" e altri {files.Count - 3}" : "");
                    problems.Add($"  {name} ({kind}): solo in {string.Join(", ", archives.Take(4))}{(archives.Count > 4 ? "..." : "")}; usato da {usedBy}");
                }
                else common++;
            }
            report.AppendLine("=== FILE USATI DAL LIVELLO CHE SULLA SWITCH SONO SOLO NEGLI ARCHIVI DI ALTRI LIVELLI ===");
            report.AppendLine($"I file convertiti usano {users.Count} file che non sono nell'archivio: {common} sono negli archivi comuni del");
            report.AppendLine($"gioco (normale), {nowhere} non sono da nessuna parte (vedi i file che il gioco Switch non ha), {problems.Count} sono solo");
            report.AppendLine("negli archivi di altri livelli, che il gioco non carica insieme a questo: non li trova.");
            foreach (string line in problems.Take(80)) report.AppendLine(line);
            if (problems.Count > 80) report.AppendLine($"  ... e altri {problems.Count - 80}");
            report.AppendLine();
        }

        /// <summary>
        /// Riferimenti dei file convertiti dal PC a oggetti che il file a cui puntano non contiene: il file c'e' (nell'archivio
        /// o negli archivi comuni del gioco Switch) ma senza quell'oggetto. In un file preso dagli originali Switch vuol dire
        /// che la Switch ce l'ha diverso dal PC (per esempio un insieme di materiali senza il materiale che un'entita' usa: la
        /// sua grafica resta senza materiale); in un file convertito di solito e' un'entita' tolta. Controlla anche i
        /// riferimenti per hash (EXID), che gli altri controlli saltano
        /// </summary>
        private static void AppendMissingObjects(StringBuilder report, IgArchive output, List<IgArchiveFile> fromSwitch, ArchiveIndex sw)
        {
            var originals = new HashSet<string>(fromSwitch.Select(f => f.Path.ToLowerInvariant()));
            var levelArchives = new HashSet<string>(LevelNames(sw.ByPath.Keys).Select(l => l.ToLowerInvariant())
                .Concat(sw.ByArchive.Keys.Where(a => a.Length > 5 && (a[0] == 'l' || a[0] == 'b') && char.IsDigit(a[1]) && a[4] == '_')));

            // File per nome (senza estensione, minuscolo): prima quelli dell'archivio del livello, poi quelli degli archivi
            // comuni Switch (quelli degli archivi di altri livelli il gioco non li carica: li elenca gia' un'altra sezione)
            var byName = new Dictionary<string, IgArchiveFile>();
            foreach (IgArchiveFile file in output.Files)
                if (file.IsIGZ() && !file.Path.StartsWith("update/", StringComparison.OrdinalIgnoreCase))
                    byName.TryAdd(NamespaceUtils.GetFileName(file.Path, false).ToLowerInvariant(), file);
            foreach (var (archive, files) in sw.ByArchive)
            {
                if (levelArchives.Contains(archive)) continue;
                foreach (IgArchiveFile file in files)
                    if (file.IsIGZ()) byName.TryAdd(NamespaceUtils.GetFileName(file.Path, false).ToLowerInvariant(), file);
            }
            var byHash = new Dictionary<uint, string>();
            foreach (string name in byName.Keys) byHash.TryAdd(NamespaceUtils.ComputeHash(name), name);
            var inOutput = new HashSet<IgArchiveFile>(output.Files);

            // Nomi (e hash dei nomi) degli oggetti di un file, letti una volta; null se il file non si legge
            var objectNames = new Dictionary<IgArchiveFile, (HashSet<string> names, HashSet<uint> hashes)?>();
            (HashSet<string> names, HashSet<uint> hashes)? ObjectNames(IgArchiveFile file)
            {
                if (objectNames.TryGetValue(file, out var cached)) return cached;
                (HashSet<string> names, HashSet<uint> hashes)? result = null;
                try
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var hashes = new HashSet<uint>();
                    foreach (igObject obj in file.ToIgzFile().Objects)
                    {
                        if (string.IsNullOrEmpty(obj.ObjectName)) continue;
                        names.Add(obj.ObjectName);
                        hashes.Add(NamespaceUtils.ComputeHash(obj.ObjectName));
                    }
                    result = (names, hashes);
                }
                catch
                {
                    // file non leggibile: i suoi oggetti non si controllano
                }
                objectNames[file] = result;
                return result;
            }

            int checkedReferences = 0, unreadable = 0;
            var missing = new Dictionary<string, (SortedSet<string> objects, SortedSet<string> users, bool switchVersion)>();
            foreach (IgArchiveFile file in output.Files)
            {
                string lower = file.Path.ToLowerInvariant();
                if (!file.IsIGZ() || originals.Contains(lower) || lower.StartsWith("update/")) continue;
                FixupCollection? fixups = RawFixups(file);
                if (fixups == null) continue;
                string own = NamespaceUtils.GetFileName(file.Path, false).ToLowerInvariant();
                foreach (NamedReference reference in fixups.handleReferences.Concat(fixups.objectReferences).Concat(fixups.exidReferences))
                {
                    string ns = (reference.namespaceName ?? "").ToLowerInvariant();
                    string obj = reference.objectName ?? "";
                    if (ns.Length == 0 || obj.Length == 0) continue;
                    if (ns.All(char.IsDigit))
                    {
                        if (!uint.TryParse(ns, out uint nsHash) || !byHash.TryGetValue(nsHash, out string? resolved)) continue;
                        ns = resolved;
                    }
                    if (ns.StartsWith("meta") || ns == own || !byName.TryGetValue(ns, out IgArchiveFile? target)) continue;
                    var targetNames = ObjectNames(target);
                    if (targetNames == null)
                    {
                        unreadable++;
                        continue;
                    }
                    checkedReferences++;
                    bool found = obj.All(char.IsDigit) && uint.TryParse(obj, out uint objHash)
                        ? targetNames.Value.hashes.Contains(objHash)
                        : targetNames.Value.names.Contains(obj) || targetNames.Value.hashes.Contains(NamespaceUtils.ComputeHash(obj));
                    if (found) continue;

                    bool switchVersion = !inOutput.Contains(target) || originals.Contains(target.Path.ToLowerInvariant());
                    if (!missing.TryGetValue(target.Path, out var entry))
                    {
                        entry = (new SortedSet<string>(StringComparer.OrdinalIgnoreCase), new SortedSet<string>(StringComparer.OrdinalIgnoreCase), switchVersion);
                        missing[target.Path] = entry;
                    }
                    entry.objects.Add(obj.All(char.IsDigit) ? "#" + obj : obj);
                    entry.users.Add(NamespaceUtils.GetFileName(file.Path));
                }
            }

            report.AppendLine($"=== OGGETTI CHE MANCANO NEI FILE USATI DAL LIVELLO ({missing.Count}) ===");
            report.AppendLine($"Controllati {checkedReferences} riferimenti dei file convertiti a oggetti di altri file" +
                              (unreadable > 0 ? $" ({unreadable} verso file che non si leggono, saltati)" : "") + ". Qui i file che ci");
            report.AppendLine("sono (nell'archivio o negli archivi comuni del gioco Switch) ma non contengono l'oggetto usato. In un file");
            report.AppendLine("preso dagli originali Switch vuol dire che la Switch ce l'ha diverso dal PC (per esempio un materiale che");
            report.AppendLine("manca: la grafica che lo usa resta senza); in un file convertito di solito e' un'entita' tolta.");
            if (missing.Count == 0) report.AppendLine("  nessuno");
            foreach (var (path, entry) in missing.OrderByDescending(m => m.Value.objects.Count).ThenBy(m => m.Key, StringComparer.Ordinal).Take(60))
            {
                string objects = string.Join(", ", entry.objects.Take(8)) + (entry.objects.Count > 8 ? $" e altri {entry.objects.Count - 8}" : "");
                string users = string.Join(", ", entry.users.Take(3)) + (entry.users.Count > 3 ? $" e altri {entry.users.Count - 3}" : "");
                report.AppendLine($"  {path} ({(entry.switchVersion ? "originale Switch" : "convertito")}): {entry.objects.Count} oggetti mancanti ({objects}); usati da {users}");
            }
            if (missing.Count > 60) report.AppendLine($"  ... e altri {missing.Count - 60}");
            report.AppendLine();
        }

        /// <summary>
        /// --togli-entita con il file indicato ("file:testo", con il testo del file non vuoto)
        /// </summary>
        private static bool FilterNamesFile(string filter)
        {
            int colon = filter.IndexOf(':');
            return colon > 0 && filter.Substring(0, colon).Trim().Length > 0;
        }

        /// <summary>
        /// Il file di un filtro "file:testo" e' nel percorso (quello del PC o quello nell'archivio convertito)
        /// </summary>
        private static bool FilterFileMatches(string filter, string path, string target)
        {
            int colon = filter.IndexOf(':');
            if (colon <= 0) return false;
            string file = filter.Substring(0, colon).Trim();
            return file.Length > 0 && (path.Contains(file, StringComparison.OrdinalIgnoreCase) || target.Contains(file, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Numero per il rapporto (con il punto, al massimo due decimali)
        /// </summary>
        private static string FormatFloat(float value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Luce, volume di colore o box visivo di un file di posizionamento (per il rapporto): posizione, raggio o misure
        /// (gia' moltiplicati per la scala dell'entita'), dettagli, se e' scuro e se il gioco lo usa (file nel pacchetto)
        /// </summary>
        private sealed record LightVolume(string File, string Name, string Kind, float X, float Y, float Z, float Radius,
                                          float SizeX, float SizeY, float SizeZ, string Details, bool Dark, bool Loaded);

        /// <summary>
        /// Luci (CPointLight, CBoxLight), volumi di colore (CTintSphere) e box visivi (CVisualDataBox) delle entita' di un file,
        /// aggiunti a <paramref name="into"/>; restituisce la posizione della partenza (CPlayerStartEntity) se il file ce l'ha.
        /// Un volume di colore scuro sopra il pavimento lo fa diventare nero, con il bordo sfumato della sfera, e lascia normali
        /// Crash e gli oggetti che si muovono
        /// </summary>
        private static (float x, float y, float z)? CollectLightVolumes(IgzFile igz, string file, bool loaded, List<LightVolume> into)
        {
            (float x, float y, float z)? foundStart = null;
            string fileName = NamespaceUtils.GetFileName(file);
            foreach (igEntity entity in igz.Objects.OfType<igEntity>())
            {
                float x = entity._parentSpacePosition._x, y = entity._parentSpacePosition._y, z = entity._parentSpacePosition._z;
                if (entity is CPlayerStartEntity && foundStart == null) foundStart = (x, y, z);
                igVec3fMetaField? scaleField = entity._transform?._nonUniformPersistentParentSpaceScale;
                float sx = scaleField?._x ?? 1f, sy = scaleField?._y ?? 1f, sz = scaleField?._z ?? 1f;
                float maxScale = Math.Max(Math.Abs(sx), Math.Max(Math.Abs(sy), Math.Abs(sz)));
                string scaleText = sx == 1f && sy == 1f && sz == 1f ? "" : $", scala {FormatFloat(sx)} {FormatFloat(sy)} {FormatFloat(sz)}";
                string name = entity.ObjectName ?? "?";
                foreach (igComponentData component in entity.GetComponents())
                {
                    if (component is CTintSphereComponentData tint)
                    {
                        float brightest = Math.Max(tint._color._x, Math.Max(tint._color._y, tint._color._z));
                        bool dark = tint._intensity > 0 && tint._additiveness < 0.5f && brightest < 0.2f;
                        into.Add(new LightVolume(fileName, name, "volume di colore", x, y, z, tint._radius * maxScale, 0, 0, 0,
                            $"raggio {FormatFloat(tint._radius)}{scaleText}, colore {FormatFloat(tint._color._x)} {FormatFloat(tint._color._y)} {FormatFloat(tint._color._z)}, " +
                            $"intensita' {FormatFloat(tint._intensity)}, additivo {FormatFloat(tint._additiveness)}, " +
                            $"sfumatura {(tint._depthBlendingEnabled ? FormatFloat(tint._depthBlendingSoftness) : "no")}", dark, loaded));
                    }
                    else if (component is CPointLightComponentData light)
                    {
                        bool dark = light._intensity < 0 || light._color._x < 0 || light._color._y < 0 || light._color._z < 0;
                        into.Add(new LightVolume(fileName, name, "luce", x, y, z, light._radius * maxScale, 0, 0, 0,
                            $"raggio {FormatFloat(light._radius)}{scaleText}, colore {FormatFloat(light._color._x)} {FormatFloat(light._color._y)} " +
                            $"{FormatFloat(light._color._z)} {FormatFloat(light._color._w)}, intensita' {FormatFloat(light._intensity)}, {light._lightBakeType}", dark, loaded));
                    }
                    else if (component is CBoxLightComponentData box)
                    {
                        bool dark = box._intensity < 0 || box._color._x < 0 || box._color._y < 0 || box._color._z < 0;
                        into.Add(new LightVolume(fileName, name, "luce a box", x, y, z, 0,
                            box._dimensions._x * Math.Abs(sx), box._dimensions._y * Math.Abs(sy), box._dimensions._z * Math.Abs(sz),
                            $"misure {FormatFloat(box._dimensions._x)} {FormatFloat(box._dimensions._y)} {FormatFloat(box._dimensions._z)}{scaleText}, " +
                            $"colore {FormatFloat(box._color._x)} {FormatFloat(box._color._y)} {FormatFloat(box._color._z)}, intensita' {FormatFloat(box._intensity)}, " +
                            $"texture '{box._cookieTextureName}'", dark, loaded));
                    }
                    else if (component is CVisualDataBoxComponentData visual)
                    {
                        into.Add(new LightVolume(fileName, name, "box visivo", x, y, z, 0,
                            visual._dimensions._x * Math.Abs(sx), visual._dimensions._y * Math.Abs(sy), visual._dimensions._z * Math.Abs(sz),
                            $"misure {FormatFloat(visual._dimensions._x)} {FormatFloat(visual._dimensions._y)} {FormatFloat(visual._dimensions._z)}{scaleText}, " +
                            $"acceso {(visual._state ? "si'" : "no")}, priorita' {visual._priority}, {visual._animationType}", false, loaded));
                    }
                }
            }
            return foundStart;
        }

        /// <summary>
        /// Rapporto: luci, volumi di colore e box visivi del livello, prima quelli che contengono la partenza e quelli scuri
        /// </summary>
        private static void AppendLightVolumes(StringBuilder report, List<LightVolume> volumes, (float x, float y, float z)? start)
        {
            report.AppendLine($"=== LUCI, VOLUMI DI COLORE E BOX VISIVI ({volumes.Count}) ===");
            report.AppendLine("Un volume di colore (CTintSphere) scuro, o una luce negativa, scurisce le decorazioni che ha dentro: pavimento e muri");
            report.AppendLine("diventano neri fino al bordo sfumato della sfera, mentre Crash, le casse e i nemici restano normali. Posizioni come");
            report.AppendLine("nel file (rispetto all'entita' genitore, se ce l'ha). Per provare a togliere un'entita': --togli-entita <file>:<nome>");
            report.AppendLine("(dalla v48 anche nei file del livello stesso, indicando il file).");
            if (start == null) report.AppendLine("  partenza (PlayerStart) non trovata: distanze non calcolate");
            else report.AppendLine($"  partenza (PlayerStart): {FormatFloat(start.Value.x)} {FormatFloat(start.Value.y)} {FormatFloat(start.Value.z)}");
            // Quante per tipo e per file (solo quelle che il gioco carica): un file di luci di un altro livello preso dal PC puo'
            // averne molte piu' dell'originale Switch (L320_TombWader_lighting nel Level 7: 104 entita', sulla Switch 84)
            List<LightVolume> loadedVolumes = volumes.Where(v => v.Loaded).ToList();
            if (loadedVolumes.Count > 0)
            {
                string Counts(IEnumerable<LightVolume> list) =>
                    string.Join(", ", list.GroupBy(v => v.Kind).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"));
                report.AppendLine($"  in tutto (file nel pacchetto): {Counts(loadedVolumes)}");
                foreach (var group in loadedVolumes.GroupBy(v => v.File).OrderByDescending(g => g.Count()).Take(12))
                    report.AppendLine($"    {group.Key}: {Counts(group)}");
            }
            var rows = new List<(LightVolume volume, double? distance, bool inside)>();
            foreach (LightVolume volume in volumes)
            {
                double? distance = null;
                bool inside = false;
                if (start != null)
                {
                    double dx = volume.X - start.Value.x, dy = volume.Y - start.Value.y, dz = volume.Z - start.Value.z;
                    distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    inside = volume.Radius > 0
                        ? distance.Value <= volume.Radius
                        : Math.Abs(dx) <= volume.SizeX / 2 && Math.Abs(dy) <= volume.SizeY / 2 && Math.Abs(dz) <= volume.SizeZ / 2;
                }
                rows.Add((volume, distance, inside));
            }
            if (rows.Count == 0) report.AppendLine("  nessuno");
            foreach (var (volume, distance, inside) in rows.OrderByDescending(r => r.inside).ThenByDescending(r => r.volume.Dark)
                                                           .ThenBy(r => r.distance ?? double.MaxValue).Take(80))
            {
                string flags = (inside ? "CONTIENE LA PARTENZA " : "") + (volume.Dark ? "SCURO " : "");
                string distanceText = distance == null ? "" : $", distanza dalla partenza {FormatFloat((float)distance.Value)}";
                report.AppendLine($"  {flags}[{volume.Kind}] {volume.File} {volume.Name}: posizione {FormatFloat(volume.X)} {FormatFloat(volume.Y)} " +
                                  $"{FormatFloat(volume.Z)}{distanceText}, {volume.Details}{(volume.Loaded ? "" : " (file fuori dal pacchetto: il gioco non la carica)")}");
            }
            if (rows.Count > 80) report.AppendLine($"  ... e altri {rows.Count - 80}");
            report.AppendLine();
        }

        /// <summary>
        /// Zona (COcclusionTrigger) o gruppo (COcclusionBox) delle occlusioni di un file, per il rapporto: limiti approssimati
        /// nel mondo (posizione + scala per i limiti, senza la rotazione) e, per un gruppo, le entita' che nasconde
        /// </summary>
        private sealed record OcclusionInfo(string File, string Kind, string Name, float X, float Y, float Z,
                                            float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ,
                                            string Details, List<string> Entities);

        /// <summary>
        /// Decorazione, blocco di collisione o oggetto con un modello di un file di posizionamento, per il rapporto
        /// </summary>
        private sealed record PlacedEntity(string File, string Name, string Kind, string Model, float X, float Y, float Z,
                                           string Details, uint NameHash);

        /// <summary>
        /// Nomi degli oggetti di una lista di riferimenti (un hash, se il nome non si conosce)
        /// </summary>
        private static List<string> HandleNames(igSmartHandleList? list)
        {
            var names = new List<string>();
            if (list == null) return names;
            foreach (igHandleMetaField handle in list._data)
            {
                string name = handle.Reference?.objectName ?? "";
                if (name.Length > 0) names.Add(name);
            }
            return names;
        }

        /// <summary>
        /// Limiti nel mondo di un volume (posizione + scala per i limiti locali), senza la rotazione
        /// </summary>
        private static (float minX, float minY, float minZ, float maxX, float maxY, float maxZ) WorldBounds(
            igVec3fMetaField position, igVec3fMetaField scale, igVec3fMetaField min, igVec3fMetaField max)
        {
            float sx = scale._x == 0 ? 1 : scale._x, sy = scale._y == 0 ? 1 : scale._y, sz = scale._z == 0 ? 1 : scale._z;
            float ax = position._x + min._x * sx, bx = position._x + max._x * sx;
            float ay = position._y + min._y * sy, by = position._y + max._y * sy;
            float az = position._z + min._z * sz, bz = position._z + max._z * sz;
            return (Math.Min(ax, bx), Math.Min(ay, by), Math.Min(az, bz), Math.Max(ax, bx), Math.Max(ay, by), Math.Max(az, bz));
        }

        /// <summary>
        /// Occlusioni di un file: zone (COcclusionTrigger: quando Crash, o la telecamera, ci entra attivano dei gruppi) e
        /// gruppi (COcclusionBox: le decorazioni e gli oggetti che nascondono, o che mostrano se il gruppo e' invertito)
        /// </summary>
        private static List<OcclusionInfo> DescribeOcclusion(IgzFile igz, string file)
        {
            var result = new List<OcclusionInfo>();
            string fileName = NamespaceUtils.GetFileName(file);
            foreach (igObject obj in igz.Objects)
            {
                if (obj is COcclusionTrigger trigger)
                {
                    var (minX, minY, minZ, maxX, maxY, maxZ) = WorldBounds(trigger._position, trigger._scale, trigger._min, trigger._max);
                    result.Add(new OcclusionInfo(fileName, "zona", trigger.ObjectName ?? "?", trigger._position._x, trigger._position._y, trigger._position._z,
                        minX, minY, minZ, maxX, maxY, maxZ,
                        $"scala {FormatFloat(trigger._scale._x)} {FormatFloat(trigger._scale._y)} {FormatFloat(trigger._scale._z)}, " +
                        $"rotazione {FormatFloat(trigger._rotation._x)} {FormatFloat(trigger._rotation._y)} {FormatFloat(trigger._rotation._z)}, " +
                        $"accesa {(trigger._state ? "si'" : "no")}, con la telecamera {(trigger._useCameraPosition ? "si'" : "no")}, " +
                        $"gruppi {trigger._boxes?._data.Count ?? 0}", []));
                }
                else if (obj is COcclusionBox box)
                {
                    var (minX, minY, minZ, maxX, maxY, maxZ) = WorldBounds(box._position, box._scale, box._min, box._max);
                    List<string> statics = HandleNames(box._entities);
                    List<string> dynamics = HandleNames(box._dynamicEntities);
                    List<string> receivers = HandleNames(box._messageReceivingEntities);
                    result.Add(new OcclusionInfo(fileName, "gruppo", box.ObjectName ?? "?", box._position._x, box._position._y, box._position._z,
                        minX, minY, minZ, maxX, maxY, maxZ,
                        $"inverte {(box._occlusionFlags._invertVisibility ? "si'" : "no")}, opzioni {box._occlusionFlags._flags}, " +
                        $"stato {(box._occlusionFlags._state ? "si'" : "no")}, decorazioni {statics.Count}, oggetti {dynamics.Count}, avvisati {receivers.Count}",
                        statics.Concat(dynamics).ToList()));
                }
            }
            return result;
        }

        /// <summary>
        /// Spegne le occlusioni di un file: zone senza gruppi da attivare, gruppi senza entita' da nascondere, non invertiti e
        /// non attivi. Sulla console nascondono le parti di un livello che non si vedono da dove sta Crash: quelle di un altro
        /// livello (Snow Go) sono fatte per quel livello e nel livello nuovo nascondono decorazioni a caso (il pavimento del
        /// Level 10), mentre Crash, casse e nemici restano. Restituisce quante zone e quanti gruppi ha spento
        /// </summary>
        private static (int triggers, int boxes) NeutralizeOcclusion(IgzFile igz)
        {
            int triggers = 0, boxes = 0;
            foreach (igObject obj in igz.Objects)
            {
                if (obj is COcclusionTrigger trigger)
                {
                    trigger._boxes?._data.Clear();
                    triggers++;
                }
                else if (obj is COcclusionBox box)
                {
                    box._entities?._data.Clear();
                    box._dynamicEntities?._data.Clear();
                    box._messageReceivingEntities?._data.Clear();
                    box._occlusionFlags._invertVisibility = false;
                    box._occlusionFlags._state = false;
                    boxes++;
                }
            }
            return (triggers, boxes);
        }

        /// <summary>
        /// Decorazioni (modello in CModelComponentData), blocchi di collisione e oggetti con un modello di un file, con quello
        /// che puo' nasconderli (per il rapporto)
        /// </summary>
        private static void CollectPlacedEntities(IgzFile igz, string file, bool loaded, List<PlacedEntity> into)
        {
            string fileName = NamespaceUtils.GetFileName(file);
            foreach (igEntity entity in igz.Objects.OfType<igEntity>())
            {
                CModelComponentData? modelComponent = entity.GetComponent<CModelComponentData>();
                CStaticComponentData? staticComponent = entity.GetComponent<CStaticComponentData>();
                string? kind = null;
                string? modelName = null;
                var details = new List<string>();
                if (modelComponent != null)
                {
                    kind = "decorazione";
                    modelName = modelComponent._fileName;
                    if (modelComponent._fadeFx != 1f) details.Add($"fadeFx {FormatFloat(modelComponent._fadeFx)}");
                    details.Add($"distanza del modello {modelComponent._distanceCullImportance}");
                }
                else if (entity is CDynamicClipEntity clip)
                {
                    kind = "blocco di collisione";
                    modelName = (clip._entityData as CGameEntityData)?._modelName;
                    details.Add($"limiti {FormatFloat(clip._min._x)} {FormatFloat(clip._min._y)} {FormatFloat(clip._min._z)} / " +
                                $"{FormatFloat(clip._max._x)} {FormatFloat(clip._max._y)} {FormatFloat(clip._max._z)}");
                }
                else if (entity._entityData is CGameEntityData gameData && !string.IsNullOrWhiteSpace(gameData._modelName))
                {
                    kind = "oggetto";
                    modelName = gameData._modelName;
                }
                if (kind == null) continue;
                if (staticComponent != null)
                {
                    details.Add($"distanza {staticComponent._flagsBitfield._distanceCullImportance}");
                    if (staticComponent._flagsBitfield._disableVisual) details.Add("NASCOSTA (disableVisual)");
                    if (staticComponent._flagsBitfield._ignoreOcclusionBoxes) details.Add("ignora le occlusioni");
                    if (staticComponent._materialOverrides != null)
                        details.Add($"sostituzioni dei materiali {staticComponent._materialOverrides._keys?._data.Count ?? 0}");
                }
                if (entity is CGameEntity gameEntity && gameEntity._ignoreOcclusionBoxes) details.Add("ignora le occlusioni");
                if (entity is CEntity baseEntity && baseEntity._properties._startHidden) details.Add("NASCOSTA all'inizio");
                if (entity._bitfield._isHidden) details.Add("isHidden");
                igVec3fMetaField? scale = entity._transform?._nonUniformPersistentParentSpaceScale;
                if (scale != null && (scale._x != 1f || scale._y != 1f || scale._z != 1f))
                    details.Add($"scala {FormatFloat(scale._x)} {FormatFloat(scale._y)} {FormatFloat(scale._z)}");
                if (!loaded) details.Add("file fuori dal pacchetto");
                string name = entity.ObjectName ?? "?";
                into.Add(new PlacedEntity(fileName, name, kind, string.IsNullOrWhiteSpace(modelName) ? "-" : modelName,
                    entity._parentSpacePosition._x, entity._parentSpacePosition._y, entity._parentSpacePosition._z,
                    string.Join(", ", details), NamespaceUtils.ComputeHash(name)));
            }
        }

        /// <summary>
        /// Rapporto: occlusioni (zone e gruppi, quali contengono la partenza) e le decorazioni piu' vicine alla partenza, con
        /// quelle che i gruppi nascondevano
        /// </summary>
        private static void AppendOcclusionAndNearStart(StringBuilder report, List<OcclusionInfo> occlusion, List<PlacedEntity> placed,
                                                        (float x, float y, float z)? start, bool neutralized)
        {
            report.AppendLine($"=== OCCLUSIONI ({occlusion.Count}) ===");
            report.AppendLine("Zone (COcclusionTrigger) e gruppi (COcclusionBox) con cui sulla console il gioco nasconde le parti del livello che non si");
            report.AppendLine("vedono da dove sta Crash. Quelle di un altro livello (Snow Go) sono fatte per quel livello: nel livello nuovo nascondono");
            report.AppendLine("decorazioni a caso, come il pavimento, mentre Crash, casse e nemici restano. Sul PC non sembrano contare.");
            report.AppendLine(neutralized
                ? "Dalla v49 si spengono (zone senza gruppi, gruppi senza entita'); --tieni-occlusioni le lascia come sono. Valori di prima."
                : "Lasciate come sono (--tieni-occlusioni).");
            var hiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hiddenHashes = new HashSet<uint>();
            foreach (OcclusionInfo info in occlusion)
                foreach (string entityName in info.Entities)
                {
                    hiddenNames.Add(entityName);
                    hiddenHashes.Add(entityName.All(char.IsDigit) && uint.TryParse(entityName, out uint entityHash) ? entityHash : NamespaceUtils.ComputeHash(entityName));
                }
            if (occlusion.Count == 0) report.AppendLine("  nessuna");
            foreach (OcclusionInfo info in occlusion)
            {
                bool inside = start != null &&
                              start.Value.x >= info.MinX && start.Value.x <= info.MaxX &&
                              start.Value.y >= info.MinY && start.Value.y <= info.MaxY &&
                              start.Value.z >= info.MinZ && start.Value.z <= info.MaxZ;
                string examples = info.Entities.Count > 0 ? $"; per esempio {string.Join(", ", info.Entities.Take(8))}" : "";
                report.AppendLine($"  {(inside ? "CONTIENE LA PARTENZA " : "")}[{info.Kind}] {info.File} {info.Name}: posizione {FormatFloat(info.X)} " +
                                  $"{FormatFloat(info.Y)} {FormatFloat(info.Z)}, limiti {FormatFloat(info.MinX)} {FormatFloat(info.MinY)} {FormatFloat(info.MinZ)} / " +
                                  $"{FormatFloat(info.MaxX)} {FormatFloat(info.MaxY)} {FormatFloat(info.MaxZ)}, {info.Details}{examples}");
            }
            report.AppendLine();

            report.AppendLine("=== DECORAZIONI VICINO ALLA PARTENZA ===");
            report.AppendLine("Le decorazioni, i blocchi di collisione e gli oggetti con un modello piu' vicini alla partenza (PlayerStart), con quello che");
            report.AppendLine("puo' nasconderli: NELLE OCCLUSIONI (un gruppo delle occlusioni le nascondeva), NASCOSTA, distanza (importanza per la");
            report.AppendLine("distanza da cui spariscono), scala. Posizioni come nel file (rispetto all'entita' genitore, se ce l'ha).");
            if (start == null)
            {
                report.AppendLine("  partenza (PlayerStart) non trovata");
                report.AppendLine();
                return;
            }
            int occluded = placed.Count(p => hiddenNames.Contains(p.Name) || hiddenHashes.Contains(p.NameHash));
            report.AppendLine($"  partenza: {FormatFloat(start.Value.x)} {FormatFloat(start.Value.y)} {FormatFloat(start.Value.z)}; entita' con un modello in tutto il livello: " +
                              $"{placed.Count}, nelle occlusioni: {occluded}");
            foreach (PlacedEntity entity in placed
                         .Select(p => (entity: p, distance: Math.Sqrt(Math.Pow(p.X - start.Value.x, 2) + Math.Pow(p.Y - start.Value.y, 2) + Math.Pow(p.Z - start.Value.z, 2))))
                         .OrderBy(p => p.distance).Take(60).Select(p => p.entity))
            {
                double distance = Math.Sqrt(Math.Pow(entity.X - start.Value.x, 2) + Math.Pow(entity.Y - start.Value.y, 2) + Math.Pow(entity.Z - start.Value.z, 2));
                bool hidden = hiddenNames.Contains(entity.Name) || hiddenHashes.Contains(entity.NameHash);
                report.AppendLine($"  {(hidden ? "NELLE OCCLUSIONI " : "")}[{entity.Kind}] {entity.File} {entity.Name}: modello {entity.Model}, posizione " +
                                  $"{FormatFloat(entity.X)} {FormatFloat(entity.Y)} {FormatFloat(entity.Z)}, distanza {FormatFloat((float)distance)}, {entity.Details}");
            }
            report.AppendLine();
        }

        /// <summary>
        /// --solo-usati: toglie dall'archivio modelli, materiali e texture (igz in models/, materialinstances/, textures/,
        /// loosetextures/) che nessun altro file del livello usa. I file usati si leggono dai fixup di ogni igz (dipendenze,
        /// riferimenti per nome e tutti i testi, compresi quelli dentro liste e memorie, come i percorsi dei modelli), senza
        /// leggere gli oggetti: anche un file che l'editor non sa rileggere conta. Tutti gli altri file, compresi quelli di
        /// altri livelli, restano e fanno da punto di partenza. Il pacchetto non conta come uso (si ricostruisce dopo).
        /// </summary>
        private static List<IgArchiveFile> RemoveUnusedFiles(IgArchive output, StringBuilder report, List<string> lines)
        {
            bool Removable(IgArchiveFile file)
            {
                string p = file.Path;
                return file.IsIGZ() && (p.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ||
                                        p.StartsWith("materialinstances/", StringComparison.OrdinalIgnoreCase) ||
                                        p.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) ||
                                        p.StartsWith("loosetextures/", StringComparison.OrdinalIgnoreCase));
            }

            var files = output.Files.Where(x => !x.Path.StartsWith("update/", StringComparison.OrdinalIgnoreCase)).ToList();
            var byName = new Dictionary<string, List<IgArchiveFile>>();
            foreach (IgArchiveFile file in files)
            {
                string name = NamespaceUtils.GetFileName(file.Path, false).ToLowerInvariant();
                if (!byName.TryGetValue(name, out List<IgArchiveFile>? list)) byName[name] = list = [];
                list.Add(file);
            }
            // riferimenti per hash (EXID) a file che il PC non conosce (nomi della Switch): fino alla v39 non contavano e
            // quei file potevano essere tolti anche se usati
            var byHash = new Dictionary<uint, string>();
            foreach (string name in byName.Keys) byHash.TryAdd(NamespaceUtils.ComputeHash(name), name);

            var deps = new Dictionary<IgArchiveFile, HashSet<string>>();
            foreach (IgArchiveFile file in files.Where(x => x.IsIGZ() && !x.Path.StartsWith("packages/", StringComparison.OrdinalIgnoreCase) &&
                                                            !x.Path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)))
            {
                HashSet<string>? names = RawReferencedNames(file);
                if (names == null)
                {
                    report.AppendLine($"--solo-usati: {file.Path} non leggibile: non tolgo nessun file");
                    report.AppendLine();
                    return [];
                }
                deps[file] = names;
            }

            var reached = new HashSet<IgArchiveFile>(files.Where(x => !Removable(x)));
            var queue = new Queue<IgArchiveFile>(reached);
            while (queue.Count > 0)
            {
                IgArchiveFile file = queue.Dequeue();
                if (!deps.TryGetValue(file, out HashSet<string>? names)) continue;
                foreach (string referenced in names)
                {
                    string name = referenced;
                    if (name.All(char.IsDigit) && uint.TryParse(name, out uint hash) && byHash.TryGetValue(hash, out string? resolved)) name = resolved;
                    if (byName.TryGetValue(name, out List<IgArchiveFile>? targets))
                        foreach (IgArchiveFile target in targets)
                            if (reached.Add(target)) queue.Enqueue(target);
                }
            }

            var unused = files.Where(x => !reached.Contains(x)).ToList();
            long bytes = unused.Sum(x => (long)x.UncompressedSize);
            foreach (IgArchiveFile file in unused) output.RemoveFile(file);
            report.AppendLine("=== GRAFICA NON USATA (--solo-usati) ===");
            report.AppendLine($"tolti {unused.Count} file ({Mb((ulong)bytes)}) di modelli, materiali e texture che nessun file del livello usa");
            foreach (IgArchiveFile file in unused.OrderByDescending(x => x.UncompressedSize).Take(20))
                report.AppendLine($"  {Mb((ulong)file.UncompressedSize),10}  {file.Path}");
            if (unused.Count > 20) report.AppendLine($"  ... e altri {unused.Count - 20} (tutti in fondo al rapporto, righe \"grafica non usata\")");
            report.AppendLine();
            foreach (IgArchiveFile file in unused.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)) lines.Add($"grafica non usata (tolta): {file.Path}");
            return unused;
        }

        /// <summary>
        /// Nomi (in minuscolo, senza cartella ne' estensione) di tutto quello che un igz nomina: dipendenze (TDEP), riferimenti
        /// per nome a oggetti di altri file (EXNM, EXID) e tutti i testi (TSTR). Letti dai fixup, senza leggere gli oggetti.
        /// null se il file non si legge
        /// </summary>
        private static HashSet<string>? RawReferencedNames(IgArchiveFile file)
        {
            // un file che non e' un igz (per esempio un .lng di testo) non nomina altri file
            byte[] data;
            try
            {
                data = file.Uncompress();
            }
            catch
            {
                return null;
            }
            if (data.Length < 0x24 || BitConverter.ToUInt32(data, 0) != 0x49475A01) return new HashSet<string>();
            FixupCollection? fixups = RawFixups(file);
            if (fixups == null) return null;
            var names = new HashSet<string>();
            void Add(string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                string name = NamespaceUtils.GetFileName(value, false).ToLowerInvariant();
                if (name.Length > 0) names.Add(name);
            }
            foreach (TDEP_Fixup.TDEP_Item item in fixups.TDEP)
            {
                Add(item.name);
                Add(item.path);
            }
            foreach (string text in fixups.TSTR) Add(text);
            foreach (NamedReference reference in fixups.handleReferences.Concat(fixups.objectReferences).Concat(fixups.exidReferences))
                Add(reference.namespaceName);
            return names;
        }

        /// <summary>
        /// Percorso di una dipendenza TDEP senza il prefisso del dispositivo ("cartella:/...") e la barra iniziale, in minuscolo
        /// </summary>
        private static string NormalizeFixupPath(string value)
        {
            string p = value.Replace('\\', '/').ToLowerInvariant();
            int colon = p.IndexOf(':');
            if (colon >= 0) p = p.Substring(colon + 1);
            return p.TrimStart('/');
        }

        /// <summary>
        /// Errore nel rileggere un igz appena scritto, null se si rilegge
        /// </summary>
        private static string? RereadError(IgArchiveFile file)
        {
            try
            {
                file.ToIgzFile();
                return null;
            }
            catch (Exception e)
            {
                return $"{e.GetType().Name}: {e.Message}";
            }
        }

        /// <summary>
        /// Asset di Crash Team Racing (Octane) importato nell'editor: una cartella Octane nel percorso (maps/Octane/...,
        /// models/Levels/Octane/..., materialinstances/Octane/...) o nel nome di una texture (...!Octane!...)
        /// </summary>
        private static bool IsCtrAsset(string path) =>
            path.Split('/').Any(p => p.Equals("Octane", StringComparison.OrdinalIgnoreCase)) ||
            path.Contains("!octane!", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Cartella del gioco scelto con --gioco ("crash3" -> "Crash3"), null senza --gioco
        /// </summary>
        private static string? GameFolder(string? game) => string.IsNullOrEmpty(game) ? null : "Crash" + game.Substring(5);

        /// <summary>
        /// Gioco ("crash1", "crash2", "crash3") di un anno della zone info, null se non e' uno dei tre
        /// </summary>
        private static string? GameOfYear(EGameYear? year) => year switch
        {
            EGameYear.eGY_2017_Crash1 => "crash1",
            EGameYear.eGY_2017_Crash2 => "crash2",
            EGameYear.eGY_2017_Crash3 => "crash3",
            _ => null
        };

        /// <summary>
        /// File principale del livello: quello con il nome del livello, oppure, per un livello creato da uno originale
        /// (L112_RoadToNowhere_Custom), quello con il nome della sua cartella (maps/Crash1/L112_RoadToNowhere/L112_RoadToNowhere.igz)
        /// </summary>
        private static bool IsMainLevelFile(string path, List<string> levels, List<string> levelDirs)
        {
            string name = NamespaceUtils.GetFileName(path, false);
            // nella cartella del livello: un file con lo stesso nome in un'altra cartella (Crash2/Custom_Level/Custom_Level.igz
            // lasciato dall'editor) non e' il file principale
            string[] parts = path.Split('/');
            bool inLevelDir = levelDirs.Count == 0 ||
                              (parts.Length == 4 && levelDirs.Any(d => d.Equals(parts[1] + "/" + parts[2], StringComparison.OrdinalIgnoreCase)));
            if (inLevelDir && levels.Any(l => name.Equals(l, StringComparison.OrdinalIgnoreCase))) return true;
            return levelDirs.Any(d => path.Equals($"maps/{d}/{d.Substring(d.LastIndexOf('/') + 1)}.igz", StringComparison.OrdinalIgnoreCase));
        }

        private static string Mb(ulong bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";

        /// <summary>
        /// Rapporto: le texture che si porta dietro ogni file in maps/ (modelli, materiali, personaggi, effetti e script che nomina
        /// e le loro texture, dai fixup come --solo-usati, senza passare per gli altri file in maps/): quelle solo sue, che escono dal
        /// livello togliendo le sue entita' (--togli-entita file:* con --solo-usati), e tutte quelle che usa
        /// </summary>
        private static void AppendTextureUsers(StringBuilder report, IgArchive output)
        {
            report.AppendLine("=== TEXTURE PER FILE ===");
            report.AppendLine("Le texture che ogni file in maps/ si porta dietro (modelli, materiali, personaggi ed effetti che nomina, con le loro");
            report.AppendLine("texture). \"solo sue\": nessun altro file in maps/ le usa, quindi togliendo le entita' del file (--togli-entita <file>:*,");
            report.AppendLine("con --solo-usati) escono dal livello. Per alleggerire la memoria delle texture si comincia dai primi.");
            var files = output.Files.Where(x => !x.Path.StartsWith("update/", StringComparison.OrdinalIgnoreCase)).ToList();
            var byName = new Dictionary<string, List<IgArchiveFile>>();
            foreach (IgArchiveFile file in files)
            {
                string name = NamespaceUtils.GetFileName(file.Path, false).ToLowerInvariant();
                if (!byName.TryGetValue(name, out List<IgArchiveFile>? list)) byName[name] = list = [];
                list.Add(file);
            }
            var byHash = new Dictionary<uint, string>();
            foreach (string name in byName.Keys) byHash.TryAdd(NamespaceUtils.ComputeHash(name), name);
            static bool IsTexture(IgArchiveFile file) =>
                file.Path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) || file.Path.StartsWith("loosetextures/", StringComparison.OrdinalIgnoreCase);
            static bool IsMap(IgArchiveFile file) => file.Path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase);
            var referencedNames = new Dictionary<IgArchiveFile, HashSet<string>?>();
            HashSet<string>? Names(IgArchiveFile file)
            {
                if (!referencedNames.TryGetValue(file, out HashSet<string>? names)) referencedNames[file] = names = RawReferencedNames(file);
                return names;
            }

            // per ogni file in maps/ le texture che raggiunge, senza passare per gli altri file in maps/ (contano per conto loro)
            // ne' per il pacchetto (elenca tutto)
            var usersOf = new Dictionary<IgArchiveFile, int>();
            var reach = new List<(IgArchiveFile map, List<IgArchiveFile> textures)>();
            foreach (IgArchiveFile map in files.Where(f => IsMap(f) && f.IsIGZ()))
            {
                var seen = new HashSet<IgArchiveFile> { map };
                var queue = new Queue<IgArchiveFile>();
                queue.Enqueue(map);
                var textures = new List<IgArchiveFile>();
                while (queue.Count > 0)
                {
                    IgArchiveFile current = queue.Dequeue();
                    if (IsTexture(current))
                    {
                        textures.Add(current);
                        continue;
                    }
                    if (!current.IsIGZ() || Names(current) is not HashSet<string> names) continue;
                    foreach (string referenced in names)
                    {
                        string target = referenced;
                        if (target.All(char.IsDigit) && uint.TryParse(target, out uint hash) && byHash.TryGetValue(hash, out string? resolved)) target = resolved;
                        if (!byName.TryGetValue(target, out List<IgArchiveFile>? candidates)) continue;
                        foreach (IgArchiveFile candidate in candidates)
                        {
                            if (IsMap(candidate) || candidate.Path.StartsWith("packages/", StringComparison.OrdinalIgnoreCase)) continue;
                            if (seen.Add(candidate)) queue.Enqueue(candidate);
                        }
                    }
                }
                reach.Add((map, textures));
                foreach (IgArchiveFile texture in textures) usersOf[texture] = usersOf.GetValueOrDefault(texture) + 1;
            }

            long allTextures = files.Where(IsTexture).Sum(f => (long)f.UncompressedSize);
            long unreached = files.Where(f => IsTexture(f) && !usersOf.ContainsKey(f)).Sum(f => (long)f.UncompressedSize);
            report.AppendLine($"  texture nel livello: {Mb((ulong)allTextures)}; non usate da nessun file in maps/ (di Crash, dell'interfaccia, degli " +
                              $"script comuni, o caricate e basta): {Mb((ulong)unreached)}");
            var rows = reach
                .Select(r => (r.map,
                              own: r.textures.Where(t => usersOf[t] == 1).Sum(t => (long)t.UncompressedSize),
                              ownCount: r.textures.Count(t => usersOf[t] == 1),
                              total: r.textures.Sum(t => (long)t.UncompressedSize),
                              count: r.textures.Count))
                .Where(r => r.count > 0)
                .OrderByDescending(r => r.own).ThenByDescending(r => r.total)
                .ToList();
            if (rows.Count == 0) report.AppendLine("  nessun file in maps/ usa texture");
            foreach (var (map, own, ownCount, total, count) in rows.Take(45))
                report.AppendLine($"  {Mb((ulong)own),9} solo sue ({ownCount}), {Mb((ulong)total)} in tutto ({count}): {map.Path}");
            if (rows.Count > 45) report.AppendLine($"  ... e altri {rows.Count - 45}");
            report.AppendLine();
        }

        /// <summary>
        /// Rapporto: le partenze (CPlayerStartEntity) dei file del livello e la telecamera con cui comincia ognuna, con i suoi valori;
        /// per una telecamera su una spline (CSplineCamera) anche la spline: punti, dove stanno, distanza dalla partenza. Se la
        /// telecamera non si trova, o guarda da un punto lontano o impossibile, lo schermo puo' restare nero con il gioco che va
        /// </summary>
        private static void AppendStartCamera(StringBuilder report, IgArchive output)
        {
            report.AppendLine("=== PARTENZA E TELECAMERA ===");
            report.AppendLine("La telecamera con cui comincia il livello (quella della partenza): se manca o ha valori impossibili lo schermo puo'");
            report.AppendLine("restare nero mentre il gioco va. Posizioni come nei file; per una telecamera su una spline, i punti della spline.");
            var files = output.Files.Where(f => f.IsIGZ() && f.Path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase)).ToList();
            var byNamespace = new Dictionary<string, IgArchiveFile>(StringComparer.OrdinalIgnoreCase);
            foreach (IgArchiveFile f in files) byNamespace.TryAdd(NamespaceUtils.GetFileName(f.Path, false), f);
            var parsed = new Dictionary<string, IgzFile?>(StringComparer.OrdinalIgnoreCase);
            IgzFile? Parse(string ns)
            {
                if (parsed.TryGetValue(ns, out IgzFile? cached)) return cached;
                IgzFile? igz = null;
                if (byNamespace.TryGetValue(ns, out IgArchiveFile? file))
                {
                    try
                    {
                        igz = file.ToIgzFile();
                    }
                    catch
                    {
                        // file che non si rilegge: lo dice un'altra sezione
                    }
                }
                parsed[ns] = igz;
                return igz;
            }
            int starts = 0;
            var startPoints = new List<(string name, float x, float y, float z)>();
            foreach (IgArchiveFile file in files)
            {
                IgzFile? igz = Parse(NamespaceUtils.GetFileName(file.Path, false));
                if (igz == null) continue;
                foreach (CPlayerStartEntity start in igz.Objects.OfType<CPlayerStartEntity>())
                {
                    starts++;
                    var (sx, sy, sz) = (start._parentSpacePosition._x, start._parentSpacePosition._y, start._parentSpacePosition._z);
                    startPoints.Add((start.ObjectName ?? "?", sx, sy, sz));
                    report.AppendLine($"  partenza {start.ObjectName ?? "?"} ({file.Path}): {PlaceText(start)}, inizio della zona {(start._zoneStart ? "si'" : "no")}");
                    // la telecamera e' un oggetto di un altro file (il file _Camera del livello: riferimento) o dello stesso file
                    igObject? camera = start._camera;
                    NamedReference? cameraRef = camera?.Reference;
                    IgzFile? cameraIgz = igz;
                    if (camera == null || (cameraRef != null && cameraRef.IsNullOrEmpty()))
                    {
                        report.AppendLine("    telecamera: nessuna (il gioco usa quella predefinita)");
                        continue;
                    }
                    string cameraName = cameraRef != null ? RefText(cameraRef) : camera.ObjectName ?? "?";
                    if (cameraRef != null)
                    {
                        cameraIgz = Parse(cameraRef.namespaceName);
                        camera = cameraIgz?.FindObject(cameraRef);
                        if (cameraIgz == null || camera == null)
                        {
                            report.AppendLine($"    telecamera {cameraName}: NON TROVATA ({(cameraIgz == null ? "il suo file non e' nell'archivio" : "il file non ha quell'oggetto")})");
                            continue;
                        }
                    }
                    report.AppendLine($"    telecamera {cameraName} [{camera.GetType().Name}]: {DeepFieldsText(camera, cameraIgz!.GameVersion, 0)}");
                    if (camera is not CSplineCamera splineCamera) continue;
                    NamedReference? pathRef = splineCamera._splineEntity?.Reference;
                    if (pathRef == null || pathRef.IsNullOrEmpty())
                    {
                        report.AppendLine("    spline: nessuna");
                        continue;
                    }
                    IgzFile? pathIgz = Parse(pathRef.namespaceName);
                    if (pathIgz?.FindObject(pathRef) is not igEntity pathEntity)
                    {
                        report.AppendLine($"    spline {RefText(pathRef)}: NON TROVATA");
                        continue;
                    }
                    igSpline2? spline = pathEntity.GetComponent<CSplineComponentData>()?._spline;
                    List<igSplineControlPoint2> points = spline?._data?._data.Where(p => p != null).Cast<igSplineControlPoint2>().ToList()
                                                         ?? new List<igSplineControlPoint2>();
                    var (px, py, pz) = (pathEntity._parentSpacePosition._x, pathEntity._parentSpacePosition._y, pathEntity._parentSpacePosition._z);
                    string pointsText = points.Count == 0 ? "nessun punto" :
                        $"{points.Count} punti, lunghezza {FormatFloat(spline!._length)}, primo {FormatFloat(px + points[0]._position._x)} {FormatFloat(py + points[0]._position._y)} " +
                        $"{FormatFloat(pz + points[0]._position._z)}, ultimo {FormatFloat(px + points[^1]._position._x)} {FormatFloat(py + points[^1]._position._y)} " +
                        $"{FormatFloat(pz + points[^1]._position._z)}, il piu' vicino alla partenza a " +
                        FormatFloat(points.Min(p => MathF.Sqrt(MathF.Pow(px + p._position._x - sx, 2) + MathF.Pow(py + p._position._y - sy, 2) + MathF.Pow(pz + p._position._z - sz, 2))));
                    report.AppendLine($"    spline {RefText(pathRef)}: entita' {PlaceText(pathEntity)}, {pointsText} (punti rispetto all'entita', senza la rotazione)");
                }
            }
            if (starts == 0) report.AppendLine("  nessuna partenza nei file del livello");

            // Box visivi (CVisualDataBox) che contengono una partenza: i valori che cambiano rispetto al gioco (quelli con il loro
            // "State" acceso), come esposizione, luminosita', nebbia e distanze a cui spariscono le decorazioni
            if (startPoints.Count > 0)
            {
                int boxes = 0;
                foreach (IgArchiveFile file in files)
                {
                    IgzFile? igz = Parse(NamespaceUtils.GetFileName(file.Path, false));
                    if (igz == null) continue;
                    foreach (igEntity entity in igz.Objects.OfType<igEntity>())
                    {
                        foreach (CVisualDataBoxComponentData box in entity.GetComponents().OfType<CVisualDataBoxComponentData>())
                        {
                            igVec3fMetaField? scale = entity._transform?._nonUniformPersistentParentSpaceScale;
                            float hx = box._dimensions._x * Math.Abs(scale?._x ?? 1f) / 2;
                            float hy = box._dimensions._y * Math.Abs(scale?._y ?? 1f) / 2;
                            float hz = box._dimensions._z * Math.Abs(scale?._z ?? 1f) / 2;
                            igVec3fMetaField center = entity._parentSpacePosition;
                            List<string> inside = startPoints
                                .Where(point => Math.Abs(point.x - center._x) <= hx && Math.Abs(point.y - center._y) <= hy && Math.Abs(point.z - center._z) <= hz)
                                .Select(point => point.name)
                                .ToList();
                            if (inside.Count == 0) continue;
                            boxes++;
                            report.AppendLine($"  box visivo {entity.ObjectName ?? "?"} ({file.Path}) contiene la partenza {string.Join(", ", inside)}: " +
                                              $"acceso {(box._state ? "si'" : "no")}, priorita' {box._priority}, {box._animationType}");
                            report.AppendLine($"    valori: {(box._data == null ? "nessun dato" : VisualDataText(box._data, igz.GameVersion))}");
                        }
                    }
                }
                if (boxes == 0) report.AppendLine("  nessun box visivo contiene la partenza: valori del gioco");
            }
            report.AppendLine();
        }

        /// <summary>
        /// Valori di un gruppo di dati visivi (CVisualDataGroup, il contenuto di un box visivo) che il box cambia: quelli con il loro
        /// "State" acceso (gli altri restano quelli dei box sotto o del gioco), con quanti sono accesi
        /// </summary>
        private static string VisualDataText(CVisualDataGroup data, GameVersion version)
        {
            if (data.Reference != null) return "@" + RefText(data.Reference);
            IReadOnlyList<CachedFieldAttr> fields = data.GetFields(version);
            var states = new Dictionary<string, bool>();
            foreach (CachedFieldAttr field in fields)
            {
                string name = field.GetName();
                if (name.EndsWith("State") && field.GetValue(data) is bool state) states[name] = state;
            }
            var parts = new List<string>();
            foreach (CachedFieldAttr field in fields)
            {
                string name = field.GetName();
                if (states.ContainsKey(name) || name.StartsWith("__")) continue;
                if (states.TryGetValue(name + "State", out bool enabled) && !enabled) continue;
                string? text = field.GetValue(data) switch
                {
                    float number => FormatFloat(number),
                    bool flag => flag ? "si'" : "no",
                    igVec2fMetaField v => $"({FormatFloat(v._x)} {FormatFloat(v._y)})",
                    igVec3fMetaField v => $"({FormatFloat(v._x)} {FormatFloat(v._y)} {FormatFloat(v._z)})",
                    igVec4fMetaField v => $"({FormatFloat(v._x)} {FormatFloat(v._y)} {FormatFloat(v._z)} {FormatFloat(v._w)})",
                    igHandleMetaField handle => handle.Reference == null ? null : HandleText(handle),
                    igObject child => child.Reference != null ? "@" + RefText(child.Reference) : child.GetType().Name,
                    _ => null,
                };
                if (text != null) parts.Add($"{name.TrimStart('_')}={text}");
            }
            int activeCount = states.Count(entry => entry.Value);
            return $"{activeCount} accesi su {states.Count}" + (parts.Count == 0 ? "" : ": " + string.Join(", ", parts));
        }

        /// <summary>
        /// Campi di un oggetto come testo, anche vettori e strutture dentro l'oggetto e, fino a depth 1, gli oggetti a cui punta
        /// (dati della telecamera...)
        /// </summary>
        private static string DeepFieldsText(igObjectBase obj, GameVersion version, int depth)
        {
            var parts = new List<string>();
            foreach (CachedFieldAttr field in obj.GetFields(version))
            {
                string name = field.GetName();
                if (name is "_meta" or "_bitfield" or "_dynamicFieldMemory" || name.StartsWith("__")) continue;
                object? value;
                try
                {
                    value = field.GetValue(obj);
                }
                catch
                {
                    continue;
                }
                string? text = value switch
                {
                    null => null,
                    string str => $"'{str}'",
                    igHandleMetaField handle => handle.Reference == null ? null : HandleText(handle),
                    float number => FormatFloat(number),
                    bool flag => flag ? "si'" : "no",
                    Enum choice => choice.ToString(),
                    int or uint or short or ushort or byte or sbyte or long or ulong => value.ToString(),
                    igVec2fMetaField v => $"({FormatFloat(v._x)} {FormatFloat(v._y)})",
                    igVec3fMetaField v => $"({FormatFloat(v._x)} {FormatFloat(v._y)} {FormatFloat(v._z)})",
                    igVec4fMetaField v => $"({FormatFloat(v._x)} {FormatFloat(v._y)} {FormatFloat(v._z)} {FormatFloat(v._w)})",
                    igObject child when depth < 1 && child.Reference == null => $"{child.GetType().Name}{{{DeepFieldsText(child, version, depth + 1)}}}",
                    igObject child => child.Reference != null ? "@" + RefText(child.Reference) : child.GetType().Name,
                    igObjectBase inner when depth < 2 => $"{{{DeepFieldsText(inner, version, depth + 1)}}}",
                    _ => null,
                };
                if (text != null && text != "{-}") parts.Add($"{name.TrimStart('_')}={text}");
            }
            return parts.Count == 0 ? "-" : string.Join(", ", parts);
        }

        /// <summary>
        /// Rapporto: valori impossibili nei file convertiti (NaN, infiniti, numeri enormi o piccolissimi che vengono da dati letti
        /// nel posto sbagliato): uno solo, in una luce, in un box visivo o in una telecamera, basta a far diventare nero lo schermo
        /// </summary>
        private static void AppendOddValues(StringBuilder report, IgArchive output)
        {
            report.AppendLine("=== VALORI IMPOSSIBILI NEI FILE CONVERTITI ===");
            report.AppendLine("Numeri che non possono essere giusti (NaN, infiniti, oltre 1e9, piccolissimi ma non zero) negli oggetti dei file dei");
            report.AppendLine("livelli: di solito un dato letto nel posto sbagliato. Uno solo in una luce, in un box visivo o nella telecamera puo'");
            report.AppendLine("far diventare nero lo schermo.");
            static string? Odd(float f) =>
                float.IsNaN(f) ? "NaN" : float.IsInfinity(f) ? "infinito" : Math.Abs(f) > 1e9f ? "enorme" : f != 0 && Math.Abs(f) < 1e-30f ? "piccolissimo" : null;
            var found = new Dictionary<string, (int count, string example)>();
            var oddScales = new List<string>();
            int scannedFiles = 0, unreadable = 0;
            foreach (IgArchiveFile file in output.Files.Where(f => f.IsIGZ() && f.Path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase)))
            {
                IgzFile igz;
                try
                {
                    igz = file.ToIgzFile();
                }
                catch
                {
                    unreadable++;
                    continue;
                }
                scannedFiles++;
                string fileName = NamespaceUtils.GetFileName(file.Path);
                void Scan(igObjectBase obj, string objectText, string path, int depth)
                {
                    foreach (CachedFieldAttr field in obj.GetFields(igz.GameVersion))
                    {
                        object? value;
                        try
                        {
                            value = field.GetValue(obj);
                        }
                        catch
                        {
                            continue;
                        }
                        string name = path + field.GetName();
                        if (value is float f && Odd(f) is string kind)
                        {
                            string key = $"{fileName}: {obj.GetType().Name}.{name}";
                            var (count, example) = found.GetValueOrDefault(key);
                            found[key] = (count + 1, example ?? $"{objectText} = {f} ({kind})");
                        }
                        else if (value is igObjectBase inner && value is not igObject && depth < 3)
                        {
                            Scan(inner, objectText, name + ".", depth + 1);
                        }
                    }
                }
                foreach (igObject obj in igz.Objects)
                {
                    try
                    {
                        Scan(obj, obj.ObjectName ?? obj.GetType().Name, "", 0);
                    }
                    catch
                    {
                        // un oggetto che non si esplora non cambia il resto
                    }
                }
                // Scale nulle, enormi o negative delle entita' (dalla v58): un modello schiacciato a zero su un asse ha normali
                // impossibili (NaN nella luce, che poi si allarga a tutto lo schermo), una scala negativa lo ribalta
                foreach (igEntity entity in igz.Objects.OfType<igEntity>())
                {
                    igVec3fMetaField? scale = entity._transform?._nonUniformPersistentParentSpaceScale;
                    if (scale == null) continue;
                    float smallest = Math.Min(Math.Abs(scale._x), Math.Min(Math.Abs(scale._y), Math.Abs(scale._z)));
                    float largest = Math.Max(Math.Abs(scale._x), Math.Max(Math.Abs(scale._y), Math.Abs(scale._z)));
                    bool negative = scale._x < 0 || scale._y < 0 || scale._z < 0;
                    if (smallest >= 0.01f && largest <= 100f && !negative) continue;
                    string why = smallest < 0.01f ? "quasi zero" : largest > 100f ? "enorme" : "negativa";
                    oddScales.Add($"{fileName}: {entity.ObjectName ?? "?"} scala {FormatFloat(scale._x)} {FormatFloat(scale._y)} {FormatFloat(scale._z)} ({why})");
                }
            }
            report.AppendLine($"  file controllati: {scannedFiles}" + (unreadable > 0 ? $" ({unreadable} non si rileggono)" : ""));
            if (found.Count == 0) report.AppendLine("  nessuno");
            foreach (var (key, (count, example)) in found.OrderByDescending(f => f.Value.count).Take(60))
                report.AppendLine($"  {key}: {count} volte, per esempio {example}");
            if (found.Count > 60) report.AppendLine($"  ... e altri {found.Count - 60}");
            report.AppendLine($"  scale delle entita' quasi zero (sotto 0,01), enormi (sopra 100) o negative: {(oddScales.Count == 0 ? "nessuna" : oddScales.Count.ToString())}");
            foreach (string scaleLine in oddScales.Take(40)) report.AppendLine("    " + scaleLine);
            if (oddScales.Count > 40) report.AppendLine($"    ... e altre {oddScales.Count - 40}");
            report.AppendLine();
        }

        /// <summary>
        /// Rapporto (--descrivi <testo>, dalla v59): gli oggetti dei file dell'archivio con il testo nel percorso, con tipo, nome e
        /// campi, e l'archivio Switch da cui viene il file: per vedere com'e' fatto un file che da' problemi (un materiale, un modello)
        /// </summary>
        private static void AppendDescribedFiles(StringBuilder report, IgArchive output, List<string> texts, Dictionary<string, string> origins)
        {
            report.AppendLine("=== FILE DESCRITTI (--descrivi) ===");
            List<IgArchiveFile> chosen = output.Files
                .Where(f => f.IsIGZ() && texts.Any(t => f.Path.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (chosen.Count == 0)
                report.AppendLine($"  nessun file dell'archivio con {string.Join(" o ", texts.Select(t => "'" + t + "'"))} nel percorso");
            foreach (IgArchiveFile file in chosen.Take(40))
            {
                string origin = origins.GetValueOrDefault(file.Path.ToLowerInvariant()) ?? "convertito dal PC";
                report.AppendLine($"  {file.Path} ({Mb((ulong)file.UncompressedSize)}, {origin})");
                IgzFile igz;
                try
                {
                    igz = file.ToIgzFile();
                }
                catch (Exception readError)
                {
                    report.AppendLine($"    non leggibile: {readError.GetType().Name}: {readError.Message}");
                    continue;
                }
                foreach (igObject obj in igz.Objects.Take(150))
                {
                    string text;
                    try
                    {
                        text = DeepFieldsText(obj, igz.GameVersion, 0);
                    }
                    catch (Exception fieldsError)
                    {
                        text = $"campi non leggibili ({fieldsError.GetType().Name})";
                    }
                    if (text.Length > 1500) text = text.Substring(0, 1500) + " ...";
                    report.AppendLine($"    [{obj.GetType().Name}] {obj.ObjectName ?? "-"}{(obj.Reference != null ? " @" + RefText(obj.Reference) : "")}: {text}");
                }
                if (igz.Objects.Count > 150) report.AppendLine($"    ... e altri {igz.Objects.Count - 150} oggetti");
            }
            if (chosen.Count > 40) report.AppendLine($"  ... e altri {chosen.Count - 40} file");
            report.AppendLine();
        }

        /// <summary>
        /// --memoria in MB: "300" (area di memoria del livello) o "300/40" (livello e globale), in byte. False se non sono numeri
        /// (allora e' modello, max o il nome di un livello); error se i numeri sono fuori misura
        /// </summary>
        private static bool TryParseMemoryMb(string text, out ulong? level, out ulong? global, out string? error)
        {
            level = null;
            global = null;
            error = null;
            string[] parts = text.Trim().Split('/');
            if (parts.Length > 2 || parts.Any(p => p.Length == 0 || p.Length > 5 || !p.All(char.IsDigit))) return false;
            ulong levelMb = ulong.Parse(parts[0]);
            ulong? globalMb = parts.Length == 2 ? (ulong?)ulong.Parse(parts[1]) : null;
            if (levelMb < 50 || levelMb > 2000 || (globalMb != null && (globalMb < 10 || globalMb > 2000)))
            {
                error = "MB fuori misura (livello tra 50 e 2000, globale tra 10 e 2000; i livelli originali hanno 200/30)";
                return true;
            }
            level = levelMb * 1024UL * 1024UL;
            global = globalMb * 1024UL * 1024UL;
            return true;
        }

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
                report.AppendLine($"  ({otherLevels} altri livelli: l'editor copia per intero i file da cui prendi anche un solo oggetto; quelli in maps/ " +
                                  "restano fuori dal pacchetto, come nell'editor)");
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
                                          "potrebbe rallentare o chiudersi sulla Switch. Togli oggetti presi da altri livelli.");
                }
                report.AppendLine("Livelli originali piu' grandi: " +
                                  string.Join(", ", originals.Take(3).Select(o => $"{o.name} {Mb((ulong)o.size)}")));
                if (total > originals[0].size)
                    report.AppendLine("ATTENZIONE: il livello e' piu' grande di tutti i livelli originali.");
            }

            // Texture: sono quasi tutta la memoria di un livello che prende oggetti da tanti livelli (ognuno porta le sue texture).
            // Confronto con le texture degli archivi dei livelli originali e provenienza (archivio Switch che ha il file; uno che
            // sta in piu' archivi conta nel primo trovato), per capire quali file di altri livelli pesano di piu'
            static bool IsTexturePath(string path) =>
                path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("loosetextures/", StringComparison.OrdinalIgnoreCase);
            long textureTotal = files.Where(f => IsTexturePath(f.Path)).Sum(f => (long)f.UncompressedSize);
            var originalTextures = sw.ByArchive
                .Where(a => a.Key.Length > 5 && (a.Key[0] == 'l' || a.Key[0] == 'b') && char.IsDigit(a.Key[1]) && a.Key[4] == '_')
                .Select(a => (name: a.Key, size: a.Value.Where(f => IsTexturePath(f.Path)).Sum(f => (long)f.UncompressedSize)))
                .OrderByDescending(a => a.size)
                .ToList();
            int textureCount = files.Count(f => IsTexturePath(f.Path));
            report.AppendLine($"Texture del livello: {Mb((ulong)textureTotal)} ({textureCount} file).");
            // texture che stanno anche in archivi che non sono di un livello (comuni): il gioco potrebbe averle gia' caricate
            static bool IsLevelArchiveName(string name) => name.Length > 5 && (name[0] == 'l' || name[0] == 'b') && char.IsDigit(name[1]) && name[4] == '_';
            var commonPaths = new HashSet<string>(sw.ByArchive.Where(a => !IsLevelArchiveName(a.Key)).SelectMany(a => a.Value).Select(f => f.Path.ToLowerInvariant()));
            List<IgArchiveFile> commonTextures = files.Where(f => IsTexturePath(f.Path) && commonPaths.Contains(f.Path.ToLowerInvariant())).ToList();
            if (commonTextures.Count > 0)
                report.AppendLine($"  di queste anche negli archivi comuni del gioco (non di un livello; se il gioco le ha gia' caricate non pesano due " +
                                  $"volte): {Mb((ulong)commonTextures.Sum(f => (long)f.UncompressedSize))} ({commonTextures.Count} file)");
            if (originalTextures.Count > 0)
            {
                string? refKey = reference?.ToLowerInvariant();
                var refTextures = originalTextures.FirstOrDefault(o => o.name == refKey);
                if (refKey != null && refTextures.name == refKey)
                    report.AppendLine($"  texture nell'archivio di {reference}: {Mb((ulong)refTextures.size)}");
                report.AppendLine("  livelli originali con piu' texture nel loro archivio: " +
                                  string.Join(", ", originalTextures.Take(5).Select(o => $"{o.name} {Mb((ulong)o.size)}")));
                if (textureTotal > originalTextures[0].size)
                    report.AppendLine("  ATTENZIONE: il livello ha piu' texture di tutti i livelli originali: la memoria per le texture potrebbe non bastare " +
                                      "(schermo nero o caricamento bloccato). Togli decorazioni di altri livelli (--togli-entita, --escludi)");
            }
            var textureOrigins = new Dictionary<string, (long size, int count)>();
            foreach (IgArchiveFile texture in files.Where(file => IsTexturePath(file.Path)))
            {
                string origin = sw.ByPath.TryGetValue(texture.Path.ToLowerInvariant(), out IgArchiveFile? original) &&
                                sw.ArchiveOf.TryGetValue(original, out string? archive) ? archive : "solo nell'archivio PC";
                var (size, count) = textureOrigins.GetValueOrDefault(origin);
                textureOrigins[origin] = (size + (long)texture.UncompressedSize, count + 1);
            }
            if (textureOrigins.Count > 0)
                report.AppendLine("  archivio Switch da cui sono prese (una che sta in piu' archivi conta nel primo in ordine alfabetico: non e' il " +
                                  "livello da cui viene la decorazione, per quello vedi TEXTURE PER FILE): " +
                                  string.Join(", ", textureOrigins.OrderByDescending(t => t.Value.size).Take(15)
                                                                  .Select(t => $"{t.Key} {Mb((ulong)t.Value.size)} ({t.Value.count})")));
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

        /// <summary>
        /// Il livello nell'editor e' con la moto d'acqua (opzione {jetski} o veicolo moto d'acqua nella zone info del PC)
        /// </summary>
        private static bool PcZoneInfoUsesJetski(List<IgArchiveFile> files)
        {
            foreach (IgArchiveFile file in files)
            {
                if (!file.Path.EndsWith("_zoneinfo.igz", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (file.ToIgzFile().FindObject<CZoneInfo>() is CZoneInfo zone)
                        return GameplayModeManager.GetSpecialZoneInfoOptions(zone._build).Contains("jetski") ||
                               (zone._zoneVehicle ?? "").Contains("jetski", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    // zone info non leggibile: senza moto d'acqua
                }
            }
            return false;
        }

        /// <summary>
        /// Modalita' scelte nell'editor: le opzioni speciali della zone info del PC ({bear}, {boulder}, {swim}...) e il
        /// veicolo (moto d'acqua, aereo)
        /// </summary>
        private static HashSet<string> PcZoneInfoModes(List<IgArchiveFile> files)
        {
            var modes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IgArchiveFile file in files)
            {
                if (!file.Path.EndsWith("_zoneinfo.igz", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (file.ToIgzFile().FindObject<CZoneInfo>() is not CZoneInfo zone) continue;
                    foreach (string option in GameplayModeManager.GetSpecialZoneInfoOptions(zone._build)) modes.Add(option.ToLowerInvariant());
                    string vehicle = zone._zoneVehicle ?? "";
                    if (vehicle.Contains("jetski", StringComparison.OrdinalIgnoreCase)) modes.Add("jetski");
                    if (vehicle.Contains("plane", StringComparison.OrdinalIgnoreCase)) modes.Add("plane");
                    return modes;
                }
                catch
                {
                    // zone info non leggibile: si prova la prossima
                }
            }
            return modes;
        }

        /// <summary>
        /// Livello da cui viene un file di un altro livello (maps/&lt;gioco&gt;/&lt;livello&gt;/... o maps/&lt;gioco&gt;/Bosses/&lt;boss&gt;/...)
        /// e le sue modalita', come le assegna l'editor (GameplayModeManager.GetSpecialLevelMode; Un-Bearable ha orso e
        /// masso). I boss contano come una modalita' a parte, che un livello non ha mai. Null per i livelli normali e le mappe
        /// </summary>
        private static (string level, HashSet<string> modes)? SourceLevelModes(string path)
        {
            string[] parts = path.Split('/');
            if (parts.Length < 4 || !parts[0].Equals("maps", StringComparison.OrdinalIgnoreCase)) return null;
            if (parts[2].Equals("Bosses", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 5) return null;
                return (parts[3], new HashSet<string> { "boss" });
            }

            string level = parts[2];
            if (level.Length < 4) return null;
            string id = level.Substring(0, 4).ToUpperInvariant();
            var modes = new HashSet<string>();
            string? editorMode = GameplayModeManager.GetSpecialLevelMode(id);
            if (editorMode != null && editorMode != "hub") modes.Add(editorMode);
            if (id is "L104" or "L113" or "L205" or "L209" or "L215") modes.Add("boulder");
            if (modes.Count == 0) return null;
            return (level, modes);
        }

        /// <summary>
        /// Entita' di altri livelli che, trovate con le prove, tengono il gioco su LOADING: testo nel nome (o nei file che usa),
        /// descrizione per il rapporto e modalita' del livello con cui restano (null: si tolgono sempre, anche in un livello con
        /// la loro modalita'). Solo dai file di altri livelli: nei file del livello stesso possono essere quelle di un livello
        /// originale modificato, dove funzionano
        /// </summary>
        private static readonly (string fragment, string description, string? keptWithMode)[] _knownBlockingEntities =
        [
            // Level 10 (v40): si carica solo togliendo i pinguini fermi di Snow Go (le foche e gli altri pinguini restano)
            ("arctic_enemy_immobile_penguin", "pinguini fermi di Snow Go", null),
            // Level A2 (v61): The Lab senza modalita' resta su LOADING con i cancelli elettrici di Crash Dash (livello del masso);
            // togliendo solo quelli si carica (le mine che rimbalzano dello stesso file restano). In un livello con il masso,
            // come in Crash Dash, restano (non provato)
            ("jungle_hazard_electricgate", "cancelli elettrici di Crash Dash", "boulder"),
        ];

        /// <summary>
        /// Descrizione per il rapporto se l'entita' e' una di quelle che bloccano il caricamento (_knownBlockingEntities) e va
        /// tolta da un livello con queste modalita', altrimenti null
        /// </summary>
        private static string? KnownBlockingEntity(igEntity entity, HashSet<string> references, HashSet<string> levelModes)
        {
            string name = entity.ObjectName ?? "";
            foreach (var (fragment, description, keptWithMode) in _knownBlockingEntities)
            {
                if (keptWithMode != null && levelModes.Contains(keptWithMode)) continue;
                if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase) ||
                    references.Any(r => r.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                    return keptWithMode == null ? description : $"{description}, senza la modalita' {ModeDescription(keptWithMode)}";
            }
            return null;
        }

        private static string FormatVector(THREE.Vector3 v) =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v.X:0.#} {v.Y:0.#} {v.Z:0.#}");

        /// <summary>
        /// --pavimento-blocchi: copre ogni blocco di collisione del file con una copia di una decorazione (igEntity con modello e
        /// CStaticComponentData) presa dai file del livello: con il testo nel nome se indicato, altrimenti la prima con "floor" o
        /// "ice" nel nome dello stesso livello del file, poi di qualunque livello. Il modello della copia, ingrandito, copre il box
        /// del blocco (da _min a _max nello spazio dell'entita'); la sua collisione e' spenta. Torna le righe per il rapporto
        /// </summary>
        private static List<string> AddFloorOverClips(IgzFile clipIgz, string clipPath, string templateText, IgArchive pc, ArchiveIndex sw)
        {
            var result = new List<string>();
            List<CDynamicClipEntity> clips = clipIgz.Objects.OfType<CDynamicClipEntity>().ToList();
            if (clips.Count == 0) return result;

            // decorazioni candidate, dai file del PC (letti come li legge l'editor): prima quelli dello stesso livello
            string[] clipParts = clipPath.Split('/');
            string sameLevel = clipParts.Length >= 3 ? $"maps/{clipParts[1]}/{clipParts[2]}/" : "";
            var candidateFiles = pc.Files
                .Where(f => f.IsIGZ() && f.Path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) &&
                            !f.Path.Equals(clipPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => sameLevel.Length > 0 && f.Path.StartsWith(sameLevel, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
            (IgzFile igz, igEntity entity, string file, string model, THREE.Vector3 min, THREE.Vector3 max)? chosen = null;
            int bestRank = int.MaxValue;
            var tried = new List<string>();
            // modelli Switch per nome (_fileName puo' essere un percorso o solo il nome): la copia si vede solo se la Switch ha
            // il suo modello
            var modelsByName = new Dictionary<string, IgArchiveFile>();
            foreach (var (path, file) in sw.ByPath)
                if (path.StartsWith("models/") && path.EndsWith(".igz")) modelsByName.TryAdd(NamespaceUtils.GetFileName(path, false), file);
            int seen = 0, withModel = 0, modelMissing = 0, noModelData = 0, badBounds = 0;
            var missingExamples = new List<string>();
            var componentExamples = new SortedSet<string>(StringComparer.Ordinal);
            foreach (IgArchiveFile candidateFile in candidateFiles)
            {
                IgzFile candidateIgz;
                try
                {
                    candidateIgz = candidateFile.ToIgzFile();
                }
                catch
                {
                    continue;
                }
                foreach (igEntity entity in candidateIgz.Objects.OfType<igEntity>().Where(e => e.GetType() == typeof(igEntity)))
                {
                    string name = entity.ObjectName ?? "";
                    seen++;
                    if (componentExamples.Count < 6)
                        componentExamples.Add(string.Join("+", entity.GetComponents().Select(c => c.GetType().Name)));
                    string? fileName = entity.GetComponent<CModelComponentData>()?._fileName;
                    if (string.IsNullOrWhiteSpace(fileName)) continue;
                    withModel++;
                    bool isStatic = entity.GetComponent<CStaticComponentData>() != null;
                    int rank;
                    if (templateText.Length > 0)
                    {
                        if (!name.Contains(templateText, StringComparison.OrdinalIgnoreCase)) continue;
                        rank = 0;
                    }
                    // "Ice" e "Floor" con la maiuscola (IceFloor, CliffFloor), non "Lattice" o "Office"
                    else rank = name.Contains("Ice", StringComparison.Ordinal) ? 0 : name.Contains("Floor", StringComparison.Ordinal) ? 1 : 2;
                    if (candidateFile.Path.StartsWith(sameLevel, StringComparison.OrdinalIgnoreCase) == false) rank += 3;
                    if (!isStatic) rank += 6;  // meglio le decorazioni statiche, come quelle del livello
                    if (rank >= bestRank) continue;

                    // misure del modello (dal modello nell'archivio o nel gioco Switch)
                    string? modelPath = ModelPathFromName(fileName);
                    IgArchiveFile? modelFile = modelPath == null ? null : sw.ByPath.GetValueOrDefault(modelPath);
                    modelFile ??= modelsByName.GetValueOrDefault(NamespaceUtils.GetFileName(fileName.Replace('\\', '/'), false).ToLowerInvariant());
                    if (modelFile == null)
                    {
                        modelMissing++;
                        if (missingExamples.Count < 3) missingExamples.Add(fileName);
                        continue;
                    }
                    try
                    {
                        if (modelFile.ToIgzFile().FindObject<igModelData>() is not igModelData modelData)
                        {
                            noModelData++;
                            continue;
                        }
                        var min = new THREE.Vector3(modelData._min._x, modelData._min._y, modelData._min._z);
                        var max = new THREE.Vector3(modelData._max._x, modelData._max._y, modelData._max._z);
                        if (max.X <= min.X || max.Y <= min.Y || max.Z <= min.Z)
                        {
                            badBounds++;
                            continue;
                        }
                        chosen = (candidateIgz, entity, candidateFile.Path, fileName, min, max);
                        bestRank = rank;
                    }
                    catch
                    {
                        if (tried.Count < 5) tried.Add(NamespaceUtils.GetFileName(fileName));
                    }
                }
                if (bestRank == 0) break;
            }
            if (chosen == null)
            {
                result.Add($"  ATTENZIONE --pavimento-blocchi, {clipPath}: nessuna decorazione da copiare" +
                           (templateText.Length > 0 ? $" con '{templateText}' nel nome" : "") +
                           (tried.Count > 0 ? $" (modelli non leggibili: {string.Join(", ", tried)})" : "") + ": blocchi lasciati invisibili");
                result.Add($"    file guardati {candidateFiles.Count}, entita' semplici {seen}, con modello {withModel}, " +
                           $"modello non trovato {modelMissing}{(missingExamples.Count > 0 ? $" (es. {string.Join(", ", missingExamples)})" : "")}, " +
                           $"senza igModelData {noModelData}, misure non valide {badBounds}");
                if (componentExamples.Count > 0) result.Add($"    componenti delle entita' semplici (esempi): {string.Join("; ", componentExamples)}");
                return result;
            }

            var template = chosen.Value;
            THREE.Vector3 modelSize = template.max - template.min;
            THREE.Vector3 modelCenter = (template.min + template.max) * 0.5f;
            int added = 0;
            foreach (CDynamicClipEntity clip in clips)
            {
                THREE.Vector3 min = clip._min.ToVector3();
                THREE.Vector3 max = clip._max.ToVector3();
                THREE.Vector3 size = max - min;
                if (size.X <= 0 || size.Y <= 0 || size.Z <= 0) continue;
                THREE.Vector3 center = (min + max) * 0.5f;
                THREE.Vector3 entityScale = clip._transform?._nonUniformPersistentParentSpaceScale.ToVector3() ?? new THREE.Vector3(1, 1, 1);
                igVec3fMetaField clipRotation = clip._transform?._parentSpaceRotation ?? new igVec3fMetaField(0, 0, 0);
                THREE.Quaternion rotation = clipRotation.ToQuaternion();
                THREE.Vector3 k = MathUtils.SafeDivide(size, modelSize);
                THREE.Vector3 offset = (entityScale * (center - k * modelCenter)).ApplyQuaternion(rotation);

                // ogni copia con i suoi dati (un dizionario nuovo: con lo stesso tornerebbe la copia gia' fatta)
                igEntity copy = clipIgz.AddClone(template.entity, template.igz, new Dictionary<igObject, igObject>());
                copy._parentSpacePosition = (clip._parentSpacePosition.ToVector3() + offset).ToVec3MetaField();
                copy._transform ??= new igEntityTransform();
                copy._transform._parentSpaceRotation = new igVec3fMetaField(clipRotation._x, clipRotation._y, clipRotation._z);
                copy._transform._nonUniformPersistentParentSpaceScale = (entityScale * k).ToVec3MetaField();
                copy._transform._isDirty = true;
                if (copy.GetComponent<CStaticCollisionComponentData>() is CStaticCollisionComponentData collision)
                    collision._flagsBitfield._disableCollision = true;
                added++;
            }
            if (added == 0) return result;
            result.Add($"  pavimento visibile, {clipPath}: {added} blocchi coperti da copie di {template.entity.ObjectName} " +
                       $"({NamespaceUtils.GetFileName(template.file)}, modello {template.model}, misure {FormatVector(modelSize)})");
            return result;
        }

        /// <summary>
        /// Percorso (minuscolo) del modello indicato nei dati di un'entita' ("Levels\Crash1\crates\Crash_Crate_Nitro" ->
        /// models/levels/crash1/crates/crash_crate_nitro.igz); null se vuoto
        /// </summary>
        private static string? ModelPathFromName(string? name, string folder = "models")
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string p = name.Trim().Replace('\\', '/');
            int colon = p.IndexOf(':');
            if (colon >= 0) p = p.Substring(colon + 1);
            p = p.TrimStart('/');
            if (p.Length == 0) return null;
            if (!p.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) p = folder + "/" + p;
            // l'estensione del formato di partenza (.igb) diventa .igz, come nei file del gioco
            if (p.EndsWith(".igb", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 4);
            if (!p.EndsWith(".igz", StringComparison.OrdinalIgnoreCase)) p += ".igz";
            return p.ToLowerInvariant();
        }

        private static string ModeDescription(string mode) => mode switch
        {
            "swim" => "sub",
            "tiger" => "tigre",
            "bear" => "orso",
            "hog" => "cinghiale",
            "bike" => "moto",
            "jetski" => "moto d'acqua",
            "plane" => "aereo",
            "jetpack" => "jetpack",
            "dig" => "scavo",
            "boulder" => "masso",
            "boss" => "boss",
            _ => mode
        };

        private static string DescribeZoneInfo(CZoneInfo zone)
        {
            return $"nome '{zone._name}', anno {zone._year}, build '{zone._build}', personaggio '{zone._overrideCharacter}', " +
                   $"veicolo '{zone._zoneVehicle}', intro {zone._flags._magicMomentIntro}, boss {zone._flags._isBoss}, menu {zone._flags._isMenu}, " +
                   $"salvataggio '{zone._saveName}', caricamento '{zone._loadScreenName}'/'{zone._loadMovieName}', " +
                   $"memoria livello {Mb(zone._levelPoolSize?._size ?? 0)}, globale {Mb(zone._globalChunkPoolSize?._size ?? 0)}, " +
                   $"riflessi planari {(zone._planarReflectionsEnabled ? "si'" : "no")}";
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

        // ------------------------------------------------------------------ tessere dei bonus di Crash 1

        /// <summary>
        /// Il livello ha le tessere dei bonus di Crash 1: teletrasporti (common_BonusRoundTeleporterData) o tessere
        /// (common_Collectible_Bonus_SpawnedData) nei file maps/ dell'archivio
        /// </summary>
        private static bool LevelHasCrash1Tokens(IgArchive output)
        {
            foreach (IgArchiveFile file in output.Files)
            {
                if (!file.Path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) || !file.Path.EndsWith(".igz", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    byte[] data = file.Uncompress();
                    if (!ContainsAscii(data, "Bonus")) continue;
                    IgzFile igz = new IgzFile(file.Path, data, file.GameVersion);
                    if (igz.Objects.Any(o => o is common_BonusRoundTeleporterData || o is common_Collectible_Bonus_SpawnedData)) return true;
                }
                catch
                {
                    // file non leggibile: non conta
                }
            }
            return false;
        }

        /// <summary>
        /// Nome di una funzione del gioco usata da un nodo VSC (CCrashBandicootNodes::checkCrashGame_VSC, ...)
        /// </summary>
        private static string FunctionName(igMetaFunction? function) =>
            function == null ? "" : function._functionName ?? function.Reference?.objectName ?? function.ObjectName ?? "";

        /// <summary>
        /// Gestore delle tessere di Crash 1 (vsc/common/Characters/Crash1_BonusRoundManager_c.igz): all'avvio (metodo
        /// startOnlyOnce) chiede se il gioco e' Crash 1 (CCrashBandicootNodes::checkCrashGame) e negli altri giochi cerca il
        /// suo componente e lo toglie (findComponent2, removeComponent). Nella copia per update.pak quel ramo si stacca: il
        /// gestore resta, conta le tessere, al terzo accende il teletrasporto. Nei livelli senza tessere non fa niente (nessuno
        /// gli manda i messaggi delle tessere). Lo script si riscrive con le sue dipendenze e si controlla rileggendolo
        /// </summary>
        private static bool PatchBonusTokenManager(ArchiveIndex sw, IgArchive update, List<(string path, byte[] data)> embedded,
                                                   StringBuilder report, EGameYear? levelYear)
        {
            IgArchiveFile? source = sw.ByPath.Where(e => e.Key.StartsWith("vsc/") &&
                                                         NamespaceUtils.GetFileName(e.Key, false).Equals("crash1_bonusroundmanager_c", StringComparison.OrdinalIgnoreCase))
                                             .OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Value).FirstOrDefault();
            if (source == null)
            {
                report.AppendLine("  ERRORE tessere dei bonus: Crash1_BonusRoundManager_c.igz non trovato nel dump, le tessere non si contano");
                return false;
            }
            static bool RemovesItself(igVscActionNode? node) =>
                node is igVscCppActionNode find && FunctionName(find._metaFunction).Contains("findComponent", StringComparison.OrdinalIgnoreCase) &&
                find._outNodes != null &&
                find._outNodes._data.Any(n => n is igVscCppActionNode remove &&
                                              FunctionName(remove._metaFunction).Contains("removeComponent", StringComparison.OrdinalIgnoreCase));
            static List<igVscCppActionNode> GameChecks(IgzFile igz) =>
                igz.Objects.OfType<igVscCppActionNode>()
                   .Where(n => FunctionName(n._metaFunction).Contains("checkCrashGame", StringComparison.OrdinalIgnoreCase)).ToList();
            try
            {
                byte[] original = source.Uncompress();
                IgzFile igz = new IgzFile(source.Path, original, source.GameVersion);
                int objects = igz.Objects.Count;
                int cut = 0;
                foreach (igVscCppActionNode check in GameChecks(igz))
                {
                    if (check._outNodes == null) continue;
                    var outs = check._outNodes._data;
                    for (int i = 0; i < outs.Count; i++)
                    {
                        if (!RemovesItself(outs[i])) continue;
                        outs[i] = null!;  // uscita scollegata, come quelle che nei grafi non portano a niente
                        cut++;
                    }
                }
                if (cut == 0)
                {
                    report.AppendLine($"  ERRORE tessere dei bonus: in {source.Path} non c'e' il controllo del gioco che toglie il gestore " +
                                      "(script diverso da quello conosciuto): non cambiato, le tessere non si contano");
                    return false;
                }
                igz.GameVersion = GameVersion.NSX;
                byte[] data = igz.Save(includeDependencies: true);

                // Controllo: lo script riscritto si rilegge, ha gli stessi oggetti e nessun controllo del gioco porta piu' a togliersi
                var again = new IgzFile(source.Path, data, GameVersion.NSX);
                List<igVscCppActionNode> checks = GameChecks(again);
                bool stillRemoves = checks.Any(n => n._outNodes != null && n._outNodes._data.Any(o => RemovesItself(o)));
                if (checks.Count == 0 || stillRemoves || again.Objects.OfType<igVscMetaObject>().Count() != igz.Objects.OfType<igVscMetaObject>().Count())
                {
                    report.AppendLine($"  ERRORE tessere dei bonus: lo script riscritto non si rilegge come previsto ({checks.Count} controlli, " +
                                      $"ramo ancora presente: {(stillRemoves ? "si'" : "no")}): non usato, le tessere non si contano");
                    return false;
                }
                ReplaceFile(update, source.Path, data);
                embedded.Add((source.Path, data));
                report.AppendLine($"  tessere dei bonus di Crash 1 in un livello di {levelYear?.ToString() ?? "un altro gioco"}: {source.Path} in update.pak " +
                                  $"con il gestore delle tessere che resta anche fuori da Crash 1 ({cut} ramo staccato, {objects} -> {again.Objects.Count} oggetti, " +
                                  $"{original.Length} -> {data.Length} byte); --tessere-solo-crash1 lo lascia com'e'");
                return true;
            }
            catch (Exception e)
            {
                report.AppendLine($"  ERRORE tessere dei bonus: {source.Path}: {e.GetType().Name}: {e.Message} (le tessere non si contano)");
                return false;
            }
        }

        /// <summary>
        /// Script VSC del dump Switch dal nome del file (vsc/..., senza estensione)
        /// </summary>
        private static IgArchiveFile? FindVscFile(ArchiveIndex sw, string name) =>
            sw.ByPath.Where(e => e.Key.StartsWith("vsc/") && NamespaceUtils.GetFileName(e.Key, false).Equals(name, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Value).FirstOrDefault();

        /// <summary>
        /// Nome di un tipo del gioco usato da un nodo VSC (messaggio, evento)
        /// </summary>
        private static string MetaName(igMetaObject? meta) =>
            meta == null ? "" : meta._name ?? meta.Reference?.objectName ?? meta.ObjectName ?? "";

        /// <summary>
        /// Contatori delle tessere (C1_BonusCount_Tawna, _Brio, _Cortex) come li usa il gestore delle tessere nei dati di Crash;
        /// se non si leggono, i nomi conosciuti in gamevariables
        /// </summary>
        private static List<NamedReference> TokenCounters(ArchiveIndex sw)
        {
            foreach (var (path, file) in sw.ByPath.Where(e => e.Key.EndsWith("crash_characterdata.igz")).OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                try
                {
                    Crash_BonusRoundManagerData? manager = file.ToIgzFile().Objects.OfType<Crash_BonusRoundManagerData>().FirstOrDefault();
                    if (manager == null) continue;
                    var counters = new List<NamedReference>();
                    foreach (igHandleMetaField handle in new[] { manager._Game_Int_Variable_0x60, manager._Game_Int_Variable_0x68, manager._Game_Int_Variable_0x70 })
                        if (handle.Reference != null && handle.Reference.objectName.StartsWith("C1_BonusCount", StringComparison.OrdinalIgnoreCase))
                            counters.Add(handle.Reference.Clone());
                    if (counters.Count == 3) return counters;
                }
                catch
                {
                    // si usano i nomi conosciuti
                }
            }
            return
            [
                new NamedReference("gamevariables", "C1_BonusCount_Tawna"),
                new NamedReference("gamevariables", "C1_BonusCount_Brio"),
                new NamedReference("gamevariables", "C1_BonusCount_Cortex"),
            ];
        }

        /// <summary>
        /// HUD di Crash (vsc/common/CrashBandicoot_HUD_c.igz, componente di Crash e Coco). Quando prendi qualcosa mostra in alto la
        /// riga dei collezionabili: quella di Crash 1, con le tessere (evento dell'HUD gk33u62b), solo se il gioco e' Crash 1
        /// (CDebugNodes::debugCheckBuildYear 7), negli altri giochi quella di Crash 2 e 3 (civ7ec21: gemme, cristalli). Nella copia
        /// per update.pak, fuori da Crash 1, se c'e' almeno una tessera presa (C1_BonusCount_Tawna, _Brio o _Cortex maggiore di 0) si
        /// mostra la riga di Crash 1 al posto di quella di Crash 2 e 3 (stanno tutte e due in alto: insieme potrebbero coprirsi), e
        /// nascondendo si nascondono tutte e due (qfcoru5s, 9rlqk1cy). Nei livelli di Crash 2 e 3 senza tessere i contatori restano a 0
        /// e l'HUD fa quello che faceva; in Crash 1 non cambia niente. Gli accessori nuovi prendono le opzioni (_isHandle) da quelli
        /// dello stesso tipo nel file. Lo script si riscrive con le sue dipendenze (piu' GameVariables) e si controlla rileggendolo
        /// </summary>
        private static bool PatchBonusTokenHud(ArchiveIndex sw, IgArchive update, List<(string path, byte[] data)> embedded, StringBuilder report)
        {
            IgArchiveFile? source = FindVscFile(sw, "crashbandicoot_hud_c");
            if (source == null)
            {
                report.AppendLine("  ERRORE HUD delle tessere: CrashBandicoot_HUD_c.igz non trovato nel dump, le tessere prese non si vedono in alto");
                return false;
            }
            static igVscCppActionNode? YearCheck(IgzFile file, string delegatorName)
            {
                igVscDelegator? delegator = file.Objects.OfType<igVscDelegator>().FirstOrDefault(d => d.ObjectName == delegatorName);
                igVscEntryPointNode? entry = delegator?._method?._nodes?._data.FirstOrDefault(n => n != null);
                return entry?._node is igVscCppActionNode check &&
                       FunctionName(check._metaFunction).Contains("debugCheckBuildYear", StringComparison.OrdinalIgnoreCase) ? check : null;
            }
            static bool SendsEvents(igVscCppActionNode? check, string first, string second) =>
                check?._outNodes != null && check._outNodes._data.Count == 2 &&
                check._outNodes._data[0] is igGuiVscSendEventNode a && MetaName(a._messageMetaObject).EndsWith(first, StringComparison.OrdinalIgnoreCase) &&
                check._outNodes._data[1] is igGuiVscSendEventNode b && MetaName(b._messageMetaObject).EndsWith(second, StringComparison.OrdinalIgnoreCase);
            static bool CountsTokens(igObject? node) =>
                node is igVscCompareIntNode compare && compare._a is igVscIndirectIntPropertyAccessor property &&
                property._object is igVscConstObjectAccessor counter &&
                counter._value.Reference?.objectName.StartsWith("C1_BonusCount", StringComparison.OrdinalIgnoreCase) == true;
            try
            {
                byte[] original = source.Uncompress();
                IgzFile igz = new IgzFile(source.Path, original, source.GameVersion);
                int objects = igz.Objects.Count;
                igVscCppActionNode? show = YearCheck(igz, "GraphEventListener_ShowBonus");
                igVscCppActionNode? hide = YearCheck(igz, "GraphEventListener_HideBonus");
                if (show == null || hide == null || !SendsEvents(show, "gk33u62b", "civ7ec21") || !SendsEvents(hide, "qfcoru5s", "9rlqk1cy"))
                {
                    report.AppendLine($"  ERRORE HUD delle tessere: in {source.Path} i controlli del gioco per mostrare e nascondere la riga dei " +
                                      "collezionabili non sono quelli conosciuti: non cambiato, le tessere prese non si vedono in alto");
                    return false;
                }
                igVscIndirectIntPropertyAccessor? sample = igz.Objects.OfType<igVscIndirectIntPropertyAccessor>()
                    .FirstOrDefault(a => FunctionName(a._get).Contains("CGameIntVariable::get_Value", StringComparison.OrdinalIgnoreCase) && a._set != null);
                if (sample == null)
                {
                    report.AppendLine($"  ERRORE HUD delle tessere: in {source.Path} non c'e' una lettura di variabile intera da copiare: non cambiato");
                    return false;
                }

                // opzioni degli accessori come quelli dello stesso tipo nel file (un riferimento costante a un oggetto, un numero)
                igVscConstObjectAccessor? constSample = igz.Objects.OfType<igVscConstObjectAccessor>().FirstOrDefault(a => a._value.Reference != null);
                igVscConstIntAccessor? intSample = igz.Objects.OfType<igVscConstIntAccessor>().FirstOrDefault();

                List<NamedReference> counters = TokenCounters(sw);
                MemoryPool pool = show.MemoryPool;
                var showOuts = show._outNodes!._data;
                var hideOuts = hide._outNodes!._data;
                igVscActionNode c1Show = showOuts[0], otherShow = showOuts[1], c1Hide = hideOuts[0], otherHide = hideOuts[1];

                // tessere > 0 (Tawna, altrimenti Brio, altrimenti Cortex): riga di Crash 1; nessuna: riga di Crash 2 e 3, come prima
                igVscActionNode chain = otherShow;
                for (int i = counters.Count - 1; i >= 0; i--)
                {
                    var compare = new igVscCompareIntNode
                    {
                        ObjectName = igz.FindSuitableName("Compare_" + counters[i].objectName + "_conv"),
                        MemoryPool = pool,
                        _a = new igVscIndirectIntPropertyAccessor
                        {
                            MemoryPool = pool,
                            _isHandle = sample._isHandle,
                            _isDelegatorTarget = sample._isDelegatorTarget,
                            _object = new igVscConstObjectAccessor
                            {
                                MemoryPool = pool,
                                _isHandle = constSample?._isHandle ?? false,
                                _isDelegatorTarget = constSample?._isDelegatorTarget ?? false,
                                _value = new igHandleMetaField { Reference = counters[i].Clone() },
                            },
                            _get = sample._get,
                            _set = sample._set,
                        },
                        _b = new igVscConstIntAccessor
                        {
                            MemoryPool = pool,
                            _isHandle = intSample?._isHandle ?? false,
                            _isDelegatorTarget = intSample?._isDelegatorTarget ?? false,
                            _value = 0,
                        },
                        _greaterThan = c1Show,
                        _lessThanOrEqualTo = chain,
                    };
                    igz.Objects.Add(compare);
                    chain = compare;
                }
                var hideSequence = new igVscSequenceNode
                {
                    ObjectName = igz.FindSuitableName("Sequence_HideBonus_C1Tokens_conv"),
                    MemoryPool = pool,
                    _out1 = otherHide,
                    _out2 = c1Hide,
                };
                igz.Objects.Add(hideSequence);
                showOuts[1] = chain;
                hideOuts[1] = hideSequence;

                // GameVariables tra le dipendenze, come negli script che usano le variabili del gioco (il gestore delle tessere)
                IgArchiveFile? managerFile = FindVscFile(sw, "crash1_bonusroundmanager_c");
                if (managerFile != null && !igz.Dependencies.Any(d => d.path != null && d.path.Contains("GameVariables", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var managerIgz = new IgzFile(managerFile.Path, managerFile.Uncompress(), managerFile.GameVersion);
                        foreach (TDEP_Fixup.TDEP_Item item in managerIgz.Dependencies.Where(d => d.path != null && d.path.Contains("GameVariables", StringComparison.OrdinalIgnoreCase)))
                            igz.Dependencies.Add(item);
                    }
                    catch
                    {
                        // senza: GameVariables e' comunque caricato all'avvio (permanent)
                    }
                }

                igz.GameVersion = GameVersion.NSX;
                byte[] data = igz.Save(includeDependencies: true);

                // Controllo: rileggendo, fuori da Crash 1 si controllano le tessere (riga di Crash 1 se ce n'e' una, poi quella di
                // Crash 2 e 3)
                static bool ShowChainOk(igVscActionNode? node, int expected)
                {
                    int found = 0;
                    while (node is igVscCompareIntNode compare && CountsTokens(compare))
                    {
                        if (compare._greaterThan is not igGuiVscSendEventNode) return false;
                        found++;
                        node = compare._lessThanOrEqualTo;
                    }
                    return found == expected && node is igGuiVscSendEventNode;
                }
                var again = new IgzFile(source.Path, data, GameVersion.NSX);
                igVscCppActionNode? showAgain = YearCheck(again, "GraphEventListener_ShowBonus");
                igVscCppActionNode? hideAgain = YearCheck(again, "GraphEventListener_HideBonus");
                bool showOk = showAgain?._outNodes != null && showAgain._outNodes._data.Count == 2 &&
                              showAgain._outNodes._data[0] is igGuiVscSendEventNode &&
                              ShowChainOk(showAgain._outNodes._data[1], counters.Count);
                bool hideOk = hideAgain?._outNodes != null && hideAgain._outNodes._data.Count == 2 &&
                              hideAgain._outNodes._data[1] is igVscSequenceNode newHide && newHide._out1 is igGuiVscSendEventNode &&
                              newHide._out2 is igGuiVscSendEventNode;
                int countChecks = again.Objects.Count(o => CountsTokens(o));
                if (!showOk || !hideOk || countChecks != counters.Count ||
                    again.Objects.OfType<igVscMetaObject>().Count() != igz.Objects.OfType<igVscMetaObject>().Count())
                {
                    report.AppendLine($"  ERRORE HUD delle tessere: lo script riscritto non si rilegge come previsto (mostra {(showOk ? "ok" : "no")}, " +
                                      $"nascondi {(hideOk ? "ok" : "no")}, controlli delle tessere {countChecks}): non usato");
                    return false;
                }
                ReplaceFile(update, source.Path, data);
                embedded.Add((source.Path, data));
                report.AppendLine($"  HUD delle tessere: {source.Path} in update.pak, fuori da Crash 1 mostra la riga delle tessere di Crash 1 al posto " +
                                  $"di quella di Crash 2 e 3 quando ce n'e' almeno una ({string.Join(", ", counters.Select(c => c.objectName))}; " +
                                  $"{objects} -> {again.Objects.Count} oggetti, {original.Length} -> {data.Length} byte; opzioni degli accessori " +
                                  $"{(constSample != null ? "copiate" : "predefinite")}); --tessere-senza-hud lo lascia com'e'");
                return true;
            }
            catch (Exception e)
            {
                report.AppendLine($"  ERRORE HUD delle tessere: {source.Path}: {e.GetType().Name}: {e.Message} (le tessere prese non si vedono in alto)");
                return false;
            }
        }

        /// <summary>
        /// Intro di Crash 3 (analisi): intro e partenze del livello convertito, le stesse nei file principali dei livelli originali
        /// di Crash 3 (dove sta l'intro rispetto alla partenza, con quali valori) e lo script dell'intro scritto per esteso in
        /// <rapporto>_intro.txt. Restituisce il percorso di quel file, o null se non si e' potuto scrivere
        /// </summary>
        private static string? AppendC3IntroAnalysis(StringBuilder report, IgArchive output, ArchiveIndex sw, string reportPath, List<string> introLines)
        {
            // Livello convertito
            var levelLines = new List<string>();
            foreach (IgArchiveFile file in output.Files)
            {
                string lower = file.Path.ToLowerInvariant();
                if (!lower.StartsWith("maps/") || !lower.EndsWith(".igz")) continue;
                try
                {
                    byte[] data = file.Uncompress();
                    if (!ContainsAscii(data, "C3_IntroSequence") && !ContainsAscii(data, "CPlayerStartEntity")) continue;
                    DescribeIntroAndStarts(new IgzFile(file.Path, data, file.GameVersion), file.Path, levelLines);
                }
                catch (Exception e)
                {
                    levelLines.Add($"  {file.Path}: non letto ({e.Message})");
                }
            }
            if (levelLines.Count == 0) levelLines.Add("  nessuna intro e nessuna partenza nei file del livello");

            // Livelli originali di Crash 3 (file principale maps/crash3/<livello>/<livello>.igz), Gone Tomorrow per primo
            static bool IsC3MainFile(string path)
            {
                string[] parts = path.Split('/');
                return parts.Length == 4 && parts[0] == "maps" && parts[1] == "crash3" && parts[3] == parts[2] + ".igz";
            }
            var mainFiles = sw.ByPath.Where(e => IsC3MainFile(e.Key))
                                     .OrderBy(e => e.Key.Contains("/l321_") ? 0 : 1).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();
            var originalLines = new List<string>();
            int withIntro = 0, checkedFiles = 0;
            const int maxLevels = 8;
            foreach (var (path, file) in mainFiles)
            {
                if (withIntro >= maxLevels) break;
                checkedFiles++;
                try
                {
                    byte[] data = file.Uncompress();
                    if (!ContainsAscii(data, "C3_IntroSequence")) continue;
                    withIntro++;
                    DescribeIntroAndStarts(new IgzFile(file.Path, data, file.GameVersion), path, originalLines);
                }
                catch (Exception e)
                {
                    originalLines.Add($"  {path}: non letto ({e.Message})");
                }
            }
            if (originalLines.Count == 0) originalLines.Add("  nessuna (file principali dei livelli di Crash 3 non trovati nel dump)");

            // Script dell'intro e della sequenza, scritti per esteso
            var detail = new StringBuilder();
            detail.AppendLine("INTRO DI CRASH 3: LIVELLO, LIVELLI ORIGINALI E SCRIPT DEL GIOCO SWITCH");
            detail.AppendLine("Posizioni come nei file; rotazioni in gradi. \"nel sistema della partenza\": coordinate rispetto alla partenza");
            detail.AppendLine("(PlayerStart) girata come la partenza. Nomi dei valori dell'intro come nell'editor.");
            detail.AppendLine();
            detail.AppendLine("===== INTRO DEL LIVELLO CONVERTITO");
            foreach (string line in introLines) detail.AppendLine("  " + line);
            foreach (string line in levelLines) detail.AppendLine(line);
            detail.AppendLine();
            detail.AppendLine($"===== LIVELLI ORIGINALI DI CRASH 3 ({withIntro} con l'intro su {checkedFiles} guardati, al massimo {maxLevels})");
            foreach (string line in originalLines) detail.AppendLine(line);
            detail.AppendLine();
            // Come comincia un livello negli altri giochi (per l'intro di Crash 2): animazioni di comparsa dei personaggi, nomi
            // di animazioni ed eventi di comparsa nei file di Crash, effetti con portali o comparse, partenza di livelli di Crash 2
            var startLines = new List<string>();
            AppendOtherGamesStart(sw, startLines);
            detail.AppendLine("===== COME COMINCIA UN LIVELLO NEGLI ALTRI GIOCHI (per l'intro di Crash 2)");
            foreach (string line in startLines) detail.AppendLine(line);
            detail.AppendLine();
            detail.AppendLine("Ogni script: eventi (EVENTO: messaggio o funzione che lo fa partire), metodi, nodi. Ogni nodo ha un numero");
            detail.AppendLine("(#n), il tipo e i valori che legge: costanti, campi (.nome: dei dati dello script o di un oggetto del gioco),");
            detail.AppendLine("funzioni del gioco (fn), riferimenti (@file::oggetto). Sotto un nodo i suoi rami (nome:), poi il nodo che viene");
            detail.AppendLine("dopo allo stesso livello; un nodo gia' scritto compare come -> #n.");
            detail.AppendLine();
            var scriptLines = new List<string>();
            foreach (string name in new[] { "common_c3_introsequence", "common_cutscenesequenceplayer_c", "crash_respawnanimationmanager",
                                            "common_level_manager_c" })
            {
                IgArchiveFile? file = FindVscFile(sw, name);
                if (file == null)
                {
                    detail.AppendLine($"script {name}: non trovato in vsc/ nel dump");
                    scriptLines.Add($"  {name}: non trovato in vsc/ nel dump");
                    continue;
                }
                detail.AppendLine($"===== {file.Path}");
                try
                {
                    IgzFile igz = file.ToIgzFile();
                    var scripts = igz.Objects.OfType<igVscMetaObject>().ToList();
                    if (scripts.Count == 0) detail.AppendLine("  nessuno script (igVscMetaObject) nel file");
                    var dumper = new VscDumper(detail, igz.GameVersion) { MaxLines = name.Contains("intro") || name.Contains("respawn") ? 6000 : 3000 };
                    foreach (igVscMetaObject script in scripts) dumper.Dump(script, file.Path);
                    dumper.DumpUnreached(igz.Objects);
                    scriptLines.Add($"  {file.Path}: {scripts.Count} script, {igz.Objects.Count} oggetti");
                }
                catch (Exception e)
                {
                    detail.AppendLine($"  non letto: {e.GetType().Name}: {e.Message}");
                    scriptLines.Add($"  {file.Path}: non letto ({e.GetType().Name}: {e.Message})");
                }
                detail.AppendLine();
            }

            string detailPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".",
                                             Path.GetFileNameWithoutExtension(reportPath) + "_intro.txt");
            string? writeError = null;
            try
            {
                File.WriteAllText(detailPath, detail.ToString());
            }
            catch (Exception e)
            {
                writeError = e.Message;
            }

            report.AppendLine("=== INTRO DI CRASH 3 (analisi) ===");
            report.AppendLine("L'intro dei livelli di Crash 3 (IntroCutsceneSequencePlayer: Crash che esce dal portale) fa comparire Crash. Qui");
            report.AppendLine("quella del livello, dove sta rispetto alla partenza e, per confronto, quelle dei livelli originali di Crash 3;");
            report.AppendLine("lo script dell'intro e' scritto per esteso in " +
                              (writeError == null ? $"{Path.GetFileName(detailPath)}: allegalo insieme a questo rapporto."
                                                  : $"{Path.GetFileName(detailPath)}, che pero' non si e' potuto scrivere ({writeError})."));
            foreach (string line in introLines) report.AppendLine("  " + line);
            report.AppendLine("intro e partenze nei file del livello convertito:");
            foreach (string line in levelLines.Take(60)) report.AppendLine(line);
            if (levelLines.Count > 60) report.AppendLine($"  ... e altre {levelLines.Count - 60} righe (nel file dell'intro)");
            report.AppendLine($"intro e partenze dei livelli originali di Crash 3 ({withIntro} con l'intro; tutte nel file dell'intro):");
            foreach (string line in originalLines.Take(24)) report.AppendLine(line);
            if (originalLines.Count > 24) report.AppendLine($"  ... e altre {originalLines.Count - 24} righe (nel file dell'intro)");
            report.AppendLine("script scritti per esteso (intro di Crash 3, sequenza, animazioni di comparsa di Crash, gestore del livello):");
            foreach (string line in scriptLines) report.AppendLine(line);
            report.AppendLine($"come comincia un livello negli altri giochi (animazioni di comparsa, nomi, effetti, partenza di livelli di Crash 2): " +
                              $"{startLines.Count} righe nel file dell'intro");
            report.AppendLine();
            return writeError == null ? detailPath : null;
        }

        /// <summary>
        /// Come comincia un livello negli altri giochi, per costruire un'intro come quella di Crash 2: dati delle animazioni di
        /// comparsa dei personaggi (Crash_RespawnAnimationManagerData), nomi di animazioni o eventi di comparsa nei file di Crash
        /// (crash.pak), effetti (vfx/) con portali, comparse o teletrasporti, partenza e entita' vicine nei livelli di Crash 2
        /// </summary>
        private static void AppendOtherGamesStart(ArchiveIndex sw, List<string> lines)
        {
            lines.Add("animazioni di comparsa dei personaggi (Crash_RespawnAnimationManagerData nei dati dei personaggi):");
            int respawnData = 0;
            foreach (var (path, file) in sw.ByPath.Where(e => e.Key.EndsWith("_characterdata.igz")).OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                try
                {
                    foreach (Crash_RespawnAnimationManagerData data in file.ToIgzFile().Objects.OfType<Crash_RespawnAnimationManagerData>())
                    {
                        respawnData++;
                        lines.Add($"  {path}: {FieldsText(data, GameVersion.NSX)}");
                    }
                }
                catch (Exception e)
                {
                    lines.Add($"  {path}: non letto ({e.Message})");
                }
            }
            if (respawnData == 0) lines.Add("  nessuno");

            // Nomi nei file di Crash (animazioni, eventi del comportamento) che parlano di comparsa
            var filter = new System.Text.RegularExpressions.Regex("^Cutscene_|respawn|warp|intro|spawn|magicmoment|teleport|portal|appear",
                                                                  System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var names = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            int filesRead = 0;
            foreach (IgArchiveFile file in sw.ByArchive.GetValueOrDefault("crash") ?? [])
            {
                string lower = file.Path.ToLowerInvariant();
                if (lower.StartsWith("textures/") || lower.StartsWith("loosetextures/") || lower.StartsWith("materialinstances/") ||
                    lower.StartsWith("models/") || lower.EndsWith(".hkx")) continue;
                byte[] data;
                try
                {
                    data = file.Uncompress();
                }
                catch
                {
                    continue;
                }
                filesRead++;
                string fileName = NamespaceUtils.GetFileName(file.Path, false);
                int start = -1;
                for (int i = 0; i <= data.Length; i++)
                {
                    bool letter = i < data.Length &&
                                  (data[i] is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') or
                                   (byte)'_' or (byte)'-' or (byte)'.');
                    if (letter)
                    {
                        if (start < 0) start = i;
                        continue;
                    }
                    if (start >= 0 && i - start >= 6 && i - start <= 80)
                    {
                        string text = Encoding.ASCII.GetString(data, start, i - start);
                        if (filter.IsMatch(text))
                        {
                            if (!names.TryGetValue(text, out SortedSet<string>? seenIn)) names[text] = seenIn = new SortedSet<string>(StringComparer.Ordinal);
                            if (seenIn.Count < 3) seenIn.Add(fileName);
                        }
                    }
                    start = -1;
                }
            }
            lines.Add($"nomi di animazioni o eventi di comparsa nei file di Crash (crash.pak, {filesRead} file letti, {names.Count} nomi):");
            foreach (var (text, seenIn) in names.Take(400)) lines.Add($"  {text}  ({string.Join(", ", seenIn)})");
            if (names.Count > 400) lines.Add($"  ... e altri {names.Count - 400}");

            // Effetti con portali, comparse, teletrasporti
            var vfxFilter = new System.Text.RegularExpressions.Regex("warp|spawn|teleport|portal|intro|magic|appear",
                                                                     System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            List<string> effects = sw.ByPath.Keys.Where(k => k.StartsWith("vfx/") && vfxFilter.IsMatch(NamespaceUtils.GetFileName(k, false)))
                                             .OrderBy(k => k, StringComparer.Ordinal).ToList();
            lines.Add($"effetti (vfx/) con portali, comparse o teletrasporti nel nome ({effects.Count}):");
            foreach (string effect in effects.Take(200)) lines.Add("  " + effect);
            if (effects.Count > 200) lines.Add($"  ... e altri {effects.Count - 200}");

            // Partenza di alcuni livelli di Crash 2 (e di Crash 1 per confronto): componenti della partenza, entita' vicine
            // (non le decorazioni) e quelle che nel nome o nei componenti parlano di intro o comparsa
            var nameFilter = new System.Text.RegularExpressions.Regex("intro|warp|cutscene|spawn|magic|portal|respawn",
                                                                      System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (string level in new[] { "maps/crash2/l201_turtlewoods/l201_turtlewoods.igz", "maps/crash2/l202_snowgo/l202_snowgo.igz",
                                             "maps/crash1/l101_nsanitybeach/l101_nsanitybeach.igz" })
            {
                if (!sw.ByPath.TryGetValue(level, out IgArchiveFile? file))
                {
                    lines.Add($"partenza di {level}: file non trovato nel dump");
                    continue;
                }
                try
                {
                    IgzFile igz = file.ToIgzFile();
                    CPlayerStartEntity? start = MainPlayerStart(igz, null);
                    lines.Add($"partenza di {level}: " + (start == null ? "nessuna partenza nel file"
                                                                       : $"{start.ObjectName} {PlaceText(start)}, componenti: {ComponentsText(start)}"));
                    int shown = 0;
                    foreach (igEntity entity in igz.Objects.OfType<igEntity>())
                    {
                        if (ReferenceEquals(entity, start) || IsStaticDecoration(entity)) continue;
                        float distance = start == null ? float.MaxValue : EntityDistance(start, entity);
                        bool named = nameFilter.IsMatch(entity.ObjectName ?? "") || entity.GetComponents().Any(c => nameFilter.IsMatch(c.GetType().Name));
                        if (distance > 800 && !named) continue;
                        if (++shown > 60)
                        {
                            lines.Add("  ... e altre");
                            break;
                        }
                        lines.Add($"  {EntityText(entity)} {PlaceText(entity)}" + (start != null ? $", distanza {FormatFloat(distance)}" : "") +
                                  $": {ComponentsText(entity)}");
                    }
                }
                catch (Exception e)
                {
                    lines.Add($"partenza di {level}: non letto ({e.GetType().Name}: {e.Message})");
                }
            }
        }

        /// <summary>
        /// Componenti di un'entita' per il rapporto; per quelli degli script (VSC) anche i valori
        /// </summary>
        private static string ComponentsText(igEntity entity)
        {
            var parts = new List<string>();
            foreach (igComponentData component in entity.GetComponents())
            {
                string name = component.GetType().Name;
                parts.Add(component is CVscComponentData ? $"{name} [{FieldsText(component, GameVersion.NSX)}]" : name);
            }
            return parts.Count == 0 ? "nessuno" : string.Join(", ", parts);
        }

        /// <summary>
        /// Partenze (CPlayerStartEntity) e intro di Crash 3 (entita' con common_C3_IntroSequenceData) di un file: posizione,
        /// valori, riferimenti e, per le entita' dello stesso file a cui l'intro rimanda, dove stanno rispetto alla partenza
        /// </summary>
        private static void DescribeIntroAndStarts(IgzFile igz, string path, List<string> lines)
        {
            List<CPlayerStartEntity> starts = igz.Objects.OfType<CPlayerStartEntity>().ToList();
            CPlayerStartEntity? main = MainPlayerStart(igz, null);
            foreach (CPlayerStartEntity start in starts)
            {
                lines.Add($"  partenza {start.ObjectName ?? "?"} ({path}): {PlaceText(start)}, inizio della zona {(start._zoneStart ? "si'" : "no")}, " +
                          $"camera {RefText(start._camera?.Reference)}" + (ReferenceEquals(start, main) && starts.Count > 1 ? " (quella usata per il confronto)" : ""));
            }
            string Relative(igEntity entity)
            {
                CPlayerStartEntity? start = MainPlayerStart(igz, entity);
                return start == null ? "nessuna partenza nel file" : $"rispetto alla partenza {start.ObjectName}: {RelativeText(start, entity)}";
            }
            foreach (igEntity entity in igz.Objects.OfType<igEntity>())
            {
                common_C3_IntroSequenceData? data = entity.GetComponent<common_C3_IntroSequenceData>();
                if (data == null) continue;
                lines.Add($"  intro {entity.ObjectName ?? "?"} ({path}): {PlaceText(entity)}, {Relative(entity)}");
                if (entity._bolt != null) lines.Add($"    attaccata (bolt): {BoltText(entity._bolt)}");
                lines.Add($"    {IntroValuesText(data)}");
                lines.Add($"    riferimenti: {IntroRefsText(data)}");
                common_CutsceneSequencePlayerData? player = entity.GetComponent<common_CutsceneSequencePlayerData>();
                if (player != null) lines.Add($"    sequenza: {CutscenePlayerText(player)}");
                var targets = new List<(string label, igHandleMetaField? handle)>
                {
                    ("Entity_0x50", data._Entity_0x50), ("Entity_0x70", data._Entity_0x70), ("camera", data._Camera_Base),
                    ("checkpoint di partenza", player?._Starting_Checkpoint), ("inquadrature", player?._CutsceneSequenceShotList),
                };
                foreach (var (label, handle) in targets)
                {
                    if (handle?.Reference == null) continue;
                    igObject? target = igz.FindObject(handle.Reference);
                    if (target is igEntity targetEntity)
                        lines.Add($"    {label}: {EntityText(targetEntity)} {PlaceText(targetEntity)}, {Relative(targetEntity)}");
                    else if (target is CEntityHandleList list)
                        lines.Add($"    {label}: {list._data.Count} elementi" + (list._data.Count > 0 ? ": " + string.Join(", ", list._data.Take(6).Select(h => RefText(h?.Reference))) : ""));
                    else if (target != null)
                        lines.Add($"    {label}: {target.ObjectName ?? "?"} [{target.GetType().Name}]");
                    else
                        lines.Add($"    {label}: {RefText(handle.Reference)} (fuori da questo file)");
                }
            }
        }

        // Script VSC delle tessere di Crash 1 e dell'HUD (nome del file nel dump Switch, senza estensione), scritti per esteso
        private static readonly HashSet<string> _tokenScriptFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "common_bonusroundteleporter", "common_collectible_bonus_spawned_c", "crash1_bonusroundmanager_c",
            "common_crash_hud_bonusroundtokens", "common_crash_gui_hud_c", "common_level_manager_c",
            // dalla v51: gestore dei bonus (Crash 1, 2 e 3), fine del bonus e HUD di Crash, che guardano il gioco
            "common_basic_bonusround_manager", "common_bonusroundend_c", "crashbandicoot_hud_c",
        };

        /// <summary>
        /// Tessere dei bonus di Crash 1 (analisi): con --gioco crash3 le tessere prese non si contano e il bonus non parte.
        /// Nel rapporto: teletrasporti e tessere del livello, gestore delle tessere nei dati del personaggio, HUD delle
        /// tessere, contatori (variabili del gioco) con chi li nomina, script che guardano l'anno del gioco. In
        /// [rapporto]_tessere.txt gli script delle tessere scritti come grafo. Niente se il livello non ha tessere ne'
        /// teletrasporti; restituisce il percorso del file con i dettagli
        /// </summary>
        private static string? AppendBonusTokens(StringBuilder report, IgArchive output, ArchiveIndex sw, string reportPath, string? game)
        {
            var levelLines = new List<string>();
            var scriptNamespaces = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var variables = new SortedDictionary<string, NamedReference>(StringComparer.OrdinalIgnoreCase);
            var tokenTypes = new SortedDictionary<string, int>();
            var otherBonus = new SortedDictionary<string, int>();
            int teleporterCount = 0;

            void AddVariable(igHandleMetaField? handle)
            {
                NamedReference? reference = handle?.Reference;
                if (reference != null && !string.IsNullOrEmpty(reference.objectName)) variables[RefText(reference)] = reference;
            }
            void AddScript(igObject? meta)
            {
                string? ns = meta?.Reference?.namespaceName;
                if (!string.IsNullOrEmpty(ns)) scriptNamespaces.Add(ns);
            }

            // Livello: teletrasporti (common_BonusRoundTeleporterData), tessere (common_Collectible_Bonus_SpawnedData)
            foreach (IgArchiveFile file in output.Files)
            {
                string lower = file.Path.ToLowerInvariant();
                if (!lower.StartsWith("maps/") || !lower.EndsWith(".igz")) continue;
                IgzFile igz;
                try
                {
                    byte[] data = file.Uncompress();
                    if (!ContainsAscii(data, "Bonus")) continue;
                    igz = new IgzFile(file.Path, data, file.GameVersion);
                }
                catch (Exception e)
                {
                    levelLines.Add($"  {file.Path}: non letto ({e.Message})");
                    continue;
                }

                teleporterCount += DescribeTeleporters(igz, file.Path, levelLines, AddScript, AddVariable);
                foreach (common_Collectible_Bonus_SpawnedData c in igz.Objects.OfType<common_Collectible_Bonus_SpawnedData>())
                {
                    AddScript(c._meta);
                    string key = c._BonusRoundType.ToString();
                    tokenTypes[key] = tokenTypes.GetValueOrDefault(key) + 1;
                }
                foreach (CVscComponentData v in igz.Objects.OfType<CVscComponentData>())
                {
                    if (v is common_BonusRoundTeleporterData || v is common_Collectible_Bonus_SpawnedData) continue;
                    string type = v.GetType().Name;
                    if (!type.Contains("Bonus", StringComparison.OrdinalIgnoreCase)) continue;
                    otherBonus[type] = otherBonus.GetValueOrDefault(type) + 1;
                }
            }
            if (teleporterCount == 0 && tokenTypes.Count == 0) return null;

            // Archivi Switch in cui c'e' ogni file
            var archivesOf = new Dictionary<string, SortedSet<string>>();
            foreach (var (file, archive) in sw.ArchiveOf)
            {
                string key = file.Path.ToLowerInvariant();
                if (!archivesOf.TryGetValue(key, out SortedSet<string>? set)) archivesOf[key] = set = new SortedSet<string>();
                set.Add(archive);
            }
            string ArchivesText(string path)
            {
                SortedSet<string>? set = archivesOf.GetValueOrDefault(path.ToLowerInvariant());
                if (set == null || set.Count == 0) return "archivio ?";
                return set.Count <= 5 ? string.Join(", ", set) : $"{set.Count} archivi: {string.Join(", ", set.Take(5))}...";
            }

            // Teletrasporti dei livelli originali di Crash 1 con le tessere (per confronto)
            var originalLines = new List<string>();
            foreach (var (path, file) in sw.ByPath.Where(e => (e.Key.StartsWith("maps/crash1/l102_") || e.Key.StartsWith("maps/crash1/l106_")) &&
                                                              e.Key.EndsWith(".igz"))
                                                  .OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                try
                {
                    byte[] data = file.Uncompress();
                    if (!ContainsAscii(data, "BonusRoundTeleporter") && !ContainsAscii(data, "BonusRoundManager")) continue;
                    IgzFile igz = new IgzFile(file.Path, data, file.GameVersion);
                    DescribeTeleporters(igz, path, originalLines, null, null);
                    foreach (Crash_BonusRoundManagerData m in igz.Objects.OfType<Crash_BonusRoundManagerData>())
                        originalLines.Add($"  gestore delle tessere in {path}: script {MetaText(m._meta)}, contatori {HandleText(m._Game_Int_Variable_0x60)}, " +
                                          $"{HandleText(m._Game_Int_Variable_0x68)}, {HandleText(m._Game_Int_Variable_0x70)}");
                }
                catch (Exception e)
                {
                    originalLines.Add($"  {path}: non letto ({e.Message})");
                }
            }
            if (originalLines.Count == 0) originalLines.Add("  nessuno trovato (L102_JungleRollers, L106_RollingStones)");

            // Gestore delle tessere nei dati dei personaggi (Crash_CharacterData.igz, ...)
            var managerLines = new List<string>();
            foreach (var (path, file) in sw.ByPath.Where(e => e.Key.EndsWith("_characterdata.igz")).OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                try
                {
                    IgzFile igz = file.ToIgzFile();
                    Dictionary<igObject, string> keys = ComponentKeys(igz);
                    foreach (Crash_BonusRoundManagerData m in igz.Objects.OfType<Crash_BonusRoundManagerData>())
                    {
                        AddScript(m._meta);
                        AddVariable(m._Game_Int_Variable_0x60);
                        AddVariable(m._Game_Int_Variable_0x68);
                        AddVariable(m._Game_Int_Variable_0x70);
                        AddVariable(m._BonusRoundActive);
                        managerLines.Add($"  {path} ({ArchivesText(path)}): componente {keys.GetValueOrDefault(m) ?? "?"}, " +
                                         $"attivo {(m._bitfield._isEnabled ? "si'" : "no")}, script {MetaText(m._meta)}");
                        managerLines.Add($"    contatori {HandleText(m._Game_Int_Variable_0x60)}, {HandleText(m._Game_Int_Variable_0x68)}, " +
                                         $"{HandleText(m._Game_Int_Variable_0x70)}; bonus in corso {HandleText(m._BonusRoundActive)}; dati {HandleText(m._Component_Data)}");
                        managerLines.Add($"    tag {HandleText(m._Entity_Tag_0x30)}, {HandleText(m._Entity_Tag_0x38)}, {HandleText(m._Entity_Tag_0x40)}; " +
                                         $"variabili dei tag {HandleText(m._EntityTagVariable_id_zak9a186_variable)}, " +
                                         $"{HandleText(m._EntityTagVariable_id_jp8n1zvm_variable)}, {HandleText(m._EntityTagVariable_id_k7diogjk_variable)}");
                    }
                }
                catch (Exception e)
                {
                    managerLines.Add($"  {path}: non letto ({e.Message})");
                }
            }
            if (managerLines.Count == 0) managerLines.Add("  nessuno: Crash_BonusRoundManagerData non c'e' nei dati dei personaggi");

            // Un giro su script (vsc/), interfaccia (ui/) e dati dei personaggi: chi nomina i contatori, script che
            // guardano l'anno del gioco, file dell'interfaccia con l'HUD delle tessere
            var counterNames = variables.Values.Select(r => r.objectName).Where(n => !string.IsNullOrEmpty(n))
                                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var users = counterNames.ToDictionary(n => n, n => new List<string>(), StringComparer.OrdinalIgnoreCase);
            var vscByName = new Dictionary<string, (string path, IgArchiveFile file)>(StringComparer.OrdinalIgnoreCase);
            var yearFiles = new List<(string path, IgArchiveFile file)>();
            var hudFiles = new List<(string path, IgArchiveFile file)>();
            int scanned = 0, notRead = 0;
            foreach (var (path, file) in sw.ByPath.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                bool vsc = path.StartsWith("vsc/"), ui = path.StartsWith("ui/"), character = path.EndsWith("_characterdata.igz");
                if (!path.EndsWith(".igz") || !(vsc || ui || character)) continue;
                if (vsc) vscByName.TryAdd(NamespaceUtils.GetFileName(path, false), (path, file));
                byte[] data;
                try
                {
                    data = file.Uncompress();
                }
                catch
                {
                    notRead++;
                    continue;
                }
                scanned++;
                foreach (string counter in counterNames)
                    if (ContainsAscii(data, counter)) users[counter].Add(path);
                if (vsc && (ContainsAscii(data, "_year") || ContainsAscii(data, "Year") || ContainsAscii(data, "CrashGame"))) yearFiles.Add((path, file));
                if (ui && (ContainsAscii(data, "BonusRoundTokens") || ContainsAscii(data, "bonusroundtokens"))) hudFiles.Add((path, file));
            }

            // HUD delle tessere (common_Crash_HUD_BonusRoundTokens) e radice dell'HUD (common_Crash_GUI_Hud)
            var hudLines = new List<string>();
            foreach (var (path, file) in hudFiles)
            {
                try
                {
                    IgzFile igz = file.ToIgzFile();
                    var widgets = igz.Objects.OfType<common_Crash_HUD_BonusRoundTokens>().ToList();
                    hudLines.Add($"  {path} ({ArchivesText(path)}): {widgets.Count} elementi con le tessere");
                    foreach (common_Crash_HUD_BonusRoundTokens w in widgets)
                    {
                        AddScript(w._meta);
                        AddVariable(w._CountGameVar);
                        string hidden = w._owner != null && w._owner._bitfieldStorage._hidden ? ", nascosto" : "";
                        hudLines.Add($"    {PlaceablePath(w._owner)}{hidden}: contatore {HandleText(w._CountGameVar)}, effetto {HandleText(w._spawnedEffect)}, " +
                                     $"numero {w._Int}, valori {FormatFloat(w._Float_0x38)} {FormatFloat(w._Float_0x3c)} {FormatFloat(w._Float_0x40)}, " +
                                     $"script {MetaText(w._meta)}");
                    }
                    foreach (common_Crash_GUI_Hud root in igz.Objects.OfType<common_Crash_GUI_Hud>())
                    {
                        AddScript(root._meta);
                        hudLines.Add($"    radice dell'HUD {PlaceablePath(root._owner)}: script {MetaText(root._meta)}");
                    }
                }
                catch (Exception e)
                {
                    hudLines.Add($"  {path}: non letto ({e.Message})");
                }
            }
            if (hudLines.Count == 0) hudLines.Add("  nessun file dell'interfaccia (ui/) nomina le tessere");

            // Contatori e altre variabili del gioco: dove sono definite, valore iniziale, durata
            var variableLines = new List<string>();
            foreach (var group in variables.Values.GroupBy(r => r.namespaceName, StringComparer.OrdinalIgnoreCase))
            {
                string ns = group.Key;
                var wanted = new HashSet<string>(group.Select(r => r.objectName), StringComparer.OrdinalIgnoreCase);
                var candidates = sw.ByPath.Where(e => e.Key.EndsWith(".igz") && !e.Key.StartsWith("maps/") &&
                                                      NameAliases(NamespaceUtils.GetFileName(e.Key, false).ToLowerInvariant())
                                                          .Contains(ns.ToLowerInvariant()))
                                          .OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
                if (candidates.Count == 0)
                {
                    foreach (string name in wanted) variableLines.Add($"  {ns}::{name}: il file {ns} non c'e' nel dump (fuori dal livello)");
                    continue;
                }
                var (path, file) = candidates[0];
                try
                {
                    IgzFile igz = file.ToIgzFile();
                    foreach (igObject obj in igz.Objects.Where(o => o.ObjectName != null &&
                                                                    (wanted.Contains(o.ObjectName) ||
                                                                     (o is CGameVariable && o.ObjectName.Contains("Bonus", StringComparison.OrdinalIgnoreCase)))))
                    {
                        variableLines.Add($"  {ns}::{obj.ObjectName}: {VariableText(obj)}" +
                                          (wanted.Contains(obj.ObjectName!) ? "" : " (non usata dal livello)"));
                    }
                    foreach (string name in wanted.Where(n => !igz.Objects.Any(o => string.Equals(o.ObjectName, n, StringComparison.OrdinalIgnoreCase))))
                        variableLines.Add($"  {ns}::{name}: NON C'E' in {path}");
                    variableLines.Add($"    (definite in {path}, {ArchivesText(path)})");
                }
                catch (Exception e)
                {
                    variableLines.Add($"  {ns}: {path} non letto ({e.Message})");
                }
            }
            if (variableLines.Count == 0) variableLines.Add("  nessuna");

            // Script delle tessere scritti per esteso, poi quelli che guardano l'anno del gioco
            var detail = new StringBuilder();
            detail.AppendLine("TESSERE DEI BONUS DI CRASH 1: SCRIPT DEL GIOCO SWITCH");
            detail.AppendLine($"gioco del livello: {game ?? "?"}");
            detail.AppendLine("Ogni script: eventi (EVENTO: messaggio o funzione che lo fa partire), metodi, nodi. Ogni nodo ha un numero");
            detail.AppendLine("(#n), il tipo e i valori che legge: costanti, campi (.nome: dei dati dello script o di un oggetto del gioco),");
            detail.AppendLine("funzioni del gioco (fn), riferimenti (@file::oggetto). Sotto un nodo i suoi rami (nome:), poi il nodo che viene");
            detail.AppendLine("dopo allo stesso livello; un nodo gia' scritto compare come -> #n.");
            detail.AppendLine();
            var tokenYearUses = new List<string>();
            var dumped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toDump = new SortedDictionary<string, IgArchiveFile>(StringComparer.Ordinal);
            foreach (string name in _tokenScriptFiles.Concat(scriptNamespaces))
                if (vscByName.TryGetValue(name, out var found)) toDump.TryAdd(found.path, found.file);
            foreach (string name in _tokenScriptFiles.Concat(scriptNamespaces).Where(n => !vscByName.ContainsKey(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                detail.AppendLine($"script {name}: non trovato in vsc/ nel dump");
            var rewriteLines = new List<string>();
            foreach (var (path, file) in toDump)
            {
                detail.AppendLine($"===== {path} ({ArchivesText(path)})");
                try
                {
                    byte[] raw = file.Uncompress();
                    IgzFile igz = new IgzFile(file.Path, raw, file.GameVersion);
                    var scripts = igz.Objects.OfType<igVscMetaObject>().ToList();
                    if (scripts.Count == 0) detail.AppendLine("  nessuno script (igVscMetaObject) nel file");
                    // quelli delle tessere per intero; HUD e gestore del livello (grandi, servono per l'anno) fino a 1500 righe
                    bool core = path.Contains("bonus", StringComparison.OrdinalIgnoreCase);
                    var dumper = new VscDumper(detail, igz.GameVersion) { MaxLines = core ? 4000 : 1500 };
                    foreach (igVscMetaObject script in scripts) dumper.Dump(script, path);
                    dumper.DumpUnreached(igz.Objects);
                    tokenYearUses.AddRange(dumper.Notes);
                    // Prova di riscrittura: per correggere uno script bisogna poterlo riscrivere uguale
                    string rewrite = RewriteCheck(igz, raw, path);
                    detail.AppendLine("  " + rewrite);
                    rewriteLines.Add($"  {NamespaceUtils.GetFileName(path, false)}: {rewrite}");
                }
                catch (Exception e)
                {
                    detail.AppendLine($"  non letto: {e.GetType().Name}: {e.Message}");
                    rewriteLines.Add($"  {NamespaceUtils.GetFileName(path, false)}: non letto ({e.GetType().Name}: {e.Message})");
                }
                // quelli scritti per intero non si rileggono nel giro dell'anno; HUD e gestore del livello si' (troncati qui)
                if (path.Contains("bonus", StringComparison.OrdinalIgnoreCase)) dumped.Add(path);
                detail.AppendLine();
            }

            var yearScripts = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            int yearParsed = 0, yearErrors = 0;
            foreach (var (path, file) in yearFiles)
            {
                if (dumped.Contains(path)) continue;
                if (++yearParsed > 800) break;
                try
                {
                    IgzFile igz = file.ToIgzFile();
                    var dumper = new VscDumper(new StringBuilder(), igz.GameVersion) { MaxLines = int.MaxValue };
                    foreach (igVscMetaObject script in igz.Objects.OfType<igVscMetaObject>()) dumper.Dump(script, path);
                    var notes = dumper.Notes.Where(n => n.Contains("_year") || n.Contains("Year") || n.Contains("CrashGame")).ToList();
                    yearScripts[path] = notes.Count > 0 ? notes : new List<string> { "(\"_year\"/\"Year\"/\"CrashGame\" nel file, ma in nessun nodo raggiunto dagli eventi)" };
                }
                catch (Exception e)
                {
                    yearErrors++;
                    yearScripts[path] = new List<string> { $"non letto: {e.GetType().Name}: {e.Message}" };
                }
            }

            detail.AppendLine("===== NODI DEGLI SCRIPT DELLE TESSERE CHE GUARDANO LA ZONA O IL GIOCO (ANNO, checkCrashGame)");
            if (tokenYearUses.Count == 0) detail.AppendLine("  nessuno");
            foreach (string line in tokenYearUses) detail.AppendLine("  " + line);
            detail.AppendLine();
            detail.AppendLine($"===== ALTRI SCRIPT CHE GUARDANO IL GIOCO (ANNO, checkCrashGame) ({yearScripts.Count})");
            foreach (var (path, notes) in yearScripts)
            {
                detail.AppendLine($"  {path} ({ArchivesText(path)})");
                foreach (string note in notes.Take(30)) detail.AppendLine("    " + note);
                if (notes.Count > 30) detail.AppendLine($"    ... e altri {notes.Count - 30}");
            }
            detail.AppendLine();
            detail.AppendLine("===== FILE CHE NOMINANO I CONTATORI");
            foreach (var (counter, files) in users)
            {
                detail.AppendLine($"  {counter}: {files.Count}");
                foreach (string path in files) detail.AppendLine($"    {path} ({ArchivesText(path)})");
            }

            string detailPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".",
                                             Path.GetFileNameWithoutExtension(reportPath) + "_tessere.txt");
            string? writeError = null;
            try
            {
                File.WriteAllText(detailPath, detail.ToString());
            }
            catch (Exception e)
            {
                writeError = e.Message;
            }

            report.AppendLine("=== TESSERE DEI BONUS DI CRASH 1 (analisi) ===");
            report.AppendLine($"Gioco del livello: {game ?? "?"}. Fuori da Crash 1 il gestore delle tessere di Crash (Crash1_BonusRoundManager) si");
            report.AppendLine("toglie all'avvio e le tessere non si contano: dalla v51, con --nuovo, la registrazione mette in update.pak una copia che resta");
            report.AppendLine("e, dalla v52, una copia dell'HUD di Crash che mostra le tessere prese (vedi REGISTRAZIONE DEL LIVELLO). Qui quello che il");
            report.AppendLine("livello usa e dove sta nel gioco Switch; gli script scritti per esteso sono in");
            report.AppendLine(writeError == null ? $"{Path.GetFileName(detailPath)}: allegalo insieme a questo rapporto."
                                                 : $"{Path.GetFileName(detailPath)}, che pero' non si e' potuto scrivere ({writeError}).");
            report.AppendLine($"teletrasporti dei bonus nel livello: {teleporterCount}");
            foreach (string line in levelLines) report.AppendLine(line);
            report.AppendLine("tessere nel livello (collezionabili): " +
                              (tokenTypes.Count == 0 ? "nessuna" : string.Join(", ", tokenTypes.Select(e => $"{e.Key} {e.Value}"))));
            if (otherBonus.Count > 0) report.AppendLine("altri componenti dei bonus: " + string.Join(", ", otherBonus.Select(e => $"{e.Key} {e.Value}")));
            report.AppendLine("teletrasporti dei livelli originali di Crash 1 (per confronto):");
            foreach (string line in originalLines) report.AppendLine(line);
            report.AppendLine("gestore delle tessere nei dati dei personaggi (Crash_BonusRoundManagerData):");
            foreach (string line in managerLines) report.AppendLine(line);
            report.AppendLine("HUD delle tessere (common_Crash_HUD_BonusRoundTokens):");
            foreach (string line in hudLines) report.AppendLine(line);
            report.AppendLine("contatori e altre variabili del gioco usate (valore iniziale, durata):");
            foreach (string line in variableLines) report.AppendLine(line);
            report.AppendLine($"file del gioco che nominano i contatori (su {scanned} script, file dell'interfaccia e dati dei personaggi" +
                              (notRead > 0 ? $", {notRead} non letti" : "") + "):");
            foreach (var (counter, files) in users)
                report.AppendLine($"  {counter}: " + (files.Count == 0 ? "nessuno" : string.Join(", ", files.Take(10)) + (files.Count > 10 ? $" e altri {files.Count - 10}" : "")));
            report.AppendLine($"script delle tessere scritti per esteso: {toDump.Count} ({string.Join(", ", toDump.Keys.Select(p => NamespaceUtils.GetFileName(p, false)))})");
            report.AppendLine("prova di riscrittura degli script delle tessere (letti, riscritti con l'editor e riletti):");
            foreach (string line in rewriteLines) report.AppendLine(line);
            report.AppendLine("nodi degli script delle tessere che guardano la zona o il gioco (anno, checkCrashGame):");
            if (tokenYearUses.Count == 0) report.AppendLine("  nessuno: il controllo, se c'e', e' nel codice del gioco o in un altro script");
            foreach (string line in tokenYearUses.Take(40)) report.AppendLine("  " + line);
            if (tokenYearUses.Count > 40) report.AppendLine($"  ... e altri {tokenYearUses.Count - 40} (nel file delle tessere)");
            report.AppendLine($"altri script che guardano il gioco (anno, checkCrashGame): {yearScripts.Count}" + (yearErrors > 0 ? $" ({yearErrors} non letti)" : "") +
                              (yearScripts.Count > 0 ? ": " + string.Join(", ", yearScripts.Keys.Take(40).Select(p => NamespaceUtils.GetFileName(p, false))) +
                                                       (yearScripts.Count > 40 ? "..." : "") : ""));
            report.AppendLine();
            return writeError == null ? detailPath : null;
        }

        private static string RefText(NamedReference? reference) =>
            reference == null ? "-" : $"{reference.namespaceName}::{reference.objectName}";

        private static string HandleText(igHandleMetaField? handle) => RefText(handle?.Reference);

        private static string MetaText(igObject? meta) =>
            meta == null ? "-" : meta.Reference != null ? RefText(meta.Reference) : meta.ObjectName ?? meta.GetType().Name;

        /// <summary>
        /// Fixup di un igz letti dai byte (tipi, riferimenti, dipendenze); null se non si leggono
        /// </summary>
        private static FixupCollection? ParseFixups(byte[] data)
        {
            try
            {
                if (data.Length < 0x24 || BitConverter.ToUInt32(data, 0) != 0x49475A01) return null;
                using var reader = new BinaryReader(new MemoryStream(data));
                reader.BaseStream.Position = BitConverter.ToInt32(data, 0x18);
                return FixupCollection.Parse(reader);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Prova di riscrittura di un file di script VSC: lo riscrive con l'editor e lo rilegge. Dice se il grafo (scritto
        /// come nel file delle tessere), i tipi, i riferimenti esterni e le dipendenze restano uguali
        /// </summary>
        private static string RewriteCheck(IgzFile igz, byte[] original, string path)
        {
            try
            {
                static string Graph(IgzFile file, string fileName)
                {
                    var text = new StringBuilder();
                    var dumper = new VscDumper(text, file.GameVersion) { MaxLines = int.MaxValue };
                    foreach (igVscMetaObject script in file.Objects.OfType<igVscMetaObject>()) dumper.Dump(script, fileName);
                    return text.ToString();
                }
                static string? Difference(IEnumerable<string> before, IEnumerable<string> after, string what)
                {
                    var a = new HashSet<string>(before);
                    var b = new HashSet<string>(after);
                    var onlyBefore = a.Except(b).Take(5).ToList();
                    var onlyAfter = b.Except(a).Take(5).ToList();
                    if (onlyBefore.Count == 0 && onlyAfter.Count == 0) return null;
                    return $"{what} diversi (solo nell'originale: {string.Join(", ", onlyBefore)}; solo nella riscrittura: {string.Join(", ", onlyAfter)})";
                }

                string graphBefore = Graph(igz, path);
                int objectsBefore = igz.Objects.Count;
                igz.GameVersion = GameVersion.NSX;
                byte[] saved = igz.Save(includeDependencies: true);
                var again = new IgzFile(path, saved, GameVersion.NSX);
                string graphAfter = Graph(again, path);

                var problems = new List<string>();
                if (graphBefore != graphAfter)
                {
                    string[] a = graphBefore.Split('\n'), b = graphAfter.Split('\n');
                    int line = 0;
                    while (line < a.Length && line < b.Length && a[line] == b[line]) line++;
                    problems.Add($"grafo diverso dalla riga {line + 1} (prima: '{(line < a.Length ? a[line].Trim() : "-")}', " +
                                 $"dopo: '{(line < b.Length ? b[line].Trim() : "-")}')");
                }
                if (again.Objects.Count != objectsBefore) problems.Add($"oggetti {objectsBefore} -> {again.Objects.Count}");
                FixupCollection? fixupsBefore = ParseFixups(original), fixupsAfter = ParseFixups(saved);
                if (fixupsBefore == null || fixupsAfter == null)
                {
                    problems.Add("fixup non leggibili");
                }
                else
                {
                    string?[] differences =
                    [
                        Difference(fixupsBefore.TMET, fixupsAfter.TMET, "tipi"),
                        Difference(fixupsBefore.objectReferences.Select(r => RefText(r)), fixupsAfter.objectReferences.Select(r => RefText(r)), "riferimenti"),
                        Difference(fixupsBefore.handleReferences.Select(r => RefText(r)), fixupsAfter.handleReferences.Select(r => RefText(r)), "handle"),
                        Difference(fixupsBefore.exidReferences.Select(r => RefText(r)), fixupsAfter.exidReferences.Select(r => RefText(r)), "riferimenti per hash"),
                        Difference(fixupsBefore.TDEP.Select(d => d.path ?? ""), fixupsAfter.TDEP.Select(d => d.path ?? ""), "dipendenze"),
                    ];
                    problems.AddRange(differences.Where(d => d != null).Select(d => d!));
                }
                return problems.Count == 0
                    ? $"riscrittura uguale ({original.Length} -> {saved.Length} byte)"
                    : $"riscrittura DIVERSA ({original.Length} -> {saved.Length} byte): {string.Join("; ", problems)}";
            }
            catch (Exception e)
            {
                return $"riscrittura non riuscita: {e.GetType().Name}: {e.Message}";
            }
        }

        private static bool ContainsAscii(byte[] data, string text) =>
            data.AsSpan().IndexOf(new ReadOnlySpan<byte>(Encoding.ASCII.GetBytes(text))) >= 0;

        /// <summary>
        /// Teletrasporti dei bonus di Crash 1 (common_BonusRoundTeleporterData) di un file: entita', tipo, tag, contatori,
        /// effetti, script. Restituisce quanti sono
        /// </summary>
        private static int DescribeTeleporters(IgzFile igz, string path, List<string> lines, Action<igObject?>? addScript,
                                               Action<igHandleMetaField?>? addVariable)
        {
            var owners = new Dictionary<igObject, igEntity>(ReferenceEqualityComparer.Instance);
            foreach (igEntity entity in igz.Objects.OfType<igEntity>())
                foreach (igComponentData component in entity.GetComponents())
                    owners.TryAdd(component, entity);

            int count = 0;
            foreach (common_BonusRoundTeleporterData t in igz.Objects.OfType<common_BonusRoundTeleporterData>())
            {
                count++;
                addScript?.Invoke(t._meta);
                addVariable?.Invoke(t._Game_Int_Variable_0x50);
                addVariable?.Invoke(t._Game_Int_Variable_0x58);
                addVariable?.Invoke(t._Game_Int_Variable_0x60);
                addVariable?.Invoke(t._Game_Bool_Variable_0x40);
                addVariable?.Invoke(t._Game_Bool_Variable_0x68);
                igEntity? owner = owners.GetValueOrDefault(t);
                string position = owner == null ? "-" : $"{FormatFloat(owner._parentSpacePosition._x)} {FormatFloat(owner._parentSpacePosition._y)} {FormatFloat(owner._parentSpacePosition._z)}";
                lines.Add($"  teletrasporto {owner?.ObjectName ?? "(senza entita')"} ({path}): tipo {t._NewEnum2_id_9tewcwq5}, " +
                          $"tag {EntityTagsText(owner)}, posizione {position}, componente attivo {(t._bitfield._isEnabled ? "si'" : "no")}");
                lines.Add($"    contatori {HandleText(t._Game_Int_Variable_0x50)}, {HandleText(t._Game_Int_Variable_0x58)}, " +
                          $"{HandleText(t._Game_Int_Variable_0x60)}; si'/no {HandleText(t._Game_Bool_Variable_0x40)}, " +
                          $"{HandleText(t._Game_Bool_Variable_0x68)}; checkpoint {HandleText(t._Entity)}");
                lines.Add($"    effetti {HandleText(t._Vfx_Effect_0x28)}, {HandleText(t._Vfx_Effect_0x48)}, {HandleText(t._Vfx_Effect_0x70)}; " +
                          $"materiali {HandleText(t._Fx_Material_Redirect_Table_0x38)}, {HandleText(t._Fx_Material_Redirect_Table_0x80)}; " +
                          $"script {MetaText(t._meta)}");
            }
            return count;
        }

        /// <summary>
        /// Tag di un'entita' (CEntityData._tags), separati da virgole
        /// </summary>
        private static string EntityTagsText(igEntity? entity)
        {
            if (entity?._entityData is not CEntityData data || data._tags == null) return "-";
            var names = data._tags.Dict.Keys.Select(tag => tag.Reference?.objectName ?? tag._name ?? tag.ObjectName ?? "?").ToList();
            return names.Count == 0 ? "-" : string.Join(",", names);
        }

        /// <summary>
        /// Componenti dei dati delle entita' di un file -> "dati/chiave del componente"
        /// </summary>
        private static Dictionary<igObject, string> ComponentKeys(IgzFile igz)
        {
            var keys = new Dictionary<igObject, string>(ReferenceEqualityComparer.Instance);
            foreach (igEntityData data in igz.Objects.OfType<igEntityData>())
            {
                if (data._componentData == null) continue;
                foreach (var (key, component) in data._componentData.Dict)
                    if (component != null) keys.TryAdd(component, $"{data.ObjectName ?? "?"}/{key}");
            }
            return keys;
        }

        /// <summary>
        /// Percorso di un elemento dell'interfaccia (nomi dei genitori, al massimo 6)
        /// </summary>
        private static string PlaceablePath(igGuiPlaceable? placeable)
        {
            var names = new List<string>();
            for (int i = 0; placeable != null && i < 6; i++, placeable = placeable._parent)
                names.Insert(0, placeable._name ?? placeable.ObjectName ?? "?");
            return names.Count == 0 ? "?" : string.Join("/", names);
        }

        private static string VariableText(igObject obj) => obj switch
        {
            CGameIntVariable v => $"numero, valore iniziale {v._defaultValue}, durata {v._variableLifetime}, salvataggio automatico {(v._triggerAutoSave ? "si'" : "no")}",
            CGameBoolVariable v => $"si'/no, valore iniziale {(v._defaultValue ? "si'" : "no")}, durata {v._variableLifetime}",
            CGameFloatVariable v => $"decimale, valore iniziale {FormatFloat(v._defaultValue)}, durata {v._variableLifetime}",
            CGameVariable v => $"{v.GetType().Name}, durata {v._variableLifetime}",
            _ => obj.GetType().Name
        };

        /// <summary>
        /// Scrive uno script VSC (igVscMetaObject) come grafo: eventi (delegator), metodi, nodi con i valori che leggono
        /// (costanti, campi, funzioni del gioco, riferimenti) e i loro rami. Ogni nodo ha un numero (#n); uno gia' scritto
        /// compare come "-> #n". Notes: i nodi che guardano la zona o l'anno del gioco (_year, ...Year..., ...ZoneInfo...)
        /// </summary>
        private sealed class VscDumper
        {
            private static readonly HashSet<string> _skip =
            [
                "__objectType", "__referenceCount", "_dynamicFieldMemory", "_instancedNodes", "_vTablePointer", "_library",
                "_lastChild", "_nextSibling", "_parent", "_metaFunctions", "_attributes", "_instanceCount",
            ];
            private readonly StringBuilder _sb;
            private readonly GameVersion _version;
            private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
            private int _next;
            private int _lines;
            private bool _truncated;
            private string _script = "";
            public int MaxLines = 4000;
            public readonly List<string> Notes = [];

            public VscDumper(StringBuilder sb, GameVersion version)
            {
                _sb = sb;
                _version = version;
            }

            public void Dump(igVscMetaObject script, string file)
            {
                _script = script._name ?? script.ObjectName ?? NamespaceUtils.GetFileName(file, false);
                _lines = 0;
                _truncated = false;
                Line(0, $"SCRIPT {_script}");
                if (script._dataMeta != null)
                {
                    Line(1, "dati: " + (Describe(script._dataMeta, 0) ?? "?"));
                    if (script._dataMeta.Reference == null && script._dataMeta._metaFields.Count > 0)
                    {
                        var fields = script._dataMeta._metaFields._data.Where(f => f != null)
                                                                       .Select(f => $"{f._fieldName ?? f.ObjectName ?? "?"}({f.GetType().Name.Replace("MetaFieldInstance", "").Replace("MetaField", "")})");
                        Line(2, "campi: " + string.Join(" ", fields));
                    }
                }
                foreach (igVscDelegator delegator in Items(script._delegators)) Chain(delegator, 1, "EVENTO ");
                foreach (igVscDelegator delegator in Items(script._privateMessageDelegatorList)) Chain(delegator, 1, "EVENTO PRIVATO ");
                foreach (igVscMethod method in Items(script._exposedMethods)) Chain(method, 1, "METODO ");
            }

            /// <summary>
            /// Metodi del file che gli eventi non raggiungono direttamente: quelli che un nodo chiama per nome (ConstObject: fine di
            /// un ritardo, aggiornamento a ogni fotogramma, fine di un teletrasporto...). Fino alla v53 non si scrivevano, e
            /// nell'intro di Crash 3 erano proprio quelli che fanno comparire Crash. In ordine di nome, dopo gli script del file
            /// </summary>
            public void DumpUnreached(IEnumerable<igObject> objects)
            {
                List<igVscMethod> methods = objects.OfType<igVscMethod>()
                                                   .Where(m => !_ids.ContainsKey(m))
                                                   .OrderBy(m => m.ObjectName ?? "", StringComparer.Ordinal)
                                                   .ToList();
                if (methods.Count == 0) return;
                _lines = 0;
                _truncated = false;
                Line(0, $"METODI CHIAMATI DAI NODI ({methods.Count}; il nodo che ne chiama uno lo nomina come @file::nome, qui 'nome')");
                foreach (igVscMethod method in methods)
                {
                    if (_ids.ContainsKey(method)) continue;  // gia' scritto dentro un altro
                    Chain(method, 1, "METODO ");
                }
            }

            private static IEnumerable<T> Items<T>(igObjectList<T>? list) where T : igObject
            {
                if (list == null) yield break;
                foreach (T? item in list._data)
                    if (item != null) yield return item;
            }

            private void Line(int depth, string text)
            {
                if (_truncated) return;
                if (_lines >= MaxLines)
                {
                    _sb.Append(' ', depth * 2).AppendLine($"... (troncato dopo {MaxLines} righe)");
                    _truncated = true;
                    return;
                }
                _sb.Append(' ', depth * 2).AppendLine(text);
                _lines++;
            }

            /// <summary>
            /// Un nodo e quelli che vengono dopo (_out, _node), allo stesso livello
            /// </summary>
            private void Chain(object? node, int depth, string prefix = "")
            {
                // rami dentro rami: oltre 60 livelli si ferma (la ricorsione non deve finire lo stack)
                if (depth > 120)
                {
                    if (node != null && !_ids.ContainsKey(node)) Line(depth, $"{prefix}... (rami troppo profondi, non scritti)");
                    return;
                }
                int guard = 0;
                while (node != null && !_truncated && guard++ < 100000)
                {
                    if (_ids.TryGetValue(node, out int seen))
                    {
                        Line(depth, $"{prefix}-> #{seen}");
                        return;
                    }
                    node = One(node, depth, prefix);
                    prefix = "";
                }
            }

            private object? One(object node, int depth, string prefix)
            {
                int id = ++_next;
                _ids[node] = id;
                string named = node is igObject named0 && named0.ObjectName != null ? $" '{named0.ObjectName}'" : "";

                if (node is igVscMethod method)
                {
                    Line(depth, $"{prefix}#{id} metodo{named}");
                    foreach (igVscEntryPointNode entry in Items(method._nodes)) Chain(entry, depth + 1, "INGRESSO ");
                    return null;
                }

                var parameters = new List<string>();
                var flows = new List<(string name, object value)>();
                if (node is igObjectBase obj)
                {
                    foreach (CachedFieldAttr field in obj.GetFields(_version))
                    {
                        string name = field.GetName();
                        if (_skip.Contains(name)) continue;
                        object? value;
                        try
                        {
                            value = field.GetValue(node);
                        }
                        catch
                        {
                            continue;
                        }
                        if (value == null) continue;
                        if (IsFlow(value))
                        {
                            flows.Add((name, value));
                            continue;
                        }
                        if (value is bool flag && !flag && (name == "_isHandle" || name == "_isDelegatorTarget")) continue;
                        string? text = Describe(value, 1);
                        if (!string.IsNullOrEmpty(text)) parameters.Add($"{name.TrimStart('_')}={text}");
                    }
                }
                string line = $"{prefix}#{id} {ShortType(node)}{named}" + (parameters.Count > 0 ? " " + string.Join(" ", parameters) : "");
                Line(depth, line);

                object? next = null;
                var branches = new List<string>();
                foreach (var (name, value) in flows)
                {
                    var targets = Targets(name, value).ToList();
                    if (next == null && targets.Count == 1 && (name == "_out" || name == "_node") && targets[0].target is not igVscMethod)
                    {
                        next = targets[0].target;
                        continue;
                    }
                    foreach (var (label, target) in targets)
                    {
                        Line(depth + 1, label + ":");
                        Chain(target, depth + 2);
                        branches.Add($"{label} -> {(_ids.TryGetValue(target, out int t) ? "#" + t : "?")} {ShortType(target)}");
                    }
                }
                if (line.Contains("_year") || line.Contains("Year") || line.Contains("CrashGame") || line.Contains("ZoneInfo") || line.Contains("zoneInfo"))
                {
                    if (next != null) branches.Add($"dopo -> {ShortType(next)}");
                    Notes.Add($"{_script}: {line.Trim()}" + (branches.Count > 0 ? " | " + string.Join("; ", branches) : ""));
                }
                return next;
            }

            private static bool IsFlowObject(object? value) =>
                value is igVscActionNode || value is igVscMethod || (value is igVscPlaceable && value is not igVscAccessor);

            private static bool IsFlow(object value) =>
                IsFlowObject(value) || value is igEnumVscActionNodeHashTable || value is igVscActionNodeList ||
                value is igVscEntryPointNodeList || value is igVscMethodList;

            private static IEnumerable<(string label, object target)> Targets(string name, object value)
            {
                string label = name.TrimStart('_');
                if (IsFlowObject(value))
                {
                    yield return (label, value);
                    yield break;
                }
                if (value is igEnumVscActionNodeHashTable table)
                {
                    foreach (var (key, target) in table.Dict.OrderBy(e => e.Key))
                        if (target != null && IsFlowObject(target)) yield return ($"{label}[{key}]", target);
                    yield break;
                }
                // indici veri della lista (le uscite scollegate sono vuote e contano)
                if (value.GetType().GetField("_data")?.GetValue(value) is IEnumerable items)
                {
                    int index = 0;
                    foreach (object? item in items)
                    {
                        if (item != null && IsFlowObject(item)) yield return ($"{label}[{index}]", item);
                        index++;
                    }
                }
            }

            private static IEnumerable<object> DataItems(object list)
            {
                if (list.GetType().GetField("_data")?.GetValue(list) is IEnumerable items)
                    foreach (object? item in items)
                        if (item != null) yield return item;
            }

            private static string ShortType(object node)
            {
                string name = node.GetType().Name;
                if (node is igVscMacroCallNode && name != nameof(igVscMacroCallNode)) return "Macro " + name;
                if (name.StartsWith("igVsc")) name = name.Substring(5);
                if (name.EndsWith("Accessor") && name.Length > 8) name = name.Substring(0, name.Length - 8);
                else if (name.EndsWith("Node") && name.Length > 4) name = name.Substring(0, name.Length - 4);
                return name;
            }

            private IEnumerable<string> Fields(igObjectBase obj, int depth)
            {
                foreach (CachedFieldAttr field in obj.GetFields(_version))
                {
                    string name = field.GetName();
                    if (_skip.Contains(name)) continue;
                    object? value;
                    try
                    {
                        value = field.GetValue(obj);
                    }
                    catch
                    {
                        continue;
                    }
                    if (value == null) continue;
                    if (value is bool flag && !flag && (name == "_isHandle" || name == "_isDelegatorTarget")) continue;
                    string? text = Describe(value, depth);
                    if (!string.IsNullOrEmpty(text)) yield return $"{name.TrimStart('_')}={text}";
                }
            }

            private string? Describe(object? value, int depth)
            {
                switch (value)
                {
                    case null:
                        return null;
                    case string text:
                        return "\"" + text + "\"";
                    case bool flag:
                        return flag ? "si" : "no";
                    case float number:
                        return number.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    case Enum choice:
                        return choice.ToString();
                    case igHandleMetaField handle:
                        return handle.Reference == null ? null : "@" + RefText(handle.Reference);
                    case igRawRefMetaField:
                        return null;
                    case NamedReference reference:
                        return RefText(reference);
                }
                Type type = value.GetType();
                if (type.IsPrimitive) return System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                if (value is igObject obj)
                {
                    string? name = obj.Reference?.objectName ?? obj.ObjectName;
                    switch (obj)
                    {
                        case igMetaFieldInstance field:
                            return "." + (field._fieldName ?? name ?? "?");
                        case igMetaFunction function:
                            return "fn " + (function._functionName ?? name ?? "?") +
                                   (obj.Reference != null && !string.IsNullOrEmpty(obj.Reference.namespaceName) ? $"[{obj.Reference.namespaceName}]" : "");
                        case igBaseMeta meta:
                            return "tipo " + (meta._name ?? name ?? "?");
                    }
                    if (obj.Reference != null) return "@" + RefText(obj.Reference);
                    if (IsFlowObject(obj)) return _ids.TryGetValue(obj, out int id) ? "#" + id : ShortType(obj);
                    if (depth > 6) return ShortType(obj) + "(...)";
                    if (obj is igVscAccessor) return ShortType(obj) + "(" + string.Join(" ", Fields(obj, depth + 1)) + ")";
                    if (obj is igDataList)
                        return "[" + string.Join(", ", DataItems(obj).Take(16).Select(item => Describe(item, depth + 1) ?? "-")) + "]";
                    return ShortType(obj) + (name != null ? $" '{name}'" : "");
                }
                if (type.GetField("_data") != null)
                    return "[" + string.Join(", ", DataItems(value).Take(16).Select(item => Describe(item, depth + 1) ?? "-")) + "]";
                if (value is igObjectBase other && depth <= 6)
                    return "(" + string.Join(" ", Fields(other, depth + 1)) + ")";
                return value.ToString();
            }
        }

        // CCollisionMaterial finisce per Material ma e' un dato della fisica, non grafica
        private static bool IsGraphicsType(string typeName) =>
            _graphicsTypes.Contains(typeName) || (typeName.EndsWith("Material") && typeName != "CCollisionMaterial");
    }
}
