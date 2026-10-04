# Strumenti per la versione Nintendo Switch

Fork di Crash NST Maker con il supporto per i file della versione Switch (archivi
versione 12, `GameVersion.NSX`).

## Perche' la struttura cambia

Il gioco PC e' compilato con MSVC, quello Switch con Clang (ABI Itanium). Clang riusa
il padding finale della classe base: i campi di una classe derivata partono dalla
fine dei dati della base (per `igObject` 12 byte) e non dalla sua dimensione
arrotondata (16 byte). `src/Utils/SwitchLayout.cs` ricalcola cosi' la struttura
Switch di ogni oggetto a partire da quella PC e la controlla con le dimensioni reali
estratte dal gioco (`assets/switch_sizes.json`), usando come riserva gli offset CTR (PS4,
anch'esso Clang).

Cambiano anche: versione dell'archivio (12), cartella interna (`nx`), intestazione
LZMA (4 byte), piattaforma negli igz (2).

## Comandi

```
NST.exe --switch layout [report.txt]
NST.exe --switch verifica <file_pc.pak> <cartella_dump_switch> [report.txt] [--max N]
NST.exe --switch converti <file_pc.pak> <cartella_dump_switch> <output.pak> [report.txt]
```

- `layout`: confronta le dimensioni calcolate con quelle reali di tutti i tipi noti.
- `verifica`: per ogni igz del .pak PC che esiste anche nel gioco Switch legge il file
  PC con la struttura PC e quello Switch con la struttura calcolata e confronta tutti i
  valori; riscrive poi il file PC in formato Switch e lo rilegge.
- `converti` (sperimentale): crea un archivio Switch prendendo dagli originali Switch
  gli asset condivisi e convertendo i file del livello. Le collisioni Havok e la grafica
  non vengono ancora convertite.

La build per Windows la produce GitHub Actions (`.github/workflows/switch-build.yml`):
artifact `NST-Switch-win-x64`.
