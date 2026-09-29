"""tray.ico from the shield art.

    python Tools/make-icon.py <art.png> src/AltStableCompanion.App/Assets/tray.ico

Needs Pillow. The art is the owner's 1254 x 1254 shield with three figures; it is not in the
repository.

16, 20 and 24 px are the centre knight's helm alone, on a dark rounded tile: three figures do
not survive those sizes. 32 px and up are the whole shield.

The helm is cut out of the art with a soft oval before it goes on the tile. A plain crop has
the shield's silver frame in its top corners and the knight's shoulders in the bottom ones,
and at 16 px that is four white corners.
"""
import sys

from PIL import Image, ImageDraw, ImageEnhance, ImageFilter

NAVY = (12, 20, 32)
HELM = (417, 222, 420)                  # x, y, side of the square around the helm
SMALL = (16, 20, 24)
LARGE = (32, 48, 64, 256)


def small_frame(tile, shape, n):
    """
    The tile is opaque and its shape is a mask of its own: each is scaled by itself and they
    meet at the end. Scaled together, the sharpening sees the transparent corner as black
    beside the navy and lights the edge up - a pale fringe on a light taskbar.
    """
    rgb = tile.resize((n, n), Image.LANCZOS)
    rgb = rgb.filter(ImageFilter.UnsharpMask(radius=0.6, percent=90, threshold=0))
    rgb = ImageEnhance.Contrast(rgb).enhance(1.12)
    rgb.putalpha(shape.resize((n, n), Image.LANCZOS))
    return rgb


def large_frame(src, n):
    out = src.resize((n, n), Image.LANCZOS)
    if n <= 48:
        out = out.filter(ImageFilter.UnsharpMask(radius=0.6, percent=90, threshold=0))
    return out


def shield(art):
    """The whole shield: the soft glow around it dropped, cropped, centred on a square."""
    r, g, b, a = art.split()
    a = a.point(lambda v: 0 if v < 90 else min(255, int((v - 90) * 255 / 150)))
    cut = Image.merge('RGBA', (r, g, b, a)).crop(a.point(lambda v: 255 if v > 128 else 0).getbbox())
    side = max(cut.size)
    out = Image.new('RGBA', (side, side), (0, 0, 0, 0))
    out.paste(cut, ((side - cut.width) // 2, (side - cut.height) // 2))
    return out


def helm(art):
    """The helm alone on a dark tile, and the rounded shape the tile is cut to."""
    x, y, s = HELM
    crop = art.crop((x, y, x + s, y + s)).convert('RGB')
    oval = Image.new('L', (s, s), 0)
    ImageDraw.Draw(oval).ellipse((48, -6, 372, 448), fill=255)
    oval = oval.filter(ImageFilter.GaussianBlur(14))
    tile = Image.new('RGB', (s, s), NAVY)
    tile.paste(crop, (0, 0), oval)
    shape = Image.new('L', (s, s), 0)
    ImageDraw.Draw(shape).rounded_rectangle((0, 0, s - 1, s - 1), radius=int(s * 0.2), fill=255)
    return tile, shape


def main(art_path, ico_path):
    art = Image.open(art_path).convert('RGBA')
    if art.size != (1254, 1254):
        sys.exit(f'{art_path} is {art.size[0]} x {art.size[1]}; the helm is placed for 1254 x 1254')
    (tile, shape), whole = helm(art), shield(art)
    frames = {n: small_frame(tile, shape, n) for n in SMALL}
    frames.update({n: large_frame(whole, n) for n in LARGE})
    order = sorted(frames)
    frames[256].save(ico_path, format='ICO', sizes=[(n, n) for n in order],
                     append_images=[frames[n] for n in order if n != 256])
    print(f'{ico_path}: {", ".join(str(n) for n in order)} px')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
