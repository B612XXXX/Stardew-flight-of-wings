"""Wall furniture made from native wing pixels, with no resampling or extra art."""
from pathlib import Path

from PIL import Image

CELL_SIZE = (80, 48)  # 5x3 furniture tiles, enough for the 66px angel pose.
WINGS = ("angel", "butterfly")
POSE_INDEX = 8  # H09: a representative open front pose from the flight atlases.


def wing_pose(assets: Path, wing: str) -> Image.Image:
    base = assets if wing == "angel" else assets / "butterfly"
    box = (POSE_INDEX * 80, 0, (POSE_INDEX + 1) * 80, 80)
    layers = [Image.open(base / "H/front" / f"{layer}.png").convert("RGBA").crop(box)
              for layer in ("behind", "in_front")]
    pose = Image.alpha_composite(*layers)
    bounds = pose.getbbox()
    assert bounds is not None, f"Missing {wing} furniture pose"
    return pose.crop(bounds)


def build_item_sheet(assets: Path) -> Image.Image:
    width, height = CELL_SIZE
    sheet = Image.new("RGBA", (width * len(WINGS), height))
    for index, wing in enumerate(WINGS):
        pose = wing_pose(assets, wing)
        assert pose.width <= width - 4 and pose.height <= height - 4, (
            f"{wing} outgrew the full-size furniture cell; enlarge the cell, never resize the wings", pose.size)
        sheet.alpha_composite(pose, (index * width + (width - pose.width) // 2,
                                     (height - pose.height) // 2))
    return sheet


def verify_item_sheet(sheet: Image.Image, assets: Path) -> list[dict]:
    expected = build_item_sheet(assets)
    assert sheet.mode == "RGBA" and sheet.size == expected.size, (
        "Furniture cells must preserve the native wing dimensions", sheet.mode, sheet.size, expected.size)
    assert sheet.tobytes() == expected.tobytes(), "Furniture must contain the exact unscaled flight-wing pixels"
    assert set(sheet.getchannel("A").tobytes()) <= {0, 255}, "Furniture edges must retain binary alpha"
    return [{"wing": wing, "native_size": list(wing_pose(assets, wing).size),
             "cell": [index * CELL_SIZE[0], 0, *CELL_SIZE], "resampled": False}
            for index, wing in enumerate(WINGS)]
