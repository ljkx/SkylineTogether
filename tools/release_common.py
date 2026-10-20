"""Shared helpers of the release tools (tools/build.sh --package, tools/installer/make_installer.py,
tools/paradox/publish_paradox.py); docs/dev/releasing.md.

- the release version (<Version> of Directory.Build.props) and the release zip of exactly that version;
- the files a release zip must contain;
- the commit a DLL was built from (its informational version ends in +<commit sha>, written by Source Link), and a
  check that it is the clean, pushed HEAD of this repository (no tracked file modified, no untracked file among the
  inputs of the build, HEAD contained in origin/main);
- reproducible zips: sorted entries, every timestamp set to the commit time, fixed attributes;
- the personal-data scan of packaged bytes (8-bit text and UTF-16 at both byte alignments).

Command line (used by tools/build.sh):
  python tools/release_common.py zip <folder> <zip>      zips <folder> (its name is the top folder) reproducibly
  python tools/release_common.py check-zip <zip>         checks that a release zip holds every required file
  python tools/release_common.py dirty                   lists what keeps the working tree from being a release build
                                                         (modified tracked files, untracked build inputs)
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

# what a build reads besides the tracked files: an untracked file below one of these reaches the DLLs or the package
# (the SDK projects compile every **/*.cs below their folder; Directory.Build.* and global.json change how everything
# is built; tools/build.sh, this file, the installer and the Paradox tools write what ships). Git pathspecs relative to
# the repository root, ":(icase)" where Windows and MSBuild ignore the case of a name.
BUILD_INPUTS = [
    'src', 'ui',
    ':(icase)Directory.Build.props', ':(icase)Directory.Build.targets', ':(icase)Directory.Build.rsp',
    ':(icase)Directory.Packages.props', ':(icase)NuGet.config', ':(icase)global.json', '.editorconfig',
    ':(icase)SkylineTogether.sln', 'LICENSE', 'THIRD-PARTY-NOTICES.txt',
    'tools/build.sh', 'tools/release_common.py', 'tools/installer', 'tools/paradox',
]

# the branch a release commit must be on, as last fetched: Source Link points players' stack traces at the commit on
# GitHub, and the release tag names it
RELEASE_BRANCH = 'origin/main'


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


def git_run(*args, root=None):
    """(exit code, stdout, stderr) of a git command in the repository (or in <root>)."""
    proc = subprocess.run(['git', '-C', root or ROOT] + list(args), capture_output=True, text=True, check=False)
    # only trailing white space goes: a porcelain status line starts with a space (" M file")
    return proc.returncode, proc.stdout.rstrip(), proc.stderr.strip()


def git(*args, root=None):
    code, out, err = git_run(*args, root=root)
    if code != 0:
        raise ReleaseError('git %s failed: %s' % (' '.join(args), err))
    return out


def dirty_files(root=None):
    """
    What keeps the working tree from being its commit: tracked files with uncommitted changes (anywhere), and untracked,
    not ignored files below the inputs of the build (BUILD_INPUTS), marked "(untracked)". An untracked file does reach a
    build: the projects compile every .cs file below their folder, so a leftover experiment or a merge backup would ship
    in a DLL that names the clean commit, and nobody could rebuild that release from its tag.
    """
    tracked = git('status', '--porcelain', '--untracked-files=no', root=root)
    untracked = git('status', '--porcelain', '--untracked-files=all', '--', *BUILD_INPUTS, root=root)
    files = [line[3:] for line in tracked.splitlines() if line.strip()]
    files += [line[3:] + ' (untracked)' for line in untracked.splitlines() if line.startswith('??')]
    return files


def head_is_pushed(root=None, branch=RELEASE_BRANCH):
    """(True, '') when HEAD is contained in <branch> (as last fetched), else (False, why)."""
    code, _, _ = git_run('rev-parse', '--verify', '--quiet', branch + '^{commit}', root=root)
    if code != 0:
        return False, 'there is no %s to compare HEAD with (git fetch origin)' % branch
    code, _, err = git_run('merge-base', '--is-ancestor', 'HEAD', branch, root=root)
    if code == 0:
        return True, ''
    if code == 1:
        return False, 'HEAD is not on %s: push it first (git push; if it is pushed already, git fetch origin)' % branch
    return False, 'cannot compare HEAD with %s: %s' % (branch, err)


def verify_built_from_clean_head(dll_bytes, version, allow_dirty=False, require_pushed=True, root=None, unpushed_override=None):
    """
    Fails unless the DLL was built from this repository's HEAD, nothing tracked is modified and no untracked file lies
    among the build inputs (dirty_files; allow_dirty: a local test), and HEAD is pushed (head_is_pushed;
    require_pushed=False for a check before the push); returns the sha. unpushed_override names the caller's option
    that accepts an unpushed HEAD (for the message).
    """
    commit = dll_commit(dll_bytes, version)
    head = git('rev-parse', 'HEAD', root=root)
    problems = []
    if commit is None:
        problems.append('SkylineTogether.dll carries no "%s+<commit>" version: it was not built from this version with Source Link' % version)
    elif commit != head:
        problems.append('SkylineTogether.dll was built from commit %s, but HEAD is %s: build again from HEAD (tools/build.sh --package)'
                        % (commit[:12], head[:12]))
    dirty = dirty_files(root=root)
    if dirty and not allow_dirty:
        problems.append('uncommitted changes or untracked build inputs (%s): a release is built from a clean, pushed commit; '
                        'commit or remove them (--allow-dirty for a local test)'
                        % ', '.join(dirty[:5] + (['...'] if len(dirty) > 5 else [])))
    if require_pushed:
        pushed, why = head_is_pushed(root=root)
        if not pushed:
            problems.append(why + ' (Source Link and the release tag name this commit on GitHub'
                            + ('; %s for a local test' % unpushed_override if unpushed_override else '') + ')')
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
        if len(argv) == 1 and argv[0] == 'dirty':
            for f in dirty_files():
                print(f)
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
