"""Crops a screenshot to the window, reduces it to a palette and writes it to Assets/Help.

    python tools/help-screenshots/quantize.py <screenshot.png> <article-picture-name>

The picture is 1280 x 820; the article's markers are fractions of it, so a new picture of the same page keeps its markers.
"""
import sys
from pathlib import Path
from PIL import Image

source, name = Path(sys.argv[1]), sys.argv[2]
target = Path(__file__).resolve().parents[2] / "src" / "Sidera.Desktop" / "Assets" / "Help" / (name + ".png")
image = Image.open(source).convert("RGB")
if image.size != (1280, 820):
    image = image.resize((1280, 820), Image.LANCZOS)
image.quantize(colors=128, method=Image.MEDIANCUT).save(target, optimize=True)
print("wrote", target)
