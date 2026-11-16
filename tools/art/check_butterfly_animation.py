"""Regress the actual butterfly exporter, without touching game state."""
from __future__ import annotations

import json
import math
import runpy
from pathlib import Path
from PIL import Image, ImageChops

ROOT = Path(__file__).resolve().parents[2]
ART = runpy.run_path(str(ROOT / "tools/art/build_butterfly_wing_animation.py"),
                     run_name="butterfly_checks")


def check_fixed_hinges() -> int:
    checks = 0
    for state in ("H", "M", "Q", "T", "L"):
        count = 10 if state in ("T", "L") else 16
        for view in ART["VIEWS"]:
            root = (38, 42) if view == "right" else (42, 42) if view == "left" else (40, 42)
            for side in (-1, 1):
                far = view in ("right", "left") and side == (-1 if view == "right" else 1)
                anchors = []
                for i in range(count):
                    motion = ART["motion_for"](state, i)
                    lobes = [ART["transform"]((0, 0), root=root, side=side,
                             spread=motion.upper if lobe == "upper" else motion.lower,
                             lobe=lobe, view=view, far=far, motion=motion)
                             for lobe in ("upper", "lower")]
                    assert lobes[0] == lobes[1], ("split hinge", state, view, side, i, lobes)
                    anchors.append(lobes[0])
                    checks += 1
                assert len(set(anchors)) == 1, ("drifting hinge", state, view, side, anchors)
                checks += 1
    return checks


def check_spatial_projection() -> int:
    checks = 0
    for side in (-1, 1):
        for lobe, tip in (("upper", (36, -35)), ("lower", (13, 35))):
            lengths = []
            for i in range(128):
                motion = ART["cycle_motion"](i / 128)
                root = ART["world_point"]((0, 0), side=side, lobe=lobe, motion=motion)
                point = ART["world_point"](tip, side=side, lobe=lobe, motion=motion)
                lengths.append(math.dist(root, point))
                checks += 1
            assert max(lengths) - min(lengths) < 1e-9, ("membrane changes size", side, lobe)
            checks += 1
    for view in ART["VIEWS"]:
        for i in range(16):
            motion = ART["motion_for"]("H", i)
            for side in (-1, 1):
                kwargs = dict(root=(40, 42), side=side, spread=motion.upper,
                              lobe="upper", view=view, motion=motion)
                assert ART["transform"]((36, -35), far=False, **kwargs) == ART["transform"]((36, -35), far=True, **kwargs), "fake far-wing shrink"
                checks += 1
    # A single camera must see substantially different areas across the stroke.
    profiles = []
    for i in range(16):
        wing = Image.new("RGBA", (80, 80))
        ART["paint_half"](wing, root=(38, 42), side=1, view="right",
                          motion=ART["motion_for"]("H", i), far=False)
        profiles.append(sum(v > 0 for v in wing.getchannel("A").get_flattened_data()))
    assert max(profiles) > 5 * min(profiles), ("side stays a squeezed strip", profiles)
    assert ART["motion_for"]("H", 10).stroke < 0 < ART["motion_for"]("H", 0).stroke, "no signed follow-through"
    start, end = ART["cycle_motion"](0), ART["cycle_motion"](1)
    assert start == end, "loop seam"
    checks += 3
    return checks


def connected_components(mask: Image.Image) -> int:
    pixels = {(x, y) for y in range(80) for x in range(80) if mask.getpixel((x, y))}
    count = 0
    while pixels:
        count += 1
        todo = [pixels.pop()]
        while todo:
            x, y = todo.pop()
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    neighbour = (x + dx, y + dy)
                    if neighbour in pixels:
                        pixels.remove(neighbour)
                        todo.append(neighbour)
    return count


