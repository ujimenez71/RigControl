"""Ilustraciones de cabecera de RigControl (arte propio, generado por procedimiento).

Siete escenas de simulación aérea de combate, una por pantalla de la app, con lo importante a la
derecha (a la izquierda va el título encima). Cielos, nubes, montañas y mar procedurales; siluetas
genéricas dibujadas aquí (caza de perfil y en planta, helicóptero de ataque, portaaviones), no
copiadas de ningún avión ni juego concreto. Semilla fija: siempre salen iguales.

    python -I tools/arte/rigcontrol_arte.py src/RigControl/Assets/Banners

Necesita Pillow y numpy.
"""
import math
import os
import random
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

W, H = 2400, 400          # tamaño final
SS = 2                    # sobremuestreo para bordes suaves
CW, CH = W * SS, H * SS


# ------------------------------------------------------------------ utilidades

def hexrgb(h):
    h = h.lstrip('#')
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float32)


def vgradient(stops):
    """Degradado vertical: stops = [(posición 0..1, '#color'), ...]."""
    ys = np.linspace(0, 1, CH)
    out = np.zeros((CH, 3), np.float32)
    pos = [p for p, _ in stops]
    cols = [hexrgb(c) for _, c in stops]
    for ch in range(3):
        out[:, ch] = np.interp(ys, pos, [c[ch] for c in cols])
    return np.repeat(out[:, None, :], CW, axis=1)


def glow(img, cx, cy, radius, color, strength=1.0, power=2.0):
    """Brillo radial aditivo (sol, postcombustión)."""
    y, x = np.ogrid[:CH, :CW]
    d = np.sqrt((x - cx) ** 2 + (y - cy) ** 2) / radius
    a = np.clip(1 - d, 0, 1) ** power * strength
    img += a[..., None] * hexrgb(color)[None, None, :]
    return img


def blend(img, color, mask):
    """Pinta color con una máscara 0..1."""
    c = hexrgb(color) if isinstance(color, str) else color
    m = mask[..., None]
    return img * (1 - m) + c * m


def mask_from(draw_fn, blur=0):
    m = Image.new('L', (CW, CH), 0)
    draw_fn(ImageDraw.Draw(m))
    if blur:
        m = m.filter(ImageFilter.GaussianBlur(blur * SS))
    return np.asarray(m, np.float32) / 255.0


def stars(img, rng, count, ymax):
    for _ in range(count):
        x, y = rng.randrange(CW), rng.randrange(int(CH * ymax))
        b = rng.random() ** 3
        r = 1 + int(b * 2 * SS)
        m = mask_from(lambda d: d.ellipse([x - r, y - r, x + r, y + r], fill=int(160 + 95 * b)), blur=0.6)
        img = blend(img, '#eef4ff', m * (0.3 + 0.7 * b))
    return img


def fbm(seed, cell, octaves=6, stretch=4.0):
    """Ruido fractal (suma de ruidos suaves a varias escalas), 0..1, estirado en horizontal como las nubes."""
    rng = np.random.default_rng(seed)
    out = np.zeros((CH, CW), np.float32)
    amp, total = 1.0, 0.0
    for o in range(octaves):
        cy = max(2, int(CH / (cell * SS) * 2 ** o))
        cx = max(2, int(CW / (cell * SS * stretch) * 2 ** o))
        g = (rng.random((cy, cx)) * 255).astype(np.uint8)
        up = np.asarray(Image.fromarray(g, 'L').resize((CW, CH), Image.BICUBIC), np.float32) / 255.0
        out += up * amp
        total += amp
        amp *= 0.5
    out /= total
    return (out - out.min()) / (out.max() - out.min())


def clouds(img, seed, y0, y1, lit, shade, density=0.5, cell=120, stretch=2.6, soft=0.16, light_px=26, alpha=1.0, xfade=None):
    """
    Capa de nubes con textura: ruido fractal dentro de una franja (y0..y1, con bordes suaves), parte de
    arriba iluminada (lit) y de abajo en sombra (shade). density = cuánto cielo tapan (0..1).
    """
    n = fbm(seed, cell, stretch=stretch)
    ys = np.linspace(0, 1, CH)[:, None]
    band = np.clip(np.minimum((ys - y0) / 0.22, (y1 - ys) / 0.16), 0, 1) ** 0.8
    if xfade is not None:   # (x0, x1): sin nubes a la izquierda de x0, del todo a partir de x1
        xs = np.linspace(0, 1, CW)[None, :]
        band = band * np.clip((xs - xfade[0]) / (xfade[1] - xfade[0]), 0, 1)
    d = n * band - (1 - density)
    m = np.clip(d / soft, 0, 1)
    m = m * m * (3 - 2 * m)
    # luz desde arriba: donde la nube "empieza" al bajar, más clara
    k = int(light_px * SS)
    above = np.vstack([np.zeros((k, CW), np.float32), m[:-k]])
    light = np.clip(m - above, 0, 1)
    light = np.asarray(Image.fromarray((light * 255).astype(np.uint8), 'L').filter(ImageFilter.GaussianBlur(7 * SS)), np.float32) / 255.0
    # sombra hacia abajo dentro de cada nube
    body = np.clip(m * (0.55 + 0.45 * n), 0, 1)
    img = blend(img, shade, body * alpha)
    img = blend(img, lit, np.clip(light * 2.2, 0, 1) * m * alpha * 0.9)
    return img


