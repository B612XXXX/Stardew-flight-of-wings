"""Rebuild the current butterfly design with a fixed-hinge 3D sprite rig.

All outputs stay in assets/butterfly-wings/v1; packing and installation are
separate steps. The reference contours and palette are retained. Wings rotate
in space before orthographic projection, rather than stretching each lobe.
"""

from __future__ import annotations

import hashlib
import csv
import json
import math
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw


ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "assets" / "butterfly-wings" / "v1"
SIZE = 80
VIEWS = ("back", "right", "front", "left")
LAYERS = ("behind", "in_front")
# A Stardew farmer is 16x32 logical pixels.  The broad airborne silhouette is
# intentionally about 32px high (rather than the previous 66px prototype), so
# the wing pair reads at roughly 1:1 character height once the game's 4x draw
# scale is applied.  Width stays a little larger for a butterfly silhouette.
SILHOUETTE_SCALE_X = 0.62
SILHOUETTE_SCALE_Y = 0.45
# The size is calibrated once, not independently compensated on every frame.
TARGET_BROAD_HEIGHT = 32
DESIGN_REVISION = "occlusion-r9"
CAMERA_ELEVATION = math.radians(8.0)
SIDE_CAMERA_AZIMUTH = math.radians(12.0)
HINGE_HALF_SPACING = 0.65
FORWARD_STROKE = -16.5

# A deliberately compact palette so the markings still read at the game's
# native scale.  Every rendered frame uses binary alpha only.
COLORS = {
    "outline": "#442440",       # dark plum
    "outline_light": "#70405b",
    "orange_shadow": "#ca5a18",
    "orange": "#f58a1c",
    "orange_light": "#ffad2e",
    "gold": "#ffd66b",
    "gold_light": "#fff0a6",
    "blue": "#62a7cb",
    "blue_shadow": "#3f7da8",
    "lavender": "#bbb5e4",
    "lavender_light": "#e4e1ff",
    "lavender_shadow": "#8b83bd",
}


@dataclass(frozen=True)
class Motion:
    upper: float
    lower: float
    phase: float
    moving: float
    view_shift: float
    dock: float = 0.0
    stroke: float = 0.0
    hind_stroke: float = 0.0
    pitch: float = 0.0


def smoothstep(value: float) -> float:
    value = max(0.0, min(1.0, value))
    return value * value * (3.0 - 2.0 * value)


def lerp(a: float, b: float, amount: float) -> float:
    return a + (b - a) * amount


def hermite(a: float, b: float, start_speed: float, end_speed: float,
            t: float, duration: float) -> float:
    return ((2 * t**3 - 3 * t**2 + 1) * a + (t**3 - 2 * t**2 + t) * start_speed * duration
            + (-2 * t**3 + 3 * t**2) * b + (t**3 - t**2) * end_speed * duration)


def stroke_angle(phase: float) -> float:
    """Signed stroke, in degrees: gathered -> open -> follow-through -> gathered.

    H09 crosses the reference plane; H11 is the lower reversal. Thus the broad
    silhouette is not a pause at the end of a scale-in/scale-out loop.
    Endpoints and piecewise derivatives agree, including across phase 1/0.
    """
    phase %= 1.0
    if phase <= 0.5:
        return hermite(72.0, 0.0, 0.0, -96.0, phase / 0.5, 0.5)
    if phase <= 0.625:
        return hermite(0.0, FORWARD_STROKE, -96.0, 0.0, (phase - 0.5) / 0.125, 0.125)
    return hermite(FORWARD_STROKE, 72.0, 0.0, 0.0, (phase - 0.625) / 0.375, 0.375)


def motion_at_angle(stroke: float, phase: float = 0.0, moving: float = 0.0,
                    dock: float = 0.0, hind_stroke: float | None = None,
                    pitch: float = 0.0) -> Motion:
    hind_stroke = stroke if hind_stroke is None else hind_stroke
    return Motion(upper=1.0 - max(0.0, stroke) / 72.0,
                  lower=1.0 - max(0.0, hind_stroke) / 72.0,
                  phase=phase, moving=moving, view_shift=0.0, dock=dock,
                  stroke=stroke, hind_stroke=hind_stroke, pitch=pitch)


def cycle_motion(phase: float, *, amplitude: float = 1.0, moving: float = 0.0) -> Motion:
    phase %= 1.0
    stroke = lerp(20.0, stroke_angle(phase), amplitude)
    # A small angular flex preserves the shared hinge and the continuous wing
    # outline; it is not the old independent hindwing shrink/translation.
    hind = stroke + 1.5 * math.sin(2 * math.pi * phase) * amplitude
    pitch = 2.8 * math.sin(2 * math.pi * phase) + moving * 1.5
    return motion_at_angle(stroke, phase, moving, hind_stroke=hind, pitch=pitch)


