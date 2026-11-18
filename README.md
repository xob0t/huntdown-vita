# Huntdown Vita

[Huntdown](https://store.steampowered.com/app/598550/) on a PS Vita. A pipeline takes the PC release (Unity 2019.4.13f1) and rebuilds it as a Unity 2018.2 project, since 2018.2 is the last Unity with a Vita build module, then builds that for the Vita. It follows [Crow Country's port](https://github.com/xob0t/crow-country-vita) and [The Drifter's](https://github.com/xob0t/the-drifter-vita), and reuses their tools where they fit.

There are no game files in this repository. You bring your own copy, and the pipeline only reads it.

## Where it stands

Work in progress, but playable. The VPK builds, and on the console the game runs from its opening cutscene through the menus into the levels, which draw as on PC and play with the pad; Start and Select pause. A level takes about 15 s to load. The game runs at an almost locked 60 fps, with minor dips only in the heaviest scenes, when a lot of explosions and enemies are on screen. To get there, debris, blood and weather use smaller pools than on PC, effects, debris and weather are drawn as a few batched meshes instead of a sprite each, and the pad is read without Rewired's per-frame work. [docs/status.md](docs/status.md) has the details and the open issues.

## How it differs from the PC release

- The game's AssetBundles (its levels, area art and sound) are rebuilt for the target, under the same names.
- On the PS Vita, sound clips stay compressed in memory as HEVAG, the console's own format, instead of being decompressed when loaded, and the music streams. Wolfmother's voice in the mission briefings is the exception: it is PCM, decompressed when loaded, since her mouth moves with its samples.
- The pad uses Rewired's PS Vita map: R dashes, L does what Left Shoulder 1 does on a PC pad. Once the game has the pad, its button and stick reads are answered from the pad directly with that map, and Rewired's own update is skipped.
- Steamworks is replaced with a stub, so no player signs in to Steam.
- Local co-op is off: the second player can't join, and the menus leave out 2 PLAYERS and JOIN PLAYER 2. The resolution menu and the entries that quit the game are left out too, since the PS Vita has no use for them.

## Building

You need Windows 10 or 11 on x64, Huntdown from Steam, about 10 GB of free disk space, and a console on HENkaku or Ensō with VitaShell.

1. Install [uv](https://docs.astral.sh/uv/): open PowerShell from the Start menu and run

   ```powershell
   powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
   ```

2. Download [huntdown-vita.zip](https://github.com/xob0t/huntdown-vita/releases/latest/download/huntdown-vita.zip), right-click it and choose **Extract All** (or clone this repository).

3. Open the extracted `huntdown-vita` folder, right-click in it, choose **Open in Terminal** (on Windows 10: Shift + right-click, **Open PowerShell window here**), and run:

   ```powershell
   uv run build.py
   ```

   The VPK is `builds/hunt-2018.2.19f1-vita.vpk`. Install it with VitaShell.

A first build takes about 25 minutes and leaves about 6.5 GB in the folder. It doesn't install Unity or need a Unity license: it downloads Unity 2018.2.19f1's installers and extracts the files the build reads, and Sony's PS Vita SDK 3.570 from archive.org, with AssetRipper, the .NET 8 SDK and Mono.Cecil. A rerun picks up where the last one stopped.

- `--game <folder>` reads the game from that folder, if it isn't in your Steam library.
- `--vita-sdk <archive or folder>` uses your own copy of the SDK.
- `--unity` builds the VPK with the Unity editor instead. It installs the editor with its PS Vita module and patches its license check with [unigate](https://github.com/xob0t/unigate).
- `--windows` builds a Windows test player with the editor: `builds/hunt-2018.2.19f1/Huntdown.exe`.

[docs/build.md](docs/build.md) covers the setup, the pipeline's stages and the options; [docs/builder.md](docs/builder.md) the editor-free builder.

## License

The pipeline is MIT-licensed ([LICENSE](LICENSE)). The USCSandbox patches in `tools/downgrade/patches` are GPL-3.0, like the code they change ([THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES)).
