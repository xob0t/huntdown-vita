"""Sprite sheets as the game shipped them. Huntdown loads its sheets whole (Resources.LoadAll<Sprite>,
AssetBundle.LoadAssetWithSubAssets<Sprite>) and picks sprites by position: the title logo's bullet-hole frames,
the HUD's digits, the effects' and enemies' animations. AssetRipper's export (SpriteExportMode Native) keeps every
sprite in its texture's importer settings, but three things there differ from the game:

- The order. The export numbers a sheet's sprites (their fileIDs, 21300000 + 2 * n) and lists them in an order of
  its own: 293 of the 419 sheets in Resources came out shuffled (Menu/MainMenu: 84, 79, 83, ...), so the game
  showed the wrong frames. The game's order is in its containers, the Resources container (ResourceManager) and
  each bundle's (AssetBundle.m_Container), which list a sheet's sprites in load order. sprite_overlay lists the
  sprites and numbers their fileIDs in that order, and points every reference to a sprite (scenes, prefabs,
  animations, assets) at its new fileID.
- The meshes. Each sprite is drawn with its own mesh, cut to its pixels, and the game's sheets rely on it: their
  packer put small sprites in larger ones' see-through parts (the burning tower's frames in the level select's
  computer monitor), and animation frames overlap (the level select's lit building, 252 and 253, 25 pixels
  apart). The export's outlines, rebuilt from those meshes, lose most of their pieces (the difficulty screen's
  bullet-hole overlay kept 6 to 8 of 294 to 1,315 holes; the computer frame kept only its monitor's opening).
  Every way of making the meshes again showed some sheet wrong: from the alpha, the computer frame showed the
  frames packed in it; cut around the sprites inside, the building's frames showed each other's pixels and
  flickered. So each sprite's outline is the game's own mesh, read from the game's files: one polygon per
  triangle, which the editor's import triangulates back into the same triangles.
- A few rects (LevelsArea1's map, 1015 pixels wide in the export, 1024 in the game), set to the game's.

Single sprites (a texture that is one sprite: the levels' 1024x512 scenery pieces, backgrounds) get the game's
mesh and pivot too. The export moved the pivots of 69 of the 283 when the picture has see-through edges: level 1-1's
second piece was drawn 290 pixels low, and the ground vanished a few screens to the right.
"""

import json
import re
import sys
from pathlib import Path

import progress
from asset_rules import current_text
from common import WORK, YAML_FILES, expect, game_data, listing, log, scan

SHEET_CACHE = WORK / 'sprite-sheets.json'
SHEET_CACHE_VERSION = 4
SPRITE_FILE_ID = 21300000  # the first sheet's first sprite; the next ones go up by 2
# Sprites (class 213) take fileIDs 21300000 to 21399998. Each sheet gets a run of its own, one after the other,
# instead of every sheet starting at 21300000: the player's build writes a file's objects in fileID order, so
# with the same numbers everywhere resources.assets held every sheet's sprite 0, then every sheet's sprite 1, and
# a sheet's sprites lay hundreds of KB apart. On the PS Vita's memory card each was a read of its own: a 256x256
# enemy sheet with 51 sprites took 400 ms to load, half of a level's 25 s of loading.
FILE_ID_LIMIT = 21399998
RECYCLE_NAMES = re.compile(r'^  fileIDToRecycleName:\n((?:    -?\d+: .*\n)*)', re.MULTILINE)
RECYCLE_ENTRY = re.compile(r'^    (-?\d+): (.*)$', re.MULTILINE)
SPRITE_LIST = re.compile(r'^    sprites:\n((?:    - .*\n|      .*\n)*)', re.MULTILINE)
SPRITE_BLOCK = re.compile(r'^    - serializedVersion: 2\n      name: (.*)\n(?:      .*\n)*', re.MULTILINE)
OUTLINE = re.compile(r'^      outline:(?: \[\])?\n(?:(?:      - - |        - ).*\n)*', re.MULTILINE)
PIVOT = re.compile(r'^      pivot: \{x: [^,]+, y: [^}]+\}\n', re.MULTILINE)
ALIGNMENT = re.compile(r'^      alignment: \d+\n', re.MULTILINE)
# A single sprite's (spriteMode 1): the importer's own pivot and alignment, and its spriteSheet's outline.
SINGLE_MODE = re.compile(r'^  spriteMode: 1$', re.MULTILINE)
SINGLE_PIVOT = re.compile(r'^  spritePivot: \{x: [^,]+, y: [^}]+\}\n', re.MULTILINE)
SINGLE_ALIGNMENT = re.compile(r'^  alignment: \d+\n', re.MULTILINE)
SINGLE_OUTLINE = re.compile(r'^    outline:(?: \[\])?\n(?:(?:    - - |      - ).*\n)*', re.MULTILINE)
SPRITE_RECT = re.compile(
    r'^(      rect:\n        serializedVersion: 2\n)'
    r'        x: (\S+)\n        y: (\S+)\n        width: (\S+)\n        height: (\S+)\n',
    re.MULTILINE,
)
REFERENCE = re.compile(r'fileID: (\d+), guid: ([0-9a-f]{32})')