def motion_for(state: str, index: int) -> Motion:
    if state == "H":
        return cycle_motion(index / 16)
    if state == "M":
        return cycle_motion(index / 16, amplitude=1.0, moving=1.0)
    if state == "Q":
        return cycle_motion(index / 16, amplitude=0.58)
    if state == "T":
        # The final take-off frame deliberately matches H09 (phase .5): the
        # runtime switches to H at phase .55, whose first displayed frame is H09.
        t = index / 9
        base = cycle_motion(0.5 * smoothstep(t))
        # Frame one doubles as the doorway fold texture, so it packs the
        # otherwise upright butterfly membranes tightly against the shoulder.
        return motion_at_angle(base.stroke, base.phase, dock=1.0 - smoothstep(t / 0.36),
                               hind_stroke=base.hind_stroke, pitch=base.pitch)
    if state == "L":
        # Landing begins on H03 (phase .125) in the current runtime.  It then
        # gathers straight upward rather than completing another downstroke.
        start = cycle_motion(2 / 16)
        t = smoothstep(index / 9)
        return motion_at_angle(lerp(start.stroke, 72.0, t), lerp(2 / 16, 1.0, t),
                               dock=smoothstep((t - 0.60) / 0.40),
                               hind_stroke=lerp(start.hind_stroke, 72.0, t),
                               pitch=start.pitch * (1.0 - t))
    raise ValueError(f"Unknown state: {state}")


# The reference's pointed forewing has a broad trailing edge; the rounded
# hindwing reaches upward under it.  Their membranes overlap across the inner
# half of the span.  Coordinates below are the two actual wing surfaces: no
# connecting third lobe is drawn between them.
UPPER_OUTER = ((0, 0), (4, -10), (10, -20), (18, -28), (28, -33),
               (36, -35), (40, -33), (39, -28), (37, -18), (34, -10),
               (29, 3), (22, 7), (14, 7), (7, 5))
UPPER_INNER = ((1, 0), (6, -10), (12, -19), (21, -26), (29, -30),
               (36, -32), (37, -26), (34, -16), (30, -6), (27, 2),
               (20, 5), (13, 5), (6, 3))
UPPER_LAVENDER = ((13, -21), (22, -29), (33, -33), (38, -32),
                  (37, -21), (34, -11), (29, -3), (23, 1), (18, -2))
UPPER_GOLD = ((2, -1), (8, -10), (14, -14), (21, -9),
              (25, -2), (22, 4), (14, 5), (7, 3))
UPPER_BLUE = ((14, -19), (21, -26), (26, -23), (25, -16), (20, -10))

# The hindwing is a descending fan, narrower than the forewing.  Its broad
# upper shoulder turns through three shallow rounded scallops into a lower
# inner tip.  This replaces the flat, wide polygon of the previous revision.
LOWER_OUTER = ((0, 0), (8, -4), (17, -3), (24, 0), (29, 4),
               (32, 8), (33, 12), (32, 16), (30, 18), (30, 21),
               (28, 25), (25, 26), (24, 30), (21, 33), (17, 34),
               (15, 33), (13, 35), (10, 34), (7, 30), (4, 24), (2, 16))
LOWER_INNER = ((1, 1), (9, -2), (17, -1), (23, 2), (28, 6),
               (30, 10), (30, 15), (27, 18), (27, 21), (24, 25),
               (21, 29), (17, 31), (13, 32), (9, 29), (6, 23), (4, 16))
LOWER_LAVENDER = ((28, 11), (31, 12), (30, 17), (27, 19),
                  (27, 22), (24, 26), (21, 31), (17, 32), (13, 33),
                  (10, 30), (8, 25), (13, 27), (19, 25), (24, 20))
LOWER_GOLD = ((2, 1), (10, 0), (18, 3), (23, 7), (24, 11),
              (20, 14), (16, 11), (12, 8), (10, 18), (7, 21),
              (5, 18), (4, 12))
LOWER_BLUE = ((7, 7), (13, 7), (20, 8), (26, 11), (28, 15),
              (26, 19), (22, 20), (18, 25), (14, 28), (10, 27),
              (7, 21), (6, 16))
LOWER_ORANGE = ((1, 0), (7, 1), (14, 4), (19, 9), (18, 12),
                (15, 12), (11, 8), (8, 7), (7, 17), (5, 18), (3, 12))


def world_point(point: tuple[float, float], *, side: int, lobe: str,
                motion: Motion) -> tuple[float, float, float]:
    """The same rigid reference membrane for every camera, in body space.

    X is lateral, Y is rearward, Z is upward. Span, chord and normal form an
    orthonormal basis. Lobe roots are never translated; scale is constant in
    airborne poses. Dock only is a deliberate compact doorway replacement.
    """
    stroke = motion.stroke if lobe == "upper" else motion.hind_stroke
    yaw = math.radians(stroke)
    elevation = math.radians(stroke * 0.085)
    pitch = math.radians(motion.pitch)
    cy, sy = math.cos(yaw), math.sin(yaw)
    ce, se = math.cos(elevation), math.sin(elevation)
    span = (side * cy * ce, sy * ce, se)
    chord = (side * cy * se, sy * se, -ce)
    normal = (-side * sy, cy, 0.0)
    chord = tuple(chord[k] * math.cos(pitch) + normal[k] * math.sin(pitch) for k in range(3))
    u = point[0] * SILHOUETTE_SCALE_X * lerp(1.0, 0.30, motion.dock)
    v = point[1] * SILHOUETTE_SCALE_Y / math.cos(CAMERA_ELEVATION) * lerp(1.0, 0.16, motion.dock)
    return (side * HINGE_HALF_SPACING + u * span[0] + v * chord[0],
            u * span[1] + v * chord[1], u * span[2] + v * chord[2])


