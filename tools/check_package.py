"""Check the distributable without loading a game session or any save."""
import hashlib
import io
import json
import sys
import zipfile
from pathlib import Path
from PIL import Image
from art.wing_furniture_art import verify_item_sheet

root = Path(__file__).resolve().parents[1]
package = Path(sys.argv[2]) if len(sys.argv) > 2 else root / 'build' / 'AngelWingsVisualPreview'
manifest = json.loads((package / 'manifest.json').read_text(encoding='utf-8-sig'))
native_furniture = tuple(map(int, manifest['Version'].split('-')[0].split('.'))) >= (0, 4, 11)
archive = Path(sys.argv[1])
expected = {'AngelWings.VisualPreview.dll', 'manifest.json', 'README.md', 'BUTTERFLY_WINGS_V1.md',
            'i18n/default.json', 'i18n/zh.json'}
effect_specs = {
    'assets/effects/ocean-wake.png': (384, 64),
    'assets/effects/river-wake.png': (320, 64),
    'assets/effects/lake-ripple.png': (512, 64),
}
expected.update(effect_specs)
expected.add('assets/items/wing-items.png')
for state in 'HMQTL':
    for view in ('back', 'right', 'front', 'left'):
        for layer in ('behind', 'in_front'):
            expected.add(f'assets/{state}/{view}/{layer}.png')
            expected.add(f'assets/butterfly/{state}/{view}/{layer}.png')
actual = {p.relative_to(package).as_posix() for p in package.rglob('*') if p.is_file()}
assert actual == expected, f'Unexpected or missing package files: {actual ^ expected}'
assert manifest['UniqueID'] == 'AngelWingsProject.VisualPreview'
assert manifest['EntryDll'] == 'AngelWings.VisualPreview.dll'
assert archive.name == f'AngelWingsVisualPreview-{manifest["Version"]}.zip', 'Archive/manifest version mismatch'
design = json.loads((root / 'assets/butterfly-wings/v1/approved-atlases.json').read_text(encoding='utf-8-sig'))
assert design['design_revision'] == 'occlusion-r9'
digests = {}
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None, 'ZIP CRC failed'
    files = [n for n in z.namelist() if not n.endswith('/')]
    assert len(files) == len(expected) and set(files) == {'AngelWingsVisualPreview/' + f for f in expected}
    for name in sorted(expected):
        data = z.read('AngelWingsVisualPreview/' + name)
        assert data == (package / name).read_bytes(), name
        digests[name] = hashlib.sha256(data).hexdigest()
        if name.startswith('assets/butterfly/'):
            design_name = name.replace('assets/butterfly/', 'assets/butterfly-wings/v1/atlases/', 1)
            assert digests[name] == design['file_sha256'][design_name], f'Differs from current r9 design: {name}'
        elif name == 'assets/items/wing-items.png' and not native_furniture:
            assert digests[name] == design['file_sha256']['integration/wing-items.png'], 'Wing item sheet differs from approved design'
        if name.endswith('.png'):
            with Image.open(io.BytesIO(data)) as image:
                if name in effect_specs:
                    assert image.mode == 'RGBA' and image.size == effect_specs[name], name
                    visible = {pixel for pixel in image.get_flattened_data() if pixel[3]}
                    assert len(visible) <= 4, f'Too many effect colors: {name}'
                    if 'wake' in name:
                        heights=[]
                        for x in range(0,image.width,64):
                            box=image.crop((x,0,x+64,64)).getchannel('A').getbbox()
                            heights.append(0 if box is None else box[3]-box[1])
                        assert all(a>=b for a,b in zip(heights,heights[1:])), f'Wake is not high-to-low: {name}'
                elif name == 'assets/items/wing-items.png':
                    if native_furniture:
                        verify_item_sheet(image, package / 'assets')
                    else:
                        assert image.mode == 'RGBA' and image.size == (128, 48), name
                else:
                    parts = name.split('/')
                    state = parts[2] if parts[1] == 'butterfly' else parts[1]
                    assert image.mode == 'RGBA' and image.size == (800 if state in 'TL' else 1280, 80), name
                alpha = set(image.getchannel('A').tobytes())
                assert alpha <= {0, 255} and 0 in alpha, name
                if 255 not in alpha:
                    assert '/front/in_front.png' in name, f'Unexpected empty layer: {name}'
for filename in ('default.json', 'zh.json'):
    translations = json.loads((package / 'i18n' / filename).read_text(encoding='utf-8-sig'))
    assert translations and all(isinstance(v, str) and v for v in translations.values())
    if filename == 'default.json':
        keys = translations.keys()
    else:
        assert translations.keys() == keys, 'Language keys differ'
report = {'version': manifest['Version'], 'files': len(expected), 'atlases': 80, 'wing_sets': ['angel', 'butterfly'],
          'water_effects': len(effect_specs), 'zip_bytes': archive.stat().st_size,
          'zip_sha256': hashlib.sha256(archive.read_bytes()).hexdigest(), 'file_sha256': digests,
          'butterfly_design_revision': design['design_revision'], 'approved_butterfly_atlases_verified': 40,
          'wing_furniture_native_scale_verified': native_furniture,
          'profile': 'QuickTest' if manifest['Version'].endswith('-quick-test') else 'Formal',
          'game_session_tested': False}
(root / 'build' / f'package-verification-{manifest["Version"]}.json').write_text(
    json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f'PASS: {len(expected)} package files, 80 RGBA atlases, wing item sheet, 3 water-effect strips, translations, ZIP CRC and every file hash.')
