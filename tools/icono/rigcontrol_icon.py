"""Icono de RigControl (caza sobre el horizonte; elegido el 10/11/2026 entre tres propuestas).

Uso: python -I tools/icono/rigcontrol_icon.py src/RigControl/Assets
Genera RigControl.ico (16-256 px) y RigControl.png (1024 px). Necesita Pillow.
Se dibuja a 1024 px y se reduce: el antialias sale de ahí.
"""
import math
import os
import sys
from PIL import Image, ImageDraw, ImageFilter

OUT = sys.argv[1]
S = 1024  # se dibuja grande y se reduce (antialias)


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def vgradient(size, top, bottom):
    w, h = size
    g = Image.new("RGBA", size)
    d = ImageDraw.Draw(g)
    for y in range(h):
        d.line([(0, y), (w, y)], fill=lerp(top, bottom, y / max(1, h - 1)))
    return g


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def circle_mask(size, box):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).ellipse(box, fill=255)
    return m


def rot(points, angle_deg, cx, cy):
    a = math.radians(angle_deg)
    ca, sa = math.cos(a), math.sin(a)
    return [(cx + (x - cx) * ca - (y - cy) * sa, cy + (x - cx) * sa + (y - cy) * ca) for x, y in points]


def horizon_layer(bank, pitch_px, sky=((96, 184, 255), (30, 110, 200)), ground=((150, 98, 52), (100, 62, 30))):
    """Cielo y tierra con la línea del horizonte inclinada (alabeo) y desplazada (cabeceo)."""
    big = S * 2
    layer = Image.new("RGBA", (big, big))
    layer.paste(vgradient((big, big // 2 + pitch_px), sky[0] + (255,), sky[1] + (255,)), (0, 0))
    layer.paste(vgradient((big, big // 2 - pitch_px), ground[0] + (255,), ground[1] + (255,)), (0, big // 2 + pitch_px))
    d = ImageDraw.Draw(layer)
    y = big // 2 + pitch_px
    d.line([(0, y), (big, y)], fill=(255, 255, 255, 255), width=int(S * 0.018))
    layer = layer.rotate(bank, resample=Image.BICUBIC, center=(big / 2, big / 2))
    return layer.crop((S // 2, S // 2, S // 2 + S, S // 2 + S))


def shadow(img, radius=S * 0.02, offset=S * 0.012, alpha=110):
    a = img.split()[3].point(lambda v: v * alpha // 255)
    sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
    sh.putalpha(a)
    sh = sh.filter(ImageFilter.GaussianBlur(radius))
    out = Image.new("RGBA", img.size, (0, 0, 0, 0))
    out.alpha_composite(sh, (0, int(offset)))
    out.alpha_composite(img)
    return out


# --------------------------------------------------------------- C: avión sobre horizonte
def jet_points(cx, cy, k):
    """Caza visto desde arriba (morro hacia arriba), coordenadas en unidades de S*k."""
    p = [
        (0, -0.42), (0.035, -0.30), (0.045, -0.10),
        (0.36, 0.08), (0.36, 0.14), (0.05, 0.08),
        (0.05, 0.24), (0.16, 0.33), (0.16, 0.38), (0.03, 0.34),
        (0, 0.40),
    ]
    right = p
    left = [(-x, y) for x, y in reversed(p[:-1])]
    pts = right + left[:-1]
    return [(cx + x * S * k, cy + y * S * k) for x, y in pts]


def concept_c():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    m = int(S * 0.06)
    tile = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    face = horizon_layer(bank=-18, pitch_px=int(S * 0.10))
    mask = Image.new("L", (S, S), 0)
    mask.paste(rounded_mask(S - 2 * m, int(S * 0.2)), (m, m))
    tile.paste(face, (0, 0), mask)
    img.alpha_composite(shadow(tile))
    jet = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    jd = ImageDraw.Draw(jet)
    pts = jet_points(S * 0.5, S * 0.5, 0.78)
    jd.polygon(pts, fill=(250, 250, 252, 255), outline=(25, 30, 40, 255), width=int(S * 0.014))
    jd.line([(S * 0.5, S * 0.5 - S * 0.25), (S * 0.5, S * 0.5 - S * 0.15)], fill=(255, 176, 0, 255), width=int(S * 0.03))  # cabina
    jet = jet.rotate(-18, resample=Image.BICUBIC, center=(S * 0.5, S * 0.5))
    img.alpha_composite(shadow(jet, radius=S * 0.02, offset=S * 0.02, alpha=150))
    return img


os.makedirs(OUT, exist_ok=True)
icon = concept_c()
icon.save(os.path.join(OUT, "RigControl.png"))
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
icon.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "RigControl.ico"), format="ICO", sizes=[(n, n) for n in sizes])
print("ok:", OUT)