def camera_point(point: tuple[float, float, float], view: str) -> tuple[float, float, float]:
    """Orthographic screen coordinates and toward-camera depth, without rounding."""
    x, y, z = point
    s, c = math.sin(CAMERA_ELEVATION), math.cos(CAMERA_ELEVATION)
    if view == "front":
        return x, -y * s - z * c, -y * c + z * s
    if view == "back":
        return -x, y * s - z * c, y * c + z * s
    if view == "right":
        a, b = math.sin(SIDE_CAMERA_AZIMUTH), math.cos(SIDE_CAMERA_AZIMUTH)
        return -x * a - y * b, (x * b - y * a) * s - z * c, (x * b - y * a) * c + z * s
    a, b = math.sin(SIDE_CAMERA_AZIMUTH), math.cos(SIDE_CAMERA_AZIMUTH)
    return -x * a + y * b, (-x * b - y * a) * s - z * c, (-x * b - y * a) * c + z * s


def transform(point: tuple[float, float], *, root: tuple[float, float], side: int,
              spread: float, lobe: str, view: str, far: bool, motion: Motion) -> tuple[int, int]:
    # spread and far remain paint API arguments, but no longer shrink geometry.
    x, y, _ = camera_point(world_point(point, side=side, lobe=lobe, motion=motion), view)
    return round(root[0] + x), round(root[1] + y)


def points(shape: tuple[tuple[float, float], ...], *, root: tuple[float, float], side: int,
           spread: float, lobe: str, view: str, far: bool, motion: Motion) -> list[tuple[int, int]]:
    return [transform(p, root=root, side=side, spread=spread, lobe=lobe, view=view, far=far, motion=motion)
            for p in shape]


def paint_lobe(draw: ImageDraw.ImageDraw, *, root: tuple[float, float], side: int,
               spread: float, lobe: str, view: str, far: bool, motion: Motion, shade: bool) -> None:
    if lobe == "upper":
        outer, inner, lavender, gold, blue = (UPPER_OUTER, UPPER_INNER, UPPER_LAVENDER,
                                               UPPER_GOLD, UPPER_BLUE)
    else:
        outer, inner, lavender, gold, blue = (LOWER_OUTER, LOWER_INNER, LOWER_LAVENDER,
                                               LOWER_GOLD, LOWER_BLUE)

    def shape(data: tuple[tuple[float, float], ...]) -> list[tuple[int, int]]:
        return points(data, root=root, side=side, spread=spread, lobe=lobe, view=view, far=far, motion=motion)

    draw.polygon(shape(outer), fill=COLORS["lavender_shadow"] if shade else COLORS["lavender"])
    draw.polygon(shape(inner), fill=(COLORS["gold"] if lobe == "lower" else
                                     COLORS["orange_shadow"] if shade else COLORS["orange"]))
    draw.polygon(shape(lavender), fill=COLORS["lavender_shadow"] if shade else COLORS["lavender"])
    draw.polygon(shape(blue), fill=COLORS["blue_shadow"] if shade else COLORS["blue"])
    draw.polygon(shape(gold), fill=COLORS["gold"])
    if lobe == "lower":
        draw.polygon(shape(LOWER_ORANGE), fill=COLORS["orange_shadow"] if shade else COLORS["orange"])

    # Butter-gold veins fan from the fixed root.  They curve visually because
    # their endpoints follow the membrane, not because the whole wing sweeps.
    vein_targets = outer[2:-1:3] if lobe == "upper" else ((30, 12), (24, 28), (13, 33))
    root_p = transform((1.5, 0), root=root, side=side, spread=spread, lobe=lobe,
                       view=view, far=far, motion=motion)
    for target in vein_targets:
        end = transform(target, root=root, side=side, spread=spread, lobe=lobe,
                        view=view, far=far, motion=motion)
        draw.line((root_p, end), fill=COLORS["gold_light"], width=1)

    # A small waterdrop highlight is more legible than dense texture at 80px.
    droplet = transform((23, -18) if lobe == "upper" else (25, 14), root=root, side=side,
                        spread=spread, lobe=lobe, view=view, far=far, motion=motion)
    radius = 0 if spread < 0.35 or lobe == "lower" else 1
    draw.ellipse((droplet[0] - radius, droplet[1] - radius, droplet[0] + radius,
                  droplet[1] + radius), fill=COLORS["gold_light"])


