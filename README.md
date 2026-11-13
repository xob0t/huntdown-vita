# Huntdown Vita

[Huntdown](https://store.steampowered.com/app/598550/) on a PS Vita. A pipeline takes the PC release (Unity 2019.4.13f1) and rebuilds it as a Unity 2018.2 project, since 2018.2 is the last Unity with a Vita build module, then builds that for the Vita. It follows [Crow Country's port](https://github.com/xob0t/crow-country-vita) and [The Drifter's](https://github.com/xob0t/the-drifter-vita), and reuses their tools where they fit.

There are no game files in this repository. You bring your own copy, and the pipeline only reads it.

## Where it stands

Work in progress. The VPK builds, and on the console the game runs from its opening cutscene through the menus into the levels at 60 fps; the levels tried load in about 15 s and draw as on PC, but none has been played to its end yet. [docs/status.md](docs/status.md) has the details and the open issues.

## How it differs from the PC release

- The game's AssetBundles (its levels, area art and sound) are rebuilt for the target by the editor, under the same names.
- On the PS Vita, sound clips stay compressed in memory as HEVAG, the console's own format, instead of being decompressed when loaded, and the music streams.
- The pad goes through Rewired's PS Vita controller: R dashes, L does what Left Shoulder 1 does on a PC pad.
- The two logo videos at start are skipped: the PS Vita's Unity has no video player.
- Steamworks is replaced with a stub, so no player signs in to Steam.

## Building

You need Windows 10 or 11, Huntdown from Steam, about 20 GB of free disk space, and a console on HENkaku or Ensō with VitaShell. The setup is the same as Crow Country's port, and its [build guide](https://github.com/xob0t/crow-country-vita/blob/main/docs/build.md) covers what it installs: Unity 2018.2.19f1 with its PS Vita module, a patch to that editor's license check made with [unigate](https://github.com/xob0t/unigate), Sony's PS Vita SDK 3.570, AssetRipper, the .NET 8 SDK and Mono.Cecil.

1. Install [uv](https://docs.astral.sh/uv/).
2. Clone this repository, and in its folder run:

   ```powershell
   uv run build.py                 # the PS Vita app: builds/hunt-2018.2.19f1-vita.vpk
   uv run build.py --windows       # a Windows test player: builds/hunt-2018.2.19f1/Huntdown.exe
   ```

`build.py` runs the setup for your targets, then the pipeline's stages (`rip`, `project`, `build`, `package`) in `tools/downgrade/pipeline.py`. A rerun picks up where the last one stopped.

## License

The pipeline is MIT-licensed ([LICENSE](LICENSE)). The USCSandbox patches in `tools/downgrade/patches` are GPL-3.0, like the code they change ([THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES)).
