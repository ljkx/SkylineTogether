"""Fails when a tracked text file contains something that looks like personal data.

Usage (repo root):  python tools/check_personal_data.py            (tracked files)
                    python tools/check_personal_data.py --commits  (commit messages, authors and committers
                                                                    reachable from HEAD, or from the given range)

Checked in every file `git ls-files` lists (binary files are skipped):
  - absolute paths into a user's home folder (C:\\Users\\<name>, /c/Users/<name>, /home/<name>, /Users/<name>),
    except placeholder names such as x, Public, Default or <name>;
  - Steam IDs (17 digits starting 7656119) that do not look like obvious test values (a run of 5+ zeros, or the
    base value 76561197960265728);
  - e-mail addresses other than noreply/example addresses.
Use %USERPROFILE%, ~ or a placeholder in docs instead of a real path. Exit code 1 when anything is found.
"""
import re
import subprocess
import sys

PLACEHOLDER_USERS = {'x', 'public', 'default', 'all users', '<name>', '<user>', 'you', 'user', 'username',
                     '%username%', '$env:username', 'runneradmin', 'runner'}

HOME_PATH = re.compile(r'(?:\b[A-Za-z]:[\\/]{1,2}Users[\\/]{1,2}|/c/Users/|/home/|(?<![\w.])/Users/)([^\\/\s"\'`<>|:*?]+(?:<[^>]*>)?)')
STEAM_ID = re.compile(r'(?<!\d)7656119\d{10}(?!\d)')
EMAIL = re.compile(r'[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}')
OK_EMAIL = re.compile(r'(noreply|no-reply)[^@]*@|@users\.noreply\.github\.com$|@anthropic\.com$|@example\.(com|org|net)$', re.I)


def tracked_files():
    out = subprocess.run(['git', 'ls-files', '-z'], capture_output=True, check=True).stdout
    return [p for p in out.decode('utf-8').split('\0') if p]


def scan_line(where, line, problems):
    for m in HOME_PATH.finditer(line):
        if m.group(1).strip().lower() not in PLACEHOLDER_USERS:
            problems.append(f'{where}: home folder path "{m.group(0)}"')
    for m in STEAM_ID.finditer(line):
        value = m.group(0)
        if '00000' not in value[7:] and value != '76561197960265728':
            problems.append(f'{where}: Steam ID {value}')
    for m in EMAIL.finditer(line):
        if not OK_EMAIL.search(m.group(0)):
            problems.append(f'{where}: e-mail address {m.group(0)}')


def scan_commits(rev_range, problems):
    # merges pulled from a local clone get "Merge branch 'x' of C:/Users/<name>/..." as their default message
    fmt = '%H%x1f%an <%ae>%x1f%cn <%ce>%x1f%B%x1e'
    out = subprocess.run(['git', 'log', '--format=' + fmt, rev_range], capture_output=True, check=True).stdout
    for record in out.decode('utf-8', errors='replace').split('\x1e'):
        record = record.strip('\n')
        if not record:
            continue
        sha, author, committer, body = record.split('\x1f', 3)
        where = 'commit ' + sha[:10]
        scan_line(where + ' author', author, problems)
        scan_line(where + ' committer', committer, problems)
        for line in body.splitlines():
            scan_line(where + ' message', line, problems)


def main():
    problems = []
    if len(sys.argv) > 1 and sys.argv[1] == '--commits':
        scan_commits(sys.argv[2] if len(sys.argv) > 2 else 'HEAD', problems)
        return report(problems)
    for path in tracked_files():
        if path == 'tools/check_personal_data.py':
            continue  # its own patterns would match
        try:
            data = open(path, 'rb').read()
        except OSError:
            continue
        if b'\0' in data[:8192]:
            continue  # binary
        text = data.decode('utf-8', errors='replace')
        for lineno, line in enumerate(text.splitlines(), 1):
            scan_line(f'{path}:{lineno}', line, problems)
    return report(problems)


def report(problems):
    for p in problems:
        print(p)
    if problems:
        print(f'{len(problems)} possible personal data finding(s); replace them with placeholders')
        return 1
    print('no personal data found')
    return 0


if __name__ == '__main__':
    sys.exit(main())
