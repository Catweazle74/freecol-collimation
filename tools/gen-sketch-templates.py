#!/usr/bin/env python3
"""Erzeugt Start-Templates für die FreeCol-Kipp-Phasen-Skizzen.

Kanonische Ausrichtung: OAZ auf 12 Uhr (oben). Die App rotiert die PNGs um die
eingestellte OAZ-Position. Konvention (siehe Justage-Plan): Schraube 1 liegt
immer 180° zum OAZ (also unten), Schrauben 2 und 3 folgen im Uhrzeigersinn.

Ablage standardmäßig in ~/.config/FreeCol/ als
sketch-fangspiegel-kippen.png / sketch-hauptspiegel-kippen.png.
"""
import cairo, math, os, sys

S = 360; C = S / 2; R = 150          # Canvas, Mitte, Tubus-Radius
SCREWS = ((1, 180), (2, 300), (3, 60))  # (Nummer, Winkel im Uhrzeigersinn von oben)

def pt(ang, r):
    a = math.radians(ang)
    return (C + r * math.sin(a), C - r * math.cos(a))

def disc(cx, x, y, r):
    cx.new_sub_path(); cx.arc(x, y, r, 0, 2 * math.pi)

def draw(path, secondary):
    surf = cairo.ImageSurface(cairo.FORMAT_ARGB32, S, S)
    cx = cairo.Context(surf)
    cx.select_font_face("sans-serif", cairo.FONT_SLANT_NORMAL, cairo.FONT_WEIGHT_BOLD)

    # Tubus-Kreis
    cx.set_line_width(2.5); cx.set_source_rgb(.55, .55, .55)
    disc(cx, C, C, R); cx.stroke()

    # OAZ-Rohr oben (12 Uhr), überlagert die Tubuswand
    rw, ri, ro = 26, R - 14, R + 22
    cx.set_source_rgb(.42, .55, .68)
    cx.rectangle(C - rw / 2, C - ro, rw, ro - ri); cx.fill()
    cx.set_line_width(1.5); cx.set_source_rgb(.24, .35, .47)
    cx.rectangle(C - rw / 2, C - ro, rw, ro - ri); cx.stroke()

    # Fangspiegel: große Halterung, die 3 Schrauben liegen innerhalb.
    # Hauptspiegel: 3 Schrauben am Zellenrand, nur Mittelpunkt angedeutet.
    if secondary:
        holder_r, screw_r = 92, 58
        cx.set_source_rgb(.5, .5, .5); cx.set_line_width(1.8)
        disc(cx, C, C, holder_r); cx.stroke()
    else:
        screw_r = 124
        cx.set_source_rgb(.5, .5, .5); cx.set_line_width(1.5)
        disc(cx, C, C, 5); cx.stroke()

    for num, ang in SCREWS:
        x, y = pt(ang, screw_r)
        cx.set_source_rgb(.78, .69, .44); disc(cx, x, y, 14); cx.fill()
        cx.set_source_rgb(.12, .12, .12); cx.set_font_size(17)
        e = cx.text_extents(str(num))
        cx.move_to(x - e.width / 2 - e.x_bearing, y - e.height / 2 - e.y_bearing)
        cx.show_text(str(num))

    surf.write_to_png(path); print("ok", os.path.basename(path))

def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.config/FreeCol")
    os.makedirs(out, exist_ok=True)
    draw(os.path.join(out, "sketch-fangspiegel-kippen.png"), True)
    draw(os.path.join(out, "sketch-hauptspiegel-kippen.png"), False)

if __name__ == "__main__":
    main()
