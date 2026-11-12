"""Project rules for the exported assets and settings: each *_overlay function returns
{path in the project: new content} for pipeline.asset_overlay, which runs them in order."""

import collections
import re
import sys
from pathlib import Path

from common import (
    expect,
    folder_meta,
    game_data,
    guid_for,
    listing,
    log,
    meta_of,
    scan,
)


def current_bytes(exported: Path, overlay: dict, relative: Path):
    """A file as the rules before left it: its overlay entry, else the export's file. An overlay entry is
    the file's bytes, or the Path of a file to copy; every rule reads entries through this, or
    current_text, so it takes either."""
    content = overlay.get(relative)
    if content is None:
        return (exported / relative).read_bytes()
    return content.read_bytes() if isinstance(content, Path) else content


def current_text(exported: Path, overlay: dict, relative: Path):
    """current_bytes as text."""
    return current_bytes(exported, overlay, relative).decode('utf-8')


# HuntCompat's GUID in the project (pipeline.assembly_overlay writes its .meta with it).
COMPAT_GUID = guid_for('plugin', 'HuntCompat')


# Rewired's PS Vita controller has one pair of shoulder buttons, which its Dual Analog Gamepad template
# file maps to Left and Right Shoulder 2. Huntdown's joystick maps (MainScene's Rewired Input Manager)
# include a template map, which puts Dash (B9 in the game's default layout, GamePad.SetDefaultAssign) on
# Right Shoulder 2 and the spare buttons B8 on Left Shoulder 1, Dash (an action the default layout
# doesn't use) on Right Shoulder 1 and B10 on Left Shoulder 2. Rewired prefers a controller's own map to
# the template's: on the PS Vita, the template map gets a copy for the PS Vita controller, with R doing
# what Right Shoulder 2 does (the game's dash) and L what Left Shoulder 1 does.
REWIRED_TEMPLATE = '83b427e4-086f-47f3-bb06-be266abd1ca5'  # Dual Analog Gamepad
REWIRED_VITA = 'd2aef070-7caa-42ff-b2dc-daac7e4a62b4'  # Sony Playstation Vita
# Template element id -> the PS Vita controller's (DualAnalogGamepad.asset, SonyPlaystationVita.asset);
# template elements not listed have no button on the PS Vita.
REWIRED_VITA_ELEMENTS = {
    **{i: i for i in range(6)},  # sticks, Cross, Circle
    7: 6,  # Action Top Row 1: Square
    8: 7,  # Action Top Row 2: Triangle
    10: 8,  # Left Shoulder 1: L
    13: 9,  # Right Shoulder 2: R
    14: 10,  # Center 1: Select
    15: 11,  # Center 2: Start
    **{i: i - 7 for i in range(19, 23)},  # the d-pad
}
REWIRED_PLATFORMS = {'vita'}
REWIRED_SECTION = re.compile(r'^    joystickMaps:\n(.*?)(?=^    keyboardMaps:)', re.MULTILINE | re.DOTALL)
REWIRED_MAP = re.compile(r'^    - id: (\d+)\n.*?(?=^    - id: |\Z)', re.MULTILINE | re.DOTALL)
REWIRED_ELEMENT_MAP = re.compile(
    r'^      - _actionCategoryId: .*?(?=^      - _actionCategoryId: |\Z)', re.MULTILINE | re.DOTALL
)


def rewired_vita_maps(text: str):
    """The scene's text with a PS Vita copy of each template joystick map, or None if it has none."""
    section = REWIRED_SECTION.search(text)
    if not section or f'hardwareGuidString: {REWIRED_TEMPLATE}' not in section[1]:
        return None
    maps = [m[0] for m in REWIRED_MAP.finditer(section[1])]
    next_id = max(int(m.split('\n', 1)[0].split(': ')[1]) for m in maps) + 1
    added = []
    for block in maps:
        if f'hardwareGuidString: {REWIRED_TEMPLATE}\n' not in block:
            continue
        head, _, body = block.partition('      actionElementMaps:\n')
        kept = []
        for entry in REWIRED_ELEMENT_MAP.findall(body):
            action = int(re.search(r'_actionId: (-?\d+)', entry)[1])
            element = int(re.search(r'_elementIdentifierId: (\d+)', entry)[1])
            if action < 0 or element not in REWIRED_VITA_ELEMENTS:
                continue
            kept.append(
                re.sub(r'_elementIdentifierId: \d+', f'_elementIdentifierId: {REWIRED_VITA_ELEMENTS[element]}', entry)
            )
        head = re.sub(r'^    - id: \d+', f'    - id: {next_id}', head, count=1)
        head = head.replace(f'hardwareGuidString: {REWIRED_TEMPLATE}', f'hardwareGuidString: {REWIRED_VITA}')
        added.append(head + '      actionElementMaps:\n' + ''.join(kept))
        next_id += 1
    return text[: section.end()] + ''.join(added) + text[section.end() :]


