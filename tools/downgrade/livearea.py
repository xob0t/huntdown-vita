"""PS Vita LiveArea artwork, composed from Huntdown's Steam store art.

    uv run tools/downgrade/livearea.py [--force]

The images come from Steam's public CDN (no Steam client, no game launch), downloaded once into
work/livearea/store/ and read from there afterwards. Writes to work/livearea/:

  icon0.png     128x128   bubble: the middle hunter's face (vertical key art)
  bg0.png       840x500   LiveArea background: the library hero art
  gate.png      280x158   LiveArea start gate: the game's logo alone (logo_2x.png), on transparency, so the
                          LiveArea background shows around it
  pic0.png      960x544   shown while the app starts: the wide key art with its logo

The LiveArea images and pic0 are 8-bit indexed: the installer rejects a truecolor pic0 (promote
error 0x8010113D), and icon0 must not have an alpha channel (The Drifter's port found both). The
outputs are remade when the cached sources or this script change. pipeline.py puts them into the
VPK (vita_app_files).
"""

import argparse
import hashlib
import urllib.request
from pathlib import Path

from common import STEAM_APP_ID, WORK, log
from PIL import Image

OUT = WORK / 'livearea'
STORE = OUT / 'store'
FILES = ['icon0.png', 'bg0.png', 'gate.png', 'pic0.png']
PALETTE_COLORS = 256  # 8-bit indexed

CDN = f'https://cdn.akamai.steamstatic.com/steam/apps/{STEAM_APP_ID}/'
# Cached name: (URL, SHA-256 of the file the crops below were made for). A changed file is only
# reported: Valve can replace store art at the same URL.
SOURCES = {
    # Wide key art, logo across the top, 616x353.
    'capsule.jpg': (CDN + 'capsule_616x353.jpg', '072ff3ababff7de8d5d3223f1ed05a9ef20a9fc05f91dfa3f695bf4258758254'),
    # Vertical key art, 600x900: the three hunters above the logo.
    'library.jpg': (CDN + 'library_600x900_2x.jpg', '4dcba96a234acec3307cbff5eb76f0cd26beb5b114da05b54fe7e39b7edd3ad0'),
    # Library hero art, 1920x620, without the logo.
    'hero.jpg': (CDN + 'library_hero.jpg', '87a0609d669fd8ea470d0e601e7d9292d7f6afdc3eab7df77c5f9af1fda98bde'),
    # The library logo, 1280x720 with a transparent background.
    'logo_2x.png': (CDN + 'logo_2x.png', 'ff64e22787882e5a7c64f5386e1bcaa8811ef3bbfd28a6442c9d32f88e280ce4'),
}
ICON_CROP = (225, 240, 355, 370)  # the middle hunter's face in library.jpg
GATE_LOGO_WIDTH = 0.92  # of the gate's width


def fetch():
    """The cached store images, downloading the missing ones."""
    STORE.mkdir(parents=True, exist_ok=True)
    paths = {}
    for name, (url, digest) in SOURCES.items():
        path = STORE / name
        if not path.exists():
            log(f'Downloading {url}')
            request = urllib.request.Request(url, headers={'User-Agent': 'huntdown-vita-livearea'})
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    data = response.read()
            except OSError as error:
                raise RuntimeError(f'cannot download {url} ({error}); the LiveArea art needs it once') from error
            part = path.with_name(path.name + '.part')
            part.write_bytes(data)
            part.replace(path)
        actual = hashlib.sha256(path.read_bytes()).hexdigest()
        if actual != digest:
            log(f'warning: {path.name} differs from the store image the crops were made for; check work/livearea/')
        paths[name] = path
    return paths


def cover(image: Image.Image, size, box=None):
    """image (or its box) scaled to fill size, centred, the overflow cut off."""
    if box:
        image = image.crop(box)
    width, height = size
    scale = max(width / image.width, height / image.height)
    scaled = image.resize((round(image.width * scale), round(image.height * scale)), Image.LANCZOS)
    left, top = (scaled.width - width) // 2, (scaled.height - height) // 2
    return scaled.crop((left, top, left + width, top + height))


def logo_gate(logo: Image.Image, size):
    """The logo (cut to what isn't transparent) centred on a transparent image of size, 8-bit indexed with its
    transparent pixels one palette entry (tRNS)."""
    logo = logo.crop(logo.getbbox())
    width = round(size[0] * GATE_LOGO_WIDTH)
    logo = logo.resize((width, round(logo.height * width / logo.width)), Image.LANCZOS)
    out = Image.new('RGBA', size, (0, 0, 0, 0))
    out.alpha_composite(logo, ((size[0] - logo.width) // 2, (size[1] - logo.height) // 2))
    # Edges blended against black, then cut at half coverage: an indexed image has one transparent entry.
    rgb = Image.new('RGB', size, (0, 0, 0))
    rgb.paste(out, mask=out.getchannel('A'))
    image = rgb.quantize(PALETTE_COLORS - 1, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    clear = PALETTE_COLORS - 1
    palette = image.getpalette()[:3 * clear] + [0, 0, 0]
    alpha = out.getchannel('A').point(lambda a: 255 if a >= 128 else 0)
    image.paste(clear, mask=alpha.point(lambda a: 255 - a))
    image.putpalette(palette)
    image.info['transparency'] = clear
    return image


def indexed(image: Image.Image):
    return image.convert('RGB').quantize(PALETTE_COLORS, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)


def compose(sources):
    capsule = Image.open(sources['capsule.jpg']).convert('RGB')
    library = Image.open(sources['library.jpg']).convert('RGB')
    hero = Image.open(sources['hero.jpg']).convert('RGB')
    logo = Image.open(sources['logo_2x.png']).convert('RGBA')
    return {
        'icon0.png': indexed(cover(library, (128, 128), ICON_CROP)),
        'bg0.png': indexed(cover(hero, (840, 500))),
        'gate.png': logo_gate(logo, (280, 158)),
        'pic0.png': indexed(cover(capsule, (960, 544))),
    }


def stamp(sources):
    digest = hashlib.sha256(Path(__file__).read_bytes())
    for path in sources.values():
        digest.update(path.read_bytes())
    return digest.hexdigest()


def generate(force=False):
    """The artwork folder, made if missing or out of date."""
    sources = fetch()
    marker = OUT / 'stamp.txt'
    current = stamp(sources)
    if not force and marker.exists() and marker.read_text() == current and all((OUT / f).exists() for f in FILES):
        return OUT
    for name, image in compose(sources).items():
        image.save(OUT / name, optimize=True)
    marker.write_text(current)
    log(f'LiveArea art in {OUT}')
    return OUT


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--force', action='store_true')
    generate(parser.parse_args().force)
