# Icone della BMS Suite: rombo grafite con bordo ambra (marchio comune) e un simbolo per ogni programma.
# Disegnate a 1024 px e ridotte (antialias); salvate come .ico multi-dimensione (16..256) e .png 256.
from PIL import Image, ImageDraw
import math, sys

S = 1024
AMBER = (242, 169, 0, 255)
HUD = (110, 231, 142, 255)
LIGHT = (230, 228, 222, 255)
DARK = (32, 35, 39, 255)
DARK2 = (23, 25, 28, 255)

def base():
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    c = S / 2; r = S * 0.495; t = S * 0.075      # bordo ambra pieno (niente giunzioni sugli spigoli)
    def dia(rr): return [(c, c - rr), (c + rr, c), (c, c + rr), (c - rr, c)]
    d.polygon(dia(r), fill=AMBER)
    d.polygon(dia(r - t), fill=DARK)
    ri = r - t   # leggera luce sulla meta' alta
    d.polygon([(c, c - ri), (c + ri, c), (c - ri, c)], fill=(40, 44, 49, 255))
    return im, d

def P(x, y):  # coordinate in unita' 0..100 dentro il riquadro centrale
    m = S * 0.25; w = S - 2 * m
    return (m + x / 100 * w, m + y / 100 * w)

def poly(d, pts, **kw): d.polygon([P(*p) for p in pts], **kw)
def line(d, pts, w, fill): d.line([P(*p) for p in pts], fill=fill, width=int(S * w), joint="curve")
def circle(d, cx, cy, r, **kw):
    a = P(cx - r, cy - r); b = P(cx + r, cy + r); d.ellipse([a, b], **kw)

def suite(d):     # sagoma di caccia vista dall'alto (ali a delta, deriva)
    poly(d, [(50, 2), (57, 30), (96, 66), (96, 76), (58, 64), (56, 82), (70, 94), (50, 90), (30, 94), (44, 82), (42, 64), (4, 76), (4, 66), (43, 30)], fill=AMBER)
    poly(d, [(50, 14), (54, 30), (50, 40), (46, 30)], fill=LIGHT)

def terrain(d):   # montagne e orizzonte
    poly(d, [(0, 80), (34, 28), (58, 62), (70, 46), (100, 80)], fill=AMBER)
    poly(d, [(26, 41), (34, 28), (42, 41), (34, 37)], fill=LIGHT)
    line(d, [(0, 92), (100, 92)], 0.035, LIGHT)

def objective(d): # bandierina sull'asta
    line(d, [(26, 6), (26, 98)], 0.05, LIGHT)
    poly(d, [(28, 8), (88, 22), (28, 40)], fill=AMBER)
    line(d, [(10, 98), (46, 98)], 0.05, LIGHT)

def art(d):       # tavolozza con tre colori
    circle(d, 50, 52, 46, fill=AMBER)
    circle(d, 68, 70, 11, fill=DARK)            # foro per il pollice
    circle(d, 30, 34, 9, fill=LIGHT)
    circle(d, 52, 24, 9, fill=HUD)
    circle(d, 26, 60, 9, fill=DARK2)

def campaign(d):  # carta piegata con rotta
    poly(d, [(4, 18), (34, 8), (66, 18), (96, 8), (96, 84), (66, 94), (34, 84), (4, 94)], fill=AMBER)
    poly(d, [(34, 8), (66, 18), (66, 94), (34, 84)], fill=(214, 148, 0, 255))
    pts = [(14, 76), (30, 58), (50, 64), (70, 40), (86, 28)]
    for i in range(len(pts) - 1):
        (x0, y0), (x1, y1) = pts[i], pts[i + 1]
        for k in range(0, 10, 2):   # tratteggio
            a, b = k / 10, (k + 1) / 10
            line(d, [(x0 + (x1 - x0) * a, y0 + (y1 - y0) * a), (x0 + (x1 - x0) * b, y0 + (y1 - y0) * b)], 0.03, DARK2)
    circle(d, 86, 28, 6, fill=DARK2)

def model(d):     # cubo isometrico
    poly(d, [(50, 6), (92, 28), (50, 50), (8, 28)], fill=LIGHT)
    poly(d, [(8, 28), (50, 50), (50, 96), (8, 74)], fill=AMBER)
    poly(d, [(92, 28), (50, 50), (50, 96), (92, 74)], fill=(196, 136, 0, 255))

def interchange(d):  # due frecce opposte
    poly(d, [(4, 24), (62, 24), (62, 6), (96, 34), (62, 62), (62, 44), (4, 44)], fill=AMBER)
    poly(d, [(96, 56), (38, 56), (38, 38), (4, 66), (38, 94), (38, 76), (96, 76)], fill=LIGHT)

def sim(d):       # missile in salita e onde del radar
    import math
    ux, uy = 1 / math.sqrt(2), -1 / math.sqrt(2)      # verso alto-destra
    nx, ny = -uy, ux                                   # normale
    def at(t, k): return (12 + ux * t + nx * k, 88 + uy * t + ny * k)
    hw = 7.5
    poly(d, [at(0, -hw), at(58, -hw), at(74, 0), at(58, hw), at(0, hw)], fill=AMBER)       # fusoliera con ogiva
    poly(d, [at(4, hw), at(22, hw), at(6, hw + 14)], fill=LIGHT)                              # alette
    poly(d, [at(4, -hw), at(22, -hw), at(6, -hw - 14)], fill=LIGHT)
    poly(d, [at(40, hw), at(50, hw), at(42, hw + 8)], fill=AMBER)
    poly(d, [at(40, -hw), at(50, -hw), at(42, -hw - 8)], fill=AMBER)
    for r in (16, 30, 44):                                                                    # onde del radar
        a2 = P(98 - r, 2 - r); b2 = P(98 + r, 2 + r)
        d.arc([a2, b2], start=95, end=175, fill=LIGHT, width=int(S * 0.05))

ICONS = {"BMSSuite": suite, "TerrainTool": terrain, "ObjectiveStudio": objective, "ArtEditor": art,
         "CampaignEditor": campaign, "ModelEditor": model, "DatabaseInterchange": interchange, "SimEditor": sim}

sizes = [256, 128, 64, 48, 32, 24, 16]
out = sys.argv[1] if len(sys.argv) > 1 else "."
for name, fn in ICONS.items():
    im, d = base()
    fn(d)
    big = im.resize((256, 256), Image.LANCZOS)
    big.save(f"{out}/{name}.png")
    big.save(f"{out}/{name}.ico", sizes=[(s, s) for s in sizes])
# anteprima: tutte le icone a 256, 48 e 16 su fondo chiaro e scuro
names = list(ICONS)
sheet = Image.new("RGBA", (len(names) * 280 + 20, 620), (240, 240, 240, 255))
dk = ImageDraw.Draw(sheet)
dk.rectangle([0, 310, sheet.width, 620], fill=(23, 25, 28, 255))
for i, n in enumerate(names):
    ic = Image.open(f"{out}/{n}.png")
    for row, y0 in ((0, 20), (1, 330)):
        x = 20 + i * 280
        sheet.alpha_composite(ic, (x, y0))
        sheet.alpha_composite(ic.resize((48, 48), Image.LANCZOS), (x + 40, y0 + 230))
        sheet.alpha_composite(ic.resize((16, 16), Image.LANCZOS), (x + 140, y0 + 246))
        sheet.alpha_composite(ic.resize((32, 32), Image.LANCZOS), (x + 190, y0 + 238))
sheet.convert("RGB").save(f"{out}/preview.png")
print("ok")