def unquoted(name: str):
    name = name.strip()
    return name[1:-1] if len(name) > 1 and name[0] == name[-1] and name[0] in '\'"' else name


def number(value: float, digits: int = 3):
    return repr(round(value, digits)).removesuffix('.0')


def pivot_text(pivot):
    """A pivot as the importer writes it, to a fraction of a pixel even on a 1024-pixel sprite (a fraction of
    the rect: 3 digits would move it)."""
    return f'{{x: {number(pivot[0], 7)}, y: {number(pivot[1], 7)}}}'


def sprite_mesh(sprite):
    """[name, [x, y, width, height], [pivot x, pivot y], triangles: x0, y0, x1, y1, x2, y2, ... in the sheet's
    pixels] of a Sprite."""
    from UnityPy.export.SpriteHelper import MeshHandler

    mesh = MeshHandler(sprite.m_RD, sprite.object_reader.version)
    mesh.process()
    rect, ppu = sprite.m_Rect, sprite.m_PixelsToUnits
    # The mesh's positions are in units from the pivot.
    px, py = rect.x + sprite.m_Pivot.x * rect.width, rect.y + sprite.m_Pivot.y * rect.height
    points = [(px + v[0] * ppu, py + v[1] * ppu) for v in mesh.m_Vertices]
    triangles = [
        round(c, 3) for group in mesh.get_triangles() for triangle in group for i in triangle for c in points[i]
    ]
    pivot = [sprite.m_Pivot.x, sprite.m_Pivot.y]
    return [sprite.m_Name, [rect.x, rect.y, rect.width, rect.height], pivot, triangles]


def original_sheets(game: Path):
    """{sheet: [[name, rect, pivot, triangles] in the game's load order]} for every texture with sprites:
    'resources/<path>' for Resources (its path there, lower case, without extension), else the bundle's asset path
    in lower case without extension ('assets/toassetbundles/...'). Read once with UnityPy and kept in SHEET_CACHE."""
    import UnityPy

    data = game_data(game)
    sources = [data / 'globalgamemanagers', data / 'resources.assets']
    bundles = sorted(
        p for p in (data / 'StreamingAssets').rglob('*') if p.is_file() and p.read_bytes()[:8] == b'UnityFS\0'
    )
    stamp = [SHEET_CACHE_VERSION, *([str(p), p.stat().st_size, p.stat().st_mtime_ns] for p in [*sources, *bundles])]
    try:
        cached = json.loads(SHEET_CACHE.read_text(encoding='utf-8'))
        if cached['stamp'] == stamp:
            return cached['sheets']
    except (OSError, ValueError, KeyError):
        pass
    sheets = {}

    def add(key, obj):
        if type(obj).__name__ == 'Sprite':
            sheets.setdefault(key, []).append(sprite_mesh(obj))

    with progress.step("Sprite sheets: reading the game's sprites") as step:
        env = UnityPy.load(*map(str, sources))
        manager = next(o for o in env.objects if o.type.name == 'ResourceManager').read()
        for path, pointer in manager.m_Container:
            try:
                add('resources/' + path.lower(), pointer.deref().read())
            except Exception:  # noqa: BLE001, S112 - an entry UnityPy can't read has no sprite
                continue
        for index, bundle in enumerate(bundles):
            step.note(f'{index + 1}/{len(bundles)} {bundle.name}')
            env = UnityPy.load(str(bundle))
            for obj in env.objects:
                if obj.type.name != 'AssetBundle':
                    continue
                for path, info in obj.read().m_Container:
                    try:
                        add(path.lower().rsplit('.', 1)[0], info.asset.deref().read())
                    except Exception:  # noqa: BLE001, S112
                        continue
    SHEET_CACHE.write_text(json.dumps({'stamp': stamp, 'sheets': sheets}), encoding='utf-8')
    return sheets