def ridge(rng, base, amp, octaves=6, rough=0.55):
    """Línea de montañas: ruido fractal (suma de senos)."""
    xs = np.linspace(0, 1, CW)
    y = np.zeros(CW, np.float32)
    a, f = 1.0, 2.0
    for _ in range(octaves):
        y += a * np.sin(xs * f * math.pi * 2 + rng.uniform(0, 6.28)) * rng.uniform(0.6, 1.0)
        y += a * 0.5 * np.sin(xs * f * 1.7 * math.pi * 2 + rng.uniform(0, 6.28))
        a *= rough
        f *= 2.1
    y = (y - y.min()) / (y.max() - y.min())
    return (base - y * amp) * CH


def mountains(img, rng, base, amp, color, fog_color, fog):
    line = ridge(rng, base, amp)
    yy = np.arange(CH)[:, None]
    m = (yy >= line[None, :]).astype(np.float32)
    c = hexrgb(color) * (1 - fog) + hexrgb(fog_color) * fog
    return blend(img, c, m)


def sea(img, horizon, top, bottom, sun_x=None, sun_color='#ffd08a'):
    hy = int(horizon * CH)
    grad = vgradient([(0, top), (1, bottom)])
    m = np.zeros((CH, CW), np.float32)
    m[hy:, :] = 1
    img = img * (1 - m[..., None]) + grad * m[..., None]
    if sun_x is not None:
        rng = random.Random(7)
        def draw(d):
            for i in range(160):
                y = hy + (i / 160) ** 1.6 * (CH - hy)
                w = (0.01 + 0.05 * (i / 160)) * CW * rng.uniform(0.4, 1.2)
                d.line([(sun_x * CW - w, y), (sun_x * CW + w, y)], fill=int(200 * (1 - i / 200)), width=SS)
        img = blend(img, sun_color, mask_from(draw, blur=1.2) * 0.7)
    return img


# ------------------------------------------------------------------ siluetas (morro a la derecha, y hacia arriba)

FIGHTER_SIDE = {
    'body': [(2, 3.0), (10, 3.5), (20, 4.5), (45, 5.5), (60, 6.5), (66, 9.5), (70, 11.5), (76, 11.8), (80, 9.5),
             (86, 6.5), (93, 4.0), (100, 1.0), (100, 0.4), (93, -1.0), (85, -2.5), (70, -3.5), (62, -5.5),
             (48, -6.0), (35, -5.5), (20, -4.5), (8, -3.5), (2, -3.0), (0, -2.5), (0, 2.5)],
    'fin': [(5, 3.0), (-1, 18), (6, 19), (19, 4.5)],
    'tail': [(-2, 0.3), (16, 0.9), (16, -0.4)],
    'wing': [(28, -1.2), (60, -0.6), (40, -2.6)],
}
FIGHTER_SIDE_NOZZLE = (0, 0)

_half = [(0, 100), (2.2, 92), (3.6, 80), (4.6, 66), (5.2, 62), (23, 38), (24.5, 30), (6.5, 30),
         (6.5, 18), (15, 7), (15, 2), (5.5, 4), (4.2, 0)]
FIGHTER_TOP = {'body': _half + [(-x, y) for x, y in reversed(_half)]}

HELO_SIDE = {
    'body': [(0, 1.0), (30, 1.6), (45, 4), (55, 10), (62, 13), (72, 13.6), (80, 11), (86, 8), (92, 4), (96, 0),
             (94, -3), (85, -6), (70, -7.2), (55, -7.2), (48, -4), (32, -1), (0, -0.6)],
    'fin': [(3, 1), (-2, 11), (4, 11), (9, 1)],
    'wing': [(55, -1), (67, -1), (65, -3.2), (57, -3.2)],
    'mast': [(63.5, 13.5), (66.5, 13.5), (66.5, 17.5), (63.5, 17.5)],
    'rotor': [(8, 17.2), (122, 18.6), (122, 18.0), (8, 16.6)],
    'gear1': [(58, -7), (59.2, -7), (60, -11), (58.8, -11)],
    'gear2': [(84, -6), (85.2, -6), (86, -10), (84.8, -10)],
    'tailrotor': [(-1, 1.5), (1, 1.5), (1, 12.5), (-1, 12.5)],
}