def rewired_vita_overlay(exported: Path, overlay: dict, platform: str):
    if platform not in REWIRED_PLATFORMS:
        return {}
    has_maps = scan(
        'rewired-template-maps',
        exported / 'Assets',
        lambda relative: relative.suffix in ('.unity', '.prefab'),
        lambda path: f'hardwareGuidString: {REWIRED_TEMPLATE}'.encode() in path.read_bytes(),
    )
    result = {}
    for found, has in has_maps.items():
        relative = Path('Assets') / found
        if has:
            new = rewired_vita_maps(current_text(exported, overlay, relative))
            if new is not None:
                result[relative] = new.encode('utf-8')
    log(f'Rewired: PS Vita joystick maps in {len(result)} files')
    expect(len(result), 1, 'files with Rewired PS Vita joystick maps')
    return result


# IL2CPP strips the class libraries down to what code references. The game's assemblies and plugins
# are kept whole: Rewired finds its platform's input classes by name. So are, per platform, engine types
# only HuntCompat's reflection reaches: the PS Vita's free memory, for the heartbeat (compat/Diag.cs).
PLATFORM_LINK_PRESERVE = {'vita': {'UnityEngine.PSVitaModule': ['UnityEngine.PSVita.Diagnostics']}}
# Per platform, plugin assemblies left to the linker instead of kept whole: no scripts, no startup hooks,
# nothing in them found by name there. On the PS Vita, Rewired's Windows input (Rewired_Core looks its
# initializer up by name only on Windows).
PLATFORM_LINKED = {'vita': {'Rewired_Windows'}}


def link_xml(assemblies: list, platform: str):
    """The linker's link.xml for the platform; assemblies: the game's and the plugins' DLLs."""
    kept = PLATFORM_LINK_PRESERVE.get(platform, {})
    linked = PLATFORM_LINKED.get(platform, set())
    lines = ['<linker>']
    lines += [
        f'  <assembly fullname="{dll.stem}" preserve="all"/>' for dll in sorted(assemblies) if dll.stem not in linked
    ]
    for assembly, types in sorted(kept.items()):
        lines.append(f'  <assembly fullname="{assembly}">')
        lines += [f'    <type fullname="{t}" preserve="all"/>' for t in sorted(types)]
        lines.append('  </assembly>')
    return '\n'.join([*lines, '</linker>', ''])