def paint_half(canvas: Image.Image, *, root: tuple[float, float], side: int, view: str,
               motion: Motion, far: bool) -> None:
    membrane = Image.new("RGBA", (SIZE, SIZE))
    draw = ImageDraw.Draw(membrane)
    # Paint both membranes as one silhouette.  An outline around each lobe
    # would put a dark seam between them and make four disconnected leaves.
    paint_lobe(draw, root=root, side=side, spread=motion.lower, lobe="lower", view=view,
               far=far, motion=motion, shade=far)
    paint_lobe(draw, root=root, side=side, spread=motion.upper, lobe="upper", view=view,
               far=far, motion=motion, shade=far)

    silhouette = Image.new("L", (SIZE, SIZE))
    mask_draw = ImageDraw.Draw(silhouette)
    for lobe, spread, outer in (("lower", motion.lower, LOWER_OUTER),
                                ("upper", motion.upper, UPPER_OUTER)):
        mask_draw.polygon(points(outer, root=root, side=side, spread=spread, lobe=lobe,
                                 view=view, far=far, motion=motion), fill=255)
    # A four-neighbour rim keeps diagonal pixel edges one logical pixel wide.
    # Eight-neighbour erosion made the small rounded hindwing look heavy.
    inset = silhouette.copy()
    for offset in ((-1, 0), (1, 0), (0, -1), (0, 1)):
        shifted = Image.new("L", (SIZE, SIZE))
        shifted.paste(silhouette, offset)
        inset = ImageChops.darker(inset, shifted)
    outline = ImageChops.subtract(silhouette, inset)
    membrane.putalpha(silhouette)
    membrane.paste(COLORS["outline"], (0, 0, SIZE, SIZE), outline)
    canvas.alpha_composite(membrane)


def root_cover(view: str, motion: Motion) -> Image.Image:
    """Tiny fixed hinge caps, never an extra membrane or a floating V mark."""

    image = Image.new("RGBA", (SIZE, SIZE))
    draw = ImageDraw.Draw(image)
    if view == "back":
        for x in (39, 41):
            draw.line(((x, 42), (x, 44)), fill=COLORS["outline"], width=1)
            draw.point((x, 43), fill=COLORS["gold"])
    elif view in ("right", "left"):
        x = 38 if view == "right" else 42
        draw.rectangle((x-1, 41, x+1, 44), fill=COLORS["outline"])
        draw.line(((x, 42), (x, 43)), fill=COLORS["gold"], width=1)
    else:
        # Front view intentionally leaves only a narrow central clasp so arms
        # and held tools remain unobscured.
        draw.rectangle((39, 41, 40, 44), fill=COLORS["outline"])
        draw.point((39, 42), fill=COLORS["gold"])
        draw.point((40, 42), fill=COLORS["gold"])
    return image


def plane_depth(root: tuple[float, float], side: int, lobe: str, view: str,
                motion: Motion) -> tuple[float, float, float]:
    """Affine depth field of the projected wing plane: depth = a*x+b*y+c."""
    origin = camera_point(world_point((0, 0), side=side, lobe=lobe, motion=motion), view)
    span = camera_point(world_point((1, 0), side=side, lobe=lobe, motion=motion), view)
    chord = camera_point(world_point((0, 1), side=side, lobe=lobe, motion=motion), view)
    ux, uy, ud = (span[k] - origin[k] for k in range(3))
    vx, vy, vd = (chord[k] - origin[k] for k in range(3))
    determinant = ux * vy - uy * vx
    if abs(determinant) < 1e-6:
        # An edge-on membrane has no invertible screen area. Its one-pixel rim
        # still needs deterministic wing/wing sorting, using the span midpoint.
        return 0.0, 0.0, origin[2] + ud * 15.0
    a = (ud * vy - vd * uy) / determinant
    b = (vd * ux - ud * vx) / determinant
    return a, b, origin[2] - a * (root[0] + origin[0]) - b * (root[1] + origin[1])


