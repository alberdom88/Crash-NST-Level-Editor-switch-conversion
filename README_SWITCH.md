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
        [--solo-usati] [--tieni-entita-modalita] [--togli-entita [file:]testo]...
        [--originale-switch <testo>]...
        [--ctr <cartella_dump_ctr_switch>] [--senza-intro | --aggiungi-intro]
        [--registra-in update|chunkinfos] [--gioco crash1|crash2|crash3] [--senza-modalita]
        [--riflessi auto|si|no] [--modello-blocchi <file>=<modello>]...
        [--memoria modello|max|<livello>] [--pc-originali <cartella_archives_pc>]
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
- `--memoria modello|max|<livello>` (con `--nuovo`): dimensione delle aree di memoria del livello
  nella zone info (`_levelPoolSize` e `_globalChunkPoolSize`). Con `modello` (predefinito) sono
  quelle del livello originale da cui viene la zone info; con `max` quelle del livello originale
  che ne ha di più; con il nome di un livello originale quelle di quel livello. Nel dump della
  versione 1.0.0 tutti i livelli hanno gli stessi valori (200 MB e 30 MB), quindi in pratica non
  cambia nulla.
- `--riflessi auto|si|no` (con `--nuovo`, dalla v41): riflessi planari nella zone info
  (`_planarReflectionsEnabled`). Il ghiaccio che riflette dei livelli di neve di Crash 2 (Snow Go,
  Cold Hard Crash, Bear It...) sulla Switch disegna il riflesso che il gioco calcola solo se la zone
  info li accende: con quella di un livello senza riflessi (N. Sanity Beach) il ghiaccio resta nero e
  sembra che non ci sia (Level 10: Crash scivola su un pavimento che non si vede). Con `auto`
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
  può ripetere).
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
- `--senza-intro` (prova): toglie l'intro che l'editor mette nei livelli di Crash 3 (Crash che
  esce dal portale, presa da Gone Tomorrow). È l'intro a far comparire Crash: se sulla Switch
  non parte, Crash non compare e non si può muovere.
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
- Modelli delle entità indicati per nome (dalla v42): i dati di molte entità (casse, piattaforme,
  blocchi di collisione) indicano il modello con un testo (`_modelName`, `_skinName`, per esempio
  `Levels\Crash1\crates\Crash_Crate_Nitro`), che i controlli sui riferimenti non vedevano. La
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
  un pavimento fatto solo di blocchi di collisione senza aprire l'editor.
- Dipendenze della grafica (dalla v40): alla fine, dopo `--solo-usati`, per ogni modello e
  materiale preso dagli originali Switch si controlla che i materiali e le texture che usa siano
  nell'archivio o negli archivi comuni del gioco, compresi quelli indicati solo per hash con nomi
  che il PC non conosce (il ciclo delle dipendenze li saltava). Quelli che mancano si aggiungono
  dall'archivio Switch del file che li usa, o da un altro archivio che li ha: sezione `DIPENDENZE
  DELLA GRAFICA AGGIUNTE ALLA FINE`. Senza, quella grafica resta senza materiale o senza texture
  (è il primo sospetto per il pavimento nero del Level 10, su cui Crash cammina senza vederlo).
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
- `--togli-entita [file:]testo` (prova, si può ripetere): toglie dai file di altri livelli (nel
  pacchetto e fuori) le entità che hanno il testo nel nome, nel tipo, nei componenti o nei file
  che usano. Con `file:` solo dai file con quel testo nel percorso; `*` le toglie tutte. Serve a
  trovare quali entità bloccano il gioco, guardando la sezione `CONTENUTO DEI FILE DI ALTRI
  LIVELLI`: per esempio `--togli-entita L318_TellNoTales:*` lascia il file ma senza entità,
  `--togli-entita L305_MakinWaves:jetski` toglie da Makin' Waves quelle che parlano di jetski.
  I file del livello stesso non si toccano (quelli si cambiano nell'editor).
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
tutti i livelli originali (200 MB e 30 MB), quindi `--memoria` non aiuta.

I rallentamenti dovuti a troppi oggetti, luci o effetti non si risolvono con la memoria: vanno
alleggeriti nell'editor.

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