def check_body_occlusion() -> tuple[int, dict]:
    """A camera-near membrane may not be clipped back behind the farmer.

    Exercise the real exporter, not a separate illustrative wing. The whole
    near membrane stays in front of body/hair/arms, without a torso/depth crop.
    """
    checks, report = 0, {"side_forward_membrane_pixels": {}, "back_basal_pixels": {}}
    for view, near_side, root in (("right", 1, (38, 42)), ("left", -1, (42, 42))):
        motion = ART["motion_for"]("H", 10)
        projected = ART["camera_point"](ART["world_point"]((36,-35),side=near_side,lobe="upper",motion=motion),view)
        advance = projected[0] * (1 if view == "right" else -1)
        assert 1.0 <= advance <= 2.5, ("forward stroke must be restrained, about one-fifth body width", view, advance)
        report.setdefault("forward_tip_advance_pixels",{})[view] = round(advance,3)
        checks += 1
        for i in (8, 9, 10):
            motion = ART["motion_for"]("H", i)
            half = Image.new("RGBA", (80, 80))
            ART["paint_half"](half, root=root, side=near_side, view=view, motion=motion, far=False)
            _, front = ART["make_frame"](view, motion)
            a, b, c = ART["plane_depth"](root, near_side, "upper", view, motion)
            # Above the old torso-only rectangle: this is genuine near forewing,
            # not the decorative root overlay. Include only safely deep pixels.
            expected = {(x, y) for y in range(28, 41) for x in range(root[0]-2, root[0]+5)
                        if half.getpixel((x,y))[3] and a*x+b*y+c > 3.0}
            missing = [p for p in expected if not front.getpixel(p)[3]]
            assert expected, ("fixture misses forward near membrane", view, i)
            assert not missing, ("camera-near wing vanishes behind body", view, i+1, missing)
            report["side_forward_membrane_pixels"][f"{view}/H{i+1:02d}"] = len(expected)
            checks += 2
        # No holes cut through the camera-near wing at the body/root seam.
        # The opposite wing is behind the farmer; the entire near membrane is
        # in front. Fine per-pixel depth clipping at a 2px root was incorrect.
        for state in ("H","M","Q","T","L"):
            count = 10 if state in ("T","L") else 16
            for index in range(count):
                motion = ART["motion_for"](state,index)
                half = Image.new("RGBA",(80,80))
                ART["paint_half"](half,root=root,side=near_side,view=view,motion=motion,far=False)
                behind, front = ART["make_frame"](view,motion)
                missing = ImageChops.subtract(half.getchannel("A"),front.getchannel("A"))
                assert not missing.getbbox(), ("near wing is sliced through body",state,view,index+1,missing.getbbox())
                checks += 1
    # Back attachment must expose actual membrane at the hinge, rather than
    # relying on a detached V-shaped mark painted over a hidden wing base.
    for i in range(16):
        motion = ART["motion_for"]("H", i)
        _, front = ART["make_frame"]("back", motion)
        for side in (-1, 1):
            half = Image.new("RGBA", (80,80))
            ART["paint_half"](half, root=(40,42), side=side, view="back", motion=motion, far=False)
            basal = {(x,y) for y in range(40,47) for x in range(34,47)
                     if half.getpixel((x,y))[3] and (x-40)**2+(y-42)**2 <= 36}
            visible = [p for p in basal if front.getpixel(p) == half.getpixel(p)]
            assert len(visible) >= 6, ("back wing base covered by body", i+1, side, len(visible))
            report["back_basal_pixels"][f"H{i+1:02d}/{side}"] = len(visible)
            checks += 1
    return checks, report


