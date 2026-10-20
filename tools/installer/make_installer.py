"""Builds dist/SkylineTogether-Setup.zip: a self-contained installer for players (the release zip embedded as base64).

Usage (repo root):  python tools/installer/make_installer.py [--zip <release zip>] [--allow-dirty] [--allow-unpushed]
Run after tools/build.sh --package. It takes the release zip of the <Version> in Directory.Build.props (never
another version's zip that is still in dist/), and refuses a zip whose SkylineTogether.dll was not built from the
current HEAD, a working tree with uncommitted changes or untracked build inputs (--allow-dirty), or a HEAD that is not
on origin/main (--allow-unpushed); both options are for local tests only. The setup zip is reproducible: the same
release zip and commit give the same bytes.
"""
import argparse, base64, hashlib, io, os, sys, zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import release_common  # noqa: E402

ap = argparse.ArgumentParser(description='Build the player installer from the release zip.')
ap.add_argument('--zip', help='the release zip (default: dist/SkylineTogether-<Version>.zip)')
ap.add_argument('--allow-dirty', action='store_true', help='accept uncommitted changes and untracked build inputs (local tests only)')
ap.add_argument('--allow-unpushed', action='store_true', help='accept a HEAD that is not on origin/main (local tests only)')
args = ap.parse_args()
dist = release_common.DIST
try:
    version = release_common.release_version()
    release = release_common.release_zip(version, args.zip)
except release_common.ReleaseError as e:
    sys.exit(str(e))
payload = open(release, 'rb').read()
with zipfile.ZipFile(io.BytesIO(payload)) as z:
    missing = release_common.missing_release_files(z.namelist())
    if missing:
        sys.exit('the release zip lacks ' + ', '.join(missing) + '; build it again with tools/build.sh --package')
    dll = z.read('SkylineTogether/SkylineTogether.dll')
    # the build id: the first 12 hex digits of the SHA-256 of the shipped SkylineTogether.dll. The game computes the same
    # value from the loaded DLL (Core Session/BuildId.cs, GameInfoProvider.Build) and the installer script from the
    # installed file, so all three print the same id.
    build = hashlib.sha256(dll).hexdigest()[:12]
try:
    commit = release_common.verify_built_from_clean_head(dll, version, args.allow_dirty, require_pushed=not args.allow_unpushed,
                                                         unpushed_override='--allow-unpushed')
except release_common.ReleaseError as e:
    sys.exit('the release zip cannot be released (not a build of a clean, pushed HEAD):' + str(e))

# nothing the builder's machine knows may ship: scan every packaged file (as bytes decoded as 8-bit text and as UTF-16
# at both byte alignments, since .NET metadata strings are UTF-16) with the repository's personal-data patterns
checks = release_common.load_personal_data_checks()
found = []
with zipfile.ZipFile(io.BytesIO(payload)) as z:
    for name in z.namelist():
        release_common.scan_bytes(name, z.read(name), checks, found)
if found:
    for f in sorted(set(found)):
        print(f)
    sys.exit('personal data in the package; build again (Directory.Build.props maps source paths to /_/)')
b64 = base64.b64encode(payload).decode('ascii')
lines = '\n'.join(b64[i:i + 120] for i in range(0, len(b64), 120))
here = os.path.dirname(__file__)
tpl = open(os.path.join(here, 'Install-SkylineTogether.template.ps1'), encoding='utf-8').read()
script = (tpl.replace('__VERSION__', version).replace('__BUILDID__', build)
          .replace('__PAYLOADSHA__', hashlib.sha256(payload).hexdigest()).replace('__PAYLOAD__', lines))
assert all(ord(c) < 128 for c in script), 'the installer must stay ASCII (Windows PowerShell 5.1 reads it as ANSI)'
script = script.replace('\n', '\r\n')
install_cmd = ('@echo off\r\n'
               'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-SkylineTogether.ps1" %*\r\n')
logs_cmd = ('@echo off\r\n'
            'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-SkylineTogether.ps1" -CollectLogs %*\r\n')
checklist = open(os.path.join(here, 'TWO-PLAYER-CHECKLIST.txt'), encoding='utf-8').read().replace('__BUILDID__', build).replace('\n', '\r\n')
out = os.path.join(dist, 'SkylineTogether-Setup.zip')
release_common.write_zip(out, [
    ('SkylineTogether-Setup/Install-SkylineTogether.ps1', script.encode('ascii')),
    ('SkylineTogether-Setup/Install.cmd', install_cmd.encode('ascii')),
    ('SkylineTogether-Setup/Collect-Logs.cmd', logs_cmd.encode('ascii')),
    ('SkylineTogether-Setup/TWO-PLAYER-CHECKLIST.txt', checklist.encode('ascii')),
])
# a loose copy for the host's own use
loose = os.path.join(dist, 'SkylineTogether-Setup')
os.makedirs(loose, exist_ok=True)
stale = os.path.join(loose, 'HOW-TO-TEST.txt')  # the checklist's name before 0.2.0
if os.path.isfile(stale):
    os.remove(stale)
for name, data in (('Install-SkylineTogether.ps1', script), ('Install.cmd', install_cmd), ('Collect-Logs.cmd', logs_cmd), ('TWO-PLAYER-CHECKLIST.txt', checklist)):
    with open(os.path.join(loose, name), 'w', encoding='ascii', newline='') as f:
        f.write(data)
print('built', out, 'version', version, 'build', build, 'commit', commit[:12], 'size', os.path.getsize(out))


def sha256_of(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


# the checksums for the release notes (docs/dev/releasing.md): players compare them with Get-FileHash
print()
print('SHA-256 for the release notes:')
for path in (out, release):
    print('  %s  %s' % (sha256_of(path), os.path.basename(path)))
