#!/usr/bin/env python3
"""Repack pinned upstream archives, never a developer's installed libraries.

python3 build/repack-jxl.py --rid osx-arm64 --archives /path/to/archives --output /isolated/assets
python3 build/repack-jxl.py --rid win-x64 --archives /path/to/archives --output /isolated/assets
Archive filenames, origins, source pins and SHA-256: src/FilmBatch.Raw/Native/Jxl/provenance.json.
No download, compilation, user configuration or original camera file mutation occurs here.
macOS requires Xcode command-line tools. Repacking fixes load commands BEFORE ad-hoc signing;
Build.cs re-signs the final published copies. Hashes of signed Mach-O may vary by toolchain.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / 'src/FilmBatch.Raw/Native/Jxl/provenance.json'


def run(*args):
    return subprocess.check_output(args, text=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rid', required=True, choices=['osx-arm64', 'win-x64'])
    parser.add_argument('--archives', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    manifest = json.loads(MANIFEST.read_text())
    archives = {}
    for item in manifest['archives']:
        if (args.rid == 'win-x64') != item['name'].startswith('windows-'):
            continue
        path = args.archives / item['filename']
        if hashlib.sha256(path.read_bytes()).hexdigest() != item['sha256']:
            raise RuntimeError(f'Archive hash mismatch: {path}')
        archives[item['name']] = path
    if args.output.exists() and any(args.output.iterdir()):
        raise RuntimeError('Output must be empty; existing assets are never overwritten')
    args.output.mkdir(parents=True, exist_ok=True)
    names = manifest['files'][args.rid]
    if args.rid == 'win-x64':
        # bsdtar or GNU tar with zstd support; read exact members to stdout only.
        # All six archives have already been verified before any extraction.
        extracted = set()
        for item in manifest['archives']:
            if not item['name'].startswith('windows-'):
                continue
            for member, spec in item['members'].items():
                relative = Path(spec['output'])
                if relative.is_absolute() or '..' in relative.parts or spec['output'] in extracted:
                    raise RuntimeError(f'Invalid or duplicate output: {relative}')
                data = subprocess.check_output(['tar', '-xOf', str(archives[item['name']]), member])
                if hashlib.sha256(data).hexdigest() != spec['sha256']:
                    raise RuntimeError(f'Member hash mismatch: {member}')
                target = args.output / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
                extracted.add(spec['output'])
        if {name for name in extracted if name.endswith('.dll')} != set(names):
            raise RuntimeError('Windows extracted DLL set differs from pinned closure')
        for name, expected in names.items():
            if hashlib.sha256((args.output / name).read_bytes()).hexdigest() != expected:
                raise RuntimeError(f'Windows payload hash mismatch: {name}')
    else:
        for name in names:
            if name.startswith('libjxl'):
                package, version = 'jpeg-xl', '0.11.2'
                original = name.replace('.0.11.dylib', '.0.11.2.dylib')
            elif name.startswith('libbrotli'):
                package, version = 'brotli', '1.2.0'
                original = name.replace('.1.dylib', '.1.2.0.dylib')
            elif name.startswith('libhwy'):
                package, version, original = 'highway', '1.3.0', 'libhwy.1.3.0.dylib'
            else:
                package, version, original = 'little-cms2', '2.18', name
            with tarfile.open(archives[package]) as archive:
                with archive.extractfile(f'{package}/{version}/lib/{original}') as source:
                    (args.output / name).write_bytes(source.read())
        for path in sorted(args.output.glob('*.dylib')):
            run('install_name_tool', '-id', '@loader_path/' + path.name, str(path))
            for line in run('otool', '-L', str(path)).splitlines()[1:]:
                dependency = line.strip().split(' (', 1)[0]
                if dependency.startswith(('/usr/lib/', '/System/Library/', '@loader_path/')):
                    continue
                target = Path(dependency).name
                if target not in names:
                    raise RuntimeError(f'Unbundled dependency: {dependency}')
                run('install_name_tool', '-change', dependency, '@loader_path/' + target, str(path))
            for rpath in re.findall(r'cmd LC_RPATH\s+cmdsize \d+\s+path (.*?) \(offset', run('otool', '-l', str(path))):
                run('install_name_tool', '-delete_rpath', rpath, str(path))
            run('codesign', '--force', '--sign', '-', str(path))
    hashes = {name: hashlib.sha256((args.output / name).read_bytes()).hexdigest() for name in names}
    (args.output / 'repack-sha256.json').write_text(json.dumps(hashes, indent=2) + '\n')


if __name__ == '__main__':
    main()
