# The editor-free builder

`tools/builder` writes the PS Vita player's data files from the translated Unity project (`work/hunt-<version>-vita`) without running the Unity editor. It started as a copy of The Drifter's port's builder. It is not part of `build.py` yet: the VPK still comes from the editor's build.

```
uv run tools/builder/build.py --out builds/hunt-2018.2.19f1-vita-builder --compare
uv run tools/builder/build.py --out builds/hunt-2018.2.19f1-vita-builder --only level0,resources.assets --compare
uv run tools/builder/build.py --out builds/hunt-2018.2.19f1-vita-builder --only StreamingAssets --compare   # the bundles
uv run tools/builder/build.py --out builds/hunt-2018.2.19f1-vita-builder --only levels/1-1 --compare        # one bundle
```

`--compare` checks each file it writes, byte for byte, against the reference: a copy of the editor's build in `builds/hunt-2018.2.19f1-vita-unity` (copy `builds/hunt-2018.2.19f1-vita` there after an editor build). The builder still needs the editor's data folder for the PS Vita player, IL2CPP and the shader compiler, but not the editor itself.

The `check_*.py` scripts compare one kind of object with the reference in detail (`check_managers.py`, `check_typehash.py`, `check_imported.py`, ...). For a file that differs, `artifacts/classdiff.py <file>` counts the differing objects by class and `artifacts/objone.py <file> <path ID>...` shows an object's fields side by side; `artifacts/bundlediff.py <bundle> [path ID...]` does the same for a bundle's serialized file.

## Where it stands

As of 2026-11-13, a full run takes about 3 minutes, and the bundles about 12 more (most of it encoding 3,791 sounds).

| File | Against Unity's |
|---|---|
| `level0`, `level1`, `sharedassets0.assets`, `sharedassets1.assets` | Identical. |
| `resources.assets.resS` | Same size; 3 DXT5 textures differ, from another DXT5 encoder. |
| `resources.assets` | 45 KB smaller. See below. |
| `globalgamemanagers.assets` | Only the Game MonoScript's `m_PropertiesHash` differs: the builder's field model of Game hashes differently, cause not found yet. Every other script matches. |
| `globalgamemanagers` | Lacks 3 managers (classes 301, 303 and 310). ResourceManager and ScriptMapper follow resources.assets's differences. |
| `Resources/unity_builtin_extra`, `resources.resource` | Not looked at yet. |
| `StreamingAssets` (252 AssetBundles: the levels and the sounds) | `tools/builder/bundles.py`. Every bundle's serialized file matches except its sprites (249, the vertex order below). The sound banks differ inside: HEVAG from the builder's encoder on another Vorbis decoder's samples, and FMOD's per-clip hash. area3/level1's DXT5 textures differ, as in resources.assets. `--compare` compares the files inside a bundle; Unity's LZ4 is older than the lz4 library's and compresses differently. |

In `resources.assets`:

- Sprites (16,542): the triangles are the same as Unity's, but the vertices and indices come in another order for 15,851 of them. For 645, Unity's triangulation of the outline gives other triangles; Unity's tessellation would have to be reproduced. Rects, pivots, offsets and `textureRect` match.
- `Legacy Shaders/Transparent/VertexLit` (the fallback of `Sprites/Diffuse Flash`) has fixed-function passes, which the builder doesn't convert into programs. The PS Vita never uses it, but Unity's programs for it shift the build-wide program IDs and keyword indices of the shaders after it.
- The AudioMixerController differs in size.

The sprite settings have no physics shapes (`tools/downgrade/sprite_rules.py`): Huntdown has no 2D colliders, and the builder doesn't trace Unity's fallback shape from the alpha.
