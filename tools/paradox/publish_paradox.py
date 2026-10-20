"""Checks and uploads Skyline Together to Paradox Mods with the game's own ModPublisher (no NuGet, no toolchain targets).

Usage (repo root, after tools/build.sh --package):

  python tools/paradox/publish_paradox.py check        validate everything and print the ModPublisher command (default);
                                                       the rules are those of the command --for names (default: Publish
                                                       while ModId is empty, else Release)
  python tools/paradox/publish_paradox.py Publish      first upload of a new mod (ModId must be empty)
  python tools/paradox/publish_paradox.py Release      a new release: NewVersion, then Update (the usual command)
  python tools/paradox/publish_paradox.py NewVersion   upload a new version (ModId set, new ModVersion and ChangeLog)
  python tools/paradox/publish_paradox.py Update       update only the page (texts, images, tags, links) from the config

  NewVersion uploads the files, the ModVersion and the ChangeLog; the page (ShortDescription, LongDescription,
  thumbnail, screenshots, tags, links) is written by Update. Release runs both, so the page always describes the
  version that was just uploaded.

  options:  --dry-run        do everything except running ModPublisher
            --yes            do not ask before uploading
            --game-dir <p>   the game's install folder (default: env GameDir, else the Steam path of Directory.Build.props)
            --publisher <p>  ModPublisher.exe (default: env CSII_MODPUBLISHERPATH, else the one inside the game folder)
            --zip <p>        the release zip to upload (default: dist/SkylineTogether-<Version>.zip)
            --for <command>  check only: the command whose rules apply (Release, Publish, NewVersion, Update)
            --allow-dirty    accept uncommitted changes and untracked build inputs (check and --dry-run only)
            --allow-prerelease  upload a <Version> with a pre-release suffix (-beta.1); without it only regular
                             releases go to Paradox Mods (docs/dev/releasing.md#version-numbers)
            --config-only    check only: validate PublishConfiguration.xml against Directory.Build.props and
                             CHANGELOG.md, without a release zip (CI runs it)

What it does (docs/dev/publishing-paradox-mods.md):
  1. reads <Version> from Directory.Build.props and src/SkylineTogether/Properties/PublishConfiguration.xml and checks
     the required fields, ModVersion == Version, the ModId rule of the command, a ChangeLog for NewVersion, a CHANGELOG.md
     section for the version, and that the thumbnail and screenshots exist;
  2. extracts the content folder dist/paradox/SkylineTogether from the release zip (the exact files that were tested and
     that the GitHub setup installs, without the zip's README.txt), so a Paradox Mods player and a GitHub player have the
     same build id; prints that build id; refuses a zip whose SkylineTogether.dll was not built from the current HEAD,
     a working tree with uncommitted changes or untracked build inputs, and (to upload) a HEAD that is not on
     origin/main or a <Version> with a pre-release suffix;
  3. scans the content folder and the configuration for personal data (tools/check_personal_data.py patterns);
  4. writes dist/paradox/PublishConfiguration.xml: the configuration with absolute image paths and without the
     indentation of multi-line texts;
  5. runs  ModPublisher.exe <command> dist/paradox/PublishConfiguration.xml -c dist/paradox/SkylineTogether -v
     (the same arguments as the toolchain's Mod.targets); Release runs NewVersion and, when it succeeded, Update. ModPublisher logs in with the account of the running game, so
     start the game and log in to Paradox first. It needs the .NET 6 runtime; without one, DOTNET_ROLL_FORWARD=Major
     lets it run on a newer runtime.
  After a successful Publish, the new mod id is written into the ModId of PublishConfiguration.xml (commit it).
"""
import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import release_common  # noqa: E402

PROJECT = os.path.join(ROOT, 'src', 'SkylineTogether')
CONFIG = os.path.join(PROJECT, 'Properties', 'PublishConfiguration.xml')
DIST = os.path.join(ROOT, 'dist', 'paradox')
CONTENT = os.path.join(DIST, 'SkylineTogether')
EFFECTIVE = os.path.join(DIST, 'PublishConfiguration.xml')
REQUIRED_FILES = ['SkylineTogether.dll', 'SkylineTogether.Core.dll', 'SkylineTogether.mjs']
LEFT_OUT = {'README.txt'}  # the zip's manual-install notes; Paradox Mods installs by itself
TOOLCHAIN = [
    ('Cities2_Data', 'Content', 'Game', '.ModdingToolchain', 'ModPublisher', 'ModPublisher.exe'),
    ('Cities2_Data', 'StreamingAssets', '~Tooling~', 'ModPublisher', 'ModPublisher.exe'),
]
COMMANDS = {'check': 'check', 'release': 'Release', 'publish': 'Publish', 'newversion': 'NewVersion', 'update': 'Update',
            'publishnewmod': 'Publish', 'publishnewversion': 'NewVersion', 'updatepublishedconfiguration': 'Update'}
