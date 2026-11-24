# ASCII Jellyfish (vendored)

Source: <https://github.com/lazaro-guerrero-losada/ascii-jellyfish> at commit `dbb6226`
(MIT, copyright Lazaro Guerrero Losada). The TypeScript tree in that repository is incomplete
(`BaseAsciiRenderer` is missing methods the renderer calls), so the vendored file is the
repository's own working build, `jellyfish.js`, with the PhoneBox patches below applied and a
licence header prepended.

It is copied to `android/app/src/main/assets/www/jelly.js` as-is; it is a UMD script that sets
`window.JellyfishEngine`, which `main.js` reads after `await import('./jelly.js')`.

## PhoneBox patches (`jellyfish.js`)

| Change | Why |
|---|---|
| background fill `#000`, grid strokes transparent | true OLED black, no grid |
| `options.dpr` (0.5–2) on `BaseLoopEngine`, default 1 | the panel's Detail setting controls the raster |
| `options.fps` and an accumulated-dt render throttle | 24 fps instead of 60 |
| `options.pointer` (default true), panel passes false | a backdrop needs no click splashes |
| `options.dim` / `JellyRenderer.dim` black cover | brightness without a CSS opacity layer |
| `JellyfishEngine(canvas, specs, options)` forwards the options | one call from the panel |

Species (`moon`, `compass`, `lion`, `crystal`) and their natural tints are upstream; the panel
cycles them for `display.jellyCount` (1–8).