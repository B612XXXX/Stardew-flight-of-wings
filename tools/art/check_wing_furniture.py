"""Verify 1:1 wall-wing art and optionally replay captured native furniture draws."""
import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

from wing_furniture_art import CELL_SIZE, verify_item_sheet

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-path", type=Path, default=ROOT / "src/AngelWings.VisualPreview")
    parser.add_argument("--draw-capture", type=Path)
    parser.add_argument("--preview", action="store_true")
    args = parser.parse_args()
    assets = args.package_path.resolve() / "assets"
    path = assets / "items/wing-items.png"
    sheet = Image.open(path)
    wings = verify_item_sheet(sheet, assets)
    if args.draw_capture:
        draws = json.loads(args.draw_capture.read_text(encoding="utf-8"))
        assert len(draws) == 4, "Need both real draw paths for both wing appearances"
        for wing in wings:
            calls = [call for call in draws if call["wing"] == wing["wing"]]
            assert {call["location_furniture"] for call in calls} == {True, False}
            for call in calls:
                assert [call[key] for key in ("source_x", "source_y", "width", "height")] == wing["cell"]
                assert call["scale"] == 4, "Furniture and worn wings must share native game pixel scale"
    output = ROOT / "build/qa-wall-wing-0.4.11"
    output.mkdir(parents=True, exist_ok=True)
    report = {"status": "passed", "sheet_size": list(sheet.size), "world_pixel_scale": 4,
              "wings": wings, "native_draw_replayed": bool(args.draw_capture),
              "sheet_sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "game_session_tested": False}
    if args.preview:
        board = Image.new("RGB", (800, 330), "#eee3c9")
        draw = ImageDraw.Draw(board)
        font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 17)
        small = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 14)
        draw.text((22, 15), "墙面翅膀 · 原图 1:1 像素尺寸", fill="#473d38", font=font)
        for index, wing in enumerate(wings):
            x = index * 400
            draw.rectangle((x + 20, 52, x + 380, 254), fill="#cbbc98")
            cell = sheet.crop((index * CELL_SIZE[0], 0, (index + 1) * CELL_SIZE[0], CELL_SIZE[1]))
            board.paste(cell.resize((320, 192), Image.Resampling.NEAREST), (x + 40, 57),
                        cell.resize((320, 192), Image.Resampling.NEAREST))
            label = "天使翅膀" if index == 0 else "琥珀蝶翼"
            width, height = wing["native_size"]
            draw.text((x + 55, 267), f"{label}：{width} × {height} 原始像素", fill="#473d38", font=font)
        draw.text((22, 307), "离线预览；按游戏原生 4 倍像素展示，与佩戴时相同，非实机截图。", fill="#675b4b", font=small)
        board.save(output / "wall-wings-native-scale.png")
    (output / "art-verification.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
