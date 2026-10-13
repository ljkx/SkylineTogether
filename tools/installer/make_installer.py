"""Builds dist/SkylineTogether-Setup.zip: a self-contained installer for players (the release zip embedded as base64).

Usage (repo root):  python tools/installer/make_installer.py
Run after tools/build.sh --package.
"""
import base64, hashlib, io, os, re, sys, zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
dist = os.path.join(ROOT, 'dist')
zips = sorted(f for f in os.listdir(dist) if re.match(r'SkylineTogether-\d.*\.zip$', f))
if not zips:
    sys.exit('no dist/SkylineTogether-<version>.zip; run tools/build.sh --package first')
release = os.path.join(dist, zips[-1])
version = re.match(r'SkylineTogether-(.*)\.zip$', zips[-1]).group(1)
payload = open(release, 'rb').read()
with zipfile.ZipFile(io.BytesIO(payload)) as z:
    build = hashlib.sha256(z.read('SkylineTogether/SkylineTogether.dll')).hexdigest()[:12]
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
howto = open(os.path.join(here, 'HOW-TO-TEST.txt'), encoding='utf-8').read().replace('__BUILDID__', build).replace('\n', '\r\n')
out = os.path.join(dist, 'SkylineTogether-Setup.zip')
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    z.writestr('SkylineTogether-Setup/Install-SkylineTogether.ps1', script)
    z.writestr('SkylineTogether-Setup/Install.cmd', install_cmd)
    z.writestr('SkylineTogether-Setup/Collect-Logs.cmd', logs_cmd)
    z.writestr('SkylineTogether-Setup/HOW-TO-TEST.txt', howto)
# a loose copy for the host's own use
loose = os.path.join(dist, 'SkylineTogether-Setup')
os.makedirs(loose, exist_ok=True)
for name, data in (('Install-SkylineTogether.ps1', script), ('Install.cmd', install_cmd), ('Collect-Logs.cmd', logs_cmd), ('HOW-TO-TEST.txt', howto)):
    with open(os.path.join(loose, name), 'w', encoding='ascii', newline='') as f:
        f.write(data)
print('built', out, 'version', version, 'build', build, 'size', os.path.getsize(out))