# the ModPublisher commands each command runs, in order: a release uploads the version, then refreshes the page
STEPS = {'Publish': ['Publish'], 'NewVersion': ['NewVersion'], 'Update': ['Update'], 'Release': ['NewVersion', 'Update']}


def fail(msg):
    sys.exit('PUBLISH CHECK FAILED: ' + msg)


props_value = release_common.props_value


def implied_command():
    """The command a check validates by default: Publish while ModId is empty, else Release (NewVersion, then Update)."""
    _, value = read_config()
    return 'Publish' if value('ModId') in ('', '0') else 'Release'


def read_config():
    try:
        tree = ET.parse(CONFIG)
    except (OSError, ET.ParseError) as e:
        fail('cannot read %s: %s' % (os.path.relpath(CONFIG, ROOT), e))
    root = tree.getroot()

    def value(tag):
        el = root.find(tag)
        if el is None:
            return ''
        return (el.get('Value') if el.get('Value') is not None else (el.text or '')).strip()

    return root, value


def check_config(command, version):
    root, value = read_config()
    problems = []
    for tag in ('DisplayName', 'ShortDescription', 'LongDescription', 'Thumbnail', 'ModVersion', 'GameVersion'):
        if not value(tag):
            problems.append(tag + ' is empty')
    tags = [t.get('Value', '').strip() for t in root.findall('Tag')]
    if not any(tags):
        problems.append('at least one Tag is needed (ModPublisher refuses otherwise)')
    if value('ModVersion') != version:
        problems.append('ModVersion "%s" differs from <Version> "%s" in Directory.Build.props' % (value('ModVersion'), version))
    mod_id = value('ModId')
    if command == 'Publish' and mod_id not in ('', '0'):
        problems.append('ModId is set (%s): a first upload needs it empty; use NewVersion or Update for the existing mod' % mod_id)
    if command in ('NewVersion', 'Update', 'Release') and not re.fullmatch(r'\d+', mod_id):
        problems.append('ModId must be the id of the published mod for %s (it is "%s")' % (command, mod_id))
    if command in ('NewVersion', 'Release') and not value('ChangeLog'):
        problems.append('%s needs a ChangeLog' % command)
    changelog = open(os.path.join(ROOT, 'CHANGELOG.md'), encoding='utf-8').read()
    if not re.search(r'^## \[%s\]' % re.escape(version), changelog, re.M):
        problems.append('CHANGELOG.md has no "## [%s]" section: move the Unreleased entries to it first' % version)
    images = [value('Thumbnail')] + [s.get('Value', '').strip() for s in root.findall('Screenshot') if s.get('Value', '').strip()]
    for rel in images:
        if rel and not os.path.isfile(os.path.join(PROJECT, rel)):
            problems.append('image not found: src/SkylineTogether/' + rel)
    for tag in ('LongDescription', 'ChangeLog'):
        el = root.find(tag)
        if el is not None and el.text and re.search(r'\n[ \t]+\S', el.text):
            problems.append(tag + ' has indented lines (leading spaces break the Paradox Mods markdown)')
    if problems:
        fail('\n  - ' + '\n  - '.join(problems))
    print('config ok: %s %s, game %s, %d screenshot(s), tags %s, ModId "%s", access %s'
          % (value('DisplayName'), value('ModVersion'), value('GameVersion'), len(images) - 1, tags, mod_id, value('AccessLevel') or 'default'))
    return images


def is_prerelease(version):
    """A version with a pre-release suffix (0.2.0-beta.1): GitHub only (docs/dev/releasing.md#version-numbers)."""
    return '-' in version


