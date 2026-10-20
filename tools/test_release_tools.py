"""Self-test of the release tools (tools/release_common.py and the scripts that use it). No game, no network.

Usage (repo root):  python tools/test_release_tools.py      (CI runs it)
"""
import os
import shutil
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import release_common as rc  # noqa: E402


def read(path):
    with open(path, 'rb') as f:
        return f.read()


class ReleaseZipSelection(unittest.TestCase):
    def setUp(self):
        self.dist = tempfile.mkdtemp(prefix='st-release-test-')

    def tearDown(self):
        shutil.rmtree(self.dist, ignore_errors=True)

    def touch(self, name):
        with open(os.path.join(self.dist, name), 'wb') as f:
            f.write(b'PK')

    def test_takes_the_zip_of_the_version_not_the_lexically_last(self):
        for v in ('0.1.0-alpha.9', '0.1.0-alpha.10', '0.1.0-beta.1', '0.1.0', '0.9.0', '0.10.0'):
            self.touch('SkylineTogether-%s.zip' % v)
        for v in ('0.1.0-alpha.10', '0.1.0-alpha.9', '0.10.0', '0.9.0'):
            self.assertEqual('SkylineTogether-%s.zip' % v, os.path.basename(rc.release_zip(v, dist=self.dist)))

    def test_a_missing_zip_fails_and_names_what_is_there(self):
        self.touch('SkylineTogether-0.1.0-alpha.3.zip')
        with self.assertRaises(rc.ReleaseError) as e:
            rc.release_zip('0.1.0-alpha.4', dist=self.dist)
        self.assertIn('0.1.0-alpha.4', str(e.exception))
        self.assertIn('SkylineTogether-0.1.0-alpha.3.zip', str(e.exception))

    def test_an_explicit_zip_wins(self):
        self.touch('custom.zip')
        self.assertEqual(os.path.join(self.dist, 'custom.zip'), rc.release_zip('9.9.9', os.path.join(self.dist, 'custom.zip'), dist=self.dist))


class ReleaseContents(unittest.TestCase):
    def test_every_required_file_is_checked(self):
        self.assertEqual([], rc.missing_release_files(rc.REQUIRED_RELEASE_FILES))
        names = [n for n in rc.REQUIRED_RELEASE_FILES if not n.endswith('.mjs')]
        self.assertEqual(['SkylineTogether/SkylineTogether.mjs'], rc.missing_release_files(names))
        self.assertEqual([], rc.missing_release_files([n.replace('/', '\\') for n in rc.REQUIRED_RELEASE_FILES]))

    def test_the_commit_is_read_from_the_informational_version(self):
        sha = '1d9f28bc822b4f38f1dae808cdcef12a472f3ab0'
        # the custom attribute blob: a length byte that happens to be a digit ('6' = 54) before the version
        dll = b'\x00\x01' + b'6' + b'0.1.0-alpha.4+' + sha.encode() + b'\x00\x00'
        self.assertEqual(sha, rc.dll_commit(dll, '0.1.0-alpha.4'))
        self.assertIsNone(rc.dll_commit(dll, '0.1.0-alpha.5'), 'another version is not this build')
        self.assertIsNone(rc.dll_commit(b'0.1.0-alpha.4 without a hash', '0.1.0-alpha.4'))