CARRIER_SIDE = {
    'hull': [(0, 8), (104, 8), (106, 9), (100, 3), (94, 0), (6, 0), (2, 2)],
    'island': [(58, 8), (70, 8), (69, 16), (66, 18), (60, 18), (59, 14)],
    'mast': [(63.4, 18), (64.6, 18), (64.4, 27), (63.6, 27)],
    'jet1': [(20, 8), (27, 8), (26, 9.4), (22, 9.4)],
    'jet2': [(85, 8), (92, 8), (91, 9.4), (87, 9.4)],
}


def place(points, x, y, scale, angle=0.0, flip=False):
    """Puntos de la silueta -> lienzo (escala en px finales, ángulo en grados, + = morro arriba)."""
    a = math.radians(angle)
    ca, sa = math.cos(a), math.sin(a)
    out = []
    for px, py in points:
        if flip:
            px = -px
        rx = px * ca - py * sa
        ry = px * sa + py * ca
        out.append(((x + rx * scale) * SS / (W / CW * SS) if False else (x * W + rx * scale) * SS,
                    (y * H - ry * scale) * SS))
    return out


def silhouette(img, shape, x, y, scale, color, angle=0.0, flip=False, alpha=1.0):
    def draw(d):
        for part in shape.values():
            d.polygon(place(part, x, y, scale, angle, flip), fill=255)
    return blend(img, color, mask_from(draw, blur=0.4) * alpha)


def afterburner(img, x, y, scale, angle, length=1.0, flip=False):
    """Llama y brillo en la tobera de un caza de perfil."""
    (nx, ny), = place([(-2, 0)], x, y, scale, angle, flip)
    (tx, ty), = place([(-14 * length, 0)], x, y, scale, angle, flip)
    def flame(d):
        d.polygon([place([(0, 2.2)], x, y, scale, angle, flip)[0], place([(0, -2.2)], x, y, scale, angle, flip)[0], (tx, ty)], fill=255)
    m = mask_from(flame, blur=2.5)
    img = blend(img, '#ffb347', m * 0.85)
    img = glow(img, nx, ny, scale * SS * 9, '#ff8a3d', 0.9, 2.0)
    img = glow(img, nx, ny, scale * SS * 3, '#fff2c8', 1.0, 1.6)
    return img


def contrail(img, pts, width, alpha=0.35, color='#f4f1ea', blur=4):
    def draw(d):
        d.line([(px * W * SS, py * H * SS) for px, py in pts], fill=255, width=int(width * SS))
    return blend(img, color, mask_from(draw, blur=blur) * alpha)


