"""
Generates the placeholder tray icons.

Deliberately plain, and deliberately not a design. The real artwork comes from Claude Design against
docs/design/ICON-BRIEF.md; this exists only so the tray has something to draw before it arrives, and
so that "the icon is missing" is visibly different from "the icon is finished".

Three amber bands on a dark ground — strata, at the crudest possible reading of the word. The state
variants change the top band only.
"""
import os
import struct
import zlib

OUT = 'docs/design/icon'
SIZES = [16, 20, 24, 32, 48, 64, 256]

BG = (0x0F, 0x0D, 0x0B, 255)          # --bg-0
AMBER = (0xF8, 0xAC, 0x3D, 255)       # --pos
CYAN = (0x5C, 0xD0, 0xEA, 255)        # --neg
WARN = (0xFF, 0x98, 0x68, 255)        # --warn
GREY = (0x78, 0x74, 0x6F, 255)        # --fg-3

# Which colour the top band takes in each state.
STATES = {
    'idle': AMBER,
    'scanning': CYAN,
    'deferred': WARN,
    'paused': GREY,
}


def render(size, top):
    """Three bands on a dark ground, on whole pixels at every size."""
    px = [[BG for _ in range(size)] for _ in range(size)]

    inset = max(1, size // 8)
    band_h = max(1, size // 6)
    gap = max(1, size // 12)
    width = size - inset * 2

    y = inset + max(0, (size - inset * 2 - (band_h * 3 + gap * 2)) // 2)

    for index in range(3):
        colour = top if index == 0 else AMBER
        # Lower bands are dimmer, so the mark reads as a section rather than as a barcode.
        if index > 0:
            colour = tuple(int(c * (0.62 if index == 1 else 0.4)) for c in colour[:3]) + (255,)

        # Each band a little shorter than the one above it: a cross-section, not a stack of rules.
        w = width - index * max(1, width // 8)

        for dy in range(band_h):
            row = y + index * (band_h + gap) + dy
            if 0 <= row < size:
                for dx in range(w):
                    col = inset + dx
                    if 0 <= col < size:
                        px[row][col] = colour

    return px


def png(px, size):
    raw = b''.join(
        b'\x00' + b''.join(bytes(px[y][x]) for x in range(size))
        for y in range(size))

    def chunk(tag, data):
        c = tag + data
        return struct.pack('>I', len(data)) + c + struct.pack('>I', zlib.crc32(c))

    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9))
            + chunk(b'IEND', b''))


def ico(images):
    """A PNG-compressed .ico. Vista and later read PNG entries directly."""
    count = len(images)
    out = struct.pack('<HHH', 0, 1, count)
    offset = 6 + count * 16

    for size, data in images:
        out += struct.pack('<BBBBHHII',
                           0 if size >= 256 else size, 0 if size >= 256 else size,
                           0, 0, 1, 32, len(data), offset)
        offset += len(data)

    return out + b''.join(d for _, d in images)


os.makedirs(OUT, exist_ok=True)

for state, top in STATES.items():
    images = [(s, png(render(s, top), s)) for s in SIZES]
    path = os.path.join(OUT, f'strata-{state}.ico')

    with open(path, 'wb') as f:
        f.write(ico(images))

    print(f'{path}  {len(open(path, "rb").read()):,} bytes')

# The tray loads strata.ico by default.
import shutil
shutil.copyfile(os.path.join(OUT, 'strata-idle.ico'), os.path.join(OUT, 'strata.ico'))
print(os.path.join(OUT, 'strata.ico'), '(copy of idle)')
