#!/usr/bin/env python3
"""Increment version.txt build number for debug builds."""

import argparse
import re
import sys
from pathlib import Path


def parse_version(text: str) -> tuple[int, int, int]:
    m = re.match(r'(\d+)\.(\d+)\.(\d+)', text.strip())
    if not m:
        raise ValueError(f'Invalid version: {text!r}')
    return int(m.group(1)), int(m.group(2)), int(m.group(3))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['build', 'release'], help='build=increment, release=keep')
    parser.add_argument('versionfile', help='Path to version.txt')
    args = parser.parse_args()

    vf = Path(args.versionfile)
    text = vf.read_text().strip()
    major, minor, patch = parse_version(text)

    if args.action == 'build':
        patch += 1
        if patch > 99:
            patch = 0
            minor += 1
        vf.write_text(f'{major}.{minor}.{patch}\n')

    print(f'{major}.{minor}.{patch}', end='')


if __name__ == '__main__':
    main()
