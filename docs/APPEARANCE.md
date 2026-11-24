# Appearance

Appearance is optional. Existing settings without appearance overrides retain their original theme, spacing,
widget-specific padding and responsive corner radii.

## Desktop editor

Open **General → Appearance** for global defaults. Choose a surface preset, then adjust:

| Setting | Range | Scope |
|---|---|---|
| Font | Original / sans / rounded / condensed / serif / mono | Global, page, widget |
| Font size | 50–200% | Global, page, widget |
| Corner radius | 0–32 CSS pixels | Global, page, widget |
| Widget spacing | 0–24 CSS pixels | Global, page |
| Widget inner padding | 0–24 CSS pixels | Global, page, widget |
| Page edge padding | 0–32 CSS pixels | Global, page |
| Widget border | Inherit / on / off | Global, page, widget |
| Widget shadow | Inherit / on / off | Global, page, widget |

Use **Show live appearance preview** and choose a page to see unsaved settings in the real panel renderer.
Save applies changes to connected phones. The preview does not save the settings by itself.

In **Pages**, open **Page appearance** to override global defaults. Select a widget and open **Widget appearance**
in its inspector to override that page. Widget duplication and page export/import keep these settings; Smart Stack
children inherit their stack's appearance.

## Presets and inheritance

- **Original appearance** restores the original renderer at that scope, including responsive corners and each
  widget's original padding. It can restore one original page or widget underneath a customized global preset.
- **Rounded cards** uses 18px corners, 12px spacing/padding, borders and no shadow.
- **Flat surfaces** uses solid surfaces, 12px corners/spacing/padding, no border and no shadow. Radius and spacing
  remain independent controls: a flat surface can have rounded corners.
- **Content only** uses transparent surfaces, square corners, 12px spacing/padding and no border/shadow.
- **Seamless panel** uses square corners, zero grid spacing, 12px inner padding and borders.

Choosing a preset replaces overrides at that scope. Individual adjustments then override its defaults. **Inherit**
on an individual control removes only that explicit adjustment, restoring the preset/parent value.
**Reset to inherited appearance** removes all appearance overrides at that scope.

Theme and accent remain under Display. Flat removes decorative surface gradients/blur/shadow defaults while
retaining meaningful content such as album art, signal-meter colors and circular dials. Explicit shadow and border
choices take precedence over a preset. Page edge padding always reserves the device safe area and page-navigation
space. Grid spacing uses CSS grid gap, keeping widget coordinates unchanged.

## Configuration

The same optional object is supported at `display.appearance`, `pages[].appearance` and
`pages[].widgets[].appearance`. Grid spacing and page edge padding are ignored at widget scope.

```json
{
  "display": {
    "appearance": {
      "style": "flat",
      "font": "sans",
      "fontScale": 120,
      "radius": 18,
      "gap": 12,
      "padding": 10,
      "border": false,
      "shadow": false
    }
  }
}
```

Supported style values are `default`, `cards`, `flat`, `borderless` and `seamless`. Supported font values are
`default`, `sans`, `rounded`, `condensed`, `serif` and `mono`, and `fontScale` is clamped between `50` and `200`.
Omitted/null properties inherit. Zero and false are explicit choices. Imported numeric values are bounded;
unknown style or font names are discarded, and appearance does not accept arbitrary CSS.

## Animated background

Settings › Display › Background can replace the theme's flat colour and glow with an animated ASCII ocean
(vendored [ascii-sharks](https://github.com/stackedlol/ascii-sharks), MIT). Card geometry, frames, spacing and
the grid are untouched: the ocean is a fixed canvas layer underneath the pages, so it works with every layout.
Card fill decides whether the inside of each card tints the animation or stays transparent.

The stage is true black and draws no grid lines, so an OLED panel keeps those pixels off. The night screen and
the idle-dim veil hide the canvas, which also pauses the animation through its IntersectionObserver; page
switching, rules and the rest of the panel keep working over it. The canvas is rasterised at CSS pixels and kept
opaque (the brightness is painted inside the canvas, not with CSS `opacity`).

Cards default to **no fill** (`display.cardFill: "none"`): the animation keeps painting full-screen behind them,
and a thin coloured frame keeps the areas apart. The frame colour and opacity are settings; the frame corner
radius and spacing stay as before. Only the frame line and the widget content itself are lit — empty water and
empty card areas stay #000. `display.cardFill: "theme"` restores the old translucent card fill.

| Setting | Values | Default |
|---|---|---|
| Background | solid / sharks / koi / jellyfish | solid |
| Card fill | none (OLED frames) / theme (translucent) | none |
| Frame colour / opacity | any colour / 0–100 % | #9aa3ad / 28 % |
| Sharks | a species and a colour per shark, 1–8 sharks | five muted defaults |
| Koi | count 1–14; natural schemes or one colour per koi | 5, natural |
| Jellyfish | count 1–8, the four species cycle | 4 |
| Detail | high (2x raster, 160 columns) / balanced / battery saver | high |
| Fish | 0–400 | 140 |
| Frames per second | 24–60 | 24 |
| Brightness | 20–100 % | 70 |
| One still frame | on / off | off |

The simulation always steps at 60 Hz; `oceanFps` only throttles painting. On the desk tablet (2560 × 1600)
painting costs roughly 1.5 cores at the high default and about 1.1 at balanced; the solid background is ~0.15
and a still frame is close to it. `oceanStill` settles the tank and paints a single frame instead of animating.
Each shark is edited on its own row in Settings › Display: a species (classic, hammerhead, zebra, tiger, great
hammerhead, mako, thresher, nurse or whale) and a colour picker. The list is stored as
`display.oceanSharks: [{ "kind": "whale", "color": "#a9c7e8" }, …]`; a missing or invalid colour keeps the
species' own tint. A picked colour is the shark's skin colour: body blocks, fins, markings, the eye and the
glyphs are all shades of that one colour, so it controls the whole animal instead of a lit patch. The defaults
are deliberately muted pastels, and the Brightness slider dims the whole ocean. Fish are tinted per species
(sardine, anchovy, silverside, mackerel, goby) so the school reads as colour over the black water.

The koi pond is the vendored [ascii-koi](https://github.com/pursurer/ascii-koi) engine (MIT): black water with
no caustic net, 1–14 koi, and either the project's own kohaku/sanke/tancho schemes or a colour per koi
(`display.koiCount`, `display.koiColourMode: "preset" | "custom"`, `display.koiColours: ["#e08a4a", …]`). A
custom colour becomes the koi's body and its saddles/fins are shades of it. The jellyfish scene is the vendored
[ascii-jellyfish](https://github.com/lazaro-guerrero-losada/ascii-jellyfish) engine (MIT): moon, compass,
lion's mane and crystal jellyfish cycle through `display.jellyCount` (1–8) in their natural tints. Both engines
are loaded only for the chosen background; Detail, FPS and Brightness apply to all three scenes. The koi pond
does not use an IntersectionObserver of its own, so the panel pauses it explicitly with the night screen and
the idle-dim veil.

The `amoled` theme only changes colours; it does not make the cards black. True-black cards come from Card fill
= none plus the frame settings. A 1px frame is still a lit pixel: set Frame opacity to 0 for a completely
frameless black surface, at the cost of not seeing where one card ends and the next begins.