# The audio import settings per platform: the game's load type -> (load type, compression format, quality;
# None keeps the clip's). Load types: 0 decompress on load, 1 compressed in memory, 2 streaming. Formats:
# 1 Vorbis, 3 MP3, 5 HEVAG (the PS Vita's hardware ADPCM). Huntdown's 3,798 short clips decompress on load,
# to PCM in memory, which the PS Vita's memory can't hold area after area; they stay compressed there as
# HEVAG, which FMOD decodes at little cost (Crow Country's port measured these on the console). Its 70
# streamed clips, the music, stream. A clip of STREAM_SECONDS or more streams, whatever the game had. The
# PS Vita's importer encodes every clip as HEVAG, whatever format is asked: the MP3 asked for its streams
# comes out HEVAG too (the built player's 70 streamed clips are).
# The export writes every clip at Vorbis quality 0, the lowest (AssetRipper can't tell the game's), and with
# preloadAudioData 0, so a clip's data would load when it first plays: elsewhere the clips keep their load
# type, at quality 0.5 (the game's bundles are about that size) and preloaded unless they stream.
PLATFORM_AUDIO = {'vita': {0: (1, 5, None), 1: (1, 3, 0.5), 2: (2, 3, 0.7)}}
DEFAULT_AUDIO = {0: (0, 1, 0.5), 1: (1, 1, 0.5), 2: (2, 1, 0.5)}
# No clip loads its audio data in the background (the importer's Load In Background stays off, as the game has
# it): the game unloads each sound bundle 0.3 s after taking its clips out (LoadManager.UnloadLevelAssetBundle),
# and on the PS Vita a clip whose data was still being read on the loading thread then stayed loading for good.
# Unloading it at the next level (Snd.ClearUnusedLevelClips, AudioClip.UnloadAudioData) waited for it forever in
# SoundManager::UnloadClip: the game hung loading a level. Nor did it shorten the frames a bundle's clips stall.
STREAM_SECONDS = 45
# Clips the game reads the samples of (AudioClip.GetData), which only works on a clip decompressed when it loads:
# the mission briefing moves Wolfmother's mouth with her voice (ComputerBrief). Kept compressed (HEVAG on the
# PS Vita), GetData failed ("Cannot get data on compressed samples") and her mouth stayed still. They decompress
# on load on every platform, and are PCM on the PS Vita: HEVAG, its hardware format, stays HEVAG in memory even
# decompressed on load, and GetData failed the same.
READ_CLIPS = ('toassetbundles/audio/gui/computermap/brief/wolfmother/',)
AUDIO_SETTINGS = re.compile(
    r'^(  defaultSettings:\n    loadType: )(\d)(\n(?:    .*\n)*?    compressionFormat: )(\d+)(\n    quality: )([\d.]+)',
    re.MULTILINE,
)


def audio_seconds(path: Path):
    """Length of an exported Ogg Vorbis or WAV clip in seconds, or None."""
    data = path.read_bytes()
    if path.suffix == '.ogg':
        header = data.find(b'\x01vorbis')
        last = data.rfind(b'OggS')
        if header < 0 or last < 0:
            return None
        rate = int.from_bytes(data[header + 12 : header + 16], 'little')
        granule = int.from_bytes(data[last + 6 : last + 14], 'little')
        return granule / rate if rate else None
    if path.suffix == '.wav':
        fmt, chunk = data.find(b'fmt '), data.find(b'data')
        if fmt < 0 or chunk < 0:
            return None
        byte_rate = int.from_bytes(data[fmt + 16 : fmt + 20], 'little')
        return int.from_bytes(data[chunk + 4 : chunk + 8], 'little') / byte_rate if byte_rate else None
    return None


def audio_overlay(exported: Path, platform: str):
    """The audio clips' import settings for the platform (PLATFORM_AUDIO, else DEFAULT_AUDIO)."""
    formats = PLATFORM_AUDIO.get(platform, DEFAULT_AUDIO)
    overlay, counts, streamed, decompressed = {}, collections.Counter(), [], []
    for relative, stat in listing(exported / 'Assets').items():
        if stat is None or relative.suffix != '.meta' or relative.suffixes[-2:-1] not in (['.ogg'], ['.wav'], ['.mp3']):
            continue
        meta = exported / 'Assets' / relative
        text = original = meta.read_text(encoding='utf-8')
        if 'AudioImporter:' not in text:
            continue

        read = relative.as_posix().lower().startswith(READ_CLIPS)

        def convert(m, meta=meta, read=read):
            game_type = int(m[2])
            load_type, compression, quality = formats[game_type]
            if load_type != 2 and (audio_seconds(meta.with_suffix('')) or 0) >= STREAM_SECONDS:
                load_type, compression, quality = formats[2]
                streamed.append(meta.with_suffix('').stem)
            elif read:
                load_type = 0
                if compression == 5:  # HEVAG decompressed on load stays HEVAG: GetData still fails
                    compression = 0  # PCM
                decompressed.append(meta.with_suffix('').stem)
            counts[(game_type, load_type)] += 1
            return m[1] + str(load_type) + m[3] + str(compression) + m[5] + (m[6] if quality is None else str(quality))

        text, count = AUDIO_SETTINGS.subn(convert, text, count=1)
        if not count:
            sys.exit(f'{meta}: an AudioImporter whose defaultSettings audio_overlay cannot read')
        streams = re.search(r'^    loadType: 2$', text, re.MULTILINE) is not None
        text = re.sub(
            r'^  preloadAudioData: \d$', f'  preloadAudioData: {0 if streams else 1}', text, flags=re.MULTILINE
        )
        if text != original:
            overlay[Path('Assets') / relative] = text.encode('utf-8')
    expect(len(overlay), 3800, f'{platform} audio clips given its import settings')
    expect(len(decompressed), 28, f'{platform} clips the game reads, decompressed on load')
    log(
        f'{platform} audio import changed for {len(overlay)} clips '
        f'({", ".join(f"{a}->{b}: {n}" for (a, b), n in sorted(counts.items()))}); '
        f'{len(streamed)} long clips stream: {", ".join(sorted(streamed)[:8])}; '
        f'{len(decompressed)} clips the game reads decompress on load'
    )
    return overlay