class CleanPushedHead(unittest.TestCase):
    """verify_built_from_clean_head in a scratch repository with a local bare 'origin' (no network)."""

    VERSION = '0.2.0'

    def setUp(self):
        self.base = tempfile.mkdtemp(prefix='st-release-git-')
        self.repo = os.path.join(self.base, 'repo')
        self.origin = os.path.join(self.base, 'origin.git')
        self.run_git('init', '-q', '--bare', self.origin, cwd=self.base)
        self.run_git('init', '-q', '-b', 'main', self.repo, cwd=self.base)
        self.write('src/SkylineTogether.Core/A.cs', 'class A {}\n')
        self.write('docs/notes.md', 'notes\n')
        self.write('.gitignore', 'bin/\nobj/\ndist/\n')
        self.run_git('add', '-A')
        self.run_git('commit', '-q', '-m', 'first')
        self.run_git('remote', 'add', 'origin', self.origin)

    def tearDown(self):
        shutil.rmtree(self.base, ignore_errors=True)

    def run_git(self, *args, cwd=None):
        import subprocess
        subprocess.run(['git', '-c', 'user.name=Test', '-c', 'user.email=test@example.com', '-c', 'commit.gpgsign=false',
                        '-c', 'core.autocrlf=false'] + list(args), cwd=cwd or self.repo, check=True, capture_output=True)

    def write(self, rel, text):
        path = os.path.join(self.repo, *rel.split('/'))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(text)

    def dll(self):
        """A stand-in DLL whose informational version names HEAD."""
        return b'\x00' + (self.VERSION + '+' + rc.git('rev-parse', 'HEAD', root=self.repo)).encode() + b'\x00'

    def verify(self, **kwargs):
        return rc.verify_built_from_clean_head(self.dll(), self.VERSION, root=self.repo, **kwargs)

    def test_an_untracked_source_file_is_refused_like_a_modified_one(self):
        self.assertEqual([], rc.dirty_files(root=self.repo))
        self.write('src/SkylineTogether.Core/UntrackedProbe.cs', 'class Probe {}\n')
        self.assertEqual(['src/SkylineTogether.Core/UntrackedProbe.cs (untracked)'], rc.dirty_files(root=self.repo))
        with self.assertRaises(rc.ReleaseError) as e:
            self.verify(require_pushed=False)
        self.assertIn('UntrackedProbe.cs (untracked)', str(e.exception))
        self.verify(require_pushed=False, allow_dirty=True)  # a local test may
        os.remove(os.path.join(self.repo, 'src', 'SkylineTogether.Core', 'UntrackedProbe.cs'))
        self.verify(require_pushed=False)

    def test_untracked_build_inputs_count_and_other_untracked_files_do_not(self):
        for rel in ('Directory.Build.targets', 'directory.build.props', 'global.json', 'ui/extra.mjs', 'tools/installer/x.ps1'):
            self.write(rel, 'x\n')
        self.write('docs/draft.md', 'x\n')
        self.write('notes.txt', 'x\n')
        self.write('src/SkylineTogether.Core/bin/Release/A.dll', 'x\n')  # build output: ignored
        found = sorted(f[:-len(' (untracked)')] for f in rc.dirty_files(root=self.repo) if f.endswith(' (untracked)'))
        self.assertEqual(['Directory.Build.targets', 'directory.build.props', 'global.json', 'tools/installer/x.ps1', 'ui/extra.mjs'], found)

    def test_a_modified_tracked_file_anywhere_is_listed_with_its_first_letter(self):
        self.write('docs/notes.md', 'changed\n')
        self.assertEqual(['docs/notes.md'], rc.dirty_files(root=self.repo))

    def test_an_unpushed_head_is_refused_until_origin_main_contains_it(self):
        pushed, why = rc.head_is_pushed(root=self.repo)
        self.assertFalse(pushed)
        self.assertIn('git fetch origin', why, 'no origin/main yet')
        self.run_git('push', '-q', 'origin', 'main')
        self.assertEqual((True, ''), rc.head_is_pushed(root=self.repo))
        self.verify()
        self.write('docs/notes.md', 'two\n')
        self.run_git('commit', '-q', '-am', 'second')
        pushed, why = rc.head_is_pushed(root=self.repo)
        self.assertFalse(pushed)
        self.assertIn('push it first', why)
        with self.assertRaises(rc.ReleaseError) as e:
            self.verify()
        self.assertIn('not on origin/main', str(e.exception))
        self.verify(require_pushed=False)
        self.run_git('push', '-q', 'origin', 'main')
        self.verify()

    def test_a_dll_of_another_commit_is_refused(self):
        dll = b'\x00' + (self.VERSION + '+' + '1' * 40).encode()
        with self.assertRaises(rc.ReleaseError) as e:
            rc.verify_built_from_clean_head(dll, self.VERSION, require_pushed=False, root=self.repo)
        self.assertIn('built from commit 111111111111', str(e.exception))


