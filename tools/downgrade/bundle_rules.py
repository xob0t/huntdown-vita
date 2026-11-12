"""AssetBundles. Huntdown keeps its levels, area art and sound in about 250 AssetBundles under
StreamingAssets, which the game opens by path (AssetBundle.LoadFromFile(Async) in LoadManager, Snd, Gfx and
DevLoader) and reads by asset name; it never reads the bundle manifest. The game's bundles are Unity
2019.4's, which a 2018.2 player can't read, so the build makes them again: AssetRipper's export keeps each
asset's bundle in its .meta (assetBundleName), and unity/DowngradeBuild.cs builds the bundles for the
target and copies them into the player's StreamingAssets under the same paths.

bundle_overlay leaves the export's copies of the 2019 bundles (and their .manifest files) out of the
project, and the two MP4 videos, which no player plays (compat/Video.cs ends a video without a clip at
once, and the game then goes on; see docs). It checks that every bundle the game ships has assets named
for it in the export."""

import re
from pathlib import Path

from common import expect, game_data, listing, log, meta_of

STREAMING = Path('Assets') / 'StreamingAssets'
# Files in StreamingAssets the project keeps: the game's own (not bundles), which the player reads as files.
KEPT = {'SaveIcon.png'}
# The game's root bundle (the AssetBundleManifest, named after its build folder): nothing loads it.
ROOT_BUNDLE = 'StreamingAssets'
BUNDLE_NAME = re.compile(rb'^  assetBundleName: (\S+)\s*$', re.MULTILINE)
# Unity's bundle files start with this signature.
SIGNATURE = b'UnityFS\0'


def game_bundles(game: Path):
    """The game's bundles, as bundle names (paths under StreamingAssets with '/', lower case)."""
    folder = game_data(game) / 'StreamingAssets'
    names = set()
    for path in folder.rglob('*'):
        if not path.is_file() or path.suffix in {'.manifest', '.png', '.mp4'}:
            continue
        with path.open('rb') as file:
            if file.read(len(SIGNATURE)) != SIGNATURE:
                continue
        names.add(path.relative_to(folder).as_posix().lower())
    names.discard(ROOT_BUNDLE.lower())
    return names


def exported_bundles(exported: Path):
    """{bundle name: number of exported assets named for it} from the export's .meta files."""
    counts = {}
    for relative, stat in listing(exported / 'Assets').items():
        if stat is None or relative.suffix != '.meta':
            continue
        data = (exported / 'Assets' / relative).read_bytes()
        match = BUNDLE_NAME.search(data)
        if match:
            name = match[1].decode('utf-8').lower()
            counts[name] = counts.get(name, 0) + 1
    return counts


def bundle_overlay(exported: Path, game: Path):
    """The exported paths the project leaves out of StreamingAssets (a set). Stops if a bundle the game ships
    has no asset in the export, which the build would then not make."""
    shipped = game_bundles(game)
    named = exported_bundles(exported)
    missing = sorted(shipped - set(named))
    if missing:
        listed = ', '.join(missing[:10])
        raise SystemExit(f"{len(missing)} of the game's bundles have no assets in the export: {listed}")
    extra = sorted(set(named) - shipped)
    if extra:
        log(f"Bundles named in the export that the game doesn't ship (built anyway): {', '.join(extra[:10])}")
    entries = listing(exported / STREAMING)
    kept = {r for r, stat in entries.items() if stat is not None and r.name in KEPT}
    # A folder stays only if it holds a kept file; the others go with their .meta files, or the editor
    # would make them again.
    kept_folders = {parent for r in kept for parent in r.parents}
    excluded = set()
    for relative, stat in entries.items():
        if relative.suffix == '.meta' or relative in kept or (stat is None and relative in kept_folders):
            continue
        path = STREAMING / relative
        excluded.update({path, meta_of(path)})
    files = sum(1 for r in excluded if r.suffix != '.meta' and entries.get(r.relative_to(STREAMING)) is not None)
    expect(len(shipped), 250, 'AssetBundles the game ships')
    log(
        f'AssetBundles: {len(shipped)} shipped, {len(named)} named in the export '
        f'({sum(named.values())} assets); {files} StreamingAssets files left out for the build to make'
    )
    return excluded
