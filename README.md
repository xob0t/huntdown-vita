# Huntdown Vita

[Huntdown](https://store.steampowered.com/app/598550/) on a PS Vita. A pipeline takes the PC release (Unity 2019.4.13f1) and rebuilds it as a Unity 2018.2 project, since 2018.2 is the last Unity with a Vita build module, then builds that for the Vita. It follows [Crow Country's port](https://github.com/xob0t/crow-country-vita) and [The Drifter's](https://github.com/xob0t/the-drifter-vita), and reuses their tools where they fit.

There are no game files in this repository. You bring your own copy, and the pipeline only reads it.

## How it plays

Very well. Huntdown holds 60 fps on the PS Vita nearly all the time, and only the biggest fights, with lots of explosions on screen, dip a little. A level takes about 15 seconds to load. If you have a Vita and the game on Steam or GOG, play it.

## What's different from PC

- It's single player only. Local co-op is off, and the menus don't offer it.
- There's a bit less debris, blood, rain and snow on screen than on PC, so the frame rate holds.
- The two logo videos at startup are skipped.

[docs/status.md](docs/status.md) has the technical details.

## Building

You need Windows 10 or 11 on x64, Huntdown from Steam or GOG, about 10 GB of free disk space, and a console on HENkaku or Ensō with VitaShell.

1. Install [uv](https://docs.astral.sh/uv/): open PowerShell from the Start menu and run

   ```powershell
   powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
   ```

2. Download [huntdown-vita.zip](https://github.com/xob0t/huntdown-vita/releases/latest/download/huntdown-vita.zip), right-click it, choose **Extract All** and change the folder to `C:\huntdown` (or clone this repository there).
   Keep the path short. Unity's IL2CPP can't open its own files from a folder whose path is longer than 72 characters, and a folder on a OneDrive desktop can be that deep. The build checks this before it starts.

3. Open `C:\huntdown` (or the clone's `huntdown-vita` folder), right-click in it, choose **Open in Terminal** (on Windows 10: Shift + right-click, **Open PowerShell window here**), and run:

   ```powershell
   uv run build.py
   ```

   The VPK is `builds/hunt-2018.2.19f1-vita.vpk`. Install it with VitaShell.

A first build takes about 25 minutes and leaves about 6.5 GB in the folder. It doesn't install Unity or need a Unity license: it downloads Unity 2018.2.19f1's installers and extracts the files the build reads, and Sony's PS Vita SDK 3.570 from archive.org, with AssetRipper, the .NET 8 SDK and Mono.Cecil. A rerun picks up where the last one stopped.

- `--game <folder>` reads the game from that folder, if it isn't in your Steam library or GOG's default folder.
- `--vita-sdk <archive or folder>` uses your own copy of the SDK.
- `--unity` builds the VPK with the Unity editor instead. It installs the editor with its PS Vita module and patches its license check with [unigate](https://github.com/xob0t/unigate).
- `--windows` builds a Windows test player with the editor: `builds/hunt-2018.2.19f1/Huntdown.exe`.

[docs/build.md](docs/build.md) covers the setup, the pipeline's stages and the options; [docs/builder.md](docs/builder.md) the editor-free builder.

## License

The pipeline is MIT-licensed ([LICENSE](LICENSE)). The USCSandbox patches in `tools/downgrade/patches` are GPL-3.0, like the code they change ([THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES)).
