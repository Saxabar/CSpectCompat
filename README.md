# CSpect Compat

A plugin for the [CSpect](https://mdf200.itch.io/cspect) ZX Spectrum Next emulator by Mike Dailly
that helps games which worked in CSpect 2 but crash in CSpect 3.

The fixes are chosen per game with the `CSPECT_COMPAT` environment variable (a comma-separated list
of options). Without the variable the plugin does nothing, so it can stay installed for all games.

## Options

| Option | What it does | Games |
|---|---|---|
| `l2bank9` | Layer 2 starts at 16K bank 9 (Next register `$12`), as in older Next cores. CSpect 3 sets it to 8 when it loads a `.snx` file, so games that never set it draw over their own data. | Montana Mike |
| `nodivmmc` | DivMMC automap off (bit 4 of Next register `$0A`). CSpect 3 maps the DivMMC memory when the CPU reaches `$0038` even when RAM is paged in place of the ROM, so a game that calls code there runs the DivMMC memory instead (random values: the game starts only sometimes). Loading files with esxDOS still works, as CSpect serves it itself. | Deltas Shadow |

Several options can be combined, e.g. `CSPECT_COMPAT=l2bank9,nodivmmc`.

## Installation

1. Build `Compat.dll` (see below) or download it from the [Releases](../../releases) page.
2. Copy it into the CSpect folder, next to `CSpect.exe`.
3. Start CSpect with the variable set, for example from a command prompt:

   ```
   set CSPECT_COMPAT=l2bank9
   CSpect.exe -w3 -vsync -zxnext -mmc=.\ MontanaMike.snx
   ```

When the plugin is active, the CSpect console shows `Compat added (l2bank9)`.

[CSpect Launcher](https://github.com/Saxabar/CSpectLauncher) sets the variable for the games that
need it.

## Building

`build.bat` compiles the plugin with the C# compiler included in Windows (.NET Framework 4); it needs
`Plugin.dll` from the CSpect folder:

```
build.bat "C:\path\to\CSpect"
```

The result is `bin\Compat.dll`.

## License

GPL-3.0, see [LICENSE](LICENSE). The CSpect plugin interface (`Plugin.dll`) is by Mike Dailly,
MIT licensed: https://github.com/mikedailly/CSpectPlugins