def check_rendered_assets() -> tuple[int, dict]:
    base = ROOT / "assets/butterfly-wings/v1"
    checks = 0
    qa = {"revision": ART["DESIGN_REVISION"], "hinge_drift_pixels": 0,
          "fore_hind_hinge_separation_pixels": 0, "runtime_frame_contract": "80x80, RGBA, binary alpha",
          "airborne_heights": {}, "side_near_surface_area_range": {}, "all_frames_connected": True}
    for state in ("H", "M", "Q", "T", "L"):
        count = 10 if state in ("T", "L") else 16
        for view in ART["VIEWS"]:
            heights = []
            for i in range(count):
                motion = ART["motion_for"](state, i)
                behind, front = ART["make_frame"](view, motion)
                assert not ImageChops.multiply(behind.getchannel("A"), front.getchannel("A")).getbbox(), ("layers overlap", state, view, i)
                for layer, image in (("behind", behind), ("in_front", front)):
                    file = base / "frames" / state / view / layer / f"{state.lower()}-{view}-{i+1:02d}.png"
                    stored = Image.open(file).convert("RGBA")
                    assert stored.tobytes() == image.tobytes(), ("stale frame", file)
                    atlas = Image.open(base / "atlases" / state / view / f"{layer}.png").convert("RGBA")
                    assert atlas.crop((i*80, 0, (i+1)*80, 80)).tobytes() == image.tobytes(), ("stale atlas", file)
                    assert set(image.getchannel("A").get_flattened_data()) <= {0, 255}
                    checks += 3
                box = Image.alpha_composite(behind, front).getbbox()
                heights.append(box[3] - box[1])
                root = (38, 42) if view == "right" else (42, 42) if view == "left" else (40, 42)
                for side in (-1, 1):
                    half = Image.new("RGBA", (80, 80))
                    ART["paint_half"](half, root=root, side=side, view=view, motion=motion, far=False)
                    assert connected_components(half.getchannel("A")) == 1, ("detached lobe", state, view, i, side)
                    for lobe in ("upper", "lower"):
                        shape = ART["UPPER_OUTER"] if lobe == "upper" else ART["LOWER_OUTER"]
                        projected = ART["points"](shape, root=root, side=side,
                            spread=motion.upper if lobe == "upper" else motion.lower,
                            lobe=lobe, view=view, far=False, motion=motion)
                        assert all(0 < x < 79 and 0 < y < 79 for x, y in projected), ("clipped contour", state, view, i, side)
                        checks += 1
                    checks += 1
                if state in ("H", "M", "Q"):
                    assert 28 <= heights[-1] <= 39, ("not character scale", state, view, i, heights[-1])
                    checks += 1
            if state in ("H", "M", "Q"):
                qa["airborne_heights"][f"{state}/{view}"] = [min(heights), max(heights)]
    for view in ART["VIEWS"]:
        for layer in ART["LAYERS"]:
            def frame(state, index):
                atlas = Image.open(base / "atlases" / state / view / f"{layer}.png").convert("RGBA")
                return atlas.crop((80*index, 0, 80*(index+1), 80)).tobytes()
            assert frame("T", 9) == frame("H", 8), ("takeoff seam", view, layer)
            assert frame("L", 0) == frame("H", 2), ("landing seam", view, layer)
            checks += 2
    for view in ("right", "left"):
        areas = []
        for i in range(16):
            half = Image.new("RGBA", (80, 80))
            ART["paint_half"](half, root=(40, 42), side=1 if view == "right" else -1,
                              view=view, motion=ART["motion_for"]("H", i), far=False)
            areas.append(sum(v > 0 for v in half.getchannel("A").get_flattened_data()))
        qa["side_near_surface_area_range"][view] = [min(areas), max(areas)]
    for state, period in (("H",960), ("M",840), ("Q",1200), ("T",700), ("L",700)):
        for view in ART["VIEWS"]:
            gif = Image.open(base / "previews" / state / f"{state.lower()}-{view}.gif")
            total = 0
            for i in range(gif.n_frames):
                gif.seek(i)
                total += gif.info["duration"]
            assert total == period, ("GIF playback differs from runtime", state, view, total, period)
            if state in ("T", "L"):
                assert "loop" not in gif.info, ("transition GIF must not replay backward", state, view)
            checks += 1
    return checks, qa


if __name__ == "__main__":
    count = check_fixed_hinges() + check_spatial_projection()
    occlusion_checks, occlusion = check_body_occlusion()
    count += occlusion_checks
    rendered, report = check_rendered_assets()
    count += rendered
    report["body_occlusion"] = occlusion
    report["regression_checks"] = count
    report["status"] = "passed"
    (ROOT / "assets/butterfly-wings/v1/perspective-qa.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))