def make_frame(view: str, motion: Motion) -> tuple[Image.Image, Image.Image]:
    behind = Image.new("RGBA", (SIZE, SIZE))
    front = Image.new("RGBA", (SIZE, SIZE))
    root = (38, 42) if view == "right" else (42, 42) if view == "left" else (40, 42)
    side_view = view in ("right", "left")
    nearest = 1 if view == "right" else -1
    if side_view:
        # The near wing is a single opaque surface between camera and farmer.
        # Splitting it at a numerical root-depth threshold made the player
        # appear to pass through its membrane. Far wing/body/near wing are
        # three physical objects, not arbitrary patches of the same object.
        paint_half(behind,root=root,side=-nearest,view=view,motion=motion,far=True)
        paint_half(front,root=root,side=nearest,view=view,motion=motion,far=False)
        front.alpha_composite(root_cover(view,motion))
        behind.putalpha(ImageChops.subtract(behind.getchannel("A"),front.getchannel("A")))
        return behind, front
    halves = []
    for side in (-1, 1):
        wing = Image.new("RGBA", (SIZE, SIZE))
        far = side_view and side != nearest
        paint_half(wing, root=root, side=side, view=view, motion=motion, far=far)
        upper_mask = Image.new("L", (SIZE, SIZE))
        ImageDraw.Draw(upper_mask).polygon(points(UPPER_OUTER, root=root, side=side,
            spread=motion.upper, lobe="upper", view=view, far=far, motion=motion), fill=255)
        halves.append((wing.load(), upper_mask.load(),
                       plane_depth(root, side, "upper", view, motion),
                       plane_depth(root, side, "lower", view, motion)))

    b, f = behind.load(), front.load()
    for y in range(SIZE):
        for x in range(SIZE):
            winner = None
            nearest_depth = -math.inf
            for pixels, upper, fore_plane, hind_plane in halves:
                if not pixels[x, y][3]:
                    continue
                a, d, c = fore_plane if upper[x, y] else hind_plane
                depth = a * x + d * y + c
                if depth > nearest_depth:
                    winner, nearest_depth = pixels[x, y], depth
            if winner is None:
                continue
            # Classify the whole camera-near membrane. A torso-only rectangle
            # incorrectly puts its upper/lower parts behind the same body.
            # On the back expose the *existing* basal membrane, not a third
            # bridge lobe. Its foreground patch stays attached to both hinges.
            back_base = (view == "back" and 40 <= y <= 47 and
                         (x-root[0])**2 + (y-root[1])**2 <= 36)
            if back_base:
                f[x, y] = winner
            else:
                b[x, y] = winner

    front.alpha_composite(root_cover(view, motion))
    # Make the two draw layers disjoint, avoiding a darker double-blended root
    # during the runtime's summon/landing opacity fades.
    b = behind.load()
    f = front.load()
    for y in range(SIZE):
        for x in range(SIZE):
            if f[x, y][3]:
                b[x, y] = (0, 0, 0, 0)
    return behind, front


def alpha_values(image: Image.Image) -> list[int]:
    return sorted(set(image.getchannel("A").get_flattened_data()))


def bounds(image: Image.Image) -> list[int]:
    box = image.getbbox()
    if box is None:
        raise AssertionError("Wing frame is empty")
    return list(box)


def validate_wing_anatomy() -> dict[str, object]:
    """Guard the two visual constraints that a generic image check misses.

    Evaluate the actual projected membranes, not just non-empty file contracts.
    In orthographic projection a far wing is not intrinsically smaller.
    """

    broad = motion_for("H", 8)
    results: dict[str, object] = {}
    for view in VIEWS:
        root = (38, 42) if view == "right" else (42, 42) if view == "left" else (40, 42)
        near_side = -1 if view in ("back", "left") else 1
        wing_layers: dict[bool, list[Image.Image]] = {}
        for far in (False, True):
            lobes: list[Image.Image] = []
            for lobe, spread in (("upper", broad.upper), ("lower", broad.lower)):
                layer = Image.new("RGBA", (SIZE, SIZE))
                paint_lobe(ImageDraw.Draw(layer), root=root, side=near_side * (-1 if far else 1), spread=spread, lobe=lobe,
                           view=view, far=far, motion=broad, shade=far)
                lobes.append(layer)
            wing_layers[far] = lobes

        near_upper, near_lower = wing_layers[False]
        overlap = sum(
            bool(upper) and bool(lower)
            for upper, lower in zip(near_upper.getchannel("A").get_flattened_data(),
                                    near_lower.getchannel("A").get_flattened_data())
        )
        minimum_overlap = 50 if view in ("back", "front") else 2
        if overlap < minimum_overlap:
            raise AssertionError(f"{view}: detached fore/hindwings ({overlap} < {minimum_overlap})")

        # The shared area must reach past the shoulder.  A bridge confined to
        # the root would still look like separate lobes after the body masks it.
        if view in ("back", "front"):
            upper_alpha = near_upper.getchannel("A").load()
            lower_alpha = near_lower.getchannel("A").load()
            visible_overlap = sum(
                bool(upper_alpha[x, y] and lower_alpha[x, y])
                for y in range(SIZE) for x in range(root[0] + 6, SIZE)
            )
            if visible_overlap < 25:
                raise AssertionError(f"{view}: fore/hind overlap hidden by player body")
        else:
            visible_overlap = None

        half_counts: dict[str, int] = {}
        half_boxes: dict[str, list[int]] = {}
        for label, far in (("near", False), ("far", True)):
            half = Image.new("RGBA", (SIZE, SIZE))
            paint_half(half, root=root, side=near_side * (-1 if far else 1), view=view, motion=broad, far=far)
            half_counts[label] = sum(
                value == 255 for value in half.getchannel("A").get_flattened_data()
            )
            half_boxes[label] = bounds(half)

        results[view] = {
            "broad_near_fore_hind_overlap_pixels": overlap,
            "overlap_beyond_body_pixels": visible_overlap,
            "wing_opaque_pixels": half_counts,
            "wing_bounds": half_boxes,
        }
    return results


