# The editor-free builder

`tools/builder` makes the PS Vita build from the translated Unity project (`work/hunt-<version>-vita`) without running the Unity editor. It started as a copy of The Drifter's port's builder. `build.py` and `pipeline.py build --platform vita` build with it; `--unity` builds with the editor instead.

```
uv run tools/downgrade/pipeline.py build                       # make.py into builds/hunt-2018.2.19f1-vita
uv run tools/builder/make.py --out <folder>                    # the whole build folder elsewhere (--vpk: and its VPK)
uv run tools/downgrade/vita.py sync                            # onto the console
```

`make.py` lays out the folder (`package.py`), writes the data files and the AssetBundles (`build.py`), builds the code module with UnityLinker and IL2CPP (`il2cpp.py`), and adds the pipeline's native plugin and boot.config. The data files alone, against the reference:

```
uv run tools/builder/build.py --compare
uv run tools/builder/build.py --only level0,resources.assets --compare
uv run tools/builder/build.py --only StreamingAssets --compare   # the bundles
uv run tools/builder/build.py --only levels/1-1 --compare        # one bundle
uv run tools/builder/il2cpp.py --compare                         # the code
```

`--compare` checks each file it writes, byte for byte, against the reference: the editor's build in `builds/hunt-2018.2.19f1-vita-unity` (`pipeline.py build --unity`). The builder reads Unity's files (the PS Vita player, IL2CPP, the engine and class libraries, CGIncludes), which `setup.py --vita` extracts from Unity's installers into `tools/external/unity-2018.2.19f1`, and the PS Vita SDK, but not the editor.

The `check_*.py` scripts compare one kind of object with the reference in detail (`check_managers.py`, `check_typehash.py`, `check_imported.py`, ...). For a file that differs, `artifacts/classdiff.py <file>` counts the differing objects by class and `artifacts/objone.py <file> <path ID>...` shows an object's fields side by side; `artifacts/bundlediff.py <bundle> [path ID...]` does the same for a bundle's serialized file.

## Where it stands

As of 2026-11-13, `make.py` takes about 25 minutes from nothing: 3 for the data files, 12 for the bundles (most of it encoding 3,791 sounds), 8 for IL2CPP. Its build plays on the PS Vita: the menus, the level select, the briefings and level 1-1 to Sweet Angel's arena at 60 fps, as the editor's build does.

| File | Against Unity's |
|---|---|
| `level0`, `level1`, `sharedassets0.assets`, `sharedassets1.assets` | Identical. |
| `globalgamemanagers` | The same but for BuildSettings' build GUID and auth token (each build's own), and its class hash list's order. |
| `globalgamemanagers.assets` | Only the Game MonoScript's `m_PropertiesHash` differs: the builder's field model of Game hashes differently, cause not found yet. Every other script matches. |
| `resources.assets` | 45 KB smaller. See below. |
| `resources.assets.resS` | 3 DXT5 textures differ, from another DXT5 encoder. |
| `resources.resource`, the bundles' `.resource` | The sound banks differ inside: HEVAG from the builder's encoder on another Vorbis decoder's samples, and FMOD's per-clip hash. The PCM clips (the briefings) differ in a few samples' rounding. |
| `Resources/unity_builtin_extra` | The shaders' programs as `resources.assets`'s (below). |
| `StreamingAssets` (252 AssetBundles: the levels and the sounds) | `tools/builder/bundles.py`. Every bundle's serialized file matches except its sprites (249, the vertex order below); the streams as above. The containers use Unity's LZ4 (r131, `lz4unity.py`). |
| The code module, `global-metadata.dat` | 0.1% larger: the builder's UnityLinker run keeps a little more. Imports and exports are the same. |

In `resources.assets`:

- Sprites (16,542): the triangles are the same as Unity's, but the vertices and indices come in another order for 15,851 of them. For 645, Unity's triangulation of the outline gives other triangles; Unity's tessellation (libtess) would have to be reproduced. Rects, pivots, offsets and `textureRect` match.
- `Legacy Shaders/Transparent/VertexLit` (the fallback of `Sprites/Diffuse Flash`) has fixed-function passes, which the builder doesn't convert into programs. The PS Vita never uses it, but Unity's programs for it shift the build-wide program IDs and keyword indices of the shaders after it.
- The AudioMixerController differs in size.

The sprite settings have no physics shapes (`tools/downgrade/sprite_rules.py`): Huntdown has no 2D colliders, and the builder doesn't trace Unity's fallback shape from the alpha.