def finish(img, path, vignette=0.35):
    # viñeta suave y algo de grano: más "de cine"
    y, x = np.ogrid[:CH, :CW]
    d = np.sqrt(((x - CW * 0.6) / CW) ** 2 + ((y - CH * 0.45) / CH * 0.55) ** 2)
    img = img * (1 - np.clip(d - 0.25, 0, 1)[..., None] * vignette)
    rng = np.random.default_rng(3)
    img += rng.normal(0, 2.2, img.shape).astype(np.float32)
    im = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8), 'RGB').resize((W, H), Image.LANCZOS)
    im.save(path, quality=88, optimize=True, progressive=True)
    print(path, im.size, os.path.getsize(path) // 1024, 'KB')


# ------------------------------------------------------------------ escenas

def monitor(path):
    """Interceptación nocturna: caza cruzando la luna sobre nubes iluminadas."""
    rng = random.Random(11)
    img = vgradient([(0, '#03060d'), (0.55, '#0a1426'), (0.8, '#13243f'), (1, '#1a2f4e')])
    img = stars(img, rng, 260, 0.75)
    mx, my = 0.80 * CW, 0.36 * CH
    img = glow(img, mx, my, 360 * SS, '#2c4a7a', 0.6, 2.2)
    img = blend(img, '#e9eef7', mask_from(lambda d: d.ellipse([mx - 70 * SS, my - 70 * SS, mx + 70 * SS, my + 70 * SS], fill=255), blur=1.5))
    img = blend(img, '#c7d0de', mask_from(lambda d: [d.ellipse([mx + a * SS, my + b * SS, mx + (a + r) * SS, my + (b + r) * SS], fill=255)
                                                     for a, b, r in [(-40, -20, 28), (5, 15, 22), (-10, -50, 14), (20, -30, 12)]], blur=3) * 0.35)
    img = clouds(img, 11, 0.62, 1.05, '#6f8cbc', '#0f1a30', density=0.55, cell=90)
    img = silhouette(img, FIGHTER_SIDE, 0.775, 0.40, 2.1, '#04070c', angle=8)
    img = contrail(img, [(0.30, 0.62), (0.55, 0.52), (0.745, 0.415)], 3, alpha=0.18, color='#9fb4d8')
    finish(img, path)


def ejecucion(path):
    """Dos cazas trepando con postcombustión al atardecer."""
    rng = random.Random(21)
    img = vgradient([(0, '#0b1230'), (0.35, '#2e1f4d'), (0.62, '#93404a'), (0.82, '#ee8a45'), (1, '#ffd27d')])
    img = glow(img, 0.88 * CW, 0.86 * CH, 700 * SS, '#ffb35a', 0.55, 1.8)
    img = glow(img, 0.88 * CW, 0.86 * CH, 120 * SS, '#fff1c4', 1.0, 1.4)
    img = clouds(img, 22, 0.12, 0.42, '#f7b78a', '#5a3352', density=0.32, cell=170, stretch=9, alpha=0.55)
    img = clouds(img, 21, 0.72, 1.05, '#ffc98a', '#4f2a3c', density=0.58)
    for x, y, s, l in [(0.62, 0.50, 2.4, 1.0), (0.76, 0.30, 1.9, 0.9)]:
        img = contrail(img, [(x - 0.30, y + 0.58), (x - 0.12, y + 0.22), (x - 0.01, y + 0.02)], 5, alpha=0.30)
        img = silhouette(img, FIGHTER_SIDE, x, y, s, '#120a12', angle=24)
        img = afterburner(img, x, y, s, 24, l)
    finish(img, path)


def perfiles(path):
    """Formación de cuatro cazas en escalón sobre un mar de nubes dorado."""
    rng = random.Random(31)
    img = vgradient([(0, '#13213f'), (0.45, '#3f5487'), (0.75, '#c7956d'), (1, '#f2c27f')])
    img = glow(img, 0.92 * CW, 0.70 * CH, 650 * SS, '#ffcf8a', 0.5, 1.8)
    img = clouds(img, 31, 0.60, 1.05, '#ffe0aa', '#7d5d6c', density=0.74)
    img = clouds(img, 32, 0.80, 1.10, '#ffe9c4', '#94707c', density=0.80, cell=80)
    for i, (x, y, s) in enumerate([(0.58, 0.30, 1.9), (0.68, 0.38, 1.6), (0.77, 0.45, 1.35), (0.85, 0.51, 1.15)]):
        img = silhouette(img, FIGHTER_SIDE, x, y, s, '#141826', angle=-3, alpha=1 - i * 0.08)
    finish(img, path)


def hangar(path):
    """Hangar en penumbra que se abre a una pista al atardecer, con un caza en la rampa."""
    rng = random.Random(41)
    img = vgradient([(0, '#090d14'), (1, '#0f151e')])
    # abertura (puerta del hangar)
    ox0, oy0, ox1, oy1 = 0.42, 0.10, 0.99, 0.98
    sky = vgradient([(0, '#1c2347'), (0.45, '#7a3b52'), (0.70, '#f08a46'), (0.78, '#ffcf7a'), (1, '#3a2a2a')])
    sky = glow(sky, 0.74 * CW, 0.70 * CH, 420 * SS, '#ffd38a', 0.9, 1.6)
    sky = mountains(sky, rng, 0.76, 0.12, '#2b1c2a', '#c76a4a', 0.35)
    # pista en perspectiva
    def runway(d):
        d.polygon([(0.70 * CW, 0.78 * CH), (0.78 * CW, 0.78 * CH), (1.05 * CW, 1.0 * CH), (0.45 * CW, 1.0 * CH)], fill=255)
    sky = blend(sky, '#2a2326', mask_from(runway))
    def centerline(d):
        for i in range(9):
            t0, t1 = i / 9, (i + 0.45) / 9
            y0, y1 = 0.78 + 0.22 * t0 ** 1.4, 0.78 + 0.22 * t1 ** 1.4
            d.line([(0.74 * CW + (0.75 - 0.74) * t0 * CW, y0 * CH), (0.74 * CW + 0.01 * t1 * CW, y1 * CH)], fill=255, width=int(SS * (1 + 5 * t1)))
    sky = blend(sky, '#e8d6b8', mask_from(centerline, blur=0.5) * 0.7)
    door = mask_from(lambda d: d.rectangle([ox0 * CW, oy0 * CH, ox1 * CW, oy1 * CH], fill=255))
    img = img * (1 - door[..., None]) + sky * door[..., None]
    # estructura del hangar: cerchas y marco
    def truss(d):
        d.rectangle([ox0 * CW - 18 * SS, 0, ox0 * CW, CH], fill=255)
        d.rectangle([0, oy0 * CH - 14 * SS, CW, oy0 * CH], fill=255)
        for i in range(14):
            x = ox0 * CW + i * 0.045 * CW
            d.line([(x, 0), (x + 0.0225 * CW, oy0 * CH)], fill=255, width=3 * SS)
            d.line([(x + 0.045 * CW, 0), (x + 0.0225 * CW, oy0 * CH)], fill=255, width=3 * SS)
        for i in range(6):
            y = oy0 * CH + i * 0.15 * CH
            d.line([(ox0 * CW - 18 * SS, y), (ox0 * CW, y + 0.075 * CH)], fill=255, width=2 * SS)
    img = blend(img, '#05080c', mask_from(truss, blur=0.3))
    # luces del techo del hangar (a la izquierda) y su reflejo en el suelo
    for lx in (0.10, 0.24, 0.36):
        img = glow(img, lx * CW, 0.06 * CH, 260 * SS, '#5b7fa8', 0.35, 2.0)
        img = glow(img, lx * CW, 0.95 * CH, 300 * SS, '#2c435e', 0.25, 2.0)
    # caza en la rampa, recortado contra el cielo
    img = silhouette(img, FIGHTER_SIDE, 0.60, 0.80, 3.1, '#06080b', angle=0)
    img = glow(img, 0.56 * CW, 0.95 * CH, 500 * SS, '#000000', 0.0)
    finish(img, path, vignette=0.25)


def mods(path):
    """Helicóptero de ataque volando bajo entre montañas al anochecer."""
    rng = random.Random(51)
    img = vgradient([(0, '#101634'), (0.4, '#3a2d5c'), (0.7, '#b4566a'), (0.9, '#f19a62'), (1, '#f6b878')])
    img = glow(img, 0.30 * CW, 0.92 * CH, 700 * SS, '#ffb070', 0.45, 1.8)
    for base, amp, col, fog in [(0.80, 0.42, '#3b2f5b', 0.55), (0.88, 0.40, '#2a2245', 0.35), (0.97, 0.34, '#1a1530', 0.18), (1.05, 0.30, '#0d0b1a', 0.0)]:
        img = mountains(img, rng, base, amp, col, '#c27a7c', fog)
    img = silhouette(img, HELO_SIDE, 0.70, 0.46, 2.6, '#07060c', angle=-6)
    def dust(d):
        for _ in range(40):
            x = rng.uniform(0.60, 0.86) * CW
            y = rng.uniform(0.88, 1.0) * CH
            r = rng.uniform(20, 60) * SS
            d.ellipse([x - r, y - r * 0.4, x + r, y + r * 0.4], fill=120)
    img = blend(img, '#c99a86', mask_from(dust, blur=14) * 0.25)
    finish(img, path)


def instantaneas(path):
    """Portaaviones al amanecer y una pareja de cazas pasando por encima."""
    rng = random.Random(61)
    horizon = 0.66
    img = vgradient([(0, '#14213f'), (0.35, '#41456d'), (0.55, '#c0705a'), (horizon, '#ffd08a'), (1, '#ffd08a')])
    img = glow(img, 0.70 * CW, horizon * CH, 520 * SS, '#ffe0a0', 0.8, 1.7)
    img = clouds(img, 61, 0.20, 0.52, '#ffc49a', '#5d4a66', density=0.52, cell=150, stretch=6, xfade=(0.2, 0.5), alpha=0.85)
    img = sea(img, horizon, '#5a4a5a', '#0f1726', sun_x=0.70)
    img = silhouette(img, CARRIER_SIDE, 0.66, horizon + 0.012, 3.2, '#0a0c12')
    img = contrail(img, [(0.25, 0.35), (0.50, 0.26), (0.585, 0.225)], 3, alpha=0.25)
    img = silhouette(img, FIGHTER_SIDE, 0.585, 0.22, 1.25, '#151622', angle=4)
    img = silhouette(img, FIGHTER_SIDE, 0.625, 0.27, 1.1, '#151622', angle=4)
    finish(img, path)


def graficos(path):
    """Caza sobre un terreno en malla verde (gráficos / VR) con el sol en el horizonte."""
    rng = random.Random(71)
    horizon = 0.62
    img = vgradient([(0, '#040a12'), (0.4, '#0a2030'), (horizon, '#1e5a5a'), (1, '#03080c')])
    img = glow(img, 0.78 * CW, horizon * CH, 600 * SS, '#3fe0a0', 0.35, 2.0)
    img = glow(img, 0.78 * CW, horizon * CH, 140 * SS, '#d8fff0', 0.8, 1.5)
    img = stars(img, rng, 120, 0.45)
    # suelo y malla en perspectiva hacia el punto de fuga
    ground = np.zeros((CH, CW), np.float32)
    ground[int(horizon * CH):, :] = 1
    img = blend(img, '#03090d', ground)
    vx, vy = 0.78 * CW, horizon * CH
    def grid(d):
        for i in range(-30, 31):
            x = vx + i * 0.06 * CW
            d.line([(vx + i * 0.004 * CW, vy), (x, CH)], fill=255, width=SS)
        for j in range(1, 14):
            t = (j / 14) ** 2.2
            y = vy + t * (CH - vy)
            d.line([(0, y), (CW, y)], fill=255, width=SS)
    m = mask_from(grid, blur=0.6)
    fade = np.clip((np.arange(CH)[:, None] - vy) / (CH - vy), 0, 1) ** 0.7
    img = blend(img, '#2fe39a', m * fade * 0.55)
    # montañas en malla al fondo
    line = ridge(rng, horizon + 0.005, 0.16)
    yy = np.arange(CH)[:, None]
    mm = ((yy >= line[None, :]) & (yy < horizon * CH)).astype(np.float32)
    img = blend(img, '#06141a', mm)
    edge = mask_from(lambda d: d.line([(i, line[i]) for i in range(0, CW, 4)], fill=255, width=2 * SS), blur=0.8)
    img = blend(img, '#3ff0a8', edge * 0.6)
    img = silhouette(img, FIGHTER_SIDE, 0.70, 0.30, 2.3, '#020407', angle=6)
    img = afterburner(img, 0.70, 0.30, 2.3, 6, 0.7)
    finish(img, path)


SCENES = {
    'monitor': monitor, 'ejecucion': ejecucion, 'perfiles': perfiles, 'hangar': hangar,
    'mods': mods, 'instantaneas': instantaneas, 'graficos': graficos,
}


# ------------------------------------------------------------------ aeronaves (tarjetas de la pantalla Perfiles)

def circle(cx, cy, r, n=28):
    return [(cx + r * math.cos(2 * math.pi * i / n), cy + r * math.sin(2 * math.pi * i / n)) for i in range(n)]


# Cessna 172: ala alta con montante, tren fijo, hélice (de perfil, morro a la derecha).
C172_SIDE = {
    'body': [(0, 9), (10, 8), (30, 7.5), (48, 9.0), (54, 13), (76, 13), (80, 10), (88, 7), (95, 6), (98, 4.6), (100, 3),
             (98, 1.4), (94, 0), (85, -1), (70, -2), (55, -2), (40, 0), (20, 3), (5, 5.5), (0, 6)],
    'wing': [(50, 12.6), (78, 12.6), (77.5, 15.4), (57, 15.8), (51.5, 14.8)],
    'strut': [(57, -0.5), (59, -0.5), (67, 13), (65, 13)],
    'fin': [(0, 9), (2.5, 23), (8.5, 23), (17, 8)],
    'stab': [(-1.5, 6.8), (15, 7.3), (15, 6.0)],
    'gear': [(58, -2), (60, -2), (63.5, -7), (61.5, -7)],
    'wheel': circle(62.5, -8.3, 2.3),
    'ngear': [(91, -0.5), (92.5, -0.5), (93.2, -7), (92, -7)],
    'nwheel': circle(92.6, -8.4, 1.9),
    'spinner': circle(100.2, 3, 1.6),
}
C172_PROP = circle(101, 3, 1, n=40)

# AH-64D: tren de cola, cabina en tándem escalonada, torreta de sensores en el morro, cañón bajo la barbilla,
# alas cortas con cargas, góndolas de los motores y la cúpula del radar sobre el mástil.
AH64_SIDE = {
    'body': [(0, 6.5), (44, 6.5), (50, 10.5), (56, 12.8), (66, 12.8), (68, 14.2), (75, 14.2), (77, 11.6), (84, 11.2),
             (88, 8.2), (94, 5), (97, 3), (97, 1), (94, -0.6), (86, -2.2), (62, -3.2), (52, -2.2), (46, 2.5), (0, 3.6)],
    'tads': circle(96.5, 0.5, 2.7),
    'gun': [(83, -3), (84.5, -3), (88.5, -6.5), (87.6, -7)],
    'wing': [(55, 2.4), (69, 2.4), (69, 0.8), (55, 0.8)],
    'pod': [(56, 0.8), (63, 0.8), (63.5, -1.6), (56.5, -1.6)],
    'missiles': [(64, 0.8), (70.5, 0.8), (71.5, -0.4), (70.5, -1.8), (64, -1.8)],
    'mast': [(59.8, 12.8), (62.2, 12.8), (62.2, 16.6), (59.8, 16.6)],
    'radar': circle(61, 18.2, 3.0, n=36),
    'rotor': [(4, 17.2), (120, 16.4), (120, 15.8), (4, 16.6)],
    'fin': [(0, 6), (-1.5, 18), (3.5, 18), (8, 6)],
    'trotor': [(1.6, 13), (2.6, 13), (2.6, 21.5), (1.6, 21.5)],
    'stab': [(-5, 4.2), (10, 4.2), (10, 3.0), (-5, 3.0)],
    'gear': [(65, -3), (66.6, -3), (65.4, -8.5), (63.8, -8.5)],
    'wheel': circle(64.6, -9.6, 2.6),
    'twheel': circle(6, -0.2, 1.5),
    'tleg': [(6.6, 3.6), (7.6, 3.6), (6.5, 0), (5.6, 0)],
}

# UH-1H "Huey": morro redondeado, cabina grande, carenado del motor en el techo, cola elevada, patines y rotor bipala.
UH1_SIDE = {
    'body': [(0, 9.5), (12, 8.2), (30, 7.6), (44, 9), (50, 12), (70, 12.6), (76, 11.6), (85, 10.2), (91, 7.5), (94.5, 4),
             (95, 1), (93, -1.2), (88, -2.6), (78, -3.2), (55, -3.2), (48, 0), (36, 4), (12, 5.6), (0, 6.6)],
    'fin': [(0, 6.6), (-1.5, 17), (3.5, 17), (8, 8.5)],
    'trotor': [(0.6, 9.5), (1.6, 9.5), (1.6, 19), (0.6, 19)],
    'stab': [(28, 6.6), (35, 6.6), (35, 5.4), (28, 5.4)],
    'mast': [(61.8, 12.6), (64.2, 12.6), (64.2, 15.8), (61.8, 15.8)],
    'bar': [(56, 15.9), (70, 15.9), (70, 15.2), (56, 15.2)],
    'rotor': [(8, 16.4), (118, 15.6), (118, 15.0), (8, 15.8)],
    'strut1': [(57, -3), (58.6, -3), (57.4, -8.2), (55.8, -8.2)],
    'strut2': [(80, -3), (81.6, -3), (83.2, -8.2), (81.6, -8.2)],
    'skid': [(46, -8.2), (90, -8.2), (93, -6.8), (92.2, -6.2), (89.4, -7.4), (46, -9.4)],
}

CARD_W, CARD_H = 1500, 300


def set_size(w, h):
    global W, H, CW, CH
    W, H, CW, CH = w, h, w * SS, h * SS


def fields(img, rng, horizon):
    """Campos de cultivo en perspectiva bajo el horizonte (luz de atardecer)."""
    hy = horizon * CH
    def draw(d, i):
        y0 = hy
        k = 0
        while y0 < CH:
            h = 3 * SS + (y0 - hy) * 0.18
            if k % 2 == i:
                d.rectangle([0, y0, CW, y0 + h], fill=255)
            y0 += h
            k += 1
    below = (np.arange(CH)[:, None] > hy).astype(np.float32)
    img = blend(img, '#2c3420', below)
    a = mask_from(lambda d: draw(d, 0))
    img = blend(img, '#3d4526', a * below * 0.8)
    b = mask_from(lambda d: draw(d, 1))
    img = blend(img, '#4a4424', b * below * 0.6)
    # neblina dorada hacia el horizonte
    t = np.clip(1 - (np.arange(CH)[:, None] - hy) / (CH - hy), 0, 1) ** 2.5
    return blend(img, '#d9a876', t * below * 0.75)


def card_c172(path):
    rng = random.Random(81)
    horizon = 0.70
    img = vgradient([(0, '#2a3f6e'), (0.45, '#7a86a8'), (horizon, '#f6c27a'), (1, '#f6c27a')])
    img = glow(img, 0.88 * CW, horizon * CH, 520 * SS, '#ffe2a8', 0.8, 1.7)
    img = clouds(img, 82, 0.15, 0.50, '#ffe3c0', '#8d7f9a', density=0.42, cell=110, stretch=5, alpha=0.8)
    img = mountains(img, rng, horizon + 0.02, 0.10, '#3c4a3a', '#c99a7a', 0.45)
    img = fields(img, rng, horizon + 0.02)
    img = silhouette(img, C172_SIDE, 0.66, 0.45, 3.3, '#0b0d12', angle=4)
    (px, py), = place([(101, 3)], 0.66, 0.45, 3.3, 4)
    img = blend(img, '#0b0d12', mask_from(lambda d: d.ellipse([px - 2 * SS, py - 22 * SS, px + 2 * SS, py + 22 * SS], fill=255), blur=1.2) * 0.35)
    finish(img, path, vignette=0.3)


def card_ah64(path):
    rng = random.Random(91)
    img = vgradient([(0, '#160f28'), (0.40, '#4a2440'), (0.66, '#c04a32'), (0.82, '#f0903f'), (1, '#f7b25a')])
    img = glow(img, 0.24 * CW, 0.84 * CH, 640 * SS, '#ffb066', 0.6, 1.7)
    img = glow(img, 0.24 * CW, 0.84 * CH, 90 * SS, '#fff0c0', 1.0, 1.5)
    # mesetas del desierto
    for base, amp, col, fog in [(0.86, 0.22, '#5a2a2e', 0.4), (0.95, 0.18, '#35181e', 0.15), (1.04, 0.12, '#1a0c10', 0.0)]:
        img = mountains(img, rng, base, amp, col, '#d07a5a', fog)
    img = silhouette(img, AH64_SIDE, 0.62, 0.46, 3.6, '#08060a', angle=-5)
    def dust(d):
        for _ in range(50):
            x = rng.uniform(0.50, 0.85) * CW
            y = rng.uniform(0.86, 1.0) * CH
            r = rng.uniform(20, 70) * SS
            d.ellipse([x - r, y - r * 0.35, x + r, y + r * 0.35], fill=120)
    img = blend(img, '#d79a74', mask_from(dust, blur=16) * 0.22)
    finish(img, path, vignette=0.3)


def card_uh1h(path):
    rng = random.Random(101)
    img = vgradient([(0, '#22324a'), (0.5, '#6f8794'), (0.72, '#d9c49a'), (1, '#c8b48c')])
    img = glow(img, 0.80 * CW, 0.60 * CH, 520 * SS, '#fff0c8', 0.55, 1.8)
    for base, amp, col, fog in [(0.70, 0.30, '#3a5248', 0.6), (0.82, 0.30, '#22382c', 0.4), (0.93, 0.26, '#13231a', 0.15)]:
        img = mountains(img, rng, base, amp, col, '#c9c2a2', fog)
    # río con reflejo
    def river(d):
        d.polygon([(0.30 * CW, CH), (0.55 * CW, 0.86 * CH), (0.72 * CW, 0.84 * CH), (0.62 * CW, 0.90 * CH), (0.48 * CW, CH)], fill=255)
    img = blend(img, '#a9ab92', mask_from(river, blur=6) * 0.35)
    img = clouds(img, 102, 0.55, 0.85, '#efe6cc', '#a7a28c', density=0.35, cell=120, stretch=6, alpha=0.5)
    img = silhouette(img, UH1_SIDE, 0.64, 0.44, 3.5, '#0a0d0c', angle=-4)
    finish(img, path, vignette=0.3)


def card_jet(path):
    rng = random.Random(111)
    img = vgradient([(0, '#0b1230'), (0.40, '#2e1f4d'), (0.68, '#93404a'), (0.86, '#ee8a45'), (1, '#ffd27d')])
    img = glow(img, 0.86 * CW, 0.88 * CH, 520 * SS, '#ffb35a', 0.55, 1.8)
    img = clouds(img, 112, 0.74, 1.05, '#ffc98a', '#4f2a3c', density=0.55, cell=90)
    img = contrail(img, [(0.25, 0.95), (0.50, 0.68), (0.61, 0.52)], 4, alpha=0.3)
    img = silhouette(img, FIGHTER_SIDE, 0.62, 0.50, 3.4, '#120a12', angle=20)
    img = afterburner(img, 0.62, 0.50, 3.4, 20, 0.9)
    finish(img, path, vignette=0.3)


def card_helo(path):
    rng = random.Random(121)
    img = vgradient([(0, '#101634'), (0.4, '#3a2d5c'), (0.7, '#b4566a'), (0.9, '#f19a62'), (1, '#f6b878')])
    for base, amp, col, fog in [(0.82, 0.40, '#3b2f5b', 0.5), (0.92, 0.36, '#2a2245', 0.3), (1.04, 0.30, '#0d0b1a', 0.0)]:
        img = mountains(img, rng, base, amp, col, '#c27a7c', fog)
    img = silhouette(img, HELO_SIDE, 0.64, 0.44, 3.6, '#07060c', angle=-5)
    finish(img, path, vignette=0.3)


def card_ga(path):
    card_c172(path)


CARDS = {'c172': card_c172, 'ah64': card_ah64, 'uh1h': card_uh1h, 'jet': card_jet, 'helo': card_helo, 'ga': card_ga}

if __name__ == '__main__':
    out = sys.argv[1] if len(sys.argv) > 1 else '.'
    only = sys.argv[2:]
    os.makedirs(out, exist_ok=True)
    if only and only[0] == 'aviones':
        # Tarjetas de aeronave (1500x300) para la pantalla Perfiles.
        set_size(CARD_W, CARD_H)
        for name in only[1:] or list(CARDS):
            CARDS[name](os.path.join(out, f'{name}.jpg'))
    else:
        for name in only or list(SCENES):
            SCENES[name](os.path.join(out, f'{name}.jpg'))
