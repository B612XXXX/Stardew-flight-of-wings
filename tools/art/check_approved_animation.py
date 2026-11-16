"""Validate the approved design, mirrored layers, root continuity and pose endpoints."""
from pathlib import Path
import hashlib
import json
import numpy as np
from PIL import Image
from pixel_art import components
import build_approved_wing_animation as art

ROOT=art.ROOT
OUT=art.OUT


def main():
    meta=json.loads((OUT/'animation-data.json').read_text(encoding='utf-8'))
    assert hashlib.sha256((ROOT/meta['source']).read_bytes()).hexdigest()==meta['source_sha256']
    assert .9 <= meta['single_wing_horizontal_reach']/meta['farmer_sprite_height'] <= 1.05
    left=np.array(Image.open(OUT/'canonical-left.png'))
    assert left.shape[:2] == (18, 32), 'Approved silhouette dimensions changed'
    assert hashlib.sha256(left[:,:,3].tobytes()).hexdigest() == '3cd09a6bf08c65712e97e75cf37b4fdb21ed838cd915ea04a8766f9ce7f91528', 'Approved silhouette changed'
    assert left.shape[0] <= 23, 'New wing must be visibly slimmer than the old 37px-tall outline'
    assert np.array_equal(left[:,::-1],np.array(Image.open(OUT/'canonical-right.png')))
    count=0;roots=0;distinct={}
    palette={tuple(v) for v in art.PALETTE.tolist()}
    for state,spec in meta['states'].items():
        for view in art.VIEWS:
            hashes=set()
            for k in range(spec['frames_per_direction']):
                stem=f'{state.lower()}-{view}-{k+1:02d}.png'
                ims=[Image.open(OUT/state/view/layer/stem) for layer in ('frames','behind','in_front')]
                a,u,o=map(np.array,ims)
                assert all(im.size==(80,80) and im.mode=='RGBA' for im in ims)
                assert set(np.unique(a[:,:,3]))=={0,255}
                assert {tuple(v) for v in a[a[:,:,3]>0,:3]}<=palette
                assert not np.any((u[:,:,3]>0)&(o[:,:,3]>0)),(state,view,k,'double-drawn pixels')
                assert np.array_equal(np.array(Image.alpha_composite(ims[1],ims[2])),a)
                assert not a[[0,-1],:,3].any() and not a[:,[0,-1],3].any(),(state,view,k,'clipped tip')
                if view in ('front','back'):
                    assert np.array_equal(a,a[:,::-1]) and np.array_equal(o,o[:,::-1]),(state,view,k,'asymmetry')
                if view=='front':assert not o[:,:,3].any(),'front wing obscures face/body'
                else:
                    # No foreground pixels are permitted in head/hand/leg regions.
                    yy,xx=np.indices((80,80))
                    allowed=(((xx>=35)&(xx<=38))|((xx>=41)&(xx<=44)))&(yy>=41)&(yy<=44) if view=='back' else \
                        ((xx>=37)&(xx<=39) if view=='right' else (xx>=40)&(xx<=42))&(yy>=41)&(yy<=44)
                    assert not o[~allowed,3].any(),(state,view,k,'foreground leaks out of shoulder area')
                    assert o[:,:,3].any(),(state,view,k,'missing root')
                    labels,counts=components(a[:,:,3]>0,diagonal=True)
                    for label in np.unique(labels[o[:,:,3]>0]):
                        if label==0:continue
                        y,x=np.where(labels==label)
                        # A folded side wing is upright; horizontal extent alone
                        # misidentifies a connected vertical wing as a loose root.
                        assert np.hypot(x.max()-x.min(),y.max()-y.min())>=10,(state,view,k,'detached root')
                        roots+=1
                if view=='left':
                    r=np.array(Image.open(OUT/state/'right'/'frames'/f'{state.lower()}-right-{k+1:02d}.png'))
                    assert np.array_equal(a,r[:,::-1]),'side views must mirror'
                hashes.add(a.tobytes());count+=1
            distinct[f'{state}/{view}']=len(hashes)
            assert len(hashes)>=min(8,spec['frames_per_direction']),'animation frozen'
    template=Image.open(OUT/'canonical-left.png');root=np.array(meta['canonical_root'])
    for view in art.VIEWS:
        t=art.export_frame(template,root,'T',view,1)[0]
        h=art.export_frame(template,root,'H',view,.55)[0]
        assert np.array_equal(np.array(t),np.array(h)),'takeoff join changed shape'
        l=art.export_frame(template,root,'L',view,0)[0]
        h=art.export_frame(template,root,'H',view,.15)[0]
        assert np.array_equal(np.array(l),np.array(h)),'landing join changed shape'
    assert count==272
    angles=[art.pose_parameters('H',p/100)[1] for p in range(100)]
    assert np.degrees(max(angles)-min(angles)) >= 95, 'Hover should follow the large skeleton shoulder sweep'
    result={'combined_frames':count,'layer_frames':count*2,'root_components_checked':roots,
        'approved_source_unchanged':True,'binary_alpha':True,'mirrored_front_back_and_sides':True,
        'foreground_confined_to_shoulder':True,'disjoint_layers':True,'no_edge_clipping':True,
        'transition_endpoints_match':True,'distinct_frames':distinct,'in_game_verified':False}
    (OUT/'qa-summary.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps(result))


if __name__=='__main__':main()
