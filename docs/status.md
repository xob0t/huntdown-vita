# Status

What works, what the build changes, and what is still open.
Prose here is one sentence per line, so a diff shows only the sentences that changed.

## What works

The pipeline exports the Steam release (build 20193748), rewrites its four assemblies for Unity 2018.2 with no unresolved references, and assembles the Unity projects for the PS Vita and Windows.
The Windows Mono test player and the PS Vita VPK build with no errors.
On the PS Vita (installed as HUNT00001), the game starts, plays its opening cutscene (X skips it), and shows the title and the main menu with their text and the PlayStation button prompts.
It loads the rebuilt AssetBundles there: the global sound bundle's 261 clips and every level's sound bundles.
Story mode's first level loads (about 15 s) and plays at 60 fps with the pad: the HUD, the player running and shooting, enemies.
Its scenery draws as the game's from the start to the alley past the tattoo parlor (about 45 s of play, the farthest tried).
Levels 2-3, 4-3, 4-5 and the boss stages 3-5a and 4-5b, loaded with the test flag `-hunt-level` (`compat/LevelJump.cs`), load, draw as the game's and run at 60 fps; their first 30 to 60 s were played.

## What the build changes

AssetBundles:

- The game's 252 bundles are Unity 2019.4's, which a 2018.2 player can't read. The export keeps each asset's bundle name, and the build makes the bundles again for the target, LZ4-compressed, under the same paths in StreamingAssets (`tools/downgrade/bundle_rules.py`, `unity/DowngradeBuild.cs`).
- Textures the game shipped uncompressed (695 of 707, RGBA32 and RGB24) are imported uncompressed. The game draws its text by copying glyphs on the GPU from its font textures into RGBA32 ones, which fails between formats: compressed, every menu text and button prompt was blank.
- Sprite sheets keep the game's order, and every sprite is drawn with the game's own mesh, read from the game's files: the export shuffled 295 sheets and lost most outline pieces. Meshes made again (from the pixels, or cut around packed sprites) showed the level select's packed animation frames through its computer frame and made its building flicker (`tools/downgrade/sprite_rules.py`).
- Single sprites (a texture that is one sprite: each level's 1024x512 scenery pieces, backgrounds) get the game's mesh and pivot too. The export moved 69 of the 283 pivots where the picture has see-through edges: level 1-1's second piece was drawn 290 pixels low, and a few screens in the ground was gone. Every level bundle's sprites now have the game's pivots and sizes.
- Sprites are exported into their textures' importer settings (AssetRipper's `SpriteExportMode: Native`): the game loads sprite sheets whole and picks sprites by position.
- Each sheet's sprites get file IDs of their own, so that the player's data keeps them together. Numbered from the same ID in every sheet, `resources.assets` held every sheet's first sprite, then every sheet's second, and on the PS Vita reading a sheet was a read from the memory card per sprite: level 1-1's sheets took 18 s to load, now 6 s (a 51-sprite enemy sheet: 400 ms, now 48 ms).

Code:

- Steamworks.NET (the game's firstpass assembly) is a stub, so no player starts or signs in to Steam. With the real one, a player started outside Steam asked Steam to start the game and quit.
- The game's analytics never log in, so nothing is sent to the developer's stats server (`compat/Offline.cs`).
- Saves go through `persistentDataPath`, which the PS Vita's player leaves empty; they go to `ux0:data/Huntdown` there.
- Five UnityEngine.UI members from Unity 2019 (the Selected state of buttons, `Selectable.allSelectableCount`) are shims.

Sound:

- The export writes every clip at Vorbis quality 0, the lowest: the Windows players import them at 0.5, about the size of the game's own bundles, preloaded unless they stream.
- On the PS Vita, the 3,796 short clips stay compressed in memory as HEVAG instead of being decompressed to PCM when loaded, and the music and the two clips of 45 s or more stream. The PS Vita's importer encodes every clip as HEVAG, the streams too (the rules ask for MP3 there, which it doesn't do).
- No clip loads its data in the background (Load In Background stays off, as the game has it). The game unloads each sound bundle 0.3 s after taking its clips out; a clip still loading in the background then never finished, and unloading it when the next level loaded waited for it forever: the game hung at the first level's loading.
- The mission briefing moves Wolfmother's mouth by reading her voice's samples, which only works on a clip decompressed when it loads: her 28 clips decompress on load, as PCM on the PS Vita (HEVAG stays compressed even then). Compressed, the read failed and her mouth stayed still.

Frame rate:

- The game's automatic frame rate (60 fps, down to its 30 fps mode after slow frames, back to 60 whenever the menu starts) kept switching on the PS Vita, with a freeze at each switch: load hitches tripped it. On the PS Vita the mode is fixed at 60 fps, paced by vsync, or 30 if the player chooses it in the options (`compat/FrameRate.cs`, `rewriter/Patches.cs`).

