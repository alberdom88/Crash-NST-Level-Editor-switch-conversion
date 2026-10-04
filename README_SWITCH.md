# Strumenti per la versione Nintendo Switch

Fork di Crash NST Maker con il supporto per i file della versione Switch (archivi
versione 12, `GameVersion.NSX`).

## Perche' la struttura cambia

Il gioco PC e' compilato con MSVC, quello Switch con Clang (ABI Itanium). Clang riusa
il padding finale della classe base: i campi di una classe derivata partono dalla
fine dei dati della base (per `igObject` 12 byte) e non dalla sua dimensione
arrotondata (16 byte). `src/Utils/SwitchLayout.cs` ricalcola cosi' la struttura
Switch di ogni oggetto a partire da quella PC:

- i dati che l'editor non descrive (dimensione PC piu' grande dei campi) vengono
  mantenuti, e i campi che l'editor ridichiara dentro quei dati (per esempio
  `igBoolMetaFieldInstance._default`) si spostano con loro;
- i campi che esistono solo su PC (Steam, mouse, opzioni grafiche PC...) vengono
  trovati confrontando con le dimensioni reali estratte dal gioco
  (`assets/switch_sizes.json`): un campo viene tolto solo se corregge una dimensione
  senza sbagliarne nessun'altra;
- gli offset CTR (PS4, anch'esso Clang) e quelli PC servono da riserva.

Cambiano anche: versione dell'archivio (12), cartella interna (`nx`), intestazione
LZMA (4 byte), piattaforma negli igz (2).

## Comandi

```
NST.exe --switch layout [report.txt]
NST.exe --switch struttura <cartella_dump_switch> [report.txt] [--pak nome] [--max N]
NST.exe --switch verifica <file_pc.pak> <cartella_dump_switch> [report.txt] [--max N]
NST.exe --switch riscrivi <archivio_switch.pak> <output.pak> [report.txt] [--igz nessuno|maps|tutti]
NST.exe --switch converti <file_pc.pak> <cartella_dump_switch> <output.pak> [report.txt]
        [--come-originale | --sostituisci <livello>] [--senza-base] [--pc-originali <cartella_archives_pc>]
```

- `layout`: confronta le dimensioni calcolate con quelle reali di tutti i tipi noti,
  elenca i campi solo PC e controlla che i campi non si sovrappongano.
- `struttura`: legge i file del gioco Switch con la struttura calcolata e, per ogni
  tipo, elenca i byte diversi da zero che non cadono in nessun campo conosciuto
  (campi mancanti o fuori posto). `--pak` limita la lettura agli archivi il cui nome
  contiene il testo indicato.
- `verifica`: per ogni igz del .pak PC che esiste anche nel gioco Switch legge il file
  PC con la struttura PC e quello Switch con la struttura calcolata e confronta tutti i
  valori; riscrive poi il file PC in formato Switch e lo rilegge. Con l'archivio
  originale PC di un livello (stessi contenuti della Switch) le differenze che restano
  sono solo quelle di piattaforma.
- `riscrivi`: riscrive un archivio Switch originale con l'editor, per provare in gioco
  la scrittura degli archivi (`--igz nessuno`) e degli igz (`maps`: file del livello e
  collisione statica, `tutti`: tutti tranne le texture, compresi i file Havok). Il rapporto dice quanti igz riscritti sono
  identici byte per byte all'originale.
- `converti` (sperimentale): crea un archivio Switch partendo dall'archivio Switch del
  livello originale. I file di posizionamento del livello (`maps/`) vengono convertiti
  dal PC; gli asset vengono presi dagli originali Switch, insieme alle loro dipendenze
  Switch (per esempio le texture Switch dei materiali, che hanno nomi diversi da quelle PC).
  - `--pc-originali`: cartella `archives` del gioco PC; i file identici agli originali
    PC vengono presi dagli originali Switch.
  - `--come-originale`: il livello creato dall'editor da un livello esistente
    (`..._Custom`) riprende il nome del livello originale, cosi' l'archivio puo' sostituirlo.
  - `--sostituisci <livello>`: per i livelli nuovi (`Custom_Level`) o per metterli al posto
    di un altro livello: cartella, nomi dei file e riferimenti diventano quelli del livello
    scelto (per esempio `L101_NSanityBeach`), e l'archivio va installato con il suo nome.
  - `--senza-base`: non aggiunge gli asset dell'archivio Switch del livello sostituito.
  La collisione statica del livello (Havok) viene convertita: i file Havok della Switch
  usano la stessa struttura di quelli CTR (`reusePaddingOptimization`), che l'editor
  conosce gia'. La grafica non viene convertita: si usano gli originali Switch.

La build per Windows la produce GitHub Actions (`.github/workflows/switch-build.yml`):
artifact `NST-Switch-win-x64`. Il log della build e il rapporto `layout` vengono
anche salvati nel ramo `ci-reports`.