def sheet_key(relative: Path):
    """The original_sheets key of an exported texture (relative to Assets)."""
    stem = relative.with_suffix('').as_posix().lower()
    return 'resources/' + stem.removeprefix('resources/') if stem.startswith('resources/') else 'assets/' + stem


def reorder(text: str, order: list, first: int = SPRITE_FILE_ID):
    """(the meta's text with its sprites listed in order and numbered first, first + 2, ..., {old fileID: new
    fileID}), or None when the sheet's sprites aren't order's (other names, or a name twice)."""
    recycle = RECYCLE_NAMES.search(text)
    sprites = SPRITE_LIST.search(text)
    if not recycle or not sprites:
        return None
    blocks = {unquoted(m[1]): m[0] for m in SPRITE_BLOCK.finditer(sprites[1])}
    ids = {unquoted(m[2]): int(m[1]) for m in RECYCLE_ENTRY.finditer(recycle[1])}
    if len(blocks) != len(order) or set(blocks) != set(order) or set(ids) != set(order):
        return None
    new_ids = {name: first + 2 * i for i, name in enumerate(order)}
    names = {m[2]: unquoted(m[2]) for m in RECYCLE_ENTRY.finditer(recycle[1])}  # as written -> plain
    written = {plain: raw for raw, plain in names.items()}
    recycle_text = ''.join(f'    {new_ids[name]}: {written[name]}\n' for name in order)
    sprites_text = ''.join(blocks[name] for name in order)
    text = text[: sprites.start(1)] + sprites_text + text[sprites.end(1) :]
    text = text[: recycle.start(1)] + recycle_text + text[recycle.end(1) :]
    return text, {ids[name]: new_ids[name] for name in order if ids[name] != new_ids[name]}


def mesh_outline(rect, triangles, indent='      '):
    """The outline lines of a sprite: one polygon per triangle, its points relative to the rect's centre (Unity's
    sprite outline coordinates). indent: a sheet's sprite's (6 spaces) or a single sprite's spriteSheet's (4)."""
    x, y, w, h = rect
    cx, cy = x + w / 2, y + h / 2
    lines = [f'{indent}outline:\n']
    for k in range(0, len(triangles), 6):
        for j in range(3):
            vx, vy = triangles[k + 2 * j], triangles[k + 2 * j + 1]
            lines.append(f'{indent}{"- - " if j == 0 else "  - "}{{x: {number(vx - cx)}, y: {number(vy - cy)}}}\n')
    return ''.join(lines) if len(lines) > 1 else f'{indent}outline: []\n'


def single_mesh(text: str, sprite: list):
    """(text with a single sprite's pivot and outline the game's, whether its pivot changed). The export writes
    them off when the picture has see-through edges: level 1-1's second scenery piece (1024x512, see-through at
    the bottom) came out with its pivot at y 1.566 instead of 1, drawn 290 pixels low: the ground was gone."""
    _name, rect, pivot, triangles = sprite
    pivot_line = f'  spritePivot: {pivot_text(pivot)}\n'
    current = SINGLE_PIVOT.search(text)
    moved = current is not None and current[0] != pivot_line
    text = SINGLE_PIVOT.sub(pivot_line, text, count=1)
    text = SINGLE_ALIGNMENT.sub('  alignment: 9\n', text, count=1)  # Custom: the pivot as given
    text = SINGLE_OUTLINE.sub(lambda _m: mesh_outline(rect, triangles, '    '), text, count=1)
    return text, moved


def game_meshes(text: str, sprites: list):
    """(text with each sprite's outline the game's mesh and its rect and pivot the game's, sprites given the game's
    mesh, rects set, sprites the game's sheet lacks): the last get an empty outline, which the editor fills from
    the sprite's alpha. A rect goes with its pivot (a fraction of the rect): the export's pivots fit its rects."""
    listed = SPRITE_LIST.search(text)
    if not listed:
        return text, 0, 0, 0
    game = {name: (rect, pivot, triangles) for name, rect, pivot, triangles in sprites}
    parts, meshed, rects, missing, at = [], 0, 0, 0, 0
    for block in SPRITE_BLOCK.finditer(listed[1]):
        parts.append(listed[1][at : block.start()])
        body = block[0]
        found = game.get(unquoted(block[1]))
        if found is None:
            body = OUTLINE.sub('      outline: []\n', body, count=1)
            missing += 1
        else:
            rect, pivot, triangles = found
            current = SPRITE_RECT.search(body)
            if current and [round(float(v), 3) for v in current.groups()[1:]] != [round(v, 3) for v in rect]:
                fields = ('x', 'y', 'width', 'height')
                values = ''.join(f'        {f}: {number(v)}\n' for f, v in zip(fields, rect, strict=True))
                body = body[: current.start()] + current[1] + values + body[current.end() :]
                body = PIVOT.sub(f'      pivot: {pivot_text(pivot)}\n', body, count=1)
                body = ALIGNMENT.sub('      alignment: 9\n', body, count=1)  # Custom: the pivot as given
                rects += 1
            body = OUTLINE.sub(lambda _m, r=rect, t=triangles: mesh_outline(r, t), body, count=1)
            meshed += 1
        parts.append(body)
        at = block.end()
    parts.append(listed[1][at:])
    return text[: listed.start(1)] + ''.join(parts) + text[listed.end(1) :], meshed, rects, missing


