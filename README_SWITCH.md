# Crash NST Maker per Nintendo Switch

Fork di [Crash NST Maker](https://github.com/kishimisu/Crash-NST-Level-Editor) con un
convertitore da riga di comando che trasforma i livelli fatti sul PC (archivi `.pak` della
versione PC) in archivi per la versione **Nintendo Switch** di Crash Bandicoot N. Sane
Trilogy (archivi versione 12, `GameVersion.NSX`).

Per installare e giocare i livelli convertiti sulla Switch c'è l'app homebrew
**[NST Pak Manager](https://github.com/alberdom88/nst-pak-manager)**: li scarica da MEGA o
dal PC, registra i livelli nuovi e avvia il gioco direttamente dentro il livello. La
procedura completa, dalla preparazione della Switch al primo livello, è nel
[README dell'app](https://github.com/alberdom88/nst-pak-manager#procedura-completa).

- [Procedura](#procedura)
- [Comando `converti`](#comando-converti) e [come scegliere l'opzione](#quale-opzione-usare)
- [Provare su Eden](#provare-su-eden)
- [Altri comandi](#altri-comandi) · [Come funziona la conversione](#come-funziona-la-conversione)

## Procedura

1. **Scarica il convertitore.** Scheda **Actions** di questo repository → ultima esecuzione
   riuscita di *Build (Switch tools)* → artifact **NST-Switch-win-x64** (zip con `NST.exe`,
   per Windows). Il log della build e il rapporto `layout` sono anche nel ramo `ci-reports`.
2. **Fai il dump del gioco Switch con Eden** (una volta, e di nuovo se aggiorni il gioco):
   tasto destro sul gioco → **Dump RomFS**, con l'aggiornamento installato. I file finiscono in
   `%APPDATA%\eden\dump\0100D1B006744000\romfs\`. Il convertitore prende dal dump gli asset
   Switch (texture, modelli, suoni) che il `.pak` PC non può contenere in formato Switch.
3. **Crea il livello** con Crash NST Maker e salvalo: ottieni il `.pak` PC.
4. **Converti** (da una finestra del prompt nella cartella di `NST.exe`):
   ```
   NST.exe --switch converti "Level 3.pak" "%APPDATA%\eden\dump\0100D1B006744000\romfs" "out\MioLivello.pak" "out\report.txt" --nuovo
   ```
   Le cartelle di uscita vengono create se mancano. Con `--nuovo` il nome lo decidi tu con il
   file di uscita: qui il livello diventa `MioLivello` (cartella, file interni, riferimenti e
   registrazione), qualunque nome avesse nell'editor. L'archivio viene scritto in minuscolo,
   `out\miolivello.pak`, perché sulla Switch gli archivi dei livelli sono tutti in minuscolo
   e il gioco li cerca così. Nel nome valgono lettere, cifre e `_` (gli spazi diventano `_`).
5. **Installa sulla Switch** con [NST Pak Manager](https://github.com/alberdom88/nst-pak-manager):
   carica l'archivio convertito nella cartella MEGA (o del PC) e nell'app premi **A** sul
   livello. Serve solo l'archivio del livello: `update.pak` lo crea l'app.

Il rapporto (`report.txt`) elenca cosa è stato convertito, cosa è stato preso dagli
originali Switch, cosa è stato saltato e, con `--nuovo`, cosa è finito nella registrazione
del livello (sezione *REGISTRAZIONE DEL LIVELLO*). Allegalo quando qualcosa non funziona.
Il rapporto viene scritto anche quando l'archivio non si può salvare (per esempio perché è
aperto in Eden): in quel caso il convertitore lo dice ed esce con un errore, e il vecchio
archivio resta com'era (archivio e `update.pak` si scrivono prima in un file `.tmp`).

## Comando `converti`

```
NST.exe --switch converti <file_pc.pak> <cartella_dump_switch> <output.pak> [report.txt]
        [--nuovo | --come-originale | --sostituisci <livello>]
        [--base <livello>] [--senza-base] [--altri-livelli converti|originali|originali+dipendenze]
        [--zoneinfo-da <livello>|pc] [--salvataggio originale|proprio] [--escludi <testo>]...
        [--solo-usati] [--dipendenze-altri-livelli] [--tieni-entita-modalita] [--tieni-occlusioni]
        [--togli-entita [file:]testo]...
        [--originale-switch <testo>]... [--tessere-solo-crash1] [--tessere-senza-hud]
        [--ctr <cartella_dump_ctr_switch>] [--senza-intro | --aggiungi-intro]
        [--intro-crash1 | --intro-crash2] [--intro-come-editor] [--intro-scostamento <numero>]
        [--intro-evento <nome>] [--intro-senza-portale]
        [--intro-togli-portale | --intro-portale-piccolo] [--intro-alla-partenza]
        [--registra-in update|chunkinfos] [--gioco crash1|crash2|crash3] [--senza-modalita]
        [--riflessi auto|si|no] [--modello-blocchi <file>=<modello>]...
        [--pavimento-blocchi <file>[=<decorazione>]]...
        [--memoria modello|max|<livello>|<MB>[/<MB>]] [--prova-texture <MB>]
        [--descrivi <testo>]... [--pc-originali <cartella_archives_pc>]
```

Un'opzione scritta male (`--solo-usato`), un'opzione senza il suo valore o con un valore non
previsto (`--registra-in chunkinfo`) fermano il convertitore con un errore, invece di essere
ignorate. L'archivio di uscita non può stare nella cartella del dump: il convertitore la
leggerebbe come un file del gioco alla conversione successiva.

- `<cartella_dump_switch>`: il dump RomFS del gioco Switch (la cartella che contiene
  `archives`); vengono letti tutti i `.pak` che contiene.
- I file di posizionamento del livello (`maps/`, il pacchetto, la zone info) e la collisione
  statica (Havok) vengono convertiti dal PC; gli asset vengono presi dagli originali Switch,
  con le loro dipendenze Switch (per esempio le texture dei materiali, che hanno nomi diversi
  da quelle PC). La grafica PC non viene convertita. Per i file di altri livelli copiati
  dall'editor vedi `--altri-livelli`. Alla fine il rapporto elenca i file usati dal livello che
  il gioco Switch non ha (per esempio asset importati da Crash Team Racing).

### Quale opzione usare

| Opzione | Quando | Risultato |
|---|---|---|
| `--nuovo` | livello nuovo, con il nome che vuoi: **consigliata** | prende il nome del file di uscita; si apre con l'avvio diretto dell'app, nei menu del gioco non compare |
| `--come-originale` | livello creato nell'editor da uno originale (`L112_RoadToNowhere_Custom`) | riprende il nome dell'originale e lo sostituisce: si gioca anche dai menu |
| `--sostituisci <livello>` | mettere il livello al posto di un livello originale scelto (per esempio `L101_NSanityBeach`) | cartella, nomi dei file e riferimenti diventano quelli del livello scelto |
| nessuna | livello che ha già il nome di un originale | conversione senza cambi di nome |

Dettagli:

- `--nuovo`: il livello prende il nome del file di uscita (`out\MioLivello.pak` → livello
  `MioLivello`, archivio `miolivello.pak`, avvio diretto `crash1/miolivello/miolivello`).
  Se il nome è già quello di un livello del gioco la conversione si ferma (per sostituirlo
  c'è `--sostituisci`), e così con il nome di un altro archivio del gioco (`update`,
  `chunkinfos`, `crash1`...). Si ferma anche se il livello nell'editor ha il nome di un
  originale (`L101_NSanityBeach`), perché i suoi file sono quelli dell'originale: salvalo con
  un nome nuovo o usa `--come-originale`. Come quando si preme *Play* nell'editor su un livello
  nuovo, il livello va registrato nel gioco (la sua *zone info* elencata nel pacchetto di `chunkInfos`, dentro
  `update.pak`). Il convertitore:
  - mette i file della registrazione nell'archivio del livello, nella cartella interna
    `update/` (non compressi): NST Pak Manager (1.7 o successivo) li unisce all'`update.pak`
    originale quando avvii il livello, quindi sulla Switch basta l'archivio del livello;
  - scrive anche `update.pak` accanto all'archivio (l'originale Switch più la
    registrazione), da usare **solo su Eden**, dove l'app non c'è.

  La zone info (i dati del livello per il gioco: a quale gioco appartiene, nome mostrato nel
  caricamento, salvataggio...) viene presa da un livello originale Switch: quella creata
  dall'editor, convertita, blocca il gioco all'avvio diretto. Della zone info dell'originale
  cambiano il nome (anche quello del menu di debug) e la voce di salvataggio (vedi
  `--salvataggio`); il gioco con `--gioco`, la memoria con `--memoria`; nei livelli di Crash 3
  l'intro "magic moment" viene spenta. Il resto si eredita dall'originale, per esempio il nome
  mostrato nel caricamento (vedi `--zoneinfo-da`). La modalità scelta nell'editor viene
  riportata nella zone info come fa l'editor quando premi Play: personaggio (Crash o Coco, anche
  quando l'originale ne usa un altro), opzioni speciali (`{boulder}`, `{hog}`, `{jetski}`, ...)
  e veicolo (moto d'acqua o aereo solo con `{jetski}` o `{plane}`, altrimenti nessuno). Per
  masso, cavalcature, jetpack e scavo il convertitore crea anche i dati del personaggio
  (`Crash_CharacterData.igz` o `Coco_CharacterData.igz`, presi da `crash.pak`/`coco.pak` del
  dump) collegati al livello, e li mette nella registrazione. L'opzione hub non è convertita.
  `--senza-modalita` lascia la zone info dell'originale com'è (per le prove).
- `--come-originale`: il livello prende il nome dell'originale da cui è stato creato e lo
  sostituisce, quindi si installa con il nome dell'archivio originale, in minuscolo come
  sulla Switch (`l112_roadtonowhere.pak`; l'app lo fa da sola, su Eden rinominalo così).
- `--sostituisci <livello>`: si indica il nome dell'archivio del livello da sostituire,
  senza `.pak`. Se il livello usa file del livello sostituito che finirebbero con lo stesso
  nome dei suoi, la conversione si ferma: scegli un altro livello.
- `--base <livello>`: archivio Switch da cui prendere gli asset del livello di partenza.
  Con `--come-originale`, `--sostituisci` e con `--nuovo` per i livelli `..._Custom` si
  trova da solo.
- `--senza-base`: non aggiunge gli asset dell'archivio Switch di partenza.
- `--zoneinfo-da <livello>` (con `--nuovo`): livello originale Switch da cui prendere la zone
  info. Senza l'opzione il convertitore sceglie da solo: per un livello creato da uno
  originale (`L112_RoadToNowhere_Custom`) quell'originale, altrimenti il primo livello del
  gioco impostato nell'editor (Crash Mode): `L101_NSanityBeach`, `L201_TurtleWoods` o
  `L301_ToadVillage`. Se il livello è nella cartella di un altro gioco (Crash Mode cambiato
  dopo averlo creato, per esempio `Crash2/Custom_Level` impostato come Crash 1) passa nella
  cartella del gioco dell'editor, come con `--gioco`: cartella e zone info devono essere dello
  stesso gioco. Fino alla v37 il livello restava nella sua cartella con la zone info dell'altro
  gioco, e il gioco restava su LOADING (Level 10 e 11). Per tenerlo nella sua cartella usa
  `--gioco` con il gioco della cartella (il livello segue allora le regole di quel gioco). Con `pc` usa la
  zone info dell'editor convertita, che però blocca il gioco sul logo all'avvio diretto (e
  `--gioco` e `--memoria` non si applicano). Nella zone info di Crash 3 presa da un originale
  l'intro "magic moment" viene sempre spenta: accesa, il gioco aspetta un'intro che non c'è e
  Crash non compare; l'intro del livello (Crash che esce dal portale) parte lo stesso. Se Crash
  continua a non comparire, `--zoneinfo-da L101_NSanityBeach` funziona (il livello segue però
  le regole di Crash 1).
- `--gioco crash1|crash2|crash3` (con `--nuovo`): il livello si gioca con le regole di quel gioco
  (mosse, interfaccia, reliquie), qualunque gioco fosse impostato nell'editor. Il livello passa
  nella cartella di quel gioco (`crash3/<livello>/<livello>`) e prende la zone info del primo
  livello di quel gioco (`L301_ToadVillage` per Crash 3); un livello `..._Custom` tiene quella del
  suo originale solo se è dello stesso gioco (con la zone info di Road to Nowhere passata a
  Crash 3 il gioco si bloccava sul titolo). Gli oggetti del livello restano quelli che sono. Per
  un livello che nell'editor era di Crash 1 o 2, `--gioco crash3` aggiunge anche l'intro di
  Crash 3 (vedi `--aggiungi-intro`). Le mosse di Crash 3 (doppio salto, scatto, ...) dipendono
  dai boss battuti nel salvataggio.
- `--memoria modello|max|<livello>|<MB>[/<MB>]` (con `--nuovo`): dimensione delle aree di memoria
  del livello nella zone info (`_levelPoolSize` e `_globalChunkPoolSize`). Con `modello`
  (predefinito) sono quelle del livello originale da cui viene la zone info; con `max` quelle del
  livello originale che ne ha di più; con il nome di un livello originale quelle di quel livello.
  Nel dump della versione 1.0.0 tutti i livelli hanno gli stessi valori (200 MB e 30 MB), quindi
  in pratica non cambia nulla. Dalla v56 si possono scrivere i MB (prova): `--memoria 300` (area
  del livello 300 MB, globale com'è) o `--memoria 300/40` (anche la globale). Serve a capire se
  un livello pesante (schermo nero, caricamento bloccato) ha finito la memoria; se la Switch non
  ha tanta memoria libera il gioco potrebbe bloccarsi al caricamento. La zone info dell'editor
  PC ha circa 910 e 890 MB.
- `--prova-texture <MB>` (prova, dalla v57): aggiunge al pacchetto del livello tante texture
  quanti MB indicati, prese dagli archivi dei livelli originali tra quelle che il livello non usa
  e che non stanno negli archivi comuni (un po' da tutti i livelli, piccole e grandi, come le
  texture vere di un livello). Il gioco le carica con il livello senza usarle: le entità restano
  le stesse e cresce solo la memoria delle texture. Su un livello che si vede (per esempio con
  `--togli-entita "*"`), se con le texture in più lo schermo diventa nero (o il caricamento si
  blocca) il problema è la memoria delle texture; se si vede ancora, è un'entità.
- `--riflessi auto|si|no` (con `--nuovo`, dalla v41): riflessi planari nella zone info
  (`_planarReflectionsEnabled`). Il ghiaccio che riflette dei livelli di neve di Crash 2 (Snow Go,
  Cold Hard Crash, Bear It...) sulla Switch disegna il riflesso che il gioco calcola solo se la zone
  info li accende (era il primo sospetto per il pavimento nero del Level 10, ma non cambiava niente:
  vedi `LUCI, VOLUMI DI COLORE E BOX VISIVI`). Con `auto`
  (predefinito) si accendono se il livello ha i file dei riflessi di un altro livello
  (`L202_SnowGo_Reflections.igz`, che l'editor copia insieme al ghiaccio) o se erano accesi nella
  zone info dell'editor; altrimenti restano come nell'originale. `si` e `no` li forzano. Il rapporto
  lo dice nella registrazione e mostra il valore delle zone info (`riflessi planari`).
- `--salvataggio` (con `--nuovo`): `proprio` (predefinito) dà al livello una voce di
  salvataggio sua, con il suo nome; `originale` usa quella del livello da cui viene la zone
  info (gemme, casse e tempi finiscono su quel livello).
- `--registra-in` (con `--nuovo`, per le prove): dove registrare il livello. `update`
  (predefinito) scrive `update.pak` come l'editor PC; `chunkinfos` scrive invece una copia di
  `chunkInfos.pak` (l'archivio con le zone info di tutti i livelli del gioco, con il nome che
  ha nel dump) con dentro anche quella del livello nuovo: si installa al posto del
  `chunkInfos.pak` del gioco, e `update.pak` non viene scritto.
- `--escludi <testo>`: lascia fuori dall'archivio i file il cui percorso contiene il testo (si
  può ripetere). Dalla v59 vale anche per le dipendenze della grafica che il convertitore
  aggiunge alla fine prendendole dagli originali Switch (anche da un altro livello): il rapporto
  le elenca come `ESCLUSA`.
- `--descrivi <testo>` (dalla v59, si può ripetere): nel rapporto, sezione `FILE DESCRITTI`, gli
  oggetti dei file dell'archivio con il testo nel percorso (tipo, nome e campi) e l'archivio
  Switch da cui viene ciascun file: per vedere com'è fatto un file che dà problemi (un materiale,
  un modello).
- Asset di Crash Team Racing (`Octane` nel percorso): la Switch non li ha e la loro grafica non si
  converte, quindi gli oggetti resterebbero senza modello. Dalla v38 restano fuori da soli (prima
  serviva `--escludi Octane`): il livello si carica senza quella parte di grafica. Con `--ctr`
  invece si convertono e se ne cerca la grafica nel dump di CTR.
- `--ctr <cartella>` (**non usarlo**: nelle prove blocca il gioco al caricamento, perché i
  modelli di CTR usano file che Crash non ha, `vertexformat` e `indexformats`): dump di Crash
  Team Racing Nitro-Fueled per Switch. La grafica
  che il gioco Crash non ha (asset CTR importati nell'editor) viene cercata lì con lo stesso
  percorso o lo stesso nome di file (per i materiali anche con un suffisso diverso) e copiata
  nell'archivio, con le sue
  dipendenze CTR. Non è detto che il gioco Crash sappia leggere i file di CTR: il rapporto
  riporta versione e piattaforma dei loro igz.
- `--aggiungi-intro` (prova): se il livello non ha l'intro di Crash 3 (Crash che esce dal portale),
  la copia da Gone Tomorrow della Switch, con le stesse impostazioni che usa l'editor per un
  livello nuovo di Crash 3, insieme ai file che le servono (elencati nel rapporto). Con
  `--gioco crash3` è automatica per i livelli di Crash 1 e 2. Conta come intro già presente
  quella nei file del livello o nei file di altri livelli che stanno nel pacchetto; le copie
  fuori dal pacchetto no (fino alla v36 bastava una copia di un livello di Crash 3 presa per un
  solo oggetto per non aggiungerla). Nella zone info di un livello di Crash 3 l'intro "magic
  moment" del livello originale viene sempre spenta: accesa, Crash non compare.
  Fino alla v53 l'intro aggiunta restava nel punto in cui sta in Gone Tomorrow, come quando la
  aggiunge l'editor: dalla v52 doveva spostarsi alla partenza del livello, ma l'intro di Gone
  Tomorrow è "attaccata" (bolt senza entità, come tutte quelle originali) e questo la fermava. La
  v54 la spostava davvero (nel Level 7 a 70 unità dalla partenza invece che a 4457) e con il
  Level 7 il caricamento si è bloccato. Lo script dell'intro però fa comparire Crash dove sta
  Crash, non dove sta l'intro (vedi sotto): dalla v55 l'intro resta dov'è e si sposta solo con
  `--intro-alla-partenza`. Una partenza o un mondo di Gone Tomorrow copiati perché l'intro li
  nomina si tolgono e l'intro usa quelli del livello. Opzioni per provare, se Crash compare
  spostato rispetto alla partenza:
  - `--intro-scostamento <numero>`: lo "Spawn offset" dell'intro (nell'editor, sotto "Crash spawn
    delay"), cioè quante unità più avanti l'intro mette Crash; per l'intro aggiunta e per quella
    che il livello ha già. Per un livello nuovo l'editor usa 300: `--intro-scostamento 0` lo
    lascia dove comincia;
  - `--intro-come-editor`: l'intro aggiunta come la aggiunge l'editor quando passi a Crash 3 un
    livello che c'è già (valori di Gone Tomorrow, "Animation delay" 0.5) invece che come in un
    livello nuovo di Crash 3 (evento `Cutscene_Crash2_Portal_Exit_Victory`, 1, 1, 2.45, 300);
  - `--intro-alla-partenza` (prova, dalla v55): l'intro aggiunta si sposta vicino alla partenza
    del livello, nella posizione e rotazione rispetto alla partenza che ha in Gone Tomorrow, come
    nella v54. `--intro-posizione-originale` (la lascia dov'è) resta valida ma dalla v55 è già così.
  Cosa fa l'intro (script `common_C3_IntroSequence`, per intero nel file dell'intro dalla v54):
  all'avvio nasconde Crash e prende la sua posizione e la sua direzione; dopo "Animation delay"
  gli toglie la collisione, lo mette "Spawn offset" unità più avanti e crea il portale ("Vfx
  effect") "Animation offset" unità più avanti, grande "Animation size"; dopo "Crash spawn delay"
  fa partire l'animazione di Crash (evento "BehaviorEventCrashIntro"), lo rende visibile e alla
  fine dell'animazione gli ridà la collisione; dopo "Animation duration" toglie il portale.
  Intro come in Crash 1 o 2 (prova, dalla v53): nei livelli di Crash 1 e 2 l'intro (Crash che si
  materializza alla partenza) non è un oggetto del livello: la fa il gioco, con l'intro "magic
  moment" della zone info. Con la zone info di Crash 3 quella intro il gioco non la fa: aspetta
  l'intro di Crash 3 del livello, ed è quella a far comparire Crash. Se le regole di Crash 3 non
  servono, `--gioco crash2` dà l'intro vera di Crash 2. Con `--gioco crash3`:
  - `--intro-crash2`: l'intro di Crash 3 resta (serve a far comparire Crash) ma senza l'effetto
    del portale (dalla v56 di nuovo tolto, come nella v53: vedi sotto le prove con il Level 7),
    con "Spawn offset" a 0 e con l'animazione di comparsa di Crash 2, se il convertitore
    la riconosce nei dati delle animazioni di comparsa di Crash (`Crash_RespawnAnimationManagerData`
    in `Crash_CharacterData.igz`: il testo che nomina Crash 2, `Cutscene_Crash2_Warp_Enter`);
    altrimenti resta quella dell'intro (`Cutscene_Crash2_Portal_Exit_Victory`, anche quella di
    Crash 2). Vale anche per l'intro che il livello ha già;
  - `--intro-crash1` (dalla v54): come `--intro-crash2`, con l'animazione di comparsa di Crash 1
    (`SpawnIn`, quella con cui Crash si materializza all'inizio dei livelli di Crash 1 e dopo una
    vita persa: il testo che lo script delle animazioni di comparsa usa per Crash 1);
  - `--intro-evento <nome>`: l'animazione con cui compare Crash (evento del comportamento di Crash,
    "BehaviorEventCrashIntro" nell'editor), per provarne un'altra;
  - `--intro-togli-portale` (dalla v54): l'effetto del portale tolto ("Vfx effect" vuoto). È
    quello che fanno `--intro-crash1` e `--intro-crash2` nella v53 e dalla v56;
  - `--intro-senza-portale`: l'effetto resta com'è, come nelle intro che funzionano, ma l'intro lo
    crea 30000 unità dietro Crash ("Animation offset" -30000), dove non si vede (nella v55 lo
    facevano anche `--intro-crash1/2`);
  - `--intro-portale-piccolo` (prova, dalla v55): l'effetto piccolissimo ("Animation size" 0.01)
    ai piedi di Crash, come nella v54.
  Le prove con il Level 7 e `--gioco crash3`: con l'effetto tolto (v53) il caricamento finiva e
  poi lo schermo restava nero, con il gioco che andava (menu di pausa); con l'effetto
  piccolissimo (v54) o lontano (v55) il caricamento si è bloccato. Lo schermo nero però c'è anche
  con `--gioco crash1`, senza nessuna intro: è del livello, non dell'intro (vedi "Schermo nero
  dopo il caricamento"). Per questo dalla v56 `--intro-crash1/2` tolgono di nuovo l'effetto.
  Il rapporto dice cosa è stato cambiato. Il file `<rapporto>_intro.txt` ha anche come comincia
  un livello negli altri giochi: i dati delle animazioni di comparsa dei personaggi, i nomi di
  animazioni ed eventi di comparsa nei file di Crash (`crash.pak`), gli effetti con portali o
  comparse, la partenza di Turtle Woods, Snow Go e N. Sanity Beach (componenti della partenza ed
  entità vicine) e gli script delle animazioni di comparsa di Crash e del gestore del livello.
  La sezione `INTRO DI CRASH 3 (analisi)` (dalla v52) dice cosa è stato copiato e spostato, con i
  valori dell'intro, dove stanno l'intro e le partenze del livello e, per confronto, quelle di
  alcuni livelli originali di Crash 3 (distanza e posizione rispetto alla partenza). Il file
  `<rapporto>_intro.txt`, accanto al rapporto, ha tutto questo e gli script dell'intro
  (`common_C3_IntroSequence`, `common_CutsceneSequencePlayer_c`) scritti per esteso; dalla v54
  anche i metodi che i nodi chiamano per nome (alla fine di un ritardo, a ogni fotogramma...), che
  prima mancavano: nell'intro sono quelli che partono dopo "Animation delay", "Crash spawn delay" e
  "Animation duration", cioè il resto dell'intro.
  Schermo nero o caricamento bloccato: per capire se dipende dall'intro, converti lo stesso
  livello con `--senza-intro` (con `--gioco crash3` Crash dovrebbe comparire lo stesso, vedi
  sotto) o con `--gioco crash1` (nessuna intro di Crash 3) e guarda se il gioco va avanti
  (musica, menu di pausa con +) e dove sta Crash. Il Level 7 resta nero anche con `--gioco
  crash1`: il problema è nel livello (vedi "Schermo nero dopo il caricamento" più sotto).
- `--senza-intro` (prova): toglie l'intro che l'editor mette nei livelli di Crash 3 (Crash che
  esce dal portale, presa da Gone Tomorrow). Quando l'intro "magic moment" della zone info di
  Crash 3 era accesa, senza l'intro Crash non compariva e non si poteva muovere; dalla v27 è
  sempre spenta e Crash dovrebbe comparire anche senza (nella v25, spenta, con un livello con
  l'intro Crash compariva): è la prova per capire se un problema all'avvio viene dall'intro.
- File di altri livelli: quando usi un oggetto di un altro livello l'editor copia nell'archivio
  l'intero file da cui viene (per esempio `maps/Crash3/L318_TellNoTales/L318_TellNoTales.igz`).
  Il convertitore mette nel pacchetto del livello (l'elenco dei file che il gioco carica come
  parte del livello, con tutte le loro entità) solo quelli che c'erano nel pacchetto del PC; gli
  altri restano nell'archivio e il rapporto li elenca come "fuori dal pacchetto". Alcuni livelli
  (Tropical Escape) hanno nel pacchetto PC anche i file interi di altri livelli, che il gioco
  carica con tutte le loro entità: vedi `--solo-usati`, `--togli-entita` e `--originale-switch`.
- Contenuto dei file di altri livelli: il rapporto ha la sezione `CONTENUTO DEI FILE DI ALTRI
  LIVELLI`. Per ognuno dice se è nel pacchetto (il gioco carica tutte le sue entità) o fuori (il
  gioco usa solo gli oggetti a cui il livello fa riferimento), elenca le entità raggruppate per
  tipo e componenti (quante, esempi di nomi, file che usano), quelle che usano file che il gioco
  Switch non ha (`MANCA ...`), gli altri oggetti e il confronto con l'originale Switch dello
  stesso file (stesse entità, o quante in più e in meno). La sezione `FILE USATI DAL LIVELLO CHE
  SULLA SWITCH SONO SOLO NEGLI ARCHIVI DI ALTRI LIVELLI` elenca i file che i file convertiti
  usano e che sulla Switch stanno solo nell'archivio di un altro livello: il gioco non li trova.
  Dalla v38 quelli che non sono pezzi di livello (banchi dei suoni, script...) vengono copiati
  dall'archivio di quel livello, con le loro dipendenze (sezione `FILE PRESI DAGLI ARCHIVI DI
  ALTRI LIVELLI`); restano nell'elenco solo i file in `maps/` (telecamere, nemici di altri livelli).
- Cartelle doppie del livello: quando nell'editor si cambia il Crash Mode, una parte dei file del
  livello può restare nella cartella dell'altro gioco (`Crash2/Custom_Level` accanto a
  `Crash1/Custom_Level`). Dalla v38 finiscono tutti nella cartella del livello; un file con lo
  stesso nome di uno della cartella del livello resta dov'è, e se è vuoto (il file principale
  senza entità lasciato dall'editor) viene tolto, perché avrebbe lo stesso nome interno del file
  principale del livello.
- Ogni igz convertito viene riletto: quelli che il convertitore non riesce a rileggere sono
  nella sezione `FILE CONVERTITI CHE NON SI RILEGGONO`, primi sospetti se il livello si blocca.
- Entità di livelli con un'altra modalità (dalla v39, sempre, anche senza `--solo-usati`): dai
  file di altri livelli nel pacchetto si tolgono le entità con un comportamento (personaggi,
  nemici, spline delle corsie, oggetti che si muovono: quelle che usano `metaobject`) se vengono
  da un livello con una modalità che il livello convertito non ha, e da tutti i boss. Le
  modalità sono quelle che l'editor assegna ai livelli originali: sub (Under Pressure, Deep
  Trouble), tigre (Orient Express, Midnight Run), orso (Bear It, Bear Down, Un-Bearable, Totally
  Bear), cinghiale (Hog Wild, Whole Hog), moto, moto d'acqua, aereo, jetpack, scavo, masso
  (Boulders, Boulder Dash, Crash Dash, Crash Crush, Un-Bearable). Il livello convertito ha quelle
  scelte nell'editor (`{bear}`, `{boulder}`... nella zone info del PC; nessuna con `--nuovo
  --senza-modalita`). Quelle entità aspettano la loro modalità e il gioco resta su LOADING: Level
  11 si carica solo togliendo i nemici di Under Pressure e le corsie e i nemici di Orient Express e
  Midnight Run. Le decorazioni restano. Il rapporto le elenca in `FILE DI ALTRI LIVELLI CAMBIATI`
  (righe `modalita'`); `--tieni-entita-modalita` le lascia tutte.
  Dalla v41 si tolgono anche le entità che bloccano il caricamento pur essendo di un livello
  normale, trovate con le prove: per ora i pinguini fermi di Snow Go
  (`Arctic_Enemy_Immobile_Penguin`, con quelli Level 10 resta su LOADING; foche e altri pinguini
  restano). Righe `blocca il caricamento` nel rapporto; anche queste restano con
  `--tieni-entita-modalita`.
- Modelli delle entità indicati per nome (dalla v42; dalla v46 anche quelli delle decorazioni):
  molte entità indicano il modello con un testo: le decorazioni (muri, rocce, pavimenti) in
  `CModelComponentData._fileName` (`models:\Levels\Crash2\Snow\C2_Snow_Island03.igb`), casse e
  piattaforme in `_modelName`, i personaggi in `_skinName`. I controlli sui riferimenti non li
  vedevano. Sul PC il gioco trova un modello in qualunque archivio, quindi l'editor, quando copi un
  oggetto da un livello, copia solo i modelli che stanno nell'archivio di quel livello: i pavimenti
  di neve e ghiaccio di Cold Hard Crash usano modelli che stanno nell'archivio di Snow Biz e nel
  livello non c'erano. Sulla Switch quelle decorazioni non si vedevano, ma la loro collisione sì
  (Level 10: Crash cammina e scivola su un pavimento che non si vede). Dalla v46 quei modelli si
  copiano dall'archivio Switch che li ha, con i loro materiali e le loro texture. La
  sezione `MODELLI DELLE ENTITA' INDICATI PER NOME` li elenca tutti con dove stanno (nell'archivio,
  negli archivi comuni, solo nell'archivio di un altro livello, da nessuna parte); quelli che sulla
  Switch stanno solo nell'archivio di un altro livello vengono copiati, con le loro dipendenze.
  Elenca anche i blocchi di collisione (`CDynamicClipEntity`, per esempio quelli di
  `L218_ColdHardCrash_FakeCollisionToBeReplaced`) con il modello che mostrano: senza modello si
  cammina sopra ma non si vedono (il ghiaccio invisibile del Level 10?). I blocchi senza modello
  prendono quello dei blocchi dell'originale Switch, se ne hanno uno.
- `--modello-blocchi <testo del file>=<modello>` (dalla v42, si può ripetere): i blocchi di
  collisione dei file di altri livelli con il testo nel percorso mostrano il modello indicato, nel
  formato dell'editor (`Levels\Crash2\...\Nome`, senza `models/` e senza `.igz`). Serve a far vedere
  un pavimento fatto solo di blocchi di collisione senza aprire l'editor. Il modello si disegna
  con la sua misura, nel punto dell'entità: per un blocco grande va meglio `--pavimento-blocchi`.
- `--pavimento-blocchi <testo del file>[=<nome della decorazione>]` (dalla v44, si può ripetere):
  sui blocchi di collisione dei file con il testo nel percorso mette una copia di una decorazione
  del livello (un'entità con modello come i muri, le rocce e i pavimenti, che sulla Switch si
  vedono), ingrandita e spostata perché copra esattamente il blocco, con la collisione spenta (il
  blocco resta com'è: si cammina e si scivola come prima). Senza nome sceglie da sola, nei file dello
  stesso livello del file dei blocchi, una decorazione con `Ice` o `Floor` nel nome (per Cold Hard
  Crash i `CliffFloor`), poi negli altri livelli; con `=<testo>` copia la prima decorazione con quel
  testo nel nome. Serve per i pavimenti fatti nell'editor con i blocchi di collisione, che l'editor
  mostra come riquadri rossi ma che nel gioco non si vedono (Level 10: il ghiaccio fatto con i blocchi
  di `L218_ColdHardCrash_FakeCollisionToBeReplaced`, che anche nell'originale Switch non hanno
  modello). Il rapporto dice quale decorazione ha copiato e quanti blocchi ha coperto
  (`pavimento visibile`). Nella v43 la copia era un'entità di gioco con un modello (anche
  `LevelAssets\Basic\box`) e nel gioco non si vedeva; nella v44 la decorazione si cercava nei file
  già convertiti e non se ne trovava nessuna. Dalla v45 si cerca nei file del PC (come li legge
  l'editor), solo tra quelle il cui modello c'è sulla Switch, e la copia si fa mentre si converte il
  file dei blocchi. Se non ne trova, il rapporto dice quante entità ha guardato e perché le ha
  scartate.
- Dipendenze della grafica (dalla v40): alla fine, dopo `--solo-usati`, per ogni modello e
  materiale preso dagli originali Switch si controlla che i materiali e le texture che usa siano
  nell'archivio o negli archivi comuni del gioco, compresi quelli indicati solo per hash con nomi
  che il PC non conosce (il ciclo delle dipendenze li saltava). Quelli che mancano si aggiungono
  dall'archivio Switch del file che li usa, o da un altro archivio che li ha: sezione `DIPENDENZE
  DELLA GRAFICA AGGIUNTE ALLA FINE`. Senza, quella grafica resta senza materiale o senza texture.
  I modelli nominano il file del loro materiale per intero (`insieme,modello,opzioni::materiale`,
  per esempio `Crash_Crates_materials,Crash_Crate_Iron,0000400::Iron`), quindi questo controllo li
  vede tutti. (La v47 li trattava come `insieme::materiale` e toglieva tutte le sostituzioni dei
  materiali, che servono per esempio al conto alla rovescia delle TNT, all'orso invisibile e
  all'ologramma del bonus, senza cambiare il pavimento nero: dalla v48 non c'è più.)
- Luci, volumi di colore e box visivi (dalla v48): la sezione `LUCI, VOLUMI DI COLORE E BOX VISIVI`
  elenca le luci (`CPointLight`, `CBoxLight`), i volumi di colore (`CTintSphere`) e i box visivi
  (`CVisualDataBox`) di tutti i file del livello, con posizione, raggio o misure (per la scala
  dell'entità), colore e intensità, la distanza dalla partenza (`PlayerStart`) e se la contengono;
  prima quelli che contengono la partenza e quelli scuri (`SCURO`: volume di colore quasi nero, luce
  negativa). Un volume di colore scuro scurisce le decorazioni che ha dentro: pavimento e muri neri
  fino al bordo sfumato della sfera, Crash, casse e nemici normali. Nel Level 10 la cupola nera
  sparisce togliendo `Black01`, `Black02` e `Black03` dall'illuminazione di Ruination
  (`--togli-entita L219_Ruination_lighting:*`); sotto, però, il pavimento non c'è (vedi le occlusioni).
- Occlusioni (dalla v49): sulla console il gioco nasconde le parti del livello che non si vedono da
  dove sta Crash, con zone (`COcclusionTrigger`: quando Crash o la telecamera ci entra) che attivano
  gruppi (`COcclusionBox`: le decorazioni e gli oggetti da nascondere, o da mostrare se il gruppo è
  invertito). Quelle copiate dall'editor con un file di un altro livello (nel Level 10 le 2 zone e i
  4 gruppi di `L202_SnowGo_Reflections`) sono fatte per quel livello: nel livello nuovo nascondono
  decorazioni a caso, mentre Crash, casse e nemici restano. Nel Level 10 i 4 gruppi di Snow Go non
  elencavano entità ma erano attivi fin dall'inizio, con i loro limiti: la partenza del Level 10 sta
  fuori e il pavimento non si vedeva. Spente le occlusioni (e tolti i `Black` di Ruination) il
  pavimento si vede. Sul PC non contano. Dalla v49 si spengono: zone senza gruppi da attivare,
  gruppi senza entità da nascondere, non invertiti e non attivi; `--tieni-occlusioni` le lascia come
  sono. La sezione `OCCLUSIONI` le elenca com'erano (posizione, limiti, opzioni, entità che
  nascondevano, se contengono la partenza) e `DECORAZIONI VICINO ALLA PARTENZA` le 60 entità con un
  modello più vicine alla partenza, con quello che può nasconderle (`NELLE OCCLUSIONI`, `NASCOSTA`,
  importanza per la distanza, scala).
- Tessere dei bonus di Crash 1 (dalla v51): le tessere (Tawna, Brio, Cortex) che escono dalle casse
  bonus le conta il gestore delle tessere, un componente di Crash e Coco
  (`vsc/common/Characters/Crash1_BonusRoundManager_c.igz`): a ogni tessera presa aumenta il
  contatore del suo tipo (`C1_BonusCount_Tawna`, `_Brio`, `_Cortex` in `gamevariables`) e al terzo
  accende il teletrasporto del bonus con lo stesso tipo (tag `Teleporter_Tawna`, ...), che va
  raggiunto per entrare nel bonus. All'avvio, però, il gestore chiede se il gioco è Crash 1
  (`CCrashBandicootNodes::checkCrashGame`) e negli altri giochi si toglie da solo
  (`findComponent2`, `removeComponent`): con `--gioco crash3` (o crash2) le tessere prese non si
  contano e il bonus non parte. Dalla v51, con `--nuovo`, se il livello ha tessere o teletrasporti
  di Crash 1 e non è di Crash 1, la registrazione mette in `update.pak` (e nei file `update/` del
  livello, che NST Pak Manager usa) una copia dello script con quel ramo staccato: il gestore resta
  e conta. Nei livelli senza tessere non fa niente, perché nessuno gli manda i messaggi delle
  tessere. Lo script viene riscritto con le sue dipendenze e riletto per controllo; la sezione
  `REGISTRAZIONE DEL LIVELLO` lo dice. `--tessere-solo-crash1` lo lascia com'è.
  HUD delle tessere (dalla v52): quando prendi qualcosa l'HUD di Crash
  (`vsc/common/CrashBandicoot_HUD_c.igz`) mostra in alto la riga dei collezionabili, quella di
  Crash 1 (con le tessere, `ui/crash_hud.igz`) solo se il gioco è Crash 1
  (`CDebugNodes::debugCheckBuildYear`), negli altri giochi quella di Crash 2 e 3: con
  `--gioco crash3` le tessere si contavano (v51) ma non si vedevano. Insieme al gestore, la
  registrazione mette in `update.pak` una copia dell'HUD che fuori da Crash 1, se hai almeno una
  tessera (un contatore `C1_BonusCount_*` maggiore di 0), mostra la riga di Crash 1 al posto di
  quella di Crash 2 e 3 (stanno tutte e due in alto: insieme potrebbero coprirsi), e nascondendo
  nasconde tutte e due. Nei livelli senza tessere i contatori restano a 0 e l'HUD fa quello che
  faceva; in Crash 1 non cambia niente. Lo script viene riscritto e riletto per controllo.
  `--tessere-senza-hud` lascia l'HUD com'è (le tessere si contano ma in alto non si vedono; il
  teletrasporto mostra comunque quante ne hai prese).
  La sezione `TESSERE DEI BONUS DI CRASH 1 (analisi)` (dalla v50) elenca i teletrasporti del livello
  e quelli degli originali di Crash 1 per confronto, le tessere per tipo, il gestore nei dati dei
  personaggi, l'HUD, i contatori (valore iniziale, durata) e gli script che guardano il gioco (anno,
  `checkCrashGame`), con una prova di riscrittura degli script delle tessere. Il file
  `<rapporto>_tessere.txt`, accanto al rapporto, ha gli script scritti per esteso: eventi, nodi
  numerati con i valori che leggono (costanti, campi, funzioni del gioco) e i loro rami.
- Oggetti che mancano (dalla v39): la sezione `OGGETTI CHE MANCANO NEI FILE USATI DAL LIVELLO`
  controlla ogni riferimento dei file convertiti a un oggetto di un altro file (anche quelli per
  hash) e elenca i file che ci sono, nell'archivio o negli archivi comuni del gioco, ma non
  contengono l'oggetto usato. In un file preso dagli originali Switch vuol dire che la Switch ce
  l'ha diverso dal PC (per esempio un insieme di materiali senza il materiale che un'entità usa:
  quella grafica resta senza materiale); in un file convertito di solito è un'entità tolta.
- `--solo-usati`: i file di altri livelli che l'editor mette nel pacchetto fanno parte del
  livello (il gioco carica tutte le loro entità, e lì dentro ci sono anche pezzi del livello come
  il pavimento), quindi restano. Da quei file toglie solo:
  - le decorazioni (entità con solo modello e collisione: rocce, muri, piante) che usano modelli
    che il gioco Switch non ha: sulla Switch non si vedrebbero comunque. Nemici, piattaforme e
    le altre entità restano anche se manca qualcosa; i prefab mancanti non contano, mancano
    anche nei livelli che funzionano;
  - i nemici dei livelli con la moto d'acqua (`Jetski_Enemy_...`: squalo, assistenti sulla barca
    e sull'albero con l'ancora, e quello che fanno comparire), se il livello non è con la moto
    d'acqua: con quelli di Tell No Tales, Tropical Escape resta su LOADING. I cannoni delle barche
    restano.

  I file di altri livelli fuori dal pacchetto non si toccano. Poi toglie dall'archivio modelli,
  materiali e texture che nessun file del livello usa più (i file usati si leggono da tutti i
  riferimenti e da tutti i testi degli igz, compresi i percorsi dei modelli; dalla v40 anche i
  riferimenti per hash con nomi che il PC non conosce, che prima non contavano). Il rapporto elenca
  le entità tolte (`FILE DI ALTRI LIVELLI CAMBIATI`) e la grafica tolta (`GRAFICA NON USATA`: le
  20 più grandi, e tutte in fondo al rapporto nelle righe `grafica non usata`).
  Un file da cui si tolgono tutte le entità resta fuori dall'archivio e dal pacchetto: fino alla
  v37 veniva scritto con la lista vuota, non si rileggeva e il livello restava su LOADING
  (`L304_BoneYard_Art` in Level 11). Se un file con le entità tolte non si rilegge, viene tenuto
  com'era, senza toglierle.
  Nella v35 l'opzione contava come file mancante anche `metaobject` (i tipi del motore) e
  toglieva tutti i nemici, i massi e le staccionate dei file di altri livelli: dalla v36 restano.
- Dipendenze della grafica aggiunte alla fine (sezione `DIPENDENZE DELLA GRAFICA AGGIUNTE ALLA
  FINE`): materiali e texture che i modelli e i materiali presi dagli originali Switch nominano
  e che mancano. Si prendono dall'archivio Switch da cui viene il file che li usa. Fino alla v59,
  se quell'archivio non li aveva, si prendevano dall'archivio di un altro livello: nel Level 7 i
  cristalli di Koala Kong nominano l'insieme di materiali `C1_KoalaKong_Cave_materials`, che
  l'archivio di Koala Kong non ha (lì il gioco funziona senza) e che ha solo Stormy Ascent;
  preso da lì, dopo il caricamento lo schermo restava nero (il menu di pausa andava). Dalla
  v60 una dipendenza che l'archivio del file che la usa non ha non si aggiunge: il rapporto la
  elenca come `NON AGGIUNTA`. `--dipendenze-altri-livelli` (prova) le prende lo stesso, come
  prima.
- `--togli-entita [file:]testo` (prova, si può ripetere): toglie dai file di altri livelli (nel
  pacchetto e fuori) le entità che hanno il testo nel nome, nel tipo, nei componenti o nei file
  che usano. Con `file:` solo dai file con quel testo nel percorso; `*` le toglie tutte. Serve a
  trovare quali entità bloccano il gioco, guardando la sezione `CONTENUTO DEI FILE DI ALTRI
  LIVELLI`: per esempio `--togli-entita L318_TellNoTales:*` lascia il file ma senza entità,
  `--togli-entita L305_MakinWaves:jetski` toglie da Makin' Waves quelle che parlano di jetski.
  Senza `file:` vale solo per i file di altri livelli; dalla v48, indicando il file, anche per quelli
  del livello stesso (il file principale, l'illuminazione...), per provare senza l'editor: per esempio
  `--togli-entita Custom_Level_lighting:*` (il nome del file come nell'editor o come nell'archivio
  convertito). Dalla v58 il testo vale anche per il modello delle decorazioni (per esempio
  `--togli-entita B103_KoalaKong_Art:C1_Rock04` toglie le decorazioni con quel modello: i modelli
  di ogni file sono nella riga `modelli` della sezione `CONTENUTO DEI FILE DI ALTRI LIVELLI`) e
  `#k/n` toglie la parte k di n delle entità del file, nell'ordine del file (`#1/2` la prima metà,
  `#3/4` il terzo quarto, `#2-3/4` il secondo e il terzo): serve a trovare un'entità dimezzando,
  senza conoscerne il nome.
- `--originale-switch <testo>` (prova, si può ripetere): i file di altri livelli con il testo
  nel percorso si prendono dall'originale Switch invece di convertire la copia del PC, con le
  loro dipendenze dallo stesso archivio Switch. Se con l'originale il livello si carica e con la
  copia convertita no, il problema è nella conversione di quel file; se si blocca anche così, è
  nel contenuto (le entità). Il confronto nel rapporto dice se la copia del PC ha le stesse
  entità dell'originale.
- `--altri-livelli`: cosa fare dei file di altri livelli che l'editor copia nell'archivio quando
  usi i loro oggetti (per esempio `maps/Crash3/L309_TombTime/L309_TombTime.igz`):
  - `converti` (predefinito): si convertono le copie PC, come fino alla v12. Provato in gioco;
  - `originali`: si usano gli originali Switch, senza le loro dipendenze (provato con
    `Level 3`: schermo nero dopo il caricamento, per ora non usarlo);
  - `originali+dipendenze`: originali Switch con tutte le loro dipendenze (archivio molto più
    grande, il caricamento può bloccarsi).
- `--pc-originali`: cartella `archives` del gioco PC, se ce l'hai; i file identici agli
  originali PC vengono presi dagli originali Switch.

## Provare su Eden

Prima di copiare un livello sulla Switch si può provare su Eden. I file vanno nella cartella
delle mod del gioco (tasto destro sul gioco → cartella dei dati delle mod, di solito
`%APPDATA%\eden\load\0100D1B006744000\`), in una sottocartella per mod:

```
load\0100D1B006744000\Mio livello\romfs\archives\
├── miolivello.pak      ← l'archivio convertito (nome in minuscolo)
└── update.pak          ← solo con --nuovo: quello scritto dal convertitore
```

Un livello convertito con `--nuovo` si apre solo con l'avvio diretto: serve la patch e un
`debug.xml` con il nome del livello. Li crea `tools/crea_avvio_livello.py` di
[NST Pak Manager](https://github.com/alberdom88/nst-pak-manager#provare-un-livello-su-eden),
indicando l'archivio convertito:

```
python tools\crea_avvio_livello.py "%APPDATA%\eden\dump\0100D1B006744000" "out\miolivello.pak"
```

La cartella `avvio_livello\eden\NST avvio livello` va copiata nella cartella delle mod. Tieni
un solo `update.pak` tra le mod attive; per tornare al gioco normale togli `debug.xml`.

Il nome dell'archivio conta: il gioco apre `archives/<livello in minuscolo>.pak` e la romfs
distingue maiuscole e minuscole, quindi `Custom_Level.pak` non viene trovato e il gioco resta
sulla schermata di caricamento. Il convertitore e `crea_avvio_livello.py` avvisano se il nome
non è quello giusto; sulla Switch NST Pak Manager (1.8.1 o successivo) lo sistema da solo.

### Livelli grandi

La Switch ha molta meno memoria e potenza del PC. Un livello fatto sul PC può pesare molto più
di quelli originali, soprattutto perché l'editor copia per intero i file degli altri livelli da
cui prendi oggetti, anche uno solo: un livello con oggetti di dieci livelli carica la grafica di
dieci livelli.

La sezione `MEMORIA` del rapporto dice quanto pesa il livello, quanto ne arriva da altri livelli
e quali file sono più grandi, e lo confronta con il livello originale da cui viene la zone info
e con i livelli originali più grandi. Se il livello è molto più grande:

nell'editor usa oggetti di meno livelli diversi (meglio dallo stesso gioco o dal livello di
partenza) e togli quelli che non servono. Le aree di memoria della zone info sono le stesse per
tutti i livelli originali (200 MB e 30 MB); dalla v56 `--memoria 300` le allarga, per provare
se un livello si blocca perché ha finito la memoria.

I rallentamenti dovuti a troppi oggetti, luci o effetti non si risolvono con la memoria: vanno
alleggeriti nell'editor.

### Schermo nero dopo il caricamento

Se il livello carica ma lo schermo resta nero, mentre il menu di pausa (+) funziona, il gioco va
ma non disegna il livello (il Level 7 di Oichi, anche senza l'intro di Crash 3). Le sezioni del
rapporto che aiutano a capire perché (dalla v56):

- `MEMORIA`: anche le texture, che in un livello fatto con oggetti di tanti livelli sono quasi
  tutta la memoria: quante sono (MB e file), quante ne hanno gli archivi dei livelli originali,
  quante stanno anche negli archivi comuni del gioco e da quale archivio Switch sono prese (dalla
  v57 detto chiaro: se una texture sta in più archivi conta il primo in ordine alfabetico, che
  non è il livello da cui viene la decorazione);
- `TEXTURE PER FILE` (dalla v57): per ogni file in `maps/` le texture che si porta dietro
  (modelli, materiali, personaggi ed effetti che nomina, con le loro texture), "solo sue" (nessun
  altro file le usa: escono dal livello togliendo le entità del file, con `--togli-entita
  <file>:*` e `--solo-usati`) e in tutto. Per alleggerire la memoria delle texture si comincia
  dai primi;
- `LUCI, VOLUMI DI COLORE E BOX VISIVI`: quante luci e volumi di colore per file. Un file di luci
  di un altro livello copiato dall'editor PC può averne più dell'originale Switch, alleggerito
  (`L320_TombWader_lighting`: 104 entità sul PC, 84 sulla Switch);
- `PARTENZA E TELECAMERA`: la telecamera con cui comincia il livello, se c'è, i suoi valori e,
  per una telecamera su una spline, dove sta la spline rispetto alla partenza; poi i box visivi
  che contengono la partenza, con i valori che cambiano (esposizione, luminosità, nebbia,
  distanze a cui spariscono le decorazioni...);
- `VALORI IMPOSSIBILI NEI FILE CONVERTITI`: NaN, infiniti o numeri enormi negli oggetti dei file
  convertiti, di solito dati letti nel posto sbagliato: uno solo in una luce o nella telecamera
  basta a far diventare nero lo schermo.

Prove, una alla volta, per capire da dove viene:

1. `--togli-entita "*"`: toglie tutte le entità dei file di altri livelli (restano i file del
   livello: partenza, telecamera, casse, piattaforme, luci del livello) e, con `--solo-usati`,
   anche la loro grafica. Se il livello si vede (magari senza pavimento) il problema viene da
   quello che è preso da altri livelli (memoria, luci, un'entità: si rimettono a metà con
   `--togli-entita <file>:*` sui singoli file); se resta nero è nei file del livello;
2. `--memoria 300`: area di memoria del livello più grande (se la Switch non ce l'ha il gioco
   potrebbe bloccarsi al caricamento: anche questo dice qualcosa);
3. `--escludi L320_TombWader_lighting --escludi L219_Ruination_lighting`: senza i file di luci
   presi da altri livelli.

Mentre lo schermo è nero conviene anche ascoltare: se la musica parte e premendo salto o
giravolta si sentono i suoni di Crash, il livello va e manca solo l'immagine (telecamera,
grafica, memoria delle texture); se non si sente niente il livello non è partito davvero.

Con il Level 7 (v56): con `--togli-entita "*"` l'immagine c'è (texture da 107 a 65 MB), senza i
file di luci di altri livelli resta nera. Quindi il problema viene da quello che il livello
prende da altri livelli, non dalle luci: o la memoria delle texture (107 MB nell'archivio del
livello; il Level 3, che si vede, ne ha 94; gli archivi dei livelli originali arrivano a 69, ma
quelli prendono anche texture dagli archivi comuni) o un'entità. Le prove della v57 per
separare le due cose:

4. `--togli-entita "*" --prova-texture 60`: come la prova 1 (che si vede) ma con 60 MB di
   texture in più che nessuno usa: se diventa nero è la memoria delle texture;
5. senza le decorazioni prese da altri livelli (`--togli-entita "_Art:*" --togli-entita
   "_Meshes:*" --togli-entita "_Geo:*" --togli-entita "Design_Core_Set:*" --togli-entita
   "L105_Upstream.igz:*"`), con nemici, piattaforme e ostacoli: se si vede il problema è nelle
   decorazioni (di solito la memoria), se resta nero è tra nemici, piattaforme e ostacoli.

Risultati con il Level 7 (v57): la prova 4 si vede (non è la memoria delle texture), la 5 pure
(è nelle decorazioni) e togliendo solo le decorazioni di `B103_KoalaKong_Art` (612 entità: sassi,
cristalli e stalagmiti di Koala Kong, sulla Switch l'originale ne ha 128) l'immagine torna. Per
trovare quali (dalla v58): per tipo, con il nome o il modello (`--togli-entita
B103_KoalaKong_Art:crystal`, `...:stalag`), o a metà (`...:#1/2`, `...:#2/2`, poi a quarti
dentro la metà giusta). Nel rapporto la riga `modelli` e la riga `materiali sostituiti` del file
dicono quali modelli e quali sostituzioni di materiale ci sono, e i `VALORI IMPOSSIBILI` anche le
scale quasi zero, enormi o negative delle entità.

Con la v58: togliendo solo i cristalli di Koala Kong (`B103_KoalaKong_Art:crystal`, 56 entità con
i modelli `C1_B103_CaveCrystals` e `C1_Cave_Crystals`) l'immagine torna. I loro materiali sono
dell'insieme `C1_KoalaKong_Cave_materials`, il cui file l'archivio PC non aveva: il convertitore
l'ha aggiunto alla fine (`DIPENDENZE DELLA GRAFICA AGGIUNTE ALLA FINE`) prendendolo con le sue
texture dall'archivio di un altro livello, Stormy Ascent (`l128_stormyascent`), perché è l'unico
che lo ha. Negli altri livelli convertiti quella sezione era vuota. Prove della v59 per capire se
è quell'aggiunta o sono i cristalli stessi: `--escludi C1_KoalaKong_Cave_materials.igz` (i
cristalli restano, l'insieme di Stormy Ascent no) e i due modelli separati
(`B103_KoalaKong_Art:C1_Cave_Crystals`, `B103_KoalaKong_Art:C1_B103_CaveCrystals`), con
`--descrivi KoalaKong_Cave_materials` per vedere nel rapporto com'è fatto quell'insieme.

Con la v59 si vede solo la prima: è l'insieme preso da Stormy Ascent. I materiali dei cristalli
(file `C1_KoalaKong_Cave_materials,<materiale>,<opzioni>` dell'archivio di Koala Kong) sono
completi da soli e nominano l'insieme solo come "proprietario"; con l'insieme di un altro livello
quel riferimento porta a materiali diversi. Dalla v60 il convertitore non prende più dipendenze
dall'archivio di un altro livello quando quello del file che le usa non le ha (vedi "Dipendenze
della grafica aggiunte alla fine"): il Level 7 si converte senza opzioni di prova.

## Altri comandi

Servono a verificare e mettere a punto la conversione, non per l'uso normale:

```
NST.exe --switch layout [report.txt]
NST.exe --switch struttura <cartella_dump_switch> [report.txt] [--pak nome] [--max N]
NST.exe --switch verifica <file_pc.pak> <cartella_dump_switch> [report.txt] [--max N]
NST.exe --switch riscrivi <archivio_switch.pak> <output.pak> [report.txt] [--igz nessuno|maps|tutti]
```

- `layout`: confronta le dimensioni calcolate con quelle reali di tutti i tipi noti, elenca
  i campi solo PC e controlla che i campi non si sovrappongano (lo esegue anche la build).
- `struttura`: legge i file del gioco Switch con la struttura calcolata e, per ogni tipo,
  elenca i byte diversi da zero che non cadono in nessun campo conosciuto. `--pak` limita la
  lettura agli archivi il cui nome contiene il testo indicato.
- `verifica`: per ogni igz del `.pak` PC che esiste anche nel gioco Switch legge il file PC
  con la struttura PC e quello Switch con la struttura calcolata e confronta tutti i valori;
  riscrive poi il file PC in formato Switch e lo rilegge.
- `riscrivi`: riscrive un archivio Switch originale con l'editor, per provare in gioco la
  scrittura degli archivi (`--igz nessuno`) e degli igz (`maps`: file del livello e
  collisione statica; `tutti`: tutti tranne le texture, compresi i file Havok). Il rapporto
  dice quanti igz riscritti sono identici byte per byte all'originale.

## Come funziona la conversione

Il gioco PC è compilato con MSVC, quello Switch con Clang (ABI Itanium). Clang riusa il
padding finale della classe base: i campi di una classe derivata partono dalla fine dei dati
della base (per `igObject` 12 byte) e non dalla sua dimensione arrotondata (16 byte).
`src/Utils/SwitchLayout.cs` ricalcola così la struttura Switch di ogni oggetto a partire da
quella PC:

- i dati che l'editor non descrive (dimensione PC più grande dei campi) vengono mantenuti, e
  i campi che l'editor ridichiara dentro quei dati (per esempio
  `igBoolMetaFieldInstance._default`) si spostano con loro;
- i campi che esistono solo su PC (Steam, mouse, opzioni grafiche PC...) vengono trovati
  confrontando con le dimensioni reali estratte dal gioco (`assets/switch_sizes.json`): un
  campo viene tolto solo se corregge una dimensione senza sbagliarne nessun'altra;
- gli offset CTR (PS4, anch'esso Clang) e quelli PC servono da riserva.

Cambiano anche: versione dell'archivio (12), cartella interna (`nx`), intestazione LZMA
(4 byte), piattaforma negli igz (2). I file Havok della Switch (collisione statica) usano la
stessa struttura di quelli CTR (`reusePaddingOptimization`), che l'editor conosce già.

Quando un livello cambia nome o cartella (`--nuovo` con un nome diverso da quello
dell'editor, `--gioco`, `--come-originale`, `--sostituisci`) cambiano solo i percorsi dei file
dei livelli (`maps/`, `models/maps/`, `packages/generated/maps/` e la zone info in `update/`):
la cartella del livello e il nome del livello nei nomi dei file, solo come parola intera
(`Custom_Level_Art.igz`, `StaticCollision_Custom_Level.hkx`, non `Custom_Level2`). Gli asset
(modelli, script, texture) tengono il loro percorso, così si trovano tra gli originali Switch.
Vengono rinominati anche i riferimenti interni: namespace degli igz (tutti, anche quelli dentro
liste e memorie), nomi nella collisione statica, chiavi della tabella delle collisioni
(`hash del namespace << 32 | hash dell'oggetto`) e voci del pacchetto.

Il codice è in `src/Switch/SwitchTools.cs` (comandi) e `src/Utils/SwitchLayout.cs`
(struttura degli oggetti). La build per Windows la produce GitHub Actions
(`.github/workflows/switch-build.yml`).
