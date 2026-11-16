"""Import the approved bitmap into the native atlas grid without reskinning its feathers.

The canonical left outline is sampled directly from the approved concept. Animation
uses a single affine wing plane about its root; the right wing is a literal mirror.
This production atlas compiler keeps the approved source immutable.
"""
from pathlib import Path
import hashlib
import json
import math
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from pixel_art import components, guide

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'assets/concepts/angel-wings-slender-v4.png'
OUT = ROOT / 'assets/animation-bound/v5'
PALETTE = np.array([[255,255,255],[207,217,219],[113,133,139]],dtype=np.uint8)
SIZE = 80
FONT = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 16)
VIEWS = ('back', 'right', 'front', 'left')


def ease(t):
    t = max(0., min(1., t))
    return t*t*(3-2*t)


def attached_pixels(im):
    # Nearest-neighbour rotation can leave one-pixel islands at feather tips.
    # Keep the connected wing after native-grid rasterization, not floating dots.
    a=np.array(im)
    labels,counts=components(a[:,:,3]>0,diagonal=True)
    counts[0]=0
    a[labels!=counts.argmax()]=0
    return Image.fromarray(a)


def import_template():
    # The newly designed single-wing source has a teal matte. Import its neutral
    # connected component; only uniform native-grid sampling, no outline squashing.
    rgb = np.array(Image.open(SOURCE).convert('RGB'))
    crop = rgb
    signed = crop.astype(np.int16)
    mask = (signed.max(2)-signed.min(2)<30) & (signed.mean(2)>45)
    labels, counts = components(mask, diagonal=True)
    counts[0] = 0
    mask = labels == counts.argmax()
    rgba = np.zeros((*mask.shape, 4), np.uint8)
    rgba[mask, :3] = crop[mask]
    rgba[mask, 3] = 255
    master = Image.fromarray(rgba)
    bounds = master.getbbox()
    master = master.crop(bounds)
    # Uniform scale: horizontal root-to-tip reach ~31 native pixels vs a 32px farmer.
    scale = 32/master.width
    native = master.resize((32, round(master.height*scale)), Image.Resampling.NEAREST)
    a = np.array(native)
    # Deterministic palette editing of the established native sprite. Preserve
    # alpha exactly; remove noisy midtones, retaining just white, shade, contour.
    alpha=a[:,:,3]>0
    padded=np.pad(alpha,1)
    interior=alpha & padded[:-2,1:-1] & padded[2:,1:-1] & padded[1:-1,:-2] & padded[1:-1,2:]
    luma=a[:,:,:3].mean(2)
    a[:,:,:3]=PALETTE[0]
    shade=interior & (luma<165)
    shade_labels,shade_counts=components(shade,diagonal=True)
    shade_counts[0]=0
    # Tiny grey flecks disappear at game scale; retain only readable shade clusters.
    a[shade & (shade_counts[shade_labels]>=3),:3]=PALETTE[1]
    a[alpha & ~interior,:3]=PALETTE[2]
    a[a[:, :, 3]==0] = 0
    native = Image.fromarray(a)
    # The rightmost rounded covert is the shoulder socket, not a hanging feather.
    ys, xs = np.where(np.array(master)[:, :, 3] > 0)
    root = np.array([31.5, float(np.median(ys[xs >= master.width-20])) * scale])
    native.save(OUT/'canonical-left.png')
    native.transpose(Image.Transpose.FLIP_LEFT_RIGHT).save(OUT/'canonical-right.png')
    return native, root


def pose_parameters(state, progress):
    phase = progress
    openness = 1.
    if state == 'T':
        phase = (.55-(1-progress)*700/960) % 1
        openness = ease(progress/.55)
    elif state == 'L':
        phase = (.15+progress*700/960) % 1
        openness = 1-ease((progress-.28)/.72)
    # skeleton-v1: a 44% downstroke, 56% recovery, then folding centred at .70.
    # Use its shoulder trajectory on the intact new silhouette; do not stretch
    # separate feather groups (the old mesh warped the outline).
    amp = {'H':50.,'M':44.,'Q':30.}.get(state,50.)
    theta = math.pi*(phase/.44 if phase<.44 else 1+(phase-.44)/.56)
    swing = 6 + amp*math.cos(theta)
    recovery_fold = math.exp(3*(math.cos(2*math.pi*(phase-.70))-1))
    width = 1 - .25*recovery_fold
    angle = math.radians(swing*openness-42*(1-openness))
    return phase, angle, width*(.48+.52*openness)


def placed(template, root, anchor, angle, width, side_view=False, far=False):
    c, s = math.cos(angle), math.sin(angle)
    # Side wings extend behind the torso together, not through the face.
    width *= (.59 if far else .72) if side_view else 1.
    transform = np.array([[c*width,-s],[s*width,c]])
    inverse = np.linalg.inv(transform)
    offset = root-inverse@np.array(anchor)
    data = tuple(np.c_[inverse, offset].ravel())
    im = template.transform((SIZE,SIZE), Image.Transform.AFFINE, data, resample=Image.Resampling.NEAREST)
    if far:
        pixels = np.array(im)
        original = pixels[:, :, :3].copy()
        pixels[(original==PALETTE[0]).all(2)&(pixels[:,:,3]>0),:3]=PALETTE[1]
        im = Image.fromarray(pixels)
    return im