def sprite_overlay(exported: Path, game: Path, overlay: dict):
    """The sheets in the game's order with the game's meshes, and the references to their renumbered sprites
    (see the module's docstring)."""
    sheets = original_sheets(game)
    result, renumbered, unmatched = {}, {}, []
    meshed = rects = missing = singles = pivots = 0
    next_id = SPRITE_FILE_ID
    for relative, stat in sorted(listing(exported / 'Assets').items()):
        if stat is None or relative.suffix != '.meta' or relative.suffixes[-2:-1] != ['.png']:
            continue
        path = Path('Assets') / relative
        text = original = current_text(exported, overlay, path)
        sprites = sheets.get(sheet_key(relative.with_suffix('')), [])
        if SINGLE_MODE.search(text):
            if len(sprites) == 1 and '    sprites: []\n' in text:
                text, moved = single_mesh(text, sprites[0])
                singles, pivots = singles + 1, pivots + moved
                if text != original:
                    result[path] = text.encode('utf-8')
            continue
        if '    sprites:\n    - ' not in text:
            continue
        order = [sprite[0] for sprite in sprites]
        names = [unquoted(m[1]) for m in SPRITE_BLOCK.finditer((SPRITE_LIST.search(text) or [None, ''])[1])]
        # Every sheet gets fileIDs of its own (see FILE_ID_LIMIT), in the game's order where the game has it.
        done = reorder(text, order or names, next_id)
        if done is None:
            if order:
                unmatched.append(str(relative.with_suffix('')))
        else:
            text, ids = done
            next_id += 2 * len(order or names)
            if ids:
                guid = re.search(r'^guid: ([0-9a-f]{32})$', text, re.MULTILINE)[1]
                renumbered[guid] = ids
        text, given, fixed, lacking = game_meshes(text, sprites)
        meshed, rects, missing = meshed + given, rects + fixed, missing + lacking
        if text != original:
            result[path] = text.encode('utf-8')
    if next_id > FILE_ID_LIMIT:
        sys.exit(f'sprite_rules: {(next_id - SPRITE_FILE_ID) // 2} sheet sprites need fileIDs past {FILE_ID_LIMIT}')
    # The references to renumbered sprites, in every YAML file that names one of their sheets.
    keys = [guid.encode() for guid in sorted(renumbered)]
    users = scan(
        'sprite-sheet-references',
        exported / 'Assets',
        lambda relative: relative.suffix in YAML_FILES,
        lambda file: any(key in file.read_bytes() for key in keys),
        version=' '.join(sorted(renumbered)),
    )
    references, files = 0, 0
    for found, uses in users.items():
        if not uses:
            continue
        path = Path('Assets') / found
        text = current_text(exported, {**overlay, **result}, path)

        def point(match):
            nonlocal references
            new = renumbered.get(match[2], {}).get(int(match[1]))
            if new is None:
                return match[0]
            references += 1
            return f'fileID: {new}, guid: {match[2]}'

        new_text = REFERENCE.sub(point, text)
        if new_text != text:
            result[path] = new_text.encode('utf-8')
            files += 1
    expect(len(renumbered), 290, "sprite sheets put back in the game's order")
    expect(meshed, 16000, "sprites given the game's mesh")
    expect(singles, 280, "single sprites given the game's mesh and pivot")
    log(
        f"Sprites: {len(renumbered)} sheets in the game's order, {references} references in {files} files "
        f"renumbered; {meshed} sprites with the game's mesh, {rects} rects set to the game's, {missing} the game's "
        f"sheets lack (mesh from the alpha); {singles} single sprites with the game's mesh, {pivots} of their "
        "pivots moved to the game's"
        + (
            f"; {len(unmatched)} sheets whose sprites differ from the game's: {', '.join(unmatched[:5])}"
            if unmatched
            else ''
        )
    )
    return result
