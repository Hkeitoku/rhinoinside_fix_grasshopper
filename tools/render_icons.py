"""Render the supplied SVGs to transparent Revit ribbon PNGs (development only).

Requires Pillow, Playwright, and Microsoft Edge. No dependencies are added to
the Revit add-in; the resulting PNGs are embedded at build time.
"""

import io
from pathlib import Path
from xml.etree import ElementTree

from PIL import Image
from playwright.sync_api import sync_playwright


ASSETS = Path(__file__).resolve().parents[1] / "assets"
ICONS = {
    "grasshopper-direct-open-32.svg": "ghfix",
    "grasshopper-version-info-32.svg": "diagnostic",
}


def main():
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(channel="msedge", headless=True)
        try:
            page = browser.new_page(viewport={"width": 256, "height": 256})
            page.route("**/*", lambda route: route.abort())
            for source, target in ICONS.items():
                svg = (ASSETS / source).read_text(encoding="utf-8-sig")
                root = ElementTree.fromstring(svg)
                for element in root.iter():
                    if element.tag.rsplit("}", 1)[-1] in {"script", "foreignObject"}:
                        raise ValueError(f"Unexpected active SVG content: {source}")
                    if any(key.lower().startswith("on") for key in element.attrib):
                        raise ValueError(f"Unexpected SVG event handler: {source}")
                page.set_content(
                    "<style>html,body{margin:0;background:transparent}"
                    "body>svg{display:block;width:256px;height:256px}</style>" + svg
                )
                rendered = Image.open(io.BytesIO(page.screenshot(omit_background=True)))
                for size in (16, 32):
                    output = ASSETS / f"{target}_{size}.png"
                    rendered.resize((size, size), Image.Resampling.LANCZOS).save(output)
                    print(f"{output.name}: {size}x{size}, transparent PNG")
        finally:
            browser.close()


if __name__ == "__main__":
    main()
