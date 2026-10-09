"""Makes tools/assets/Kakitome-icon.png from the original art (tools/assets/icon-original.png): the white background is
kept and cut into an app-icon shape (a rounded square around the motif, transparent outside the corners), with a faint
grey rim so the white tile stays visible on a light taskbar. Output is 512 px.
Then run tools/New-AppAssets.ps1 to regenerate the app assets. Requires Pillow."""
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
SIZE = 512
# Motif size relative to the tile, corner radius relative to the tile, rim width in output pixels.
MOTIF = 0.74
RADIUS = 0.22
RIM = 4

src = Image.open(os.path.join(HERE, 'icon-original.png')).convert('RGB')
W, H = src.size
px = src.load()


def motif(c):
    return min(c) < 225 or max(c) - min(c) > 30


# Bounding box of the motif (everything that is not near-white background).
xs = [x for x in range(W) for y in range(0, H, 2) if motif(px[x, y])]
ys = [y for y in range(H) for x in range(0, W, 2) if motif(px[x, y])]
left, right, top, bottom = min(xs), max(xs), min(ys), max(ys)
side = round(max(right - left, bottom - top) / MOTIF)
cx, cy = (left + right) / 2, (top + bottom) / 2
x0, y0 = round(cx - side / 2), round(cy - side / 2)

# Crop on a white canvas so a tile larger than the art never shows anything but white.
canvas = Image.new('RGB', (side, side), (255, 255, 255))
canvas.paste(src.crop((max(0, x0), max(0, y0), min(W, x0 + side), min(H, y0 + side))), (max(0, -x0), max(0, -y0)))
tile = canvas.resize((SIZE, SIZE), Image.LANCZOS).convert('RGBA')

# Rounded-square mask, drawn 4x and downsampled for smooth corners.
scale = 4
big = SIZE * scale
radius = round(SIZE * RADIUS * scale)
mask = Image.new('L', (big, big), 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, big - 1, big - 1), radius=radius, fill=255)
rim = Image.new('L', (big, big), 0)
draw = ImageDraw.Draw(rim)
draw.rounded_rectangle((0, 0, big - 1, big - 1), radius=radius, fill=255)
inset = RIM * scale
draw.rounded_rectangle((inset, inset, big - 1 - inset, big - 1 - inset), radius=max(0, radius - inset), fill=0)
mask = mask.resize((SIZE, SIZE), Image.LANCZOS)
rim = rim.resize((SIZE, SIZE), Image.LANCZOS)

grey = Image.new('RGBA', (SIZE, SIZE), (200, 204, 208, 255))
tile = Image.composite(grey, tile, rim.point(lambda v: v * 0.6))
tile.putalpha(mask)
tile.save(os.path.join(HERE, 'Kakitome-icon.png'))