def stage_content(zip_path, version, allow_dirty, require_pushed=True):
    try:
        zip_path = release_common.release_zip(version, zip_path)
    except release_common.ReleaseError as e:
        fail(str(e) + ' (and test exactly that build)')
    with zipfile.ZipFile(zip_path) as z:
        missing = release_common.missing_release_files(z.namelist())
        if missing:
            fail('the release zip lacks ' + ', '.join(missing))
        try:
            commit = release_common.verify_built_from_clean_head(z.read('SkylineTogether/SkylineTogether.dll'), version, allow_dirty,
                                                                 require_pushed=require_pushed)
        except release_common.ReleaseError as e:
            fail('the release zip is not a build of the clean HEAD:' + str(e))
    print('release zip: %s, built from commit %s' % (os.path.relpath(zip_path, ROOT), commit[:12]))
    if not require_pushed:
        pushed, why = release_common.head_is_pushed()
        if not pushed:
            print('warning: ' + why + ' (an upload refuses it)')
    if os.path.isdir(CONTENT):
        shutil.rmtree(CONTENT)
    os.makedirs(CONTENT)
    with zipfile.ZipFile(zip_path) as z:
        for name in z.namelist():
            parts = name.replace('\\', '/').split('/')
            if len(parts) < 2 or parts[0] != 'SkylineTogether' or not parts[-1] or name.endswith('/'):
                continue
            rel = '/'.join(parts[1:])
            if rel in LEFT_OUT or '..' in parts:
                continue
            target = os.path.join(CONTENT, *parts[1:])
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, 'wb') as f:
                f.write(z.read(name))
    for f in REQUIRED_FILES:
        if not os.path.isfile(os.path.join(CONTENT, f)):
            fail('the release zip has no SkylineTogether/' + f)
    build =hashlib.sha256(open(os.path.join(CONTENT, 'SkylineTogether.dll'), 'rb').read()).hexdigest()[:12]
    print('content: %s (%s)' % (os.path.relpath(CONTENT, ROOT), ', '.join(sorted(os.listdir(CONTENT)))))
    print('build id: %s  (the same id the GitHub setup prints; players of both installs can play together)' % build)
    return build


def scan_personal_data():
    checks = release_common.load_personal_data_checks()
    found = []
    files = [os.path.join(dp, f) for dp, _, fs in os.walk(CONTENT) for f in fs] + [CONFIG]
    for path in files:
        data = open(path, 'rb').read()
        release_common.scan_bytes(os.path.relpath(path, ROOT), data, checks, found)
    if found:
        for f in sorted(set(found)):
            print(f)
        fail('personal data in the upload (see above)')
    print('personal-data scan: clean (%d files)' % len(files))


def write_effective_config():
    text = open(CONFIG, encoding='utf-8').read()

    def absolute(m):
        path = os.path.abspath(os.path.join(PROJECT, m.group(2)))
        return m.group(1) + path.replace('&', '&amp;').replace('"', '&quot;') + '"'

    text = re.sub(r'(<(?:Thumbnail|Screenshot)\s+Value=")([^"]+)"', absolute, text)

    def dedent(m):
        body = '\n'.join(line.strip() for line in m.group(3).strip().splitlines())
        return m.group(1) + '\n' + body + '\n' + m.group(4)

    text = re.sub(r'(<(LongDescription|ChangeLog)>)(.*?)(</\2>)', dedent, text, flags=re.S)
    os.makedirs(DIST, exist_ok=True)
    with open(EFFECTIVE, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)
    ET.parse(EFFECTIVE)  # still well-formed
    print('config for ModPublisher: ' + os.path.relpath(EFFECTIVE, ROOT))


def find_publisher(explicit, game_dir):
    candidates = []
    if explicit:
        candidates.append(explicit)
    if os.environ.get('CSII_MODPUBLISHERPATH'):
        candidates.append(os.environ['CSII_MODPUBLISHERPATH'])
    game = game_dir or os.environ.get('GameDir') or props_value('GameDir')
    if game:
        for parts in TOOLCHAIN:
            candidates.append(os.path.join(game, *parts))
    for c in candidates:
        if c and os.path.isfile(c):
            return c
    fail('ModPublisher.exe not found (looked at: %s). Pass --publisher or --game-dir.' % '; '.join(candidates))


def dotnet_env():
    env = dict(os.environ)
    try:
        out = subprocess.run(['dotnet', '--list-runtimes'], capture_output=True, text=True, check=False).stdout
    except OSError:
        out = ''
    if not re.search(r'^Microsoft\.NETCore\.App 6\.', out, re.M):
        env['DOTNET_ROLL_FORWARD'] = 'Major'
        print('no .NET 6 runtime: ModPublisher (built for .NET 6) runs with DOTNET_ROLL_FORWARD=Major on a newer runtime')
    return env


