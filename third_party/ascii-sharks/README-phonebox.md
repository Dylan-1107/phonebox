# ascii-sharks (vendored)

Source: <https://github.com/stackedlol/ascii-sharks> at commit `fff1af0` (2026-11-19), MIT,
copyright (c) 2026 drewdi. Only `src/` and `LICENSE` are copied; the demo pages and the Vite setup
stay upstream.

The panel bundles this into `android/app/src/main/assets/www/ocean.js` (flat in `www/`, because the
agent embeds only `www/*.*` and `www/widgets/*.*` and `/panel/{*file}` has no nested routes).

Build (no node_modules needed):

```sh
npx --yes esbuild third_party/ascii-sharks/src/index.ts --bundle --format=esm --minify \
  --target=chrome80 --legal-comments=none \
  --outfile=android/app/src/main/assets/www/ocean.js
```

Then prepend the `/*! ascii-sharks (MIT) … */` header that is already at the top of `ocean.js`
(the build overwrites it).

## PhoneBox patches

| File | Change | Why |
|---|---|---|
| `src/renderer.ts` | `PALETTES.dark.grid` is `rgba(0,0,0,0)` | untouched cells must be true OLED black |
| `src/renderer.ts` | `FISH_SCHOOLS` + per-species fish buckets | the school is tinted instead of one pale colour |
| `src/renderer.ts` | no background bitmap; `clearRect` on the opaque context, `dim` fill at the end | true black stage and an opaque layer that is cheap to composite under the cards |
| `src/engine.ts` | `fps` option, render throttle with accumulated dt | 60 fps full-screen canvas is wasteful on the desk tablet |
| `src/engine.ts` | DPR fixed at 1 | backdrop texture; 2x costs 4x the pixels for no visible gain |
| `src/engine.ts` | `dim` and `cellScale` options | brightness without CSS opacity; fewer glyphs when a device needs it |

Everything else is upstream. `OceanEngine(canvas, sharks, { theme: "dark", fish, fps, still, dim } )` is what
`main.js` creates.