def draw_farmer(view: str) -> Image.Image:
    """A schematic fitting guide for previews only; never exported as art."""

    image = Image.new("RGBA", (SIZE, SIZE))
    draw = ImageDraw.Draw(image)
    draw.rectangle((35, 31, 44, 39), fill="#e9d7b7", outline="#53483f")
    draw.rectangle((34, 40, 45, 53), fill="#5f8f89", outline="#394e4a")
    draw.rectangle((35, 53, 38, 63), fill="#585b78")
    draw.rectangle((41, 53, 44, 63), fill="#585b78")
    if view == "right":
        draw.rectangle((44, 34, 46, 36), fill="#e9d7b7")
    elif view == "left":
        draw.rectangle((33, 34, 35, 36), fill="#e9d7b7")
    elif view == "front":
        draw.point((37, 35), fill="#3a2e36")
        draw.point((42, 35), fill="#3a2e36")
    return image


def composite_preview(view: str, behind: Image.Image, front: Image.Image) -> Image.Image:
    board = Image.new("RGBA", (SIZE, SIZE), "#71955d")
    board.alpha_composite(behind)
    board.alpha_composite(draw_farmer(view))
    board.alpha_composite(front)
    return board


def save_sheet(name: str, rows: list[tuple[str, list[Image.Image]]]) -> None:
    cell = 160
    header = 25
    canvas = Image.new("RGB", (cell * 4, header + cell * len(rows)), "#f3efe4")
    draw = ImageDraw.Draw(canvas)
    labels = {"back": "BACK", "right": "RIGHT", "front": "FRONT", "left": "LEFT"}
    for col, view in enumerate(VIEWS):
        draw.text((col * cell + 6, 6), labels[view], fill="#322f35")
    for row_index, (label, images) in enumerate(rows):
        y = header + row_index * cell
        for col, image in enumerate(images):
            tile = image.resize((cell, cell), Image.Resampling.NEAREST).convert("RGB")
            canvas.paste(tile, (col * cell, y))
        draw.text((4, y + 4), label, fill="#ffffff")
    canvas.save(OUT / name)


def save_hover_contact(previews: dict[str, list[Image.Image]]) -> None:
    # Keep each direction on one row so reviewers can inspect its complete
    # 16-frame motion rather than mistaking four alternating directions for a
    # single animation.
    cell = 80
    header = 16
    canvas = Image.new("RGB", (cell * 16, header + cell * 4), "#f3efe4")
    draw = ImageDraw.Draw(canvas)
    for index in range(16):
        draw.text((index * cell + 3, 3), f"H{index + 1:02d}", fill="#322f35")
    for row, view in enumerate(VIEWS):
        y = header + row * cell
        for index, source in enumerate(previews[view]):
            canvas.paste(source.convert("RGB"), (index * cell, y))
        draw.text((3, y + 3), view.upper(), fill="#ffffff")
    canvas.save(OUT / "hover-motion-contact-v1.png")


def save_gif(previews: dict[str, list[Image.Image]]) -> None:
    frames: list[Image.Image] = []
    for index in range(16):
        board = Image.new("RGB", (SIZE * 4, SIZE), "#71955d")
        for column, view in enumerate(VIEWS):
            board.paste(previews[view][index].convert("RGB"), (column * SIZE, 0))
        frames.append(board.resize((SIZE * 4 * 3, SIZE * 3), Image.Resampling.NEAREST))
    frames[0].save(OUT / "butterfly-hover-motion-v1.gif", save_all=True, append_images=frames[1:],
                   duration=60, loop=0, disposal=2)


def save_front_fit() -> None:
    """A large nearest-neighbour view for judging the whole silhouette."""

    behind, front = make_frame("front", motion_for("H", 8))
    composite_preview("front", behind, front).crop((10, 20, 70, 66)).resize(
        (360, 276), Image.Resampling.NEAREST
    ).save(OUT / "butterfly-front-fit-v1.png")


def save_state_previews(state: str, previews: dict[str, list[Image.Image]]) -> None:
    directory = OUT / "previews" / state
    directory.mkdir(parents=True, exist_ok=True)
    period = {"H": 960, "M": 840, "Q": 1200, "T": 700, "L": 700}[state]
    count = len(previews["front"])
    durations = [10 * (round((i + 1) * period / (10 * count)) - round(i * period / (10 * count)))
                 for i in range(count)]
    for view in VIEWS:
        # Fixed 60x54 crop and 4x nearest-neighbour, never per-frame recentering.
        # T/L play once; only airborne cycles loop.
        frames = [tile.crop((10, 12, 70, 66)).resize((240, 216), Image.Resampling.NEAREST).convert("RGB")
                  for tile in previews[view]]
        settings = {"loop": 0} if state in ("H", "M", "Q") else {}
        frames[0].save(directory / f"{state.lower()}-{view}.gif", save_all=True,
                       append_images=frames[1:], duration=durations, disposal=2, **settings)
    board = Image.new("RGB", (SIZE * count, 16 + SIZE * 4), "#f3efe4")
    draw = ImageDraw.Draw(board)
    for i in range(count):
        draw.text((i * SIZE + 3, 3), f"{state}{i + 1:02}", fill="#322f35")
    for row, view in enumerate(VIEWS):
        for i, tile in enumerate(previews[view]):
            board.paste(tile.convert("RGB"), (i * SIZE, 16 + row * SIZE))
        draw.text((3, 18 + row * SIZE), view.upper(), fill="white")
    board.save(directory / f"{state.lower()}-contact.png")