def export_frame(template, root, state, view, p):
    phase, angle, width = pose_parameters(state,p)
    if view in ('front','back'):
        left = placed(template,root,(37.5,42),angle,width)
        # The user requested a tiny fixed white block mount instead of coverts.
        socket=Image.new('RGBA',(SIZE,SIZE))
        d=ImageDraw.Draw(socket)
        d.rectangle((35,41,38,42),fill=tuple(PALETTE[0])+(255,))
        d.rectangle((36,43,38,44),fill=tuple(PALETTE[0])+(255,))
        left=Image.alpha_composite(left,socket)
        # Clip to its own half to make the generated pair mathematically symmetric.
        a = np.array(left);a[:,40:]=0;left=attached_pixels(Image.fromarray(a))
        right = left.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
        full = Image.alpha_composite(left,right)
        foreground = np.zeros((SIZE,SIZE),bool)
        if view=='back':
            yy,xx=np.indices((SIZE,SIZE))
            foreground=(((xx>=35)&(xx<=38))|((xx>=41)&(xx<=44)))&(yy>=41)&(yy<=44)
    else:
        far = placed(template,root,(37,41),angle*.85,width,True,True)
        near = placed(template,root,(39,42),angle,width,True)
        full=Image.alpha_composite(far,near)
        socket=Image.new('RGBA',(SIZE,SIZE))
        ImageDraw.Draw(socket).rectangle((37,41,39,44),fill=tuple(PALETTE[0])+(255,))
        full=attached_pixels(Image.alpha_composite(full,socket))
        yy,xx=np.indices((SIZE,SIZE))
        foreground=(xx>=37)&(xx<=39)&(yy>=41)&(yy<=44)
        if view=='left':
            full=full.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
            foreground=foreground[:,::-1]
    a=np.array(full);over=a.copy();over[~foreground]=0;a[foreground]=0
    return full,Image.fromarray(a),Image.fromarray(over)


def preview(under,over,view):
    im=Image.new('RGBA',(SIZE,SIZE),'#6f9a91')
    ImageDraw.Draw(im).ellipse((33,67,46,70),fill='#456b62')
    im.alpha_composite(under,(0,-5));im.alpha_composite(guide(view),(0,-5));im.alpha_composite(over,(0,-5))
    return im


def main():
    OUT.mkdir(parents=True,exist_ok=True)
    digest=hashlib.sha256(SOURCE.read_bytes()).hexdigest()
    template,root=import_template()
    report={'source':str(SOURCE.relative_to(ROOT)),'source_sha256':digest,'canvas':[80,80],
        'method':'unchanged v4 silhouette, three-colour native palette, skeleton-v1 motion and fixed white block mounts',
        'palette':PALETTE.tolist(),
        'canonical_size':list(template.size),'canonical_root':root.tolist(),'single_wing_horizontal_reach':float(root[0]),
        'farmer_sprite_height':32,'hover_flap_degrees':[-44,56],'downstroke_fraction':.44,
        'in_game_verified':False,'states':{}}
    for state in 'HMQTL':
        count=10 if state in 'TL' else 16
        period={'H':960,'M':840,'Q':1200,'T':700,'L':700}[state]
        boards=[]
        for k in range(count):
            p=k/(count-1) if state in 'TL' else k/count
            board=Image.new('RGB',(640,704),'#eeeee8');draw=ImageDraw.Draw(board)
            draw.text((14,8),f'{state} {k+1:02d}/{count} · 已确认轮廓 / 分层比例检查',font=FONT,fill='#293d37')
            for i,view in enumerate(VIEWS):
                full,under,over=export_frame(template,root,state,view,p)
                for name,im in [('frames',full),('behind',under),('in_front',over)]:
                    dest=OUT/state/view/name;dest.mkdir(parents=True,exist_ok=True)
                    im.save(dest/f'{state.lower()}-{view}-{k+1:02d}.png')
                x,y=i%2*320,40+i//2*332
                draw.text((x+10,y),{'front':'正面','back':'背面','right':'右侧','left':'左侧'}[view],font=FONT,fill='#293d37')
                board.paste(preview(under,over,view).resize((320,320),Image.Resampling.NEAREST).convert('RGB'),(x,y+22))
            boards.append(board)
        boundaries=np.rint(np.linspace(0,period/10,count+1)).astype(int)*10
        boards[0].save(OUT/f'{state.lower()}-preview.gif',save_all=True,append_images=boards[1:],duration=np.diff(boundaries).tolist(),loop=0,disposal=2)
        boards[0].save(OUT/f'{state.lower()}-four-view-check.png')
        report['states'][state]={'frames_per_direction':count,'period_ms':period}
    assert hashlib.sha256(SOURCE.read_bytes()).hexdigest()==digest
    (OUT/'animation-data.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(report,ensure_ascii=False))


if __name__=='__main__':main()