class ReleaseRules(unittest.TestCase):
    def test_build_sh_warns_with_the_same_rule(self):
        with open(os.path.join(rc.ROOT, 'tools', 'build.sh'), encoding='utf-8') as f:
            self.assertIn('tools/release_common.py dirty', f.read())

    def test_only_regular_releases_go_to_paradox_mods(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location('publish_paradox', os.path.join(rc.ROOT, 'tools', 'paradox', 'publish_paradox.py'))
        pp = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(pp)
        self.assertTrue(pp.is_prerelease('0.1.0-alpha.4'))
        self.assertTrue(pp.is_prerelease('0.2.0-beta.1'))
        self.assertFalse(pp.is_prerelease('0.2.0'))
        self.assertFalse(pp.is_prerelease('1.10.3'))


class ReproducibleZips(unittest.TestCase):
    def test_the_same_entries_give_the_same_bytes_in_any_order(self):
        d = tempfile.mkdtemp(prefix='st-zip-test-')
        try:
            a, b = os.path.join(d, 'a.zip'), os.path.join(d, 'b.zip')
            entries = [('X/b.txt', b'two'), ('X/a.dll', bytes(range(256)) * 40)]
            rc.write_zip(a, entries, (2026, 10, 20, 12, 0, 0))
            rc.write_zip(b, list(reversed(entries)), (2026, 10, 20, 12, 0, 0))
            self.assertEqual(read(a), read(b))
            with zipfile.ZipFile(a) as z:
                self.assertEqual(['X/a.dll', 'X/b.txt'], z.namelist())
                self.assertEqual((2026, 10, 20, 12, 0, 0), z.getinfo('X/b.txt').date_time)
                self.assertEqual(b'two', z.read('X/b.txt'))
            with self.assertRaises(rc.ReleaseError):
                rc.write_zip(a, [('X/a', b''), ('X/a', b'')])
        finally:
            shutil.rmtree(d, ignore_errors=True)

    def test_a_folder_zips_identically_twice(self):
        d = tempfile.mkdtemp(prefix='st-zip-test-')
        try:
            folder = os.path.join(d, 'SkylineTogether')
            os.makedirs(os.path.join(folder, 'sub'))
            for name, data in (('a.txt', b'a'), ('sub/b.bin', b'\x00\x01')):
                with open(os.path.join(folder, name), 'wb') as f:
                    f.write(data)
            rc.zip_folder(folder, os.path.join(d, '1.zip'))
            os.utime(os.path.join(folder, 'a.txt'), (1, 1))  # file times never reach the zip
            rc.zip_folder(folder, os.path.join(d, '2.zip'))
            self.assertEqual(read(os.path.join(d, '1.zip')), read(os.path.join(d, '2.zip')))
            with zipfile.ZipFile(os.path.join(d, '1.zip')) as z:
                self.assertEqual(['SkylineTogether/a.txt', 'SkylineTogether/sub/b.bin'], z.namelist())
        finally:
            shutil.rmtree(d, ignore_errors=True)


class PersonalDataScan(unittest.TestCase):
    def found(self, data):
        checks = rc.load_personal_data_checks()
        found = []
        rc.scan_bytes('x', data, checks, found)
        return found

    def test_utf16_at_both_alignments_and_8_bit_text(self):
        path = 'C:' + chr(92) + 'Users' + chr(92) + 'alice' + chr(92) + 'src'
        utf16 = path.encode('utf-16-le')
        self.assertTrue(self.found(b'\0\0' + utf16), 'even offset')
        self.assertTrue(self.found(b'\0' + utf16), 'odd offset, odd length')
        self.assertTrue(self.found(b'\0' + utf16 + b'\0'), 'odd offset, even length')
        self.assertTrue(self.found(b'x ' + path.encode('latin-1')), '8-bit text')
        self.assertFalse(self.found(b'/_/src/SkylineTogether.Core/Session/SessionHost.cs'.decode().encode('utf-16-le')), 'mapped paths are fine')


if __name__ == '__main__':
    unittest.main(verbosity=1)
