"""Smoke test of the player installer on Windows (CI runs it; no game needed).

Usage (repo root):  python tools/installer/smoke_test.py <SkylineTogether.Core.dll> [--keep] [--allow-dirty]

Builds a stand-in release zip dist/SkylineTogether-<Version>.zip whose DLLs are the given Core DLL (CI cannot build
the game layer; any DLL built from HEAD carries the commit the release tools check), runs make_installer.py, then
runs the generated installer with -NoPause -SkipGameCheck against a fake user profile and checks the installed files
and the build id it prints, and finally runs it with -Uninstall. Refuses to overwrite an existing release zip.
"""
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import release_common  # noqa: E402


def fail(msg):
    sys.exit('INSTALLER SMOKE TEST FAILED: ' + msg)


def main(argv):
    if not argv:
        fail('usage: smoke_test.py <dll> [--keep]')
    dll_path = argv[0]
    keep = '--keep' in argv
    version = release_common.release_version()
    zip_path = os.path.join(release_common.DIST, 'SkylineTogether-%s.zip' % version)
    if os.path.exists(zip_path):
        fail('%s exists; this test writes a stand-in release zip and does not overwrite a real one' % os.path.relpath(zip_path, ROOT))
    dll = open(dll_path, 'rb').read()
    pdb_path = os.path.splitext(dll_path)[0] + '.pdb'
    pdb = open(pdb_path, 'rb').read() if os.path.isfile(pdb_path) else b''

    def text(rel):
        with open(os.path.join(ROOT, rel), 'rb') as f:
            return f.read()

    os.makedirs(release_common.DIST, exist_ok=True)
    release_common.write_zip(zip_path, [
        ('SkylineTogether/SkylineTogether.dll', dll),
        ('SkylineTogether/SkylineTogether.pdb', pdb),
        ('SkylineTogether/SkylineTogether.Core.dll', dll),
        ('SkylineTogether/SkylineTogether.Core.pdb', pdb),
        ('SkylineTogether/SkylineTogether.mjs', text('ui/SkylineTogether.mjs')),
        ('SkylineTogether/LICENSE', text('LICENSE')),
        ('SkylineTogether/THIRD-PARTY-NOTICES.txt', text('THIRD-PARTY-NOTICES.txt')),
        ('SkylineTogether/README.txt', b'stand-in release for the installer smoke test\n'),
    ])
    profile = tempfile.mkdtemp(prefix='st-installer-smoke-')
    try:
        build = hashlib.sha256(dll).hexdigest()[:12]
        made = subprocess.run([sys.executable, os.path.join(ROOT, 'tools', 'installer', 'make_installer.py')] + (['--allow-dirty'] if '--allow-dirty' in argv else []), cwd=ROOT,
                              capture_output=True, text=True, check=False)
        print(made.stdout + made.stderr)
        if made.returncode != 0:
            fail('make_installer.py exited with %d' % made.returncode)
        if 'build ' + build not in made.stdout:
            fail('make_installer.py did not print build id ' + build)

        user_data = os.path.join(profile, 'AppData', 'LocalLow', 'Colossal Order', 'Cities Skylines II')
        os.makedirs(user_data)
        env = dict(os.environ, USERPROFILE=profile)
        script = os.path.join(release_common.DIST, 'SkylineTogether-Setup', 'Install-SkylineTogether.ps1')

        def installer(*args):
            proc = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, '-NoPause', '-SkipGameCheck'] + list(args),
                                  env=env, capture_output=True, text=True, check=False)
            print(proc.stdout + proc.stderr)
            return proc

        proc = installer()
        if proc.returncode != 0:
            fail('the installer exited with %d' % proc.returncode)
        mod = os.path.join(user_data, 'Mods', 'SkylineTogether')
        for name in ('SkylineTogether.dll', 'SkylineTogether.Core.dll', 'SkylineTogether.mjs'):
            if not os.path.isfile(os.path.join(mod, name)):
                fail('the installer did not install ' + name)
        with open(os.path.join(mod, 'SkylineTogether.dll'), 'rb') as f:
            if hashlib.sha256(f.read()).hexdigest()[:12] != build:
                fail('the installed DLL is not the packaged one')
        if build not in proc.stdout:
            fail('the installer did not print build id ' + build)

        proc = installer('-Uninstall')
        if proc.returncode != 0 or os.path.exists(mod):
            fail('-Uninstall did not remove the mod (exit %d)' % proc.returncode)
        print('installer smoke test passed: build %s installed and removed' % build)
    finally:
        shutil.rmtree(profile, ignore_errors=True)
        if not keep:
            os.remove(zip_path)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
