# Dockyard icon

Source of truth for the Dockyard app icon. Generated from the byhofman icon suite; regenerate rather than hand-edit.

| File | Use |
| --- | --- |
| `icon.svg` | Rounded tile: web favicon (modern browsers), desktop, docs |
| `icon-full.svg` | Full-bleed square: iOS / iPadOS / App Store (the OS applies its own mask) |
| `adaptive-foreground.svg`, `adaptive-background.svg` | Android adaptive icon layers (108 dp canvas; mark inside the 66 dp safe zone) |
| `png/icon-{16..1024}.png` | Rounded tile rasters; use 192/512 for web-manifest icons with `purpose: "any"` (desktop installs show them as-is) |
| `png/icon-full-{180,192,512,1024}.png` | Full-bleed rasters: apple-touch-icon (180), web-manifest `purpose: "maskable"` (512), store (1024). Store/iOS uploads need no alpha channel: flatten before upload |
| `png/adaptive-*-432.png` | Android adaptive layers at xxxhdpi |
| `favicon.ico` | 16/32/48 favicon |
| `app.ico` | Windows application icon, 16 to 256 |

## Light variant

The same mark on a light tile, with colours deepened where they would wash out on white. The dark tile above stays the default icon; use the light one where a light appearance is wanted: the iOS default appearance next to the dark icon, `prefers-color-scheme: light` favicons, docs and sites on light backgrounds.

| File | Use |
| --- | --- |
| `icon-light.svg`, `icon-light-full.svg` | Rounded tile and full-bleed square |
| `png/icon-light-{16..1024}.png` | Rounded tile rasters |
| `png/icon-light-full-{180,192,512,1024}.png` | Full-bleed rasters (flatten before store/iOS upload) |
| `favicon-light.ico` | 16/32/48 favicon |