# Player settings whose 2018 defaults differ from the source version's behaviour.
SETTING_RULES = {
    # Unity 2019.3 removed the launch-time resolution dialog; 2018 shows it unless disabled.
    'displayResolutionDialog': '0',
    # The game ships without a player log (boot.config nolog); the port's test tools read it.
    'usePlayerLog': '1',
}
# Per platform. The PS Vita player starts in power mode A, with the GPU at 111 MHz; mode C (2)
# raises the GPU clock and gives up the camera and full screen brightness (The Drifter's port).
PLATFORM_SETTING_RULES = {
    'vita': {'psp2PowerMode': '2'},
    # Unity keeps PlayerPrefs (the game's display, sound and control settings) under the company and
    # product name, in the registry on Windows: test players get their own, apart from the game's.
    'windows': {'productName': 'Huntdown 2018 port'},
}

# Quality settings per platform, written into every quality level. None: the game's data already has a level
# for each platform (m_PerPlatformDefaultQuality: PSP2 2, Simple; Standalone 5, Fantastic).
PLATFORM_QUALITY_RULES = {}

# Physics settings. Newer Unity says when physics steps with m_SimulationMode (0: in FixedUpdate, the
# game's), where 2018 has m_AutoSimulation, which the export writes as 0: physics would never step.
PHYSICS_RULES = {'m_AutoSimulation': '1'}


# The export writes the game's productGUID as Unity 2022 does, a mapping of its four 32-bit words, which 2018.2
# doesn't read: the editor then made a new random one at every build, into the project's settings and the
# player's PlayerSettings, so no two builds were the same. Written in 2018's form, 32 hex digits, each word's
# from its lowest four bits up.
PRODUCT_GUID = re.compile(r'^  productGUID:\n((?:    data\[\d\]: \d+\n){4})', re.MULTILINE)


def product_guid(text: str):
    match = PRODUCT_GUID.search(text)
    if not match:
        sys.exit('ProjectSettings has no productGUID in the form the export writes (four data[] words)')
    words = [int(word) for word in re.findall(r': (\d+)', match[1])]
    digits = ''.join(f'{(word >> (4 * i)) & 15:x}' for word in words for i in range(8))
    return text[: match.start()] + f'  productGUID: {digits}\n' + text[match.end() :]


def settings_overlay(exported: Path, platform: str):
    relative = Path('ProjectSettings') / 'ProjectSettings.asset'
    text = (exported / relative).read_text(encoding='utf-8')
    text = product_guid(text)
    for key, value in {**SETTING_RULES, **PLATFORM_SETTING_RULES.get(platform, {})}.items():
        text, count = re.subn(rf'^(  {key}:).*$', rf'\g<1> {value}', text, flags=re.MULTILINE)
        if count == 0:
            # Keys the source version no longer has; the file holds one PlayerSettings mapping.
            text = text.rstrip('\n') + f'\n  {key}: {value}\n'
        elif count > 1:
            sys.exit(f'ProjectSettings has {count} entries for {key}')
    overlay = {relative: text.encode('utf-8')}
    quality_rules = PLATFORM_QUALITY_RULES.get(platform, {})
    if quality_rules:
        relative = Path('ProjectSettings') / 'QualitySettings.asset'
        text = (exported / relative).read_text(encoding='utf-8')
        for key, value in quality_rules.items():
            # The levels' entries, indented under m_QualitySettings; the game has one level.
            text, count = re.subn(rf'^(    {key}:).*$', rf'\g<1> {value}', text, flags=re.MULTILINE)
            if count == 0:
                sys.exit(f'QualitySettings has no {key}')
        overlay[relative] = text.encode('utf-8')
    relative = Path('ProjectSettings') / 'DynamicsManager.asset'
    text = (exported / relative).read_text(encoding='utf-8')
    for key, value in PHYSICS_RULES.items():
        text, count = re.subn(rf'^(  {key}:).*$', rf'\g<1> {value}', text, flags=re.MULTILINE)
        if count != 1:
            sys.exit(f'DynamicsManager has {count} entries for {key}')
    overlay[relative] = text.encode('utf-8')
    return overlay


