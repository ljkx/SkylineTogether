"""Renders the Paradox Mods images of Skyline Together with a headless Edge or Chrome (no packages, no downloads).

Usage (repo root):  python tools/paradox/make_images.py [--only thumbnail|screenshots] [--browser <path to msedge/chrome>]

Writes
  src/SkylineTogether/Properties/Thumbnail.png              1024 x 1024, from tools/paradox/thumbnail.html (shapes only)
  src/SkylineTogether/Properties/Screenshots/NN-<state>.png  1920 x 1080, the UI module rendered by tools/ui_smoke.mjs --html
                                                              with stub data (no game assets, no real players)

The screenshots show the hand-written UI module with the smoke test's invented players ("Anna", "Bo") and placeholder
Steam IDs; they use the game's fonts and design tokens only when the game is installed (ui_smoke.mjs finds them through
GameDir). Run it again after UI changes and look at the images before committing them.
"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
PROPS = os.path.join(ROOT, 'src', 'SkylineTogether', 'Properties')

# (ui_smoke state file, output name, zoom): the host panel with the invite list, the title screen's join page and a join
# in progress. A zoom above 1 lays the page out in a smaller viewport (1920 / zoom wide) and enlarges it to 1920 x 1080,
# so the panels stay readable in the Paradox Mods gallery.
SCREENSHOTS = [
    ('hosting.game.html', '01-hosting.png', 1.38),
    ('lobbyMenu.menu-join.html', '02-join-from-title-screen.png', 1.0),
    ('joiningDownload.game.html', '03-joining.png', 1.0),
]

FRAME = '''<!doctype html><html><head><meta charset="utf-8"><style>
html, body { margin: 0; padding: 0; overflow: hidden; background: #0b1622; width: 1920px; height: 1080px; }
iframe { border: 0; width: %(width)dpx; height: %(height)dpx; transform-origin: 0 0; transform: scale(%(zoom)s); }
</style></head><body><iframe src="%(src)s"></iframe></body></html>
'''

BROWSERS = [
    r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    r'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
    r'C:\Program Files\Google\Chrome\Application\chrome.exe',
    r'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe',
]


def find_browser(explicit):
    for candidate in ([explicit] if explicit else []) + BROWSERS + [shutil.which('msedge'), shutil.which('chrome'), shutil.which('chromium')]:
        if candidate and os.path.isfile(candidate):
            return candidate
    sys.exit('no Edge or Chrome found; pass --browser <path>')


def file_url(path):
    return 'file:///' + os.path.abspath(path).replace('\\', '/')


def screenshot(browser, url, out, width, height):
    """One headless screenshot; a fresh profile per call, because a shared one hands the job to a running instance."""
    if os.path.exists(out):
        os.remove(out)
    profile = tempfile.mkdtemp(prefix='st-shot-')
    try:
        subprocess.run([browser, '--headless', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
                        '--no-default-browser-check', '--force-device-scale-factor=1', '--virtual-time-budget=3000',
                        '--user-data-dir=' + profile, '--window-size=%d,%d' % (width, height), '--screenshot=' + out, url],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120, check=False)
        # Edge may return before its renderer wrote the file
        for _ in range(120):
            if os.path.exists(out) and os.path.getsize(out) > 0:
                time.sleep(0.5)
                break
            time.sleep(0.5)
    finally:
        shutil.rmtree(profile, ignore_errors=True)
    if not os.path.exists(out):
        sys.exit('the browser wrote no screenshot for ' + url)
    print('wrote', os.path.relpath(out, ROOT), os.path.getsize(out), 'bytes')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--only', choices=['thumbnail', 'screenshots'])
    ap.add_argument('--browser')
    args = ap.parse_args()
    browser = find_browser(args.browser)

    if args.only in (None, 'thumbnail'):
        screenshot(browser, file_url(os.path.join(ROOT, 'tools', 'paradox', 'thumbnail.html')),
                   os.path.join(PROPS, 'Thumbnail.png'), 1024, 1024)

    if args.only in (None, 'screenshots'):
        node = shutil.which('node')
        if not node:
            sys.exit('node is needed for the screenshots (tools/ui_smoke.mjs --html)')
        html = tempfile.mkdtemp(prefix='st-ui-html-')
        try:
            subprocess.run([node, os.path.join(ROOT, 'tools', 'ui_smoke.mjs'), '--html', html], cwd=ROOT, check=True,
                           stdout=subprocess.DEVNULL)
            folder = os.path.join(PROPS, 'Screenshots')
            os.makedirs(folder, exist_ok=True)
            for state, name, zoom in SCREENSHOTS:
                page = os.path.join(html, state)
                if not os.path.exists(page):
                    sys.exit('ui_smoke.mjs wrote no ' + state + '; update SCREENSHOTS in ' + os.path.relpath(__file__, ROOT))
                frame = os.path.join(html, 'frame-' + name + '.html')
                with open(frame, 'w', encoding='utf-8') as f:
                    f.write(FRAME % {'zoom': zoom, 'width': round(1920 / zoom), 'height': round(1080 / zoom) + 1, 'src': os.path.basename(page)})
                screenshot(browser, file_url(frame), os.path.join(folder, name), 1920, 1080)
        finally:
            shutil.rmtree(html, ignore_errors=True)


if __name__ == '__main__':
    main()
