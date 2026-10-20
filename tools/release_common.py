"""Shared helpers of the release tools (tools/build.sh --package, tools/installer/make_installer.py,
tools/paradox/publish_paradox.py); docs/dev/releasing.md.

- the release version (<Version> of Directory.Build.props) and the release zip of exactly that version;
- the files a release zip must contain;
- the commit a DLL was built from (its informational version ends in +<commit sha>, written by Source Link), and a
  check that it is the clean HEAD of this repository;
- reproducible zips: sorted entries, every timestamp set to the commit time, fixed attributes;
- the personal-data scan of packaged bytes (8-bit text and UTF-16 at both byte alignments).

Command line (used by tools/build.sh):
  python tools/release_common.py zip <folder> <zip>      zips <folder> (its name is the top folder) reproducibly
  python tools/release_common.py check-zip <zip>         checks that a release zip holds every required file
"""
import os
import re
import subprocess
import sys
import zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
DIST = os.path.join(ROOT, 'dist')

# every file of a release zip (tools/build.sh --package): the mod folder as players extract it into Mods
REQUIRED_RELEASE_FILES = [
    'SkylineTogether/LICENSE',
    'SkylineTogether/README.txt',
    'SkylineTogether/SkylineTogether.Core.dll',
    'SkylineTogether/SkylineTogether.Core.pdb',
    'SkylineTogether/SkylineTogether.dll',
    'SkylineTogether/SkylineTogether.mjs',
    'SkylineTogether/SkylineTogether.pdb',
    'SkylineTogether/THIRD-PARTY-NOTICES.txt',
]


class ReleaseError(Exception):
    pass


def props_value(name):
    text = open(os.path.join(ROOT, 'Directory.Build.props'), encoding='utf-8').read()
    m = re.search(r'<%s(?:\s[^>]*)?>([^<]+)</%s>' % (name, name), text)
    return m.group(1).strip() if m else None


def release_version():
    version = props_value('Version')
    if not version:
        raise ReleaseError('no <Version> in Directory.Build.props')
    return version


def release_zip(version, explicit=None, dist=None):
    """The release zip of <version> (dist/SkylineTogether-<version>.zip) or the explicit path; never another version's."""
    dist = dist or DIST
    path = explicit or os.path.join(dist, 'SkylineTogether-%s.zip' % version)
    if not os.path.isfile(path):
        others = sorted(f for f in os.listdir(dist) if re.match(r'SkylineTogether-\d.*\.zip$', f)) if os.path.isdir(dist) else []
        raise ReleaseError('no %s (the version in Directory.Build.props is %s); run tools/build.sh --package first%s'
                           % (os.path.relpath(path, ROOT), version,
                              ('; dist/ holds other versions: ' + ', '.join(others)) if others else ''))
    return path


def missing_release_files(names):
    present = set(n.replace('\\', '/') for n in names)
    return [f for f in REQUIRED_RELEASE_FILES if f not in present]


def dll_commit(dll_bytes, version):
    """The commit sha a DLL of <version> was built from (its informational version '<version>+<sha>'), or None."""
    m = re.search(re.escape(version.encode('utf-8')) + rb'\+([0-9a-f]{40})', dll_bytes)
    return m.group(1).decode('ascii') if m else None


