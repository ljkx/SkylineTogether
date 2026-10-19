"""Original artwork for the Paradox Mods screenshots: a stylised top-down city and simple UI glyphs, drawn from shapes.

No game assets are used. make_images.py puts the city behind the rendered UI panels (the panels are translucent, as in
the game) and replaces the game's glyph paths, which a browser cannot load as masks from file:// URLs, with the glyphs
below. Everything is deterministic: the same code draws the same picture.
"""
import base64
import random

W, H = 1920, 1080


def _rect(x, y, w, h, fill, extra=''):
    return '<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" fill="%s"%s/>' % (x, y, w, h, fill, extra)


def city_svg(preview=None):
    """A top-down city: blocks of buildings and parks between roads, a river and a highway.

    preview: optional (x, y, colour), the end point of another player's road preview drawn in their colour.
    """
    rnd = random.Random(20261019)
    lot_road_x = preview[0] - 200 if preview else 0
    out = ['<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 %d %d">' % (W, H, W, H)]
    out.append('<defs>'
               '<radialGradient id="vig" cx="50%" cy="50%" r="75%"><stop offset="55%" stop-color="#000" stop-opacity="0"/>'
               '<stop offset="100%" stop-color="#000" stop-opacity="0.55"/></radialGradient>'
               '<linearGradient id="water" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#2b6c8c"/>'
               '<stop offset="1" stop-color="#1f5673"/></linearGradient>'
               '</defs>')
    out.append(_rect(0, 0, W, H, '#55684b'))  # land

    roofs = ['#9aa0a8', '#b3aa9c', '#7d8995', '#c4b49b', '#8c7d72', '#d2cabd', '#6f7d73', '#a8917c', '#bfc4c9']
    step_x, step_y = 176, 150
    road = 16
    xs = list(range(-40, W + step_x, step_x))
    ys = list(range(-30, H + step_y, step_y))

    # blocks
    for i, x0 in enumerate(xs[:-1]):
        for j, y0 in enumerate(ys[:-1]):
            bx, by = x0 + road / 2, y0 + road / 2
            bw, bh = step_x - road, step_y - road
            r = rnd.random()
            if preview and x0 < preview[0] - 60 < x0 + step_x and y0 < preview[1] < y0 + step_y:
                # a cleared lot where the other player is drawing a new road
                out.append(_rect(bx, by, bw, bh, '#8a8062', ' rx="3"'))
                out.append(_rect(bx + 10, by + 10, bw - 20, bh - 20, '#7d7357', ' rx="2" opacity="0.6"'))
                lot_road_x = x0
                continue
            if r < 0.12:  # park
                out.append(_rect(bx, by, bw, bh, '#5d8a4e', ' rx="4"'))
                for _ in range(rnd.randint(10, 18)):
                    cx, cy = bx + 8 + rnd.random() * (bw - 16), by + 8 + rnd.random() * (bh - 16)
                    rad = 4 + rnd.random() * 6
                    out.append('<circle cx="%.1f" cy="%.1f" r="%.1f" fill="#2f4d2a" opacity="0.35"/>' % (cx + 2, cy + 2, rad))
                    out.append('<circle cx="%.1f" cy="%.1f" r="%.1f" fill="#46733d"/>' % (cx, cy, rad))
                continue
            out.append(_rect(bx, by, bw, bh, '#8f9488', ' rx="3"'))  # pavement
            # lots along the block edges
            dense = r > 0.55
            lot = 22 if dense else 30
            for side in range(2):
                yy = by + 6 if side == 0 else by + bh / 2 + 3
                xx = bx + 6
                while xx < bx + bw - 14:
                    w = lot + rnd.randint(-6, 14)
                    w = min(w, bx + bw - 6 - xx)
                    if w < 10:
                        break
                    h = bh / 2 - 9 - rnd.randint(0, 10 if not dense else 4)
                    colour = rnd.choice(roofs)
                    out.append(_rect(xx + 3, yy + 3, w - 3, h, '#000', ' opacity="0.28"'))
                    out.append(_rect(xx, yy, w - 3, h, colour))
                    if rnd.random() < 0.5:  # roof detail
                        out.append(_rect(xx + (w - 3) * 0.25, yy + h * 0.3, (w - 3) * 0.4, h * 0.3, '#000', ' opacity="0.12"'))
                    xx += w + 2

    # river
    out.append('<path d="M -40 860 C 300 760, 560 980, 900 900 S 1500 700, 1980 780 L 1980 900 C 1500 830, 1260 1040, 900 1010 '
               'S 300 900, -40 990 Z" fill="url(#water)"/>')
    out.append('<path d="M -40 860 C 300 760, 560 980, 900 900 S 1500 700, 1980 780" fill="none" stroke="#a9b8a0" stroke-width="5"/>')

    # roads
    for x in xs:
        out.append(_rect(x - road / 2, -10, road, H + 20, '#4b5057'))
        out.append('<line x1="%d" y1="-10" x2="%d" y2="%d" stroke="#8c9198" stroke-width="1.2" stroke-dasharray="10 9"/>' % (x, x, H + 10))
    for y in ys:
        out.append(_rect(-10, y - road / 2, W + 20, road, '#4b5057'))
        out.append('<line x1="-10" y1="%d" x2="%d" y2="%d" stroke="#8c9198" stroke-width="1.2" stroke-dasharray="10 9"/>' % (y, W + 10, y))
    # a highway with a bridge over the river
    hw = 'M 1180 -20 C 1260 300, 1120 520, 1330 760 S 1560 1000, 1620 1100'
    out.append('<path d="%s" fill="none" stroke="#000" stroke-opacity="0.35" stroke-width="44" transform="translate(6,6)"/>' % hw)
    out.append('<path d="%s" fill="none" stroke="#5a5f66" stroke-width="40"/>' % hw)
    out.append('<path d="%s" fill="none" stroke="#c9b45a" stroke-width="1.6" stroke-dasharray="16 12"/>' % hw)
    out.append('<path d="%s" fill="none" stroke="#9aa0a6" stroke-width="1.2" transform="translate(-10,0)"/>' % hw)
    out.append('<path d="%s" fill="none" stroke="#9aa0a6" stroke-width="1.2" transform="translate(10,0)"/>' % hw)

    # another player's road preview in their colour, ending at the name tag
    if preview:
        px, py, colour = preview
        d = 'M %d %d L %d %d' % (lot_road_x, py, px, py)
        out.append('<path d="%s" fill="none" stroke="%s" stroke-opacity="0.35" stroke-width="18" stroke-linecap="round"/>' % (d, colour))
        out.append('<path d="%s" fill="none" stroke="%s" stroke-width="2.5" stroke-dasharray="12 7" stroke-linecap="round"/>' % (d, colour))
        out.append('<circle cx="%d" cy="%d" r="8" fill="%s" fill-opacity="0.35" stroke="%s" stroke-width="2.5"/>' % (lot_road_x, py, colour, colour))

    out.append(_rect(0, 0, W, H, 'url(#vig)'))
    out.append('</svg>')
    return ''.join(out)