def save_design_parameters() -> None:
    shapes = {name: value for name, value in globals().items()
              if name.startswith(("UPPER_", "LOWER_")) and isinstance(value, tuple)}
    parameters = {"revision": DESIGN_REVISION, "authoritative_source": "tools/art/build_butterfly_wing_animation.py",
                  "note": "Exported parameter snapshot; edit the Python source to rebuild.",
                  "palette": COLORS, "contours_and_markings": shapes,
                  "rig": {"projection": "orthographic", "camera_elevation_degrees": 8,
                          "side_camera_front_bias_degrees": 12,
                          "hinge_half_spacing": HINGE_HALF_SPACING, "span_scale": SILHOUETTE_SCALE_X,
                          "chord_scale": SILHOUETTE_SCALE_Y, "stroke_extrema_degrees": [FORWARD_STROKE, 72],
                          "hindwing_flex_max_degrees": 1.5, "airborne_scale_changes": False,
                          "side_layer_rule": "whole far wing / farmer / whole near wing",
                          "foreground_torso_crop": False, "near_membrane_depth_crop": False,
                          "forward_overlap_target_body_width_fraction": 0.2,
                          "back_basal_foreground_radius": 6,
                          "runtime_origins": {"back": [40,42], "right": [38,42], "front": [40,42], "left": [42,42]}}}
    (OUT / "design-parameters.json").write_text(json.dumps(parameters, ensure_ascii=False, indent=2), encoding="utf-8")
    with (OUT / "motion-trajectories.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["state", "frame", "phase", "view", "side", "lobe", "stroke_degrees", "pitch_degrees",
                         "hinge_screen_x", "hinge_screen_y", "tip_world_x", "tip_world_y", "tip_world_z",
                         "tip_screen_x", "tip_screen_y", "tip_camera_depth"])
        for state in ("H", "M", "Q", "T", "L"):
            count = 10 if state in ("T", "L") else 16
            for i in range(count):
                motion = motion_for(state, i)
                for view in VIEWS:
                    root = (38, 42) if view == "right" else (42, 42) if view == "left" else (40, 42)
                    for side in (-1, 1):
                        for lobe, tip in (("upper", (36, -35)), ("lower", (13, 35))):
                            world = world_point(tip, side=side, lobe=lobe, motion=motion)
                            projected = camera_point(world, view)
                            hinge = transform((0, 0), root=root, side=side, spread=0, lobe=lobe,
                                              view=view, far=False, motion=motion)
                            screen = transform(tip, root=root, side=side, spread=0, lobe=lobe,
                                               view=view, far=False, motion=motion)
                            writer.writerow([state, i+1, round(motion.phase, 6), view, side, lobe,
                                round(motion.stroke if lobe == "upper" else motion.hind_stroke, 6),
                                round(motion.pitch, 6), *hinge, *[round(v, 6) for v in world],
                                *screen, round(projected[2], 6)])
    # Standalone art at native resolution, without farmer or background.
    behind, front = make_frame("front", motion_for("H", 8))
    Image.alpha_composite(behind, front).save(OUT / "butterfly-open-source-v1.png")


