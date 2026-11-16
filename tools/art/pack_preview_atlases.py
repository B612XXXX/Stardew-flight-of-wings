"""Assemble validated angel/butterfly runtime atlases and wall-item sprites."""
from pathlib import Path
from PIL import Image, ImageChops
import json
from wing_furniture_art import build_item_sheet, verify_item_sheet

ROOT=Path(__file__).resolve().parents[2]
ANGEL_SOURCE=ROOT/'assets/animation-bound/v5'
BUTTERFLY_SOURCE=ROOT/'assets/butterfly-wings/v1'
DEST=ROOT/'src/AngelWings.VisualPreview/assets'
records=[]

# Preserve the original runtime paths for backwards-compatible angel wings.
for state in ['H','M','Q','T','L']:
    for view in ['back','right','front','left']:
        for layer in ['behind','in_front']:
            files=sorted((ANGEL_SOURCE/state/view/layer).glob('*.png'))
            count=10 if state in ['T','L'] else 16
            assert len(files)==count,(state,view,layer,len(files))
            atlas=Image.new('RGBA',(count*80,80))
            for k,file in enumerate(files):
                frame=Image.open(file)
                assert frame.size==(80,80) and frame.mode=='RGBA'
                atlas.paste(frame,(k*80,0))
            dest=DEST/state/view/(layer+'.png')
            dest.parent.mkdir(parents=True,exist_ok=True)
            atlas.save(dest)
            records.append(str(dest.relative_to(ROOT)))

# The butterfly design is an independent approved source. Validate and copy its
# atlases without resampling so every runtime pixel remains rebuildable from the
# supplied v1 script.
for state in ['H','M','Q','T','L']:
    for view in ['back','right','front','left']:
        for layer in ['behind','in_front']:
            count=10 if state in ['T','L'] else 16
            source=BUTTERFLY_SOURCE/'atlases'/state/view/(layer+'.png')
            atlas=Image.open(source).convert('RGBA')
            assert atlas.size==(count*80,80),(source,atlas.size)
            alpha_values={value for _,value in atlas.getchannel('A').getcolors(maxcolors=256)}
            assert alpha_values <= {0,255},source
            dest=DEST/'butterfly'/state/view/(layer+'.png')
            dest.parent.mkdir(parents=True,exist_ok=True)
            atlas.save(dest)
            records.append(str(dest.relative_to(ROOT)))

# Verify the transition frames used by the existing flight state machine.
for view in ['back','right','front','left']:
    for layer in ['behind','in_front']:
        h=Image.open(BUTTERFLY_SOURCE/'atlases'/'H'/view/(layer+'.png')).convert('RGBA')
        t=Image.open(BUTTERFLY_SOURCE/'atlases'/'T'/view/(layer+'.png')).convert('RGBA')
        l=Image.open(BUTTERFLY_SOURCE/'atlases'/'L'/view/(layer+'.png')).convert('RGBA')
        assert ImageChops.difference(t.crop((720,0,800,80)),h.crop((640,0,720,80))).getbbox() is None
        assert ImageChops.difference(l.crop((0,0,80,80)),h.crop((160,0,240,80))).getbbox() is None

item_sheet=build_item_sheet(DEST)
verify_item_sheet(item_sheet,DEST)
item_path=DEST/'items'/'wing-items.png'
item_path.parent.mkdir(parents=True,exist_ok=True)
item_sheet.save(item_path)

print(json.dumps({'atlases':len(records),'wing_sets':['angel','butterfly'],
    'frames_per_atlas':{'H/M/Q':16,'T/L':10},'item_sheet':str(item_path.relative_to(ROOT)),
    'destination':str(DEST)}))
