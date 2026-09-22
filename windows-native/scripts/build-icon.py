"""Build the Wake PC Windows icon from resolution-independent primitives."""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "WakePcBridge" / "Assets"
PNG_PATH = ASSETS / "WakePcBridge.png"
ICO_PATH = ASSETS / "WakePcBridge.ico"
ICON_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def _scaled(value: float, scale: float) -> int:
    return round(value * scale)


def render(size: int) -> Image.Image:
    oversample = 4
    canvas_size = size * oversample
    scale = canvas_size / 1024
    image = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))

    circle_mask = Image.new("L", image.size, 0)
    mask_draw = ImageDraw.Draw(circle_mask)
    circle_box = tuple(_scaled(value, scale) for value in (96, 96, 928, 928))
    mask_draw.ellipse(circle_box, fill=255)

    gradient_strip = Image.new("RGBA", (1, canvas_size))
    gradient_pixels = gradient_strip.load()
    top = (7, 35, 62)
    bottom = (8, 118, 174)
    for y in range(canvas_size):
        progress = y / max(1, canvas_size - 1)
        red = round(top[0] + ((bottom[0] - top[0]) * progress))
        green = round(top[1] + ((bottom[1] - top[1]) * progress))
        blue = round(top[2] + ((bottom[2] - top[2]) * progress))
        gradient_pixels[0, y] = (red, green, blue, 255)
    gradient = gradient_strip.resize(image.size)
    image.alpha_composite(Image.composite(gradient, image, circle_mask))

    draw = ImageDraw.Draw(image)
    ring_width = max(1, _scaled(18, scale))
    draw.ellipse(
        circle_box,
        outline=(40, 205, 238, 210),
        width=ring_width,
    )

    white = (248, 252, 255, 255)
    power_width = max(1, _scaled(82, scale))
    power_box = tuple(_scaled(value, scale) for value in (302, 306, 722, 726))
    draw.arc(
        power_box,
        start=315,
        end=585,
        fill=white,
        width=power_width,
    )
    stem_box = tuple(_scaled(value, scale) for value in (469, 224, 555, 508))
    draw.rounded_rectangle(
        stem_box,
        radius=_scaled(43, scale),
        fill=white,
    )

    wake = (93, 231, 255, 255)
    ray_width = max(1, _scaled(28, scale))
    rays = (
        ((664, 218), (688, 153)),
        ((742, 265), (796, 222)),
        ((785, 348), (854, 339)),
    )
    for start, end in rays:
        draw.line(
            tuple(_scaled(value, scale) for point in (start, end) for value in point),
            fill=wake,
            width=ray_width,
        )

    if oversample > 1:
        image = image.resize((size, size), Image.Resampling.LANCZOS)
    return image


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    render(1024).save(PNG_PATH, format="PNG", optimize=True)
    render(256).save(
        ICO_PATH,
        format="ICO",
        sizes=[(size, size) for size in ICON_SIZES],
        bitmap_format="png",
    )


if __name__ == "__main__":
    main()
