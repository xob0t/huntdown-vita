# PS Vita

The app is Unity 2018.2.19f1's own PS Vita player, built without the editor by [the builder](builder.md) from Unity's files and Sony's PS Vita SDK 3.570 (with `--unity`, by the editor with its PS Vita module).
[build.md](build.md) sets them up and finds the game.
Prose here is one sentence per line, so a diff shows only the sentences that changed.

The build is a PC Hosted player in `builds/hunt-2018.2.19f1-vita`.
`pipeline.py package` turns it into `builds/hunt-2018.2.19f1-vita.vpk`.
In the package the player is `eboot.bin` with its trial flag cleared, and the LiveArea art comes from the game's Steam store images (`livearea.py`).
The title ID is `HUNT00001`.

## Installing

You need a PS Vita on HENkaku or Ensō with [VitaShell](https://github.com/TheOfficialFloW/VitaShell).
[vitacompanion](https://github.com/devnoname120/vitacompanion) is optional.
It adds an FTP server that works while the game runs, and a command server that `vita.py` uses to start and quit the game and press buttons.

1. In VitaShell, press Select to start its FTP server. Upload the VPK to `ux0:data/` with an FTP client on port 1337, or copy it over USB.
2. In VitaShell, open the VPK and install it.

The app asks for the +109 MB memory expansion (`unity/DowngradeBuild.cs`).
The system reads that from `param.sfo` at installation, so a build that changes `param.sfo` needs a new installation.
`vita.py sync` updates everything else in place.

On the console the app keeps its files in these folders:

| Folder | What |
| --- | --- |
| `ux0:app/HUNT00001` | The installed app. It is read-only to the game. |
| `ux0:data/Huntdown` | The game's saves and settings (`compat/Saves.cs`). |
| `ux0:data/hunt` | The probe's flags in `args.txt`, and the player's log in `log.txt` (`compat/ProbeFiles.cs`). The log is capped at 8 MB; the two runs before are in `log.prev.txt` and `log.prev2.txt`. |

## vita.py

`tools/downgrade/vita.py` drives the installed app at the console's address.
It reads `HUNT_VITA_HOST`, then `CROW_VITA_HOST`, then `DRIFTER_VITA_HOST`, so a console set up for Crow Country's or The Drifter's port works as it is.
`uv run tools/setup.py --vita-host <address>` sets `HUNT_VITA_HOST`.

What each command needs on the console:

- `seed`, and `crashes --dump`, need no console.
- `sync`, `log`, `args`, `files` and `crashes` need only an FTP server on port 1337, VitaShell's or vitacompanion's.
- The others need vitacompanion's command server on port 1338.
- `install`, `perf` and `shot` use commands the released vitacompanion lacks (`install`, `perf`, `screenshot`), from an extended build. They ask the console's `help` first and stop with a message when the command isn't there.

```powershell
uv run tools/downgrade/vita.py sync                          # upload what changed since the last sync (the first uploads everything)
uv run tools/downgrade/vita.py run                           # quit, sync, launch
uv run tools/downgrade/vita.py log --tail 40                 # the player's log
uv run tools/downgrade/vita.py args -- -hunt-heartbeat 5     # the probe's flags for the next launch
uv run tools/downgrade/vita.py args --                       # no flags: clears them
uv run tools/downgrade/vita.py args --show                   # the flags the next launch gets, changing nothing
uv run tools/downgrade/vita.py crashes --pull                # the newest crash dump, symbolized
```

The flags can follow `args` directly or after `--`; both write the same line.

| Command | Effect |
| --- | --- |
| `sync [--build dir]` | Upload the files that changed since the last sync into `ux0:app/HUNT00001`, and delete the ones an earlier sync or install put there that the build no longer has. It quits a running game first, since replacing files under it crashes it. |
| `seed [--build dir]` | Record a build as installed, from its VPK say, without uploading. The next sync then sends only the differences. |
| `install [--build dir]` | Package the build as a VPK and stream it to vitacompanion's installer. This also applies `param.sfo` changes. |
| `launch`, `quit`, `run` | Start the game, quit all apps, or quit, sync and launch. |
| `do "<chain>"` | Run a vitacompanion command chain, for example `do "press cross; wait 100ms; release cross"`. |
| `shot [out.png]` | A system screenshot of what the screen shows. Not while the game runs: see [Screenshots](#screenshots). |
| `perf` | Clocks, CPU load per core, frame rate and battery. |
| `log [--tail N] [--prev [N]]` | Print the player's log. `--prev` prints the run before's, `--prev 2` the one before that. |
| `args -- [flags...]` | Write the probe's flags to `ux0:data/hunt/args.txt`. A value with spaces is quoted in the file. With no flags it empties the file. |
| `args --show` | Print the flags the next launch gets. |
| `files [folder]` | List the files under a console folder with their sizes and times. The default is the game's saves and settings, `ux0:/data/Huntdown`. |
| `crashes [--pull] [--thread NAME] [--dump FILE]` | List the crash dumps in `ux0:data`. See [Crash dumps](#crash-dumps). |
| `reboot` | Reboot the console. |

`--build` takes another build folder, such as `builds/hunt-2018.2.19f1-vita-scenes`.

### Locks

When several jobs share the console, take the `vita-device` lock first with `tools/downgrade/lock.py`. That lock is one file for every port on this machine, `%USERPROFILE%\.t3\vita-device.lock` (`VITA_DEVICE_LOCK` overrides it), so another port's run holds the console for this one too; start the holder text with the project's name (`huntdown: perf run of 3-3`). The `vita-build` and `pc-build` locks stay in this project's `work/`.
A person at the console holds it as `user: <purpose>`.
These commands refuse while someone else holds it: `sync`, `install`, `launch`, `quit`, `run`, `do`, `reboot` and `args`.
Run them inside `lock.py run vita-device ...`, or set `HUNT_HOLDER=<your holder name>` after `lock.py take`.

`sync`, `install` and `seed` read the build folder under the `vita-build` lock, so a `build` or `quickcode` can't rewrite the files while they go out.
`run` takes it before it quits the game, so a busy lock leaves the game running.
They hold the lock as a reader.
A build or `quickcode` waits for a reader as for any holder, while a Windows build under `pc-build` and a reader don't wait for each other.
That is why `pipeline.py` stops a PS Vita `build`, `package` or `quickcode` started under `pc-build`.

A reader waits up to `--build-wait` seconds (900 by default) for a build that holds the lock, then stops with exit code 75.
Inside `lock.py run vita-build ...` the lock is the caller's, and they don't wait.
The same goes for a `lock.py take vita-build` when `HUNT_HOLDER` names its holder.

### Working with the console

- Don't kill `vita.py` while it sends or reads a file. An FTP transfer cut off part way can hang the console's FTP server until the console reboots. Ctrl+C or Ctrl+Break lets the current file finish and then stops; a second one closes the transfer's connection cleanly and stops at once. `lock.py run` waits for its command after Ctrl+C.
- `sync` and `args` write each file as `<name>.part` and then rename it, so an interrupted upload never leaves the app a short file. `sync` sends an interrupted file again next time.
- Add `-hunt-mute` to test runs.
- Test runs can write saves. If the saves in `ux0:data/Huntdown` matter, copy them off the console first.

Unattended runs need the display kept on.
vitacompanion's `nosleep` holds off standby but not the display's idle timeout, and when the display goes off the system pauses the game.
`perf` then shows `app: none` and 0 fps, while FTP and the command server still answer.
`screen on` resumes the game where it was.
A script that finds the log stalled then and quits the game leaves a log that ends with no dump, which looks like a silent crash.
`vita.py log` sends `nosleep on; screen on` with every read, and `launch`, `quit` and `run` send it with their commands.
A script that polls the console another way should send `vita.py do "nosleep on; screen on"` every few seconds.

## Probe flags

HuntCompat is the compatibility library the pipeline adds to the game.
Its measurement probe is off unless the player gets a `-hunt-*` flag; some flags below belong to other modules and work on their own.
A Windows player takes the flags on its command line.
The PS Vita passes no command line, so there the player reads them from `ux0:data/hunt/args.txt`, which `vita.py args` writes.
`-hunt-help` logs the list of Diag's own flags.
The header comment of each source file in `tools/downgrade/compat` describes its flags in more detail.

How to use them on the console:

- The player reads `args.txt` once, as it starts. Quit the game before you launch it, or it resumes with the flags it had: `vita.py run` does both.
- `args.txt` stays on the card and applies to every launch, the user's own play included. Clear it after each test run with `vita.py args --`.

```powershell
uv run tools/downgrade/vita.py args -- -hunt-mute -hunt-level 2-3 -hunt-heartbeat 5
uv run tools/downgrade/vita.py run
uv run tools/downgrade/vita.py log --tail 40
uv run tools/downgrade/vita.py args --
```

| Flag | Effect |
| --- | --- |
| `-hunt-level <a>-<l>[a\|b]` | Load this level instead of the first one picked, as in the bundles' names: `1-1` to `4-5`, `1-5a`, `2-5a`, `3-5a`, `4-5a`, `4-5b`. Dying, checkpoints and the next level go on as the game has them. HUNT_LEVEL in the log. `compat/LevelJump.cs`. |
| `-hunt-cheats <cheat>[,<cheat>...]` | Turn on the game's own cheats as each level starts, so a test run survives into a level's busiest parts: `god` (no damage), `ammo` (unlimited), or a start weapon by the game's name (`grenadelauncher`, `howitzer`, `flamer`, `minigun`, ...). HUNT_CHEATS in the log. `compat/LevelJump.cs`. |
| `-hunt-fps 60\|30\|game` | The fixed frame rate. The PS Vita's default is 60, other players' `game`, the game's own automatic mode. `compat/FrameRate.cs`. |
| `-hunt-view full\|game` | `full` shows all 480x270 game pixels, 2 screen pixels each, the PS Vita's default. `game` is the game's own scale, 3 on the PS Vita. `compat/View.cs`. |
| `-hunt-menus vita\|game` | `vita` leaves out what a PS Vita doesn't have: 2 PLAYERS in the main menu (its first entry then reads START GAME), JOIN PLAYER 2 in the pause menus, the prompt for a second pad to join, and RESOLUTION in Options > Video. The PS Vita's default; `game` keeps the game's menus. `compat/Menus.cs`. |
| `-hunt-preload on\|off` | Read the opening cutscene's and the menus' sprite sheets while the screen is still black at startup. On by default on the PS Vita only. `compat/Preload.cs`. |
| `-hunt-load-ms <ms>` | Log every Resources load of ms or more (HUNT_LOADTIME). Default 30; 0 logs none. `compat/LoadTimer.cs`. |
| `-hunt-load-priority low\|belownormal\|normal\|high` | `Application.backgroundLoadingPriority`, for experiments. The game's own unless given. |
| `-hunt-scene <name>` | Load this build scene 2 s after the first scene loaded. |
| `-hunt-heartbeat <s>` | Every s seconds, a HEARTBEAT line with frame times, GC and memory, visible renderers and lights. On the PS Vita it includes the free main, video and physically contiguous memory (`freeLPDDRMB`, `freeCDRAMMB`, `freePHYSCONTMB`). |
| `-hunt-quit-after <s>` | Quit s seconds after the probe starts. |
| `-hunt-shot <s> <path>` | Save a screenshot s seconds after the last scene load. |
| `-hunt-shots <s> <prefix> [after]` | A screenshot every s seconds from `after` seconds on (default 0), as `<prefix>-<seconds>.png`, the seconds since the probe started. |
| `-hunt-no-vsync` | vSyncCount 0 and no frame cap. |
| `-hunt-mute` | No sound. |
| `-hunt-stall-dump <s>`, `-hunt-stall-after <t>` | Crash the player when a frame has taken s seconds, ignoring frames that start in the first t seconds. See [Crash dumps](#crash-dumps). |
| `-hunt-stall-from render` | With `-hunt-stall-dump`, time each frame from just before the engine renders, not from the probe's Update. |
| `-hunt-visible-top` | With `-hunt-heartbeat`, a HUNT_VISIBLE line of the visible renderers by name (digits dropped) and sorting layer, the most first, and a HUNT_COVER line of the screen they cover by name, layer and shader. It scans every renderer, so its own seconds show frames of 30-40 ms. Every heartbeat has `cover=`: the visible renderers' screen areas summed, in screens (how many times the GPU fills the screen). |
| `-hunt-input-log` | Each joystick button Unity reports (HUNT_INPUT down/up JoystickButtonN; Select is 6, Start 7) and each of the game's Rewired actions player 1 starts (HUNT_INPUT action Back). Screenshots (`-hunt-shots`) stall the game about 0.65 s each, long enough to miss a short press: hold a button for a second there. |
| `-hunt-pools game\|<name>=<n>,...` | The game's effect pools: `game` for its own sizes, or sizes of one's own (never above the game's) for debris, blood, rain, snow, ash and dust, e.g. `-hunt-pools debris=128,dust=160`. The PS Vita's defaults are in `compat/Pools.cs`; each pool kept smaller logs a HUNT_POOL line. |
| `-hunt-gc game\|held` | When the garbage collector runs: `game`, its own timing (about every 60 s in a level, a 0.2 s stop); `held`, the PS Vita's default, at level starts and checkpoint restarts, at the pause menu, and at the cap. Each collection held logs a HUNT_GC line with its time. `compat/Gc.cs`. |
| `-hunt-gc-cap <MB>` | With the collector held, the garbage that collects anywhere (default 24). `-hunt-gc-cap 1` collects about once a minute in play, to try the path. |
| `-hunt-unload-assets on\|off` | `on`, the PS Vita's default: when a level is made (its load, RESTART LEVEL's reload, a boss stage), `Resources.UnloadUnusedAssets` frees what the last level left loaded, its scenery and the game's old text textures, before the new level's bundles are read (HUNT_UNLOAD with its time). `off` is the game's own: each reload kept the last one's scenery, about 18 MB in level 1-1. `compat/LevelAssets.cs`. |
| `-hunt-prof alloc` | In a `HUNT_PROF=1` build, the HUNT_PROF lines count KB allocated per frame instead of ms. See [Where a frame's time goes](#where-a-frames-time-goes). |
| `-hunt-phases` | With `-hunt-heartbeat`, a HUNT_PHASES line: the steps of Unity's player loop that took the most, in ms per frame (`Update.ScriptRunBehaviourUpdate`, the scripts' Updates; `Update.ScriptRunDelayedDynamicFrameRate`, coroutines; `PostLateUpdate.FinishFrameRendering`, rendering and, at 60 fps, the wait for vsync). `compat/Phases.cs`. |
| `-hunt-sprite-bench <after s>` | From `after` seconds on, add test sprites (0, 600 and 1200) set up in different ways and log the frame time of each (HUNT_BENCH): what a sprite costs the engine. `compat/SpriteBench.cs` lists the modes. |
| `-hunt-effect-batch on\|off\|check` | The game's effects, debris, blood, shells, bullet holes and weather drawn as a few batched meshes instead of a sprite each: `on`, the PS Vita's default; `off`, the game's renderers; `check`, both, the batched ones tinted magenta and half transparent over the game's. With `-hunt-heartbeat`, a HUNT_EFFECT_BATCH line every 600 frames (sprites and vertices drawn, Build's gather, emit and upload ms, the meshes kept as they were or with only their vertices uploaded), and one at each level start or restart for the meshes, materials and sprite shapes it no longer needs and frees. `compat/EffectBatch.cs`. |
| `-hunt-rewired game\|log\|trim\|pad` | Rewired's controllers. The PS Vita's default disables its keyboard and mouse; `game` leaves them on; `log` logs its controllers (HUNT_REWIRED); `trim` logs and disables; `pad` also disables the joysticks without a name. `compat/RewiredTrim.cs`. |
| `-hunt-rewired-axes all` | Rewired reads every axis and button of every joystick slot and updates Unity's mouse and its FixedUpdate, as the game does. The PS Vita's default reads the first 8 axes of the slots with a pad in them and skips the rest. `compat/RewiredInput.cs`. |
| `-hunt-rewired-direct on\|off\|check` | `on`, the PS Vita's default: once the game's first player has the PS Vita's pad (and the second none), Rewired's frame (InputManager_Base.Update) is skipped and the game's reads of its actions (GamePad's GetButton, GetButtonDown, GetButtonUp, GetAxis, GetAnyButton) are answered from the pad's Unity buttons and axes, mapped as Rewired's PS Vita map maps them, with its 0.2 radial dead zone on the left stick. `off`: Rewired answers, as the game does. `check`: Rewired answers, and each answer is compared with the direct one; HUNT_REWIRED_DIRECT lines log the pad's changes, each answer that differs, and a count every 600 frames. `compat/RewiredDirect.cs`. |
| `-hunt-census <s>` | Every s seconds, a HUNT_CENSUS line: the loaded textures (count and MB at 4 bytes a pixel; the run-time ones, with no name, apart), meshes, sprites, clips (all/loaded), materials, GameObjects and AssetBundles, and a HUNT_CENSUS_DUP line of the textures loaded more than once under one name: for what a restart or a level change leaves behind. Each takes some ms. |
| `-hunt-find <regex> <s>` | s seconds (default 5) after each scene load, log every renderer whose hierarchy path matches, active or not, with its state, bounds, mesh, materials and shaders (HUNT_FIND). |
| `-hunt-activate <name> <s>` | s seconds after each scene load, activate every GameObject of that name with its parents. Repeatable. `compat/Video.cs`. |
| `-hunt-video-log` | Log each playing video's clip, frame and time every second. `compat/Video.cs`. |
| `-hunt-cpu-mhz <n>` | The ARM clock to keep. Default 444, the highest an application may set; it only raises the clock. 0 leaves the power mode's 333. `compat/Clock.cs`. |
| `-hunt-gpu-mhz <n>` | The GPU clock to keep: 41, 55, 111, 166 or 222. Default 222; 0 leaves the power mode's 166. |
| `-hunt-xbar-mhz <n>` | The GPU crossbar clock to keep, 111 or 166. Default 0, which leaves it as it is. |
| `-hunt-culture invariant\|system\|<name>` | The culture numbers and dates use. The PS Vita's default is invariant. `compat/Settings.cs`. |
| `-hunt-log-sync` | Write the log on the thread that logs instead of the log's own thread. HUNT_LOG_SLOW marks writes of 150 ms or more. `compat/ProbeFiles.cs`. |
| `-hunt-windowed` | Keep a Windows player in a 960x544 window whatever the game's display setting. `run_player.py` passes it. `compat/Window.cs`. |

The player also logs, without a flag, each sound the game looks up by a name that isn't loaded, as `HUNT_CLIP_MISSING <name> (<n> clips loaded) at <s> s`, before the game's KeyNotFoundException (`compat/Clips.cs`).
IL2CPP's stack trace stops at `Dictionary.get_Item`, so that line is what names the sound.

### Where a frame's time goes

A build made with the build switch `HUNT_PROF=1` (`build.py`, or `pipeline.py code` then `quickcode`, with it set) times the frame methods of the game's and Rewired's assemblies: every FrameEvent, Update, LateUpdate and FixedUpdate, the enemies' and objects' DoFrameEvent, the steps of `Game.UpdateGame` (`GoObjects`, `GoBullets`, `GoCam`, `GoPlayer`, ...), and each step of a coroutine (as `Type.Name()`) of its classes.
Each heartbeat then also logs a HUNT_PROF line: the methods that took the most, in ms per frame, with their calls per frame (`compat/Prof.cs`).
Times include the calls inside, so `Game.Update` is the game's whole frame; what the heartbeat's `ms=` has beyond it is the engine's (rendering, the sprites' transforms, sound) and the wait for vsync.
To see what the engine spends that time on, a stall dump some milliseconds into the rendering samples it: `-hunt-stall-dump 0.008 -hunt-stall-from render`.
Build again without the switch to play: each timed call costs a little.
With `-hunt-prof alloc` the lines count KB allocated per frame instead, for what the garbage collector has to collect; the heartbeat's `allocKBs=` is the whole player's per second, its own lines among it (about 8 KB/s).

The CPU clock tells the main thread's limits from the GPU's: `-hunt-cpu-mhz 333` slows a CPU-bound spot, `-hunt-gpu-mhz 166` a GPU-bound one.

### Screenshots

vitacompanion's `screenshot`, which `vita.py shot` uses, runs out of memory while the game runs.
Use `-hunt-shot` or `-hunt-shots` with a path in `ux0:data/hunt`, such as `-hunt-shots 5 ux0:data/hunt/run 30`.
On the PS Vita the player reads the frame back and saves a 960x544 PNG itself, which stalls the game about 650 ms per screenshot.
`vita.py files ux0:/data/hunt` lists them, and an FTP client on port 1337 fetches them; `vita.py` has no command that downloads a file.

## Crash dumps

When the player crashes, the console writes a dump to `ux0:data`.
`vita.py crashes` lists the dumps, newest last.
`vita.py crashes --pull` fetches the newest into `artifacts/vita/` and prints the crashed thread's call chain.
It reads the dump with [vita-parse-core](https://github.com/xyzz/vita-parse-core), which it downloads into `tools/external` on first use, and names the frames with the SDK's `psp2bin` and the build's `SymbolFiles`.
`--thread main` prints the main thread instead, and `--dump FILE` reads a dump already on the PC.
Check that the dump it pulled is the new one: the list shows each dump's time.

`sync`, `install` and `seed` keep a copy of the game module's symbols for each build they send, in `artifacts/vita/symbols/<fingerprint>`.
They keep the last 5, about 55 MB each.
`crashes` uses the copy whose fingerprint the dump lists, so a dump still reads right after the next `quickcode`.

A frame that takes seconds doesn't crash, and the console has no sampling profiler.
`-hunt-stall-dump 15` makes the player crash once a frame has taken 15 seconds, so the dump holds the main thread's stack in the middle of it (HUNT_STALL_DUMP in the log).
`-hunt-stall-after <t>` skips frames that start in the first t seconds, such as the startup's loads.
The watcher checks every quarter of the limit, so a fraction of a second works too.

```powershell
uv run tools/downgrade/vita.py args -- -hunt-stall-dump 15 -hunt-stall-after 20
uv run tools/downgrade/vita.py run
uv run tools/downgrade/vita.py crashes --pull --thread main
uv run tools/downgrade/vita.py args --              # clears the flags: args.txt stays until then
```

## Code changes without a full build

For a change to the rewriter or HuntCompat, `pipeline.py project` rewrites the assemblies into the project, and `tools/builder/il2cpp.py` rebuilds only the code module into the builder's build, recompiling only the C++ files whose content changed (about 5 minutes; a whole `build` takes about 10, as it writes the data again):

```powershell
uv run tools/downgrade/lock.py run vita-build "me: what for" --wait 1800 -- uv run tools/downgrade/pipeline.py project
uv run tools/downgrade/lock.py run vita-build "me: what for" --wait 1800 -- uv run tools/builder/il2cpp.py
uv run tools/downgrade/vita.py run
```

The editor's build (`build --unity`) takes half an hour or more when the editor imports the sound again ([build.md](build.md#time-and-disk-space)).
For it, `quickcode` takes about 100 s instead.
It rewrites the assemblies and replays the last full build's UnityLinker and IL2CPP steps on the inputs that build saved in `work/vita-code-stage`.
Then it puts the new `il2CppAssemblies.suprx` and `global-metadata.dat` in `builds/hunt-2018.2.19f1-vita-unity`, with the HuntClock plugin and `boot.config`.
`vita.py sync --build builds/hunt-2018.2.19f1-vita-unity` uploads the changed files in seconds.

```powershell
uv run tools/downgrade/lock.py run vita-build "me: what for" --wait 1800 -- uv run tools/downgrade/pipeline.py quickcode
uv run tools/downgrade/vita.py run --build builds/hunt-2018.2.19f1-vita-unity
uv run tools/downgrade/vita.py log --tail 40
```

The build's scenes and assets keep the scripts' fields as they were built.
So `quickcode` stops when a type the data serializes changed, such as a field or a new MonoBehaviour, or when a `[RuntimeInitializeOnLoadMethod]` came or went.
It warns when the asset rules or the build script changed since the full build.
Those changes need `project` and `build` again; `--force` tries anyway.
It also stops when the last full build failed or was stopped, and when an assembly isn't among that build's.

The time `lock.py` waits for a lock counts against whatever limits the caller's own run.
An agent's background command stopped at its time limit stops `lock.py` and its command with it, in the middle of a build or a console transfer.
So keep `--wait` plus the command's time well inside that limit.
To queue for longer, wait in short steps without holding anything, then take the lock without waiting:

1. `lock.py wait vita-build --wait 540` exits 0 once the lock is free, or 75 if it isn't.
2. `lock.py run vita-build "me: what for" --wait 0 -- ...` takes it. If another job took it first, this exits 75, and you go back to step 1.

`lock.py status` says who holds each lock and since when.