- The garbage collector no longer stops a level about once a minute (about 0.2 s each time: IL2CPP's collector goes through the whole 18-24 MB managed heap). On the PS Vita it is held and collects when a level starts or restarts from a checkpoint, behind the load or the fade, with 2 MB or more of garbage (a level's load leaves 3-12 MB, a restart after a death about 1), and when the pause menu opens with 4 MB or more (`compat/Gc.cs`). The game allocates only about 0.2 KB a frame in play, so the 24 MB that still collect anywhere take about half an hour of play without a death, a pause or a new level. Level 1-1 to Sweet Angel's arena: one collection, at the load (178 ms), and none in play, where the game's collector ran about every 60 s.

- Start and Select pause the game. Rewired's PS Vita map comes from the game's Dual Analog Gamepad template map, which has no Start or Back; the PS Vita copy gets them on Start and Select (`asset_rules.py`, REWIRED_VITA_ADDED), as the game's maps for other pads have them.
- The touch screen does nothing. The game has phone controls a touch would switch to (its on-screen stick and buttons, which followed the finger); on the PS Vita it sees no touch screen (`compat/TouchInput.cs`).

- The game's effect pools are smaller on the PS Vita (`compat/Pools.cs`): debris 96 instead of 256, blood decals 64 instead of 96, rain 50 drops instead of 100, snow 40 flakes instead of 80, ash 32 instead of 50, area 2's floating dust 80 motes instead of 160. The engine spends about 50 us of the main thread a frame on each visible sprite, so a few hundred on screen take the frame past 16.7 ms whatever the GPU does: at Sweet Angel's arena in level 1-1, 240 pieces of debris took it down to 41-52 fps, with the game's own code at 3 ms of the frame. A survey of every level (`artifacts/gpusurvey.sh`, a heartbeat each second while running right and shooting) found every slow second that wasn't one long frame (a load, or a restart after a death) with 230-435 sprites visible, most of them these effects: level 2-4 fell to 42-49 fps with 160 dust motes, 110 pieces of debris and 50 blood decals on screen, level 4-3 to about 50 with 80 snowflakes and 120 pieces of debris. With the smaller pools 2-4 holds 60 (at most 255 sprites), and 4-3 dips to 55 for a few seconds (at most 311). Explosions throw as many pieces as before; when a pool is full the game takes its oldest piece, so old shards and blood go sooner, and the weather is sparser.
- The main menu's full-screen fade sprite isn't drawn while it is transparent (`compat/Fades.cs`): the game leaves it on at alpha 0 through every level, a whole screen of blending each frame for the GPU. Any sprite the game fades out is left out the same way while its alpha is 0. The screen coverage (`cover=` in the heartbeat) went from 5-6 screens to 4-5 in level 4-1.

- The game's view is the full 480x270 game pixels at 2 screen pixels each: the game's own scale, ceil(544 / 270), was 3 on the PS Vita, which showed 320x181 and looked zoomed in (`compat/View.cs`).
- The opening cutscene's and the menus' sprite sheets (19 sheets, 840 sprites, about 10 MB uncompressed) are read while the screen is still black at startup, which lasts about 5 s longer: read as each screen opened, they stalled a frame for up to 1.3 s on the PS Vita. Reading them on the loading thread instead made the game's own synchronous loads wait for them (`compat/Preload.cs`). `compat/LoadTimer.cs` logs every Resources load of 30 ms or more (HUNT_LOADTIME).

Input:

- The PS Vita pad uses a copy of the game's Rewired template map for Rewired's PS Vita controller. R does what Right Shoulder 2 does, the dash of the game's default layout; L what Left Shoulder 1 does.

## Open issues

- The two logo videos at start (H.264 MP4) are skipped: the PS Vita's Unity has no video player, and HuntCompat's stand-in ends a video that has no converted clip at once.
- The rebuilt level bundles are about twice the size of the game's, which are LZ4HC; the editor writes LZ4.
- No level has been played to its end.
- Loading a level takes about 15 s on the PS Vita: 9 s of its sheets, read synchronously (the loading screen at 1 to 10 fps), then 5 s of its sound bundles, one after the other.
- The main menu offers EXIT GAME, a PC option.
- On the PS Vita the cutscene, title and menus hold 60 fps, with frames of 50-60 ms when a screen opens, and one frame of about 0.12 s each when the global sound bundle (at the splash) and the title's level sound bundles finish loading.
- The black screen at startup is about 10 s: 4.8 s of the game's own loading and 5 s of the sheets read ahead.
- The levels' sheets are uncompressed and fill the PS Vita's video memory (CDRAM, 5 to 8 MB free in a level, from 64 MB at the title); the rest goes to main memory, of which 165 MB or more stay free in every level tried.