NATIVE_PLUGIN_META = """fileFormatVersion: 2
guid: {guid}
PluginImporter:
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  isPreloaded: 0
  isOverridable: 0
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: x86_64
        OS: Windows
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def native_plugin_overlay(game: Path):
    """AssetRipper exports managed code only; carry the game's 64-bit native plugins over (Rewired's
    DirectInput; not Steam's, which the stubbed Steamworks never loads) for Windows players."""
    source = game_data(game) / 'Plugins' / 'x86_64'
    folder = Path('Assets') / 'Plugins' / 'x86_64'
    overlay = folder_meta(folder)
    for dll in sorted(source.glob('*.dll')):
        if dll.stem.startswith('steam_api'):  # Steamworks is stubbed (pipeline.STUBBED_ON)
            continue
        overlay[folder / dll.name] = dll.read_bytes()
        overlay[meta_of(folder / dll.name)] = NATIVE_PLUGIN_META.format(guid=guid_for('native', dll.name)).encode()
    log('Native plugins: ' + ', '.join(p.name for p in overlay if p.suffix == '.dll'))
    return overlay


# Textures as the game shipped them. The export writes each texture's format in its .meta (textureFormat, at the
# importer's top level) but imports it compressed ("automatic", textureCompression 2: DXT5 or DXT1). The game ships
# 695 of its 707 textures uncompressed (RGBA32, RGB24), and draws its text by copying glyphs on the GPU
# (Graphics.CopyTexture) from the font and button textures into RGBA32 ones, which only works between the same
# formats: compressed, every copy failed and the menus' text, the HUD's numbers and the button prompts were blank.
# A texture the game shipped uncompressed is imported uncompressed (textureCompression 0) on every platform, which
# also keeps the pixel art's exact colours; the twelve it shipped as DXT stay compressed.
UNCOMPRESSED_TEXTURE_FORMATS = {1, 2, 3, 4, 5, 7, 9, 13, 14, 15, 16, 17, 18, 19, 20}
TEXTURE_FORMAT = re.compile(r'^  textureFormat: (-?\d+)$', re.MULTILINE)
TEXTURE_COMPRESSION = re.compile(r'^(    textureCompression: )\d+$', re.MULTILINE)


def texture_compression_overlay(exported: Path, overlay: dict):
    """The textures the game shipped uncompressed, imported uncompressed (see UNCOMPRESSED_TEXTURE_FORMATS)."""
    result, kept = {}, 0
    for relative, stat in listing(exported / 'Assets').items():
        if stat is None or relative.suffix != '.meta' or relative.suffixes[-2:-1] != ['.png']:
            continue
        path = Path('Assets') / relative
        text = current_text(exported, overlay, path)
        match = TEXTURE_FORMAT.search(text)
        if not match or 'TextureImporter:' not in text:
            continue
        if int(match[1]) not in UNCOMPRESSED_TEXTURE_FORMATS:
            kept += 1
            continue
        new, count = TEXTURE_COMPRESSION.subn(r'\g<1>0', text)
        if not count:
            sys.exit(f'{path}: a TextureImporter without textureCompression, which texture_compression_overlay sets')
        if new != text:
            result[path] = new.encode('utf-8')
    expect(len(result), 650, 'textures imported uncompressed, as the game shipped them')
    log(f'Textures: {len(result)} imported uncompressed as the game shipped them; {kept} kept compressed')
    return result
