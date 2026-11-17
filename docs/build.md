# Building

The pipeline turns your copy of Huntdown into a Unity 2018.2 project, and builds a PS Vita app or a Windows test player from it.
The PS Vita app is built without the Unity editor, by [the builder](builder.md), from files setup extracts from Unity's installers: nothing of Unity is installed, and no Unity license is needed.
The Windows players, and the PS Vita app with `--unity`, are built by the Unity editor.
The pipeline only reads the game.
It writes the export and the Unity projects to `work/`, the players and the VPK to `builds/`, and the logs to `artifacts/`. Every step shows a bar or a spinner while it runs, with what it is on; the tools it runs (the C# compiler, the rewriter, UnityLinker, IL2CPP, the native plugin's build) write their output to logs, `artifacts/downgrade/tools/` for the small ones, and a step that fails prints its log's last lines.
Prose here is one sentence per line, so a diff shows only the sentences that changed.

You need Windows 10 or 11 on x64, and Huntdown from Steam.
With [uv](https://docs.astral.sh/uv/) installed, the short version is:

```powershell
uv run build.py                 # the PS Vita app: builds/hunt-2018.2.19f1-vita.vpk
uv run build.py --windows       # a Windows test player: builds/hunt-2018.2.19f1/Huntdown.exe
```

`build.py` runs the setup for your targets (step 1), then the pipeline's stages in order (step 2).
After a failure or a change, run it again.
It skips what is installed and what is up to date.
The builder writes the player's data and AssetBundles again on each run, and reuses the code module when the code hasn't changed; the editor imports only what changed.

## 1. Set up

1. Install Huntdown from Steam and update it to the current release.
2. Install uv, then open a new terminal so it finds uv:

   ```powershell
   powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
   ```

3. Clone the repository, and in its folder run the setup for your targets:

   ```powershell
   uv run tools/setup.py                 # Windows Mono player
   uv run tools/setup.py --vita          # PS Vita app
   uv run tools/setup.py --vita --il2cpp # every target, the Windows IL2CPP player included
   ```

`build.py` runs setup itself, so you can skip step 3.
Setup can run again at any time: it installs what is missing and skips the rest.
In this order, it:

- Runs `uv sync` for the Python packages.
- For the Windows players, and with `--vita --unity`, installs the Unity 2018.2.19f1 editor into `%LOCALAPPDATA%\Unity\Editors\2018.2.19f1`, with the Windows IL2CPP module for `--il2cpp` and the PS Vita module for `--vita --unity`.
- With `--vita`, downloads the editor's and the PS Vita module's installers and extracts what the pipeline and the builder read (CGIncludes, the engine and class libraries, IL2CPP, the built-in resources, the PS Vita player), about 1.2 GB, into `tools/external/unity-2018.2.19f1`, with 7-Zip: an installed one, or 7-Zip's own package from GitHub. Nothing is installed or run. It also installs the current Visual C++ runtime when Windows has an older one: the builder's texture encoder crashes in the 2015 runtime that Unity 2018's installer brings.
- Downloads AssetRipper 2.0.0 into `tools/external/assetripper-2.0.0`, the .NET 8 SDK 8.0.425 into `tools/external/dotnet`, and Mono.Cecil 0.10.0-beta6 from NuGet.
- Finds [the game](#the-game).
- With `--vita`, installs [the PS Vita SDK](#the-ps-vita-sdk), and with `--unity` its tools.
- When it installed the editor: removes the editor's license checks ([the Unity license](#the-unity-license)), turns on the editor's local cache server (Preferences > Cache Server: Local, 40 GB) unless you set that preference yourself (it is a setting of every Unity editor on the machine; `--no-cache-server` leaves it alone), and starts the editor once in batch mode.
- Prints [the doctor's checklist](#checking-the-setup).

Setup checks every download against a pinned hash, and runs a Unity installer, or extracts files from it, only if it carries Unity Technologies' Authenticode signature; the Visual C++ Redistributable must carry Microsoft's.
Installers that need administrator rights ask for them.

| Option | Effect |
| --- | --- |
| `--il2cpp` | Also install the editor's Windows IL2CPP module. An IL2CPP player also needs Visual Studio 2022 Build Tools with "Desktop development with C++"; the doctor checks for them. |
| `--vita` | The PS Vita build: extract Unity's files from its installers (no editor) and install the PS Vita SDK. |
| `--unity` | With `--vita`, what the editor's PS Vita build (`pipeline.py build --unity`) needs instead: the editor and its PS Vita module, Target Manager Server, Publishing Tools, and the license check. |
| `--vita-sdk <path>` | Use your own PS Vita SDK, an archive or a folder, instead of the download. Implies `--vita`. |
| `--vita-sdk-dir <dir>` | Extract the archive or copy the folder here. The default for an archive is `C:\vitasdk`; a folder is used where it is. |
| `--vita-host <address>` | Set `HUNT_VITA_HOST`, the console's address, for `vita.py`. |
| `--game <folder>` | Set `HUNT_GAME` to this copy of the game. |
| `--yes`, `-y` | Accept every offer (setting a variable for a game or SDK it found, running the SDK's tool installers). Without a terminal to ask in, setup declines and says to rerun with `--yes`. |
| `--dry-run` | Report what setup would do, and change nothing. |
| `--keep-downloads` | Keep the installers and archives in `work/installers`. |
| `--no-unigate` | Keep the editor's license checks, for an editor with a license of its own. `build.py` takes it too. |
| `--no-cache-server` | Leave the editor's cache server preference as it is. |
| `--skip-license-check` | Don't start the editor at the end. |

To download and check installers without installing them, for another machine say:

```powershell
uv run tools/setup.py download editor il2cpp vita assetripper dotnet cecil --to <folder>
```

### The game

The pipeline reads the game from `HUNT_GAME` if it is set, else from `C:\Program Files (x86)\Steam\steamapps\common\Huntdown`.
Setup also searches your other Steam libraries, and offers to set `HUNT_GAME` if it finds the game in one.
The tools also look in `C:\GOG Games\Huntdown`, but no GOG release has been tried.

The rules were written for Steam build 20193748, made with Unity 2019.4.13f1.
The doctor tells it from other releases by the hash of `Huntdown_Data\Managed\Assembly-CSharp.dll`.
Steamworks.NET is replaced with a stub (`STUBBED_ON` in `pipeline.py`), so no player signs in to Steam.

### The PS Vita SDK

With `--vita`, setup looks for an SDK in `SCE_PSP2_SDK_DIR`, in `C:\vitasdk` and in the SCE folder.
If it finds none, it downloads `PSVITA.zip` (350 MB) from archive.org's [copy of the SDK 3.570 DevNet release](https://archive.org/details/psvita-sdk-3.570-devnet-full), extracts it to `C:\vitasdk\PSVITA\sdk`, and applies Sony's `psp2cgc` and `libc` patches from the same item, which bring it to 3.570.071.
An SDK passed with `--vita-sdk` must be 3.570 or later and have the tools Unity checks for before a build (`psp2snc`, `psp2ld`, `arm-eabi-as`, `psp2snarl`, `psp2bin` and `psp2psarc`).

The builder uses the SDK alone.
The editor's PS Vita build (`--unity`) also runs `psp2ctrl` and `psp2run` from Target Manager Server, and `psp2pubcmd` from Publishing Tools, under `SCE_ROOT_DIR` (usually `C:\Program Files (x86)\SCE`).
With `--vita --unity`, if they are missing, setup offers to run their installers, from the SDK's folder or from the same archive.org item.
Publishing Tools needs .NET Framework 3.5, which setup turns on first.

Setup sets `SCE_PSP2_SDK_DIR`, and with `--unity` `SCE_ROOT_DIR`, for your user.
Shells opened before that don't see them; `build.py` reads both from the registry, but if you run `pipeline.py build` yourself, use a new shell.

### The Unity license

Only the editor's builds need this: the Windows players, and the PS Vita app with `--unity`.
They run the editor in batch mode, and Unity hides the PS Vita target unless the license includes console access.
So setup patches `Unity.exe` with [unigate](https://github.com/xob0t/unigate), downloaded at a pinned commit into `tools/external/unigate`, which keeps the original beside it.
Close Unity before you run setup, since Windows can't write the `Unity.exe` of a running editor.
With a license of your own, pass `--no-unigate`.
To undo the patch, close Unity and run:

```powershell
uv run tools/external/unigate/unigate.py --editor "$env:LOCALAPPDATA\Unity\Editors\2018.2.19f1\Editor" --restore
```

Setup then starts the editor once with an empty project, which takes about a minute, and remembers a pass in `work/license-check.ok` until the editor changes.
`uv run tools/setup.py doctor --license` checks again.

### Checking the setup

```powershell
uv run tools/setup.py doctor                                 # every target
uv run tools/setup.py doctor --target vita                   # one target: vita, windows or il2cpp
uv run tools/setup.py doctor --target vita --unity           # the editor's PS Vita build
```

The doctor prints a checklist of what the targets need, with the command that fixes each missing item, and the free space on the repository's drive.
It changes nothing, and exits with code 1 if a required item is missing.
Items marked `optional` don't stop a build: Mono.Cecil and USCSandbox (fetched or built on first use), the console's address, and a release of the game other than Steam build 20193748.

## 2. Build

```powershell
uv run build.py                       # the PS Vita app
uv run build.py --unity               # the PS Vita app, built by the Unity editor
uv run build.py --windows             # a Windows player with Mono
uv run build.py --il2cpp              # a Windows player with IL2CPP, which runs the code as the PS Vita does
uv run build.py --vita --windows      # both, side by side
uv run build.py --dry-run             # print the setup command and the stages, run nothing
```

`build.py` runs `tools/setup.py` for the targets, then these stages of `tools/downgrade/pipeline.py`:

1. `rip`: AssetRipper exports the game as a 2018.2 project into `work/rip-2018.2.19f1`, shared by the targets. `build.py` skips it while the export comes from the same game files, AssetRipper and settings.
2. `project`: assembles the Unity project in `work/hunt-2018.2.19f1-vita`, or `work/hunt-2018.2.19f1` for Windows. It rewrites the game's assemblies, decompiles the shaders and applies the asset, bundle and sprite rules.
3. `build`: builds the player and makes the game's AssetBundles again for the target. The builder writes the PS Vita player into `builds/hunt-2018.2.19f1-vita` ([builder.md](builder.md)). With `--unity`, and for the Windows players, the editor imports the project in batch mode and builds the player into `builds/hunt-2018.2.19f1-vita-unity`, or `builds/hunt-2018.2.19f1/Huntdown.exe` (`builds/hunt-2018.2.19f1-il2cpp/Huntdown.exe` for IL2CPP).
4. `package`, PS Vita only: makes `builds/hunt-2018.2.19f1-vita.vpk` (`-vita-unity.vpk` with `--unity`) with the title ID `HUNT00001`, with LiveArea art from the game's Steam store images.

`build.py` holds a build lock from `tools/downgrade/lock.py` for the whole run: `vita-build` when the targets include the PS Vita, `pc-build` otherwise.
If another job runs the pipeline, it waits.

| Option | Effect |
| --- | --- |
| `--vita`, `--windows`, `--il2cpp` | The targets. Without any, `build.py` builds the PS Vita app. |
| `--unity` | Build the PS Vita app with the Unity editor instead of the builder. Setup then installs the editor and patches its license check. |
| `--game <folder>` | The game's folder, if it isn't in Steam's default folder. Setup saves it in `HUNT_GAME`. |
| `--vita-sdk <path>`, `--no-unigate` | Passed to setup. |
| `--skip-setup` | Go straight to the pipeline. |
| `--from <stage>` | Start at `rip`, `project`, `build` or `package`, when the earlier stages are done. |
| `--wait <seconds>` | How long to wait for the build lock. The default is 6 hours. |
| `--dry-run` | Print the setup command and the stages, and run nothing. |

### Running the stages one at a time

`tools/downgrade/pipeline.py` runs one stage.
Its platform is `vita` unless you pass `--platform windows`.

```powershell
uv run tools/downgrade/pipeline.py rip                       # always exports again when run alone
uv run tools/downgrade/pipeline.py code                      # rewrite the game's assemblies, list what doesn't resolve
uv run tools/downgrade/pipeline.py link                      # Unity's linker on the rewritten assemblies, without the editor
uv run tools/downgrade/pipeline.py project                   # assemble work/hunt-2018.2.19f1-vita
uv run tools/downgrade/pipeline.py build                     # the PS Vita player, with the builder
uv run tools/downgrade/pipeline.py build --unity             # the PS Vita player, imported and built by the editor
uv run tools/downgrade/pipeline.py package                   # builds/hunt-2018.2.19f1-vita.vpk (--unity: -vita-unity.vpk)
uv run tools/downgrade/pipeline.py quickcode                 # code changes only, into the last editor build (-vita-unity)
```

For Windows players, pass `--platform windows` to `code`, `link`, `project` and `build`, and `--backend il2cpp` to `build` for IL2CPP.
`loadcheck` loads every rewritten type and compiles every method under the 2018 Mono, so it needs a Windows Mono player.
`build --unity --development` makes Unity's development player.
`build --unity --scenes <a,b>` makes a test build of only some of the build settings' scenes into `builds/hunt-2018.2.19f1-vita-scenes`; Huntdown's build has two scenes, and its levels are AssetBundles.
`--game <folder>` reads the game from another folder.

Other jobs on the machine share `work/`, `builds/` and `artifacts/`, so run each stage under a build lock:

```powershell
uv run tools/downgrade/lock.py run vita-build "me: what for" --wait 1800 -- uv run tools/downgrade/pipeline.py project
```

Use `pc-build` for a Windows stage.
A stage that writes the PS Vita build (a PS Vita `build`, `package`, `quickcode`) needs `vita-build`, and stops under `pc-build`.
Without a lock, `pipeline.py` stops if another run holds one, and warns otherwise.

For a code change (the rewriter or HuntCompat) to the builder's build, `tools/builder/il2cpp.py` rebuilds just the code module, after `pipeline.py code` and `project`; a whole `build` also writes the data again.
`quickcode` does the same for the editor's build: [vita.md](vita.md#code-changes-without-a-full-build) describes it and what it refuses.

### Build switches

These environment variables change what a stage writes.
The list is `BUILD_SWITCHES` in `tools/downgrade/common.py`, and there is one:

| Variable | Values | Default | Read by | What it does |
| --- | --- | --- | --- | --- |
| `HUNT_GFX_JOBS` | `on`, `off` | `on`, PS Vita only | `build`, `quickcode` | Writes `gfx-enable-gfx-jobs=1` into the PS Vita build's `Media/boot.config`, so Unity's job workers record the camera's draws beside the render thread. The 2018.2 editor has no setting for it on the PS Vita. `off` leaves it out. |

`pipeline.py` stops before any stage on a value a switch doesn't take, and each stage logs the values in effect.
`project` records the switches with the project and `build` warns when they differ; a full PS Vita `build` records them for `quickcode`, which applies `HUNT_GFX_JOBS` itself.

### Time and disk space

| Step | Time |
| --- | --- |
| A first `uv run build.py` from the release ZIP, setup and downloads included | about 25 minutes |
| A builder rebuild (`pipeline.py build`), the code module reused | about 10 minutes |
| The builder's code module alone (`tools/builder/il2cpp.py`) after a code change | about 5 minutes |
| An editor build (`--unity`), with the editor's import of every sound clip | 30 minutes or more |
| An editor rerun that changes a few assets | 4 to 5 minutes |
| `quickcode` (the editor's build) | about 100 s |
| `vita.py sync` of a code change | seconds |

Most of a builder run is encoding the sound into the bundles; most of a first editor build is the editor importing the game's 3,868 sound clips for the target, on one thread.
A change that makes the editor import every clip again takes as long.

A first `uv run build.py` from the release ZIP left 6.4 GB in the folder: 2.8 GB of work files, 1.5 GB in `builds/` (the build and its 521 MB VPK), 2.0 GB of tools in `tools/external` (Unity's files among them) and 0.2 GB of Python packages.
The doctor asks for about 13 GB free on the repository's drive for a first PS Vita or Mono build, 14 GB for IL2CPP, and 4 to 5 GB for a rebuild, plus 3 GB for the export when several targets are built at once; those figures, carried over from Crow Country's port, leave room for the editor's builds.
The editor with its modules wants another 5 GB in `%LOCALAPPDATA%`, and the PS Vita SDK takes about 0.6 GB.

### Reproducible outputs

Two builds of the same tree aim to give the same files.
`pipeline.reproducible_assembly` sets the time stamp, module ID and GUID of the assemblies the pipeline compiles, since the .NET Framework's `csc` has no `/deterministic`.
`pipeline.reproducible_player_data` sorts the lists the editor writes in another order on each build in `globalgamemanagers`, and derives its build GUID from the content; the `[RuntimeInitializeOnLoadMethod]` methods then run in a fixed order, HuntCompat's first.
`asset_rules.product_guid` fixes the project's `productGUID`, and `package` gives every file in the VPK the same date and attributes.
This repository has no tool that compares two builds.

## 3. Install the PS Vita app

Install `builds/hunt-2018.2.19f1-vita.vpk` with VitaShell on a console with HENkaku or Ensō.
After that, `vita.py sync` updates the app from the PC.
[vita.md](vita.md#installing) has the steps and the console tools.

## When something fails

`build.py` stops at the first failure and says what failed.
Fix it and run `build.py` again: it picks up where it stopped.

- Setup: a download whose hash doesn't match is deleted, and setup stops; run it again. "unigate could not patch" means Unity is running while the editor still needs the patch; close it. "To do" lines at the end are steps setup couldn't take, such as an offer you declined; `build.py` won't build until they are done.
- The game: "another release than the rules were written for" means your `Assembly-CSharp.dll` differs from Steam build 20193748's. It may still build; if a rule stops in `project`, update the game or have Steam verify its files. "built with Unity ..., the rules expect 2019.4.13f1" has the same fix. If the doctor finds no `Huntdown.exe`, run `uv run tools/setup.py --game <folder>`.
- `rip`: "AssetRipper did not start" or "AssetRipper export produced no project": check `tools/external/assetripper-2.0.0` and `artifacts/downgrade/assetripper.log`.
- `project`: "No .NET 8 SDK" means `tools/external/dotnet` is missing; run setup. A rewriter failure names the assemblies that don't resolve, with its log in `artifacts/downgrade/rewrite-<version>-<platform>.log`. "expected at least ... The export changed or the rule no longer matches it" means a rule matched less than on the export it was written for, usually another release of the game.
- `build` with the builder: each step names what it is on, and a failure prints the step and the file. [builder.md](builder.md) lists the checks that compare its output with the editor's.
- `build` with the editor: the editor's log is `artifacts/downgrade/build-<version>-<platform>.log` (with `-il2cpp` or `-scenes` for those builds), and the pipeline prints a summary of it. A good build logs `DOWNGRADE_BUILD Succeeded errors=0`; the stage stops on any error in the build report and lists them, and they are also in `<player>.messages.txt` beside the player. "A Unity editor has ... open" means an editor still has the project open. If the editor stops at once, run `uv run tools/setup.py doctor --license`; a failed check leaves its log in `work/license-check.log`. If IL2CPP or the PS Vita steps fail, run `uv run tools/setup.py doctor --target vita`, from a new shell.
- `quickcode`: "No saved PS Vita build to update" means there is no full editor build yet; run `pipeline.py build --unity` once.
- Locks: "Held by another run" means another pipeline holds a build lock; `uv run tools/downgrade/lock.py status` shows who.
- `vita.py`: "Set HUNT_VITA_HOST" means the console's address is not set; run `uv run tools/setup.py --vita-host <address>`.
