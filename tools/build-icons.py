"""
Assembles the delivered PNGs into the .ico files the tray and the executable load.

Claude Design delivers one PNG per state per size, which is the right thing to hand over — an .ico is
a container format and building it here means the sizes are never resampled from one bitmap. Windows
picks the entry matching GetSystemMetrics(SM_CXSMICON), so 16 and 32 both being real drawings is what
keeps the tray crisp at 100% and 200% DPI.

    python tools/build-icons.py

Reads docs/design/icon/strata-<state>-<size>.png, writes docs/design/icon/strata-<state>.ico,
and copies the idle state to strata.ico for the executable and to the Angular app's favicon.
"""
import os
import shutil
import struct

ICONS = 'docs/design/icon'
STATES = ['idle', 'scanning', 'deferred', 'paused']

# Every size Claude Design delivered. 16 is the one that matters — it is what Windows draws in the
# notification area at 100% DPI — but 256 is what File Explorer shows for the executable.
SIZES = [16, 20, 24, 32, 48, 64, 256]


def ico(entries):
    """A PNG-compressed .ico. Vista and later read PNG entries directly, so no BMP conversion."""
    count = len(entries)
    out = struct.pack('<HHH', 0, 1, count)
    offset = 6 + count * 16

    for size, data in entries:
        # 256 is stored as 0 in a single byte, which is the format's way of saying "or larger".
        dim = 0 if size >= 256 else size

        out += struct.pack(
            '<BBBBHHII',
            dim, dim,
            0,      # no colour palette
            0,      # reserved
            1,      # colour planes
            32,     # bits per pixel
            len(data),
            offset)

        offset += len(data)

    return out + b''.join(data for _, data in entries)


def build(state):
    entries = []

    for size in SIZES:
        path = os.path.join(ICONS, f'strata-{state}-{size}.png')

        if not os.path.exists(path):
            raise SystemExit(f'missing {path} — re-export from Claude Design')

        with open(path, 'rb') as f:
            entries.append((size, f.read()))

    target = os.path.join(ICONS, f'strata-{state}.ico')

    with open(target, 'wb') as f:
        f.write(ico(entries))

    return target, os.path.getsize(target)


for state in STATES:
    path, size = build(state)
    print(f'{path}  {size:,} bytes  ({len(SIZES)} sizes)')

# The executable's own icon, and the tray's default before the first status poll.
shutil.copyfile(os.path.join(ICONS, 'strata-idle.ico'), os.path.join(ICONS, 'strata.ico'))
print(os.path.join(ICONS, 'strata.ico'), '(idle)')

# The browser tab. Same mark, same file: the UI is the same application, and a tab showing a
# different icon from the tray it was opened from is a small lie about that.
favicon = 'src/Strata.Web/public/favicon.ico'
shutil.copyfile(os.path.join(ICONS, 'strata-idle.ico'), favicon)
print(favicon, '(idle)')
