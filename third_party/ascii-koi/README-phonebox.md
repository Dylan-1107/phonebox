# ASCII Koi (vendored)

Source: <https://github.com/pursurer/ascii-koi> at commit `ed90b6e` (MIT, copyright
pursurer). Only the engine (`pond.js`) is used; the page, clock and cat-paw input stay upstream.

Built into `android/app/src/main/assets/www/koi.js`:

```sh
npx --yes esbuild third_party/ascii-koi/entry.js --bundle --format=esm --minify \
  --target=chrome80 --legal-comments=none \
  --outfile=android/app/src/main/assets/www/koi.js
```

Then prepend the `/*! ascii-koi (MIT) … */` header that the build overwrites.

## PhoneBox patches (`pond.js`)

| Change | Why |
|---|---|
| `opts.count` fixes 1–14 koi (`MOST`, `count()`) | a single koi or a small shoal |
| `opts.koiColours` (one hex per koi) and `customKoi()` | per-koi custom skin; the body, saddles and fins are shades of the picked colour |
| `opts.fps` caps the paint rate | 24 fps is enough on the desk tablet |
| `opts.plainWater` skips the caustic net and sparkles | true OLED black water |
| `opts.veil` paints a black cover over the frame | brightness without a CSS opacity layer |
| `opts.dpr()` is already upstream; the panel passes the detail level | 1:1 physical raster at High |

The palette is supplied by the panel (`koiPalette()` in `main.js`): `water #000`, warm ink,
`dark: true`. Input (paw, food) is page-level upstream and is not wired here.