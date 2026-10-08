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
   carica l'archivio convertito nella cartella MEGA (o del PC) e nell'app premi **Y** sul
   livello. Serve solo l'archivio del livello: `update.pak` lo crea l'app.

Il rapporto (`report.txt`) elenca cosa è stato convertito, cosa è stato preso dagli
originali Switch, cosa è stato saltato e, con `--nuovo`, cosa è finito nella registrazione
del livello (sezione *REGISTRAZIONE DEL LIVELLO*). Allegalo quando qualcosa non funziona.

## Comando `converti`

```
NST.exe --switch converti <file_pc.pak> <cartella_dump_switch> <output.pak> [report.txt]
        [--nuovo | --come-originale | --sostituisci <livello>]
        [--base <livello>] [--senza-base] [--altri-livelli converti|originali|originali+dipendenze]
        [--zoneinfo-da <livello>|pc] [--salvataggio originale|proprio] [--escludi <testo>]...
        [--ctr <cartella_dump_ctr_switch>] [--registra-in update|chunkinfos]
        [--pc-originali <cartella_archives_pc>]
```

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
  c'è `--sostituisci`); lo stesso se il livello nell'editor ha il nome di un originale
  (`L101_NSanityBeach`), perché i suoi file sono quelli dell'originale: salvalo con un nome
  nuovo o usa `--come-originale`. Come quando si preme *Play* nell'editor su un livello
  nuovo, il livello va registrato nel gioco (la sua *zone info* elencata nel pacchetto di `chunkInfos`, dentro
  `update.pak`). Il convertitore:
  - mette i file della registrazione nell'archivio del livello, nella cartella interna
    `update/` (non compressi): NST Pak Manager (1.7 o successivo) li unisce all'`update.pak`
    originale quando avvii il livello, quindi sulla Switch basta l'archivio del livello;
  - scrive anche `update.pak` accanto all'archivio (l'originale Switch più la
    registrazione), da usare **solo su Eden**, dove l'app non c'è.

  La zone info (i dati del livello per il gioco: a quale gioco appartiene, nome mostrato nel
  caricamento, salvataggio...) viene presa da un livello originale Switch, cambiando solo il
  nome e la voce di salvataggio: quella creata dall'editor, convertita, blocca il gioco
  all'avvio diretto. Il livello nuovo appartiene allo stesso gioco dell'originale (Crash 1, 2
  o 3) e ne eredita il resto, per esempio il nome mostrato nel caricamento (vedi
  `--zoneinfo-da` e `--salvataggio`). Le opzioni speciali dell'editor (personaggio, hub,
  veicoli) non sono ancora convertite.
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
  originale (`L112_RoadToNowhere_Custom`) quell'originale, altrimenti il primo livello dello
  stesso gioco (`L101_NSanityBeach`, `L201_TurtleWoods`, `L301_ToadVillage`). Con `pc` usa la
  zone info dell'editor convertita, che però blocca il gioco sul logo all'avvio diretto.
- `--salvataggio` (con `--nuovo`): `proprio` (predefinito) dà al livello una voce di
  salvataggio sua, con il suo nome; `originale` usa quella del livello da cui viene la zone
  info (gemme, casse e tempi finiscono su quel livello).
- `--registra-in` (con `--nuovo`, per le prove): dove registrare il livello. `update`
  (predefinito) scrive `update.pak` come l'editor PC; `chunkinfos` scrive invece una copia di
  `chunkInfos.pak` (l'archivio con le zone info di tutti i livelli del gioco) con dentro anche
  quella del livello nuovo: si installa al posto di `update.pak`.
- `--escludi <testo>`: lascia fuori dall'archivio i file il cui percorso contiene il testo (si
  può ripetere). Per esempio `--escludi Octane` toglie gli asset importati da Crash Team Racing,
  che la Switch non ha: il livello si carica senza quella parte di grafica.
- `--ctr <cartella>` (prova): dump di Crash Team Racing Nitro-Fueled per Switch. La grafica
  che il gioco Crash non ha (asset CTR importati nell'editor) viene cercata lì con lo stesso
  percorso (per i materiali anche con un suffisso diverso) e copiata nell'archivio, con le sue
  dipendenze CTR. Non è detto che il gioco Crash sappia leggere i file di CTR: il rapporto
  riporta versione e piattaforma dei loro igz.
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

Quando un livello cambia nome (`--come-originale`, `--sostituisci`) vengono rinominati anche
i riferimenti interni: namespace degli igz, nomi nella collisione statica, chiavi della
tabella delle collisioni (`hash del namespace << 32 | hash dell'oggetto`) e voci del
pacchetto.

Il codice è in `src/Switch/SwitchTools.cs` (comandi) e `src/Utils/SwitchLayout.cs`
(struttura degli oggetti). La build per Windows la produce GitHub Actions
(`.github/workflows/switch-build.yml`).