def git(*args):
    proc = subprocess.run(['git', '-C', ROOT] + list(args), capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        raise ReleaseError('git %s failed: %s' % (' '.join(args), proc.stderr.strip()))
    return proc.stdout.strip()


def dirty_files():
    """Tracked files with uncommitted changes (untracked files do not reach a build: every source is tracked)."""
    out = git('status', '--porcelain', '--untracked-files=no')
    return [line[3:] for line in out.splitlines() if line.strip()]


def verify_built_from_clean_head(dll_bytes, version, allow_dirty=False):
    """Fails unless the DLL was built from this repository's HEAD and nothing tracked is modified; returns the sha."""
    commit = dll_commit(dll_bytes, version)
    head = git('rev-parse', 'HEAD')
    problems = []
    if commit is None:
        problems.append('SkylineTogether.dll carries no "%s+<commit>" version: it was not built from this version with Source Link' % version)
    elif commit != head:
        problems.append('SkylineTogether.dll was built from commit %s, but HEAD is %s: build again from HEAD (tools/build.sh --package)'
                        % (commit[:12], head[:12]))
    dirty = dirty_files()
    if dirty and not allow_dirty:
        problems.append('uncommitted changes (%s): a release is built from a clean, pushed commit (--allow-dirty for a local test)'
                        % ', '.join(dirty[:5] + (['...'] if len(dirty) > 5 else [])))
    if problems:
        raise ReleaseError('\n  - ' + '\n  - '.join(problems))
    return commit


def commit_time():
    """The zip timestamp: the HEAD commit's time (UTC), so the same commit always gives the same bytes."""
    try:
        seconds = int(git('log', '-1', '--format=%ct'))
    except (ReleaseError, ValueError):
        seconds = 315532800  # 1980-01-01, the earliest time a zip can store
    import time
    t = time.gmtime(max(seconds, 315532800))
    return (t.tm_year, t.tm_mon, t.tm_mday, t.tm_hour, t.tm_min, t.tm_sec - t.tm_sec % 2)


def write_zip(path, entries, date_time=None):
    """
    Writes <entries> ((name, bytes) pairs) reproducibly: sorted by name, one fixed timestamp, MS-DOS attributes, the
    same compression level, no directory entries. The same input gives the same bytes (with the same zlib).
    """
    date_time = date_time or commit_time()
    names = [n for n, _ in entries]
    if len(set(names)) != len(names):
        raise ReleaseError('duplicate zip entries')
    tmp = path + '.tmp'
    with zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, data in sorted(entries, key=lambda e: e[0]):
            info = zipfile.ZipInfo(name.replace('\\', '/'), date_time=date_time)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 0
            info.external_attr = 0
            z.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    os.replace(tmp, path)


def zip_folder(folder, path):
    """Zips <folder> (its own name becomes the top folder of every entry) with write_zip."""
    folder = os.path.abspath(folder)
    base = os.path.dirname(folder)
    entries = []
    for dp, _, fs in os.walk(folder):
        for f in fs:
            full = os.path.join(dp, f)
            with open(full, 'rb') as fh:
                entries.append((os.path.relpath(full, base).replace(os.sep, '/'), fh.read()))
    write_zip(path, entries)


def load_personal_data_checks():
    import importlib.util
    spec = importlib.util.spec_from_file_location('check_personal_data', os.path.join(ROOT, 'tools', 'check_personal_data.py'))
    checks = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(checks)
    return checks


def decoded_texts(data):
    """The bytes as 8-bit text and as UTF-16 at both byte alignments (.NET metadata strings are UTF-16 and may start at
    an odd offset)."""
    yield data.decode('latin-1')
    yield data[0:len(data) - len(data) % 2].decode('utf-16-le', errors='ignore')
    odd = data[1:]
    yield odd[0:len(odd) - len(odd) % 2].decode('utf-16-le', errors='ignore')


def scan_bytes(name, data, checks, found):
    for text in decoded_texts(data):
        for line in text.splitlines():
            checks.scan_line(name, line, found)


def main(argv):
    try:
        if len(argv) == 3 and argv[0] == 'zip':
            zip_folder(argv[1], argv[2])
            return 0
        if len(argv) == 2 and argv[0] == 'check-zip':
            with zipfile.ZipFile(argv[1]) as z:
                missing = missing_release_files(z.namelist())
            if missing:
                print('the release zip lacks: ' + ', '.join(missing), file=sys.stderr)
                return 1
            return 0
    except (ReleaseError, OSError, zipfile.BadZipFile) as e:
        print(str(e), file=sys.stderr)
        return 1
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
