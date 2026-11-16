"""Create an immutable, runtime-only Nexus ZIP from the verified Formal package."""
import argparse
import hashlib
import io
import json
import re
import shutil
import subprocess
import zipfile
from pathlib import Path
from PIL import Image
from art.wing_furniture_art import verify_item_sheet


ROOT = Path(__file__).resolve().parents[1]
PREFIX = 'AngelWingsVisualPreview/'
EFFECTS = {
    'assets/effects/ocean-wake.png',
    'assets/effects/river-wake.png',
    'assets/effects/lake-ripple.png',
}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def atlas_paths(base):
    return {
        f'{base}/{state}/{view}/{layer}.png'
        for state in 'HMQTL'
        for view in ('back', 'right', 'front', 'left')
        for layer in ('behind', 'in_front')
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package-path', type=Path,
                        default=ROOT / 'build/formal/AngelWingsVisualPreview')
    parser.add_argument('--game-path', type=Path, required=True,
                        help='Local game assemblies for the compiled 8-heart/369-flower check; no save is opened.')
    parser.add_argument('--dotnet', default=shutil.which('dotnet'),
                        help='Host with the system .NET 6 runtime.')
    parser.add_argument('--include-docs', action='store_true',
                        help='Optional full-with-docs variant; runtime-only is the upload default.')
    args = parser.parse_args()
    package = args.package_path.resolve()
    manifest = json.loads((package / 'manifest.json').read_text(encoding='utf-8-sig'))
    version = manifest['Version']
    require(re.fullmatch(r'\d+\.\d+\.\d+', version) is not None,
            'Nexus packaging requires a numbered Formal version, not a QuickTest build.')
    require(manifest['UniqueID'] == 'AngelWingsProject.VisualPreview', 'Unexpected mod identity')
    require(manifest['EntryDll'] == 'AngelWings.VisualPreview.dll', 'Unexpected entry DLL')
    suffix = 'Nexus' if args.include_docs else 'Nexus-Minimal'
    output = ROOT / f'release/AngelWings-{version}-{suffix}.zip'
    require(not output.exists(), f'Existing release archives are immutable: {output}')
    report_name = 'nexus-package-verification' if args.include_docs else 'nexus-runtime-verification'
    report_path = ROOT / f'build/{report_name}-{version}.json'
    require(not report_path.exists(), f'Existing Nexus verification report: {report_path}')

    angel_atlases = atlas_paths('assets')
    butterfly_atlases = atlas_paths('assets/butterfly')
    expected = angel_atlases | butterfly_atlases | EFFECTS | {
        'AngelWings.VisualPreview.dll', 'manifest.json', 'README.md', 'BUTTERFLY_WINGS_V1.md',
        'i18n/default.json', 'i18n/zh.json', 'assets/items/wing-items.png',
    }
    verified = json.loads((ROOT / f'build/package-verification-{version}.json').read_text(encoding='utf-8-sig'))
    require(verified['version'] == version and verified['profile'] == 'Formal', 'Formal verification mismatch')
    require(verified['files'] == len(expected) == 90, 'Incomplete full-mod verification')
    require(set(verified['file_sha256']) == expected, 'Missing angel, butterfly or flight resources')
    staged = {p.relative_to(package).as_posix() for p in package.rglob('*') if p.is_file()}
    require(staged == expected, f'Unexpected or missing staged files: {staged ^ expected}')

    source = ROOT / f'release/AngelWingsVisualPreview-{version}.zip'
    source_data = source.read_bytes()
    require(sha(source_data) == verified['zip_sha256'], 'Source ZIP changed since verification')
    payload = {}
    with zipfile.ZipFile(io.BytesIO(source_data)) as archive:
        require(archive.testzip() is None, 'Source ZIP CRC failed')
        entries = archive.namelist()
        names = [name for name in entries if not name.endswith('/')]
        require(len(entries) == len(set(entries)) and len(names) == len(expected),
                'Source ZIP duplicate or extra file entries')
        require(all(name.startswith(PREFIX) and '..' not in name.split('/') for name in entries),
                'Source ZIP contains an unsafe path')
        require(set(names) == {PREFIX + name for name in expected}, 'Source ZIP file set mismatch')
        for name in sorted(expected):
            data = archive.read(PREFIX + name)
            require(sha(data) == verified['file_sha256'][name], f'Source file hash mismatch: {name}')
            require(data == (package / name).read_bytes(), f'Staging changed since verification: {name}')
            payload[PREFIX + name] = data

    # Reject a mislabeled QuickTest DLL, not just a QuickTest manifest.
    require(args.dotnet is not None, 'A .NET 6 runtime is required; specify --dotnet.')
    inspector = ROOT / 'tools/inspect/bin/Release/net6.0/GameSignatures.dll'
    require(inspector.is_file(), 'Build the game inspector with validate-flight.ps1 first.')
    result = subprocess.run(
        [args.dotnet, str(inspector), str(args.game_path.resolve()),
         str(package / manifest['EntryDll']), '--quest-profile', 'Formal'],
        capture_output=True, text=True, check=True,
    )
    profile = json.loads(result.stdout.strip())
    require(profile['version'] == version and profile['profile'] == 'Formal'
            and (profile['haley_hearts'], profile['flowers']) == (8, 369),
            'The compiled DLL is not the verified 8-heart/369-flower version.')

    design = json.loads((ROOT / 'assets/butterfly-wings/v1/approved-atlases.json')
                        .read_text(encoding='utf-8-sig'))
    require(design['design_revision'] == 'occlusion-r9', 'Unexpected butterfly design')
    for name in sorted(butterfly_atlases):
        approved = name.replace('assets/butterfly/', 'assets/butterfly-wings/v1/atlases/', 1)
        require(sha(payload[PREFIX + name]) == design['file_sha256'][approved],
                f'Butterfly atlas differs from approved design: {name}')
    native_furniture = tuple(map(int, version.split('.'))) >= (0, 4, 11)
    if native_furniture:
        verify_item_sheet(Image.open(io.BytesIO(payload[PREFIX + 'assets/items/wing-items.png'])), package / 'assets')
    else:
        require(sha(payload[PREFIX + 'assets/items/wing-items.png'])
                == design['file_sha256']['integration/wing-items.png'], 'Wing furniture sheet differs from approved design')

    excluded_docs = [] if args.include_docs else ['README.md', 'BUTTERFLY_WINGS_V1.md']
    if args.include_docs:
        readme = payload[PREFIX + 'README.md']
        public_text = readme.decode('utf-8')
        require(version in public_text.splitlines()[0], 'Public README version mismatch')
        require(all(word in public_text for word in ('1,000g', 'Haley', '369', '8 hearts', '1000 金', '8 心')),
                'Public README must cover both original angel flight and the Formal butterfly quest')
        payload[PREFIX + 'README.md'] = readme
    else:
        for name in excluded_docs:
            del payload[PREFIX + name]
        require(len(payload) == 88 and not any(name.endswith('.md') for name in payload),
                'The default upload must contain only the 88 runtime files')
    for name, data in payload.items():
        if name.endswith(('.md', '.json')):
            require(re.search(r'[A-Za-z]:[\\/]', data.decode('utf-8-sig')) is None,
                    f'Personal/workspace absolute path in public text: {name}')

    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(payload.items()):
            archive.writestr(name, data)
    output_data = stream.getvalue()
    with zipfile.ZipFile(io.BytesIO(output_data)) as archive:
        require(archive.testzip() is None, 'Nexus ZIP CRC failed')
        require(len(archive.namelist()) == len(payload) and set(archive.namelist()) == set(payload),
                'Nexus ZIP file set mismatch')
        for name, data in payload.items():
            require(archive.read(name) == data, f'Nexus ZIP file readback failed: {name}')
        require(json.loads(archive.read(PREFIX + 'manifest.json')) == manifest, 'Nexus manifest changed')

    # Exclusive creation is also a final race-safe guard against replacing an archive.
    with output.open('xb') as archive_file:
        archive_file.write(output_data)
    require(output.read_bytes() == output_data, 'Written Nexus ZIP changed')
    report = {
        'version': version, 'profile': 'Formal',
        'distribution': 'with-docs' if args.include_docs else 'runtime-only',
        'archive': str(output), 'files': len(payload),
        'bytes': len(output_data), 'sha256': sha(output_data),
        'source_archive': str(source), 'source_zip_sha256': verified['zip_sha256'],
        'only_changed_payload': 'README.md' if args.include_docs else None,
        'removed_payloads': excluded_docs, 'runtime_assets_and_translations_unchanged': True,
        'dll_sha256': sha(payload[PREFIX + manifest['EntryDll']]),
        'compiled_profile_verified_by_reflection': True,
        'haley_hearts': profile['haley_hearts'], 'required_flowers': profile['flowers'],
        'wing_sets': ['angel', 'butterfly'], 'angel_atlases': len(angel_atlases),
        'butterfly_atlases': len(butterfly_atlases), 'water_effects': len(EFFECTS),
        'wing_furniture_sheet_included': True, 'languages': ['default', 'zh'],
        'wing_furniture_native_scale_verified': native_furniture,
        'butterfly_design_revision': design['design_revision'],
        'approved_butterfly_atlases_verified': len(butterfly_atlases),
        'excluded': ['config.json', 'saves', 'source', 'logs', 'game assemblies', 'design/development archives'] + excluded_docs,
        'personal_paths_in_public_text': False, 'zip_crc_and_readback_passed': True,
        'existing_releases_overwritten': False, 'game_installation_changed': False,
        'game_session_tested': False, 'publicly_uploaded': False,
        'file_sha256': {name: sha(data) for name, data in sorted(payload.items())},
    }
    with report_path.open('x', encoding='utf-8') as report_file:
        report_file.write(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
    print(f'PASS: {len(payload)} files; original angel + butterfly atlases (40 each); '
          f'compiled Formal 8/369; runtime/assets unchanged; '
          f'{"bilingual docs" if args.include_docs else "runtime-only, no docs"}; ZIP CRC and readback.')
    print(f'{output} ({len(output_data):,} bytes)')
    print(f'SHA256: {report["sha256"]}')


if __name__ == '__main__':
    main()