def main():
    ap = argparse.ArgumentParser(description='Check and upload Skyline Together to Paradox Mods.')
    ap.add_argument('command', nargs='?', default='check')
    ap.add_argument('--dry-run', action='store_true')
    ap.add_argument('--yes', action='store_true')
    ap.add_argument('--game-dir')
    ap.add_argument('--publisher')
    ap.add_argument('--zip')
    ap.add_argument('--for', dest='for_command')
    ap.add_argument('--allow-dirty', action='store_true')
    ap.add_argument('--allow-prerelease', action='store_true')
    ap.add_argument('--config-only', action='store_true')
    args = ap.parse_args()
    command = COMMANDS.get(args.command.lower())
    if not command:
        fail('unknown command "%s" (check, Release, Publish, NewVersion, Update)' % args.command)
    if args.for_command and command != 'check':
        fail('--for applies to check only')
    if args.allow_dirty and command != 'check' and not args.dry_run:
        fail('--allow-dirty applies to check and --dry-run only: an upload is built from a clean commit')
    if args.config_only and command != 'check':
        fail('--config-only applies to check only')
    rules = command
    if command == 'check':
        rules = COMMANDS.get((args.for_command or implied_command()).lower())
        if rules not in ('Release', 'Publish', 'NewVersion', 'Update'):
            fail('--for takes Release, Publish, NewVersion or Update')

    version = props_value('Version')
    if not version:
        fail('no <Version> in Directory.Build.props')
    print('Skyline Together %s -> Paradox Mods (%s)' % (version, command if command != 'check' else 'check for ' + rules))
    check_config(rules, version)
    uploading = command != 'check' and not args.dry_run
    if is_prerelease(version):
        if uploading and not args.allow_prerelease:
            fail('%s is a pre-release: only regular releases go to Paradox Mods (docs/dev/releasing.md#version-numbers); '
                 'pass --allow-prerelease to upload it anyway' % version)
        print('note: %s is a pre-release; uploading it needs --allow-prerelease' % version)
    if args.config_only:
        return
    stage_content(args.zip, version, args.allow_dirty, require_pushed=uploading)
    scan_personal_data()
    write_effective_config()
    publisher = find_publisher(args.publisher, args.game_dir)
    env = dotnet_env()
    steps = STEPS[rules]
    for step in steps:
        cmd = [publisher, step, EFFECTIVE, '-c', CONTENT, '-v']
        print('ModPublisher command:\n  ' + ' '.join('"%s"' % c if ' ' in c else c for c in cmd))
    if command == 'check' and rules == 'Release':
        print('a release runs NewVersion, then Update: python tools/paradox/publish_paradox.py Release')
    if command == 'check' or args.dry_run:
        print('not uploading (%s)' % ('check' if command == 'check' else '--dry-run'))
        return
    print('\nModPublisher logs in with the Paradox account of the running game: start Cities: Skylines II and log in first.')
    if not args.yes:
        answer = input('Upload Skyline Together %s to Paradox Mods now (%s)? Type yes: ' % (version, ', then '.join(steps)))
        if answer.strip().lower() != 'yes':
            print('cancelled')
            return
    output = []
    for step in steps:
        print('\n== ModPublisher ' + step)
        # stream ModPublisher's output as it comes (progress, a login prompt) and keep it for the mod id
        output = []
        proc = subprocess.Popen([publisher, step, EFFECTIVE, '-c', CONTENT, '-v'], env=env, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, text=True, errors='replace', bufsize=1)
        for line in proc.stdout:
            sys.stdout.write(line)
            sys.stdout.flush()
            output.append(line)
        returncode = proc.wait()
        if returncode != 0:
            later = '; the new version is up: run Update to refresh the page' if command == 'Release' and step == 'Update' else ''
            fail('ModPublisher %s exited with %d%s' % (step, returncode, later))
    m = re.search(r'Mod published with Id=\s*(\d+)', ''.join(output))
    if command == 'Publish' and not m:
        fail('ModPublisher reported no "Mod published with Id=": write the new mod id into ModId of %s by hand' % os.path.relpath(CONFIG, ROOT))
    if command == 'Publish' and m:
        text = open(CONFIG, encoding='utf-8').read()
        text, replaced = re.subn(r'<ModId Value="[^"]*"', '<ModId Value="%s"' % m.group(1), text, count=1)
        if replaced != 1:
            fail('published as mod %s, but %s has no <ModId Value="..."> to write it into: add it by hand' % (m.group(1), os.path.relpath(CONFIG, ROOT)))
        with open(CONFIG, 'w', encoding='utf-8', newline='\n') as f:
            f.write(text)
        print('\nPublished as mod %s; written into ModId of %s. Commit that change.' % (m.group(1), os.path.relpath(CONFIG, ROOT)))


if __name__ == '__main__':
    main()
