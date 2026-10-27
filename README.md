# CSpect Compat

A plugin for the [CSpect](https://mdf200.itch.io/cspect) ZX Spectrum Next emulator by Mike Dailly
that helps games which worked in CSpect 2, or work on the Next, but crash in CSpect 3.

It is used by [CSpect Launcher](https://github.com/Saxabar/CSpectLauncher), a game launcher for CSpect
(Windows, Linux and macOS) where each fix can be turned on per game from the right-click menu.

The fixes are chosen per game with the `CSPECT_COMPAT` environment variable (a comma-separated list
of options). Without the variable, they come from [Compat.ini](#without-cspect-launcher-compatini), a
list of games found by the MD5 of their file. Without either, the plugin does nothing, so it can stay
installed for all games.

## Options

| Option | What it does | Games |
|---|---|---|
| `l2bank9` | Layer 2 starts at 16K bank 9 (Next register `$12`), as in older Next cores. CSpect 3 sets it to 8 when it loads a `.snx` file, so games that never set it draw over their own data. | Montana Mike, Dweebs Drop |
| `nodivmmc` | DivMMC automap off (bit 4 of Next register `$0A`). CSpect 3 maps the DivMMC memory when the CPU reaches `$0038` even when RAM is paged in place of the ROM, so a game that calls code there runs the DivMMC memory instead (random values: the game starts only sometimes). Loading files with esxDOS still works, as CSpect serves it itself. | Deltas Shadow |
| `dd01nop` | `DD 01` in the game's `.nex` becomes a 2-byte NOP, as in CSpect 2, where it was the debugger's BREAK opcode and was skipped when the debugger was off. CSpect 3 uses `FD 00` for BREAK, so breakpoints left in a game run as `LD BC,nn` and swallow the next instruction. The `.nex` is the one in `CSPECT_COMPAT_FILE`, or else the one CSpect has loaded; only the memory is changed, before the game starts, and only where the bytes are still `DD 01`. The bytes are searched in all the banks, data included, so use it only for games that need it. | Elite Next |
| `zeroram` | The RAM outside the 128K snapshot (8K pages 16-255) is cleared before the game starts, as in CSpect 2. CSpect 3 fills it with random values, so a game that shows Layer 2 without clearing it shows them. Only for `.snx`/`.sna` games: a `.nex` can load its banks there. | Spice Invaderz |
| `divfirst` | DivMMC memory comes before the Layer 2 mapping, as on the Next. When Layer 2 is mapped for writing at `$0000` (port `$123B` bit 0), CSpect 3 sends the writes of DivMMC code (NextZXOS's interrupt handler and tape loader) to Layer 2, so NextZXOS loses its saved stack pointer and the machine resets. While DivMMC code runs, the plugin keeps those writes and gives them back when they are read. | 4K Race Next (loaded from tape with NextZXOS) |
| `im2ff` | The IM 2 vector is read at `I*256+$FF`, as on the Next, where the data bus reads `$FF`. CSpect 3 reads it at `I*256+$00`, so a game that sets only the vector at `$xxFF` jumps to a random address at the first interrupt. Right after CSpect has taken the frame interrupt, the plugin sends the CPU through the vector at `$xxFF`; the game's memory is not changed. | 4K Race Next, Manic Miner Trick Or Treat AY, Space Racing |
| `sdstream` | Streaming from the SD card done by the plugin instead of NextZXOS. NextZXOS's `DISK_STRMSTART` fails in CSpect 3, so a game that streams its music from the SD card stops on a black screen. `RST $08` `DISK_STRMSTART` / `DISK_STRMEND` return at once without error; while the stream is on, the game's own reads of the SD card port `$EB` get the blocks of the file it mapped with `DISK_FILEMAP`, read from the game's folder on the PC (`CSPECT_COMPAT_FILE` gives the folder, else the current one; silence if the file is not there). NextZXOS's own SD card reads are not changed. The game must be started with NextZXOS. | The Way of the Exploding Fist |
| `divnmi` | DivMMC NMIs (Next register `$02` bit 2) page in the DivMMC memory, as on the Next: from `$0066` to the `RETN`, `$0000-$1FFF` is the DivMMC ROM and `$2000-$3FFF` the DivMMC RAM bank of port `$E3`, as the game wrote them (the RAM with CONMEM, port `$E3` bit 7; the ROM in config mode, Next registers `$03` and `$04`), and a new request while it is paged in is ignored. CSpect 3 runs the game's RAM at `$0066` instead, so a game that plays its sound from DivMMC NMIs crashes, or stays on the title screen waiting for the sound. The plugin keeps its own copy of that memory. | Atic Atac Next |

Several options can be combined, e.g. `CSPECT_COMPAT=l2bank9,nodivmmc`.

## Installation

1. Build `Compat.dll` (see below) or download it from the [Releases](../../releases) page.
2. Copy it into the CSpect folder, next to `CSpect.exe` (with `Compat.ini`, to use it without CSpect
   Launcher).
3. Start CSpect with the variable set, for example from a command prompt:

   ```
   set CSPECT_COMPAT=l2bank9
   CSpect.exe -w3 -vsync -zxnext -mmc=.\ MontanaMike.snx
   ```

For `dd01nop`, `CSPECT_COMPAT_FILE` can give the path of the game's `.nex`; without it, the plugin uses
the file loaded by CSpect.

When the plugin is active, the CSpect console shows `Compat added (l2bank9)`.

[CSpect Launcher](https://github.com/Saxabar/CSpectLauncher) sets the variable for the games that
need it.

## Without CSpect Launcher: Compat.ini

To start games straight from CSpect, without setting a variable each time, copy
[Compat.ini](Compat.ini) next to `Compat.dll`. When CSpect loads a `.nex`, `.snx` or `.sna` file, the
plugin computes its MD5 and uses the options listed for it:

```
; Montana Mike (MontanaMike.snx / MontanaMike.sna)
4d94f98ab1ee9e3bdf879c5599f78a36=l2bank9
```

The file already lists the games above that need a fix when they are loaded by CSpect (Elite Next is
left out: `dd01nop` is better turned on by hand). A renamed file is still found; a different version of
a game has another MD5 and must be added. The CSpect console shows the MD5 of each file it loads
(`Compat: MontanaMike.snx MD5 4d94...`); on Windows it can also be found with
`certutil -hashfile <file> MD5`.

`default=` gives the options for every file not listed. Games started from NextZXOS (the Browser, or a
tape loaded with the Tape Loader) are not found by MD5, because CSpect has loaded the SD card image, not
the game: for those, put the options in `default=`, e.g. `default=divfirst,im2ff` for 4K Race Next.

`Compat.ini` is not used when `CSPECT_COMPAT` is set: CSpect Launcher's choices come first.

## Building

`build.bat` compiles the plugin with the C# compiler included in Windows (.NET Framework 4); it needs
`Plugin.dll` from the CSpect folder:

```
build.bat "C:\path\to\CSpect"
```

The result is `bin\Compat.dll`.

## Fixed esxDOS plugin

Some fixes cannot be made from a separate plugin, because they are in the esxDOS emulation of CSpect
3, which is itself a plugin (`esxDOS.dll`, open source). The [esxDOS](esxDOS) folder holds Mike
Dailly's esxDOS plugin source from [CSpectPlugins](https://github.com/mikedailly/CSpectPlugins/tree/master/esxDOS)
with these changes in `CRST8.cs`:

| Change | Why | Games |
|---|---|---|
| `DISK_FILEMAP` ($85) returns `HL` = address after the last entry, instead of 6 bytes further | games that check the length of the map (e.g. one entry for a file in one piece) stopped | Dreamworld Pogie Demo |
| Game reads of the SD card (`CMD18`, after `DISK_FILEMAP`): the 2 CRC bytes after each 512-byte block, instead of over the block's bytes 508-509 | the first block of each read had two `$FF` bytes in it: in music streamed from the SD card, a click every block (a constant whine) | Atic Atac Next |
| `eCPUFlags : UInt16` written as `: ushort` | same type, so that the C# 5 compiler included in Windows accepts it | - |

To install it, rename CSpect's `esxDOS.dll` (e.g. to `esxDOS.dll.orig`, to go back to it later) and copy
the fixed one next to `CSpect.exe`. It is used by every game, with or without `CSPECT_COMPAT`.

Download `esxDOS.dll` from the [Releases](../../releases) page, or build it with `build-esxdos.bat`,
which also needs `Plugin.dll` from the CSpect folder:

```
build-esxdos.bat "C:\path\to\CSpect"
```

The result is `bin\esxDOS.dll`.

Known limit, not fixable from a plugin: CSpect does not pass block I/O instructions (`INI`, `INIR`,
`OTIR`...) to plugins, so games that read streamed data with them (e.g. Dreamworld Pogie Demo's music)
get no data from the esxDOS streaming.

## Related projects

- [CSpect Launcher](https://github.com/Saxabar/CSpectLauncher): game launcher for CSpect, with covers,
  ratings and favourites; it sets `CSPECT_COMPAT` and `CSPECT_COMPAT_FILE` for the games that need a fix
- [CSpect Scanlines](https://github.com/Saxabar/CSpectScanlines): TV-style scanlines over the CSpect window

## License

GPL-3.0, see [LICENSE](LICENSE). The CSpect plugin interface (`Plugin.dll`) is by Mike Dailly,
MIT licensed: https://github.com/mikedailly/CSpectPlugins

The files in the [esxDOS](esxDOS) folder are Mike Dailly's esxDOS plugin with the changes listed
above, MIT licensed: see [esxDOS/LICENSE](esxDOS/LICENSE).
