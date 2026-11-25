---
name: loopscape-style
description: Turn the user's photo or video into a Loopscape wallpaper style (LED dots, halftone, dither, duotone or pixel art) that loops seamlessly, and add it to Loopscape. Use when the user wants their own Loopscape wallpaper or style, or a picture or clip made into an animated dot/LED/pixel wallpaper.
---

# Loopscape style

`scripts/make_style.py` styles a photo or video with dotfx (`scripts/dotfx.py`, by Thibaud Francony, MIT license,
https://github.com/thibaud360/dotfx). It makes the result a 1920x1080 video that loops with no jump, and saves it in
Loopscape's styles folder. Loopscape lists every video in that folder in its tray menu, so the new style can be picked
right away.

The user is usually not technical: explain in plain words, run the commands yourself, and don't ask them to type any.

## Steps

1. **Find the picture or clip** and ask what the style should be called, unless they said. The name is lowercase
   letters, digits and dashes (`harbour`, `city-night`). It can't be one of Loopscape's own styles (riso, shore,
   nebula, …); the script says so if it is.
2. **Check the tools.** Python 3 with Pillow and NumPy, and ffmpeg:
   - `python -c "import PIL, numpy"`; if that fails: `python -m pip install pillow numpy`
   - `ffmpeg -version`; if missing on Windows: `winget install Gyan.FFmpeg` (the script also finds winget's copy when it
     isn't on PATH yet)
3. **Check where Loopscape is.** Its styles folder is `%USERPROFILE%\Loopscape\styles`. If you're not on the user's
   PC (a cloud sandbox) or that folder doesn't exist, add `--styles-dir out` and, at the end, give them the `.mp4` and
   `.png` and tell them to put both in that folder.
4. **Pick the look.** If they didn't name one, render a preview of a few presets and show them side by side:
   `python scripts/make_style.py INPUT --name NAME --preset led --preview led.png` (then `dither`, `pixel`, `soft-led`, …).
   A preview takes a few seconds. Look at each before showing it; if one is washed out or too dark, try `--bright 0.9`
   or `--bright 1.2`.
5. **Make it:** `python scripts/make_style.py INPUT --name NAME --preset PRESET [same tweaks as the preview]`.
   On a laptop a 40-second photo loop takes about 3 minutes; a 60-second video about 4. Say so before you start.
6. **Tell them how to use it:** right-click the Loopscape tray icon > *Change wallpaper* > the new name. Also say:
   - It isn't part of *Rotate day/night*, which only cycles Loopscape's own styles. They pick it from the tray menu.
   - To remove it, delete `NAME.mp4` and `NAME.png` from the styles folder.
   - Its size (the script prints it). If it's large (over about 60 MB), offer `--format hevc` (about half the size;
     most graphics chips from the last several years play it without using the CPU) or bigger dots (`--cell 18`).

## Presets

| Preset | Look |
|---|---|
| `led` (default) | Round dots on black, like an LED screen |
| `soft-led` | Blurred first, soft glowing gradients through LED dots |
| `dither` | Small round dots, ordered dithering, grainy |
| `fine-dither` | Tiny square pixels, bright, full colour |
| `duotone` | Two colours only (blue and white unless `--duo`), print-like |
| `pixel` | Square pixels with lit gaps instead of black |

## Options

- Photos move slowly so the wallpaper isn't a still: `--motion drift` (default: the view circles slowly),
  `breathe` (slow zoom in and out) or `none`. `--seconds` sets the loop length (default 40; longer = slower).
- Videos: the first 60 seconds are used (`--seconds` to change). The last second crossfades into the first, so the
  loop has no jump (`--fade`, 0.1 to 5 seconds); the loop is that much shorter than the clip. Clips need to be longer
  than twice the fade (2 seconds by default). Sound is dropped.
- dotfx tweaks, same as for dotfx itself: `--cell N` (dot size in px: 6 fine, 14 LED, 20+ chunky),
  `--shape circle|square|line`, `--dot 0.2–1.0`, `--bg black|white|soft|#hex`, `--blur PX`, `--levels N`,
  `--bayer 2|4|8`, `--bright`, `--contrast`, `--sat` (1.0 = unchanged), `--glow 0–1`, `--duo DARK LIGHT`
  (two hex colours).
- `--format h264` (default, plays everywhere) or `hevc` (smaller). `--force` replaces an existing style with the same
  name. Everything is 1920x1080 at 24 fps, like Loopscape's own styles.