# 24 x 24 glyphs, white on transparent (used as CSS masks, so only the alpha counts)
_S = 'fill="none" stroke="#fff" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"'
GLYPHS = {
    'Population': '<circle cx="9" cy="7.5" r="3.6" fill="#fff"/><path d="M2.5 20.5c0-4.2 2.9-7 6.5-7s6.5 2.8 6.5 7z" fill="#fff"/>'
                  '<circle cx="16.6" cy="8.6" r="3" fill="#fff" opacity="0.85"/><path d="M15.4 13.7c3.6-.4 6.3 2.2 6.3 6.8h-4.6c0-2.8-.6-5-1.7-6.8z" fill="#fff" opacity="0.85"/>',
    'Close': '<path d="M6 6l12 12M18 6L6 18" %s/>' % _S,
    'Copy': '<rect x="8.5" y="8.5" width="11" height="12" rx="2" %s/><path d="M5 15.5V5.5a2 2 0 0 1 2-2h8" %s/>' % (_S, _S),
    'More': '<circle cx="5" cy="12" r="2.2" fill="#fff"/><circle cx="12" cy="12" r="2.2" fill="#fff"/><circle cx="19" cy="12" r="2.2" fill="#fff"/>',
    'Plus': '<path d="M12 5v14M5 12h14" %s/>' % _S,
    'ArrowCircular': '<path d="M19 12a7 7 0 1 1-2.1-5" %s/><path d="M18.5 3.5v4h-4" %s/>' % (_S, _S),
    'ThickStrokeArrowDown': '<path d="M6 9l6 6 6-6" %s/>' % _S,
    'ThickStrokeArrowRight': '<path d="M9 6l6 6-6 6" %s/>' % _S,
    'ThickStrokeArrowUp': '<path d="M6 15l6-6 6 6" %s/>' % _S,
    'TriangleArrowLeft': '<path d="M16 5v14L6 12z" fill="#fff"/>',
    'Checkmark': '<path d="M5 12.5l4.5 4.5L19 7.5" %s/>' % _S,
    'Warning': '<path d="M12 3.8l9 16.2H3z" %s/><path d="M12 10v4.6" %s/><circle cx="12" cy="17.4" r="1.3" fill="#fff"/>' % (_S, _S),
    'Info': '<circle cx="12" cy="12" r="9" %s/><path d="M12 11v5.6" %s/><circle cx="12" cy="7.8" r="1.3" fill="#fff"/>' % (_S, _S),
    'Lock': '<rect x="5" y="10.5" width="14" height="10" rx="2" fill="#fff"/><path d="M8 10.5V8a4 4 0 0 1 8 0v2.5" %s/>' % _S,
}


def glyph_svg(name):
    body = GLYPHS.get(name)
    if body is None:  # an unknown glyph: a plain dot keeps the layout without pretending to be an icon
        body = '<circle cx="12" cy="12" r="4" fill="#fff"/>'
    return '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">%s</svg>' % body


def data_uri(svg):
    return 'data:image/svg+xml;base64,' + base64.b64encode(svg.encode('utf-8')).decode('ascii')
