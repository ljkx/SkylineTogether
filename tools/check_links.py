"""Checks the relative links of every tracked Markdown file.

Usage (repo root):  python tools/check_links.py

For each [text](target) and [text]: target reference in a tracked *.md file:
  - external links (http:, https:, mailto:) are not fetched;
  - a relative target must exist (file or folder), resolved against the linking file;
  - a #fragment pointing into a Markdown file must match one of its headings (GitHub's anchor rules) or an
    explicit <a id="..."> / <a name="...">.
Links inside fenced code blocks and inline code are ignored. Exit code 1 when a link is broken.
"""
import os
import re
import subprocess
import sys
from urllib.parse import unquote

INLINE = re.compile(r'(?<!\\)!?\[(?:[^\[\]]|\[[^\]]*\])*\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)')
REFDEF = re.compile(r'^\s{0,3}\[[^\]]+\]:\s*<?(\S+?)>?(?:\s+.*)?$')
HEADING = re.compile(r'^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$')
HTML_ANCHOR = re.compile(r'<a\s+(?:id|name)="([^"]+)"', re.I)
FENCE = re.compile(r'^\s{0,3}(```|~~~)')


def tracked_markdown():
    out = subprocess.run(['git', 'ls-files', '-z', '*.md'], capture_output=True, check=True).stdout
    return [p for p in out.decode('utf-8').split('\0') if p]


def strip_code(lines):
    """Yields (line number, line) outside fenced blocks, with inline code spans blanked."""
    fenced = False
    for i, line in enumerate(lines, 1):
        if FENCE.match(line):
            fenced = not fenced
            continue
        if not fenced:
            yield i, re.sub(r'`+[^`]*`+', '', line)


def slug(heading):
    """GitHub's heading anchor: lower case, markup removed, punctuation dropped, spaces to hyphens."""
    text = re.sub(r'!?\[([^\]]*)\]\([^)]*\)', r'\1', heading)   # links -> their text
    text = re.sub(r'<[^>]+>', '', text)                         # inline HTML
    text = text.replace('`', '').strip().lower()
    text = re.sub(r'[^\w\- ]', '', text)                        # \w keeps unicode letters, digits and _
    return text.replace(' ', '-')


_anchor_cache = {}


def anchors(path):
    if path not in _anchor_cache:
        seen = {}
        found = set()
        with open(path, encoding='utf-8') as f:
            lines = f.read().splitlines()
        fenced = False
        for line in lines:
            if FENCE.match(line):
                fenced = not fenced
                continue
            if fenced:
                continue
            for m in HTML_ANCHOR.finditer(line):
                found.add(m.group(1))
            m = HEADING.match(line)
            if m:
                base = slug(m.group(2))
                n = seen.get(base, 0)
                seen[base] = n + 1
                found.add(base if n == 0 else f'{base}-{n}')
        _anchor_cache[path] = found
    return _anchor_cache[path]


def main():
    broken = []
    files = tracked_markdown()
    for md in files:
        with open(md, encoding='utf-8') as f:
            lines = f.read().splitlines()
        for lineno, line in strip_code(lines):
            targets = [m.group(1) for m in INLINE.finditer(line)]
            m = REFDEF.match(line)
            if m:
                targets.append(m.group(1))
            for target in targets:
                if re.match(r'^[a-z][a-z0-9+.-]*:', target, re.I):
                    continue  # http:, https:, mailto:, ...
                path_part, _, fragment = target.partition('#')
                path_part = unquote(path_part)
                if path_part:
                    resolved = os.path.normpath(os.path.join(os.path.dirname(md), path_part))
                    if not os.path.exists(resolved):
                        broken.append(f'{md}:{lineno}: missing target {target}')
                        continue
                else:
                    resolved = md
                if fragment and resolved.lower().endswith('.md') and os.path.isfile(resolved):
                    if unquote(fragment) not in anchors(resolved):
                        broken.append(f'{md}:{lineno}: no heading for #{fragment} in {resolved}')
    for b in broken:
        print(b)
    if broken:
        print(f'{len(broken)} broken link(s) in {len(files)} Markdown file(s)')
        return 1
    print(f'all relative links resolve ({len(files)} Markdown files)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
