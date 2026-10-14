"""Fails when a tracked text file contains something that looks like personal data.

Usage (repo root):  python tools/check_personal_data.py

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


def main():
    problems = []
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
            for m in HOME_PATH.finditer(line):
                if m.group(1).strip().lower() not in PLACEHOLDER_USERS:
                    problems.append(f'{path}:{lineno}: home folder path "{m.group(0)}"')
            for m in STEAM_ID.finditer(line):
                value = m.group(0)
                if '00000' not in value[7:] and value != '76561197960265728':
                    problems.append(f'{path}:{lineno}: Steam ID {value}')
            for m in EMAIL.finditer(line):
                if not OK_EMAIL.search(m.group(0)):
                    problems.append(f'{path}:{lineno}: e-mail address {m.group(0)}')
    for p in problems:
        print(p)
    if problems:
        print(f'{len(problems)} possible personal data finding(s); replace them with placeholders')
        return 1
    print('no personal data found')
    return 0


if __name__ == '__main__':
    sys.exit(main())