def build() -> dict[str, object]:
    OUT.mkdir(parents=True, exist_ok=True)
    output: dict[str, object] = {
        "kind": "current rebuildable butterfly-wing design",
        "design_revision": DESIGN_REVISION,
        "runtime_integration": "source for assets/butterfly; packaging and installation verified separately",
        "canvas": [SIZE, SIZE],
        "broad_silhouette_target_height": TARGET_BROAD_HEIGHT,
        "silhouette_scale": {
            "horizontal": SILHOUETTE_SCALE_X,
            "vertical_broad": SILHOUETTE_SCALE_Y,
            "airborne_scale_is_constant": True,
        },
        "states": {},
        "views": list(VIEWS),
        "layers": list(LAYERS),
        "palette": COLORS,
        "motion": {
            "description": "fixed adjacent hinges; orthographic signed 3D stroke and chord pitch; light hindwing flex; whole far wing / farmer / whole opaque near wing; restrained one-fifth forward occlusion",
            "projection": "orthographic",
            "stroke_angle_degrees": [FORWARD_STROKE, 72],
            "hindwing_flex_max_degrees": 1.5,
            "camera_elevation_degrees": 8,
            "side_camera_front_bias_degrees": 12,
            "height_compensation_per_frame": False,
            "far_wing_scale_factor": 1.0,
            "H_period_ms": 960,
            "M_period_ms": 840,
            "Q_period_ms": 1200,
            "transition_period_ms": 700,
            "continuity": {"T10_equals": "H09", "L01_equals": "H03"},
        },
        "source_reference": "reference/butterfly-shape-reference-2026-10-06.png",
    }
    output["anatomy_qa"] = validate_wing_anatomy()
    qa: list[dict[str, object]] = []
    hover_previews: dict[str, list[Image.Image]] = {view: [] for view in VIEWS}
    key_previews: dict[str, list[Image.Image]] = {view: [] for view in VIEWS}

    for state in ("H", "M", "Q", "T", "L"):
        count = 16 if state in ("H", "M", "Q") else 10
        output["states"][state] = {"frame_count": count, "atlas_size": [count * SIZE, SIZE]}
        atlases = {view: {layer: Image.new("RGBA", (count * SIZE, SIZE)) for layer in LAYERS} for view in VIEWS}
        state_previews: dict[str, list[Image.Image]] = {view: [] for view in VIEWS}
        for frame_index in range(count):
            motion = motion_for(state, frame_index)
            for view in VIEWS:
                behind, front = make_frame(view, motion)
                combined = Image.alpha_composite(behind, front)
                state_previews[view].append(composite_preview(view, behind, front))
                for layer, image in (("behind", behind), ("in_front", front)):
                    values = alpha_values(image)
                    if values not in ([0], [0, 255], [255]):
                        raise AssertionError(f"{state}/{view}/{layer}/{frame_index}: alpha {values}")
                    # A folded back membrane may lie entirely in the basal
                    # foreground patch. Empty individual layers are valid;
                    # the combined wing and its runtime atlas remain nonempty.
                    box = list(image.getbbox() or (0, 0, 0, 0))
                    if box[0] < 0 or box[1] < 0 or box[2] > SIZE or box[3] > SIZE:
                        raise AssertionError(f"{state}/{view}/{layer}/{frame_index}: bounds {box}")
                    path = OUT / "frames" / state / view / layer / f"{state.lower()}-{view}-{frame_index + 1:02d}.png"
                    path.parent.mkdir(parents=True, exist_ok=True)
                    image.save(path)
                    atlases[view][layer].paste(image, (frame_index * SIZE, 0))
                    qa.append({"state": state, "view": view, "layer": layer,
                               "frame": frame_index + 1, "bounds": box,
                               "opaque_pixels": sum(pixel == 255 for pixel in image.getchannel("A").get_flattened_data()),
                               "alpha": values})
                if state == "H":
                    hover_previews[view].append(composite_preview(view, behind, front))
                    if frame_index in (0, 4, 8, 10):
                        key_previews[view].append(composite_preview(view, behind, front))
        for view in VIEWS:
            for layer in LAYERS:
                destination = OUT / "atlases" / state / view / f"{layer}.png"
                destination.parent.mkdir(parents=True, exist_ok=True)
                atlas = atlases[view][layer]
                if atlas.size != (count * SIZE, SIZE):
                    raise AssertionError(f"Wrong atlas size: {destination}")
                atlas.save(destination)
        save_state_previews(state, state_previews)

    # Rows are the requested three key poses.  H01 is gathered, H05 is the
    # turn/opening posture, and H09 is the broad downstroke / takeoff hand-off.
    save_sheet("butterfly-keyposes-v1.png", [
        ("UPSTROKE / GATHERED", [key_previews[view][0] for view in VIEWS]),
        ("TURN / HALF OPEN", [key_previews[view][1] for view in VIEWS]),
        ("DOWNSTROKE / BROAD", [key_previews[view][2] for view in VIEWS]),
        ("LOWER REVERSAL", [key_previews[view][3] for view in VIEWS]),
    ])
    save_hover_contact(hover_previews)
    save_gif(hover_previews)
    save_front_fit()
    save_design_parameters()

    # Only the completely docked back pose can fit entirely into the exposed
    # basal patch. It still has a visible foreground layer; never accept an
    # empty airborne frame or silently lose the combined wing.
    empty_layers = []
    for row in qa:
        if row["opaque_pixels"] == 0:
            if not (row["view"] == "back" and row["layer"] == "behind" and
                    (row["state"], row["frame"]) in (("T",1),("L",10))):
                raise AssertionError(f"Empty required layer: {row}")
            empty_layers.append({key: row[key] for key in ("state","view","layer","frame")})
    output["qa"] = {
        "frame_files": len(qa),
        "atlas_files": 40,
        "alpha": [0, 255],
        "all_airborne_layers_nonempty": True,
        "empty_docked_layers": empty_layers,
        "source_reference_sha256": hashlib.sha256(
            (OUT / "reference" / "butterfly-shape-reference-2026-10-06.png").read_bytes()
        ).hexdigest(),
    }
    (OUT / "animation-data.json").write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding="utf-8")
    (OUT / "qa-summary.json").write_text(json.dumps(qa, ensure_ascii=False, indent=2), encoding="utf-8")
    return output


if __name__ == "__main__":
    report = build()
    print(json.dumps({"output": str(OUT), "qa": report["qa"], "states": report["states"]}, ensure_ascii=False))
