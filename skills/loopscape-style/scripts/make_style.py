#!/usr/bin/env python3
"""Turn a photo or a video into a Loopscape wallpaper style.

The picture gets a dotfx look (LED dots, halftone, dither, pixel art; dotfx.py next to this file, by Thibaud Francony,
MIT), is made into a 1920x1080 video that loops without a jump, and is saved in Loopscape's styles folder. Loopscape
lists it in the tray icon's style menu straight away.

  python make_style.py photo.jpg --name harbour --preset led
  python make_style.py clip.mp4 --name waves --preset dither --preview waves.png   (one frame only, to check the look)

A photo gets slow motion so the wallpaper isn't a still: --motion drift (a slow circle, default), breathe (slow zoom
in and out) or none. A video is cut to --seconds and its end is crossfaded into its start (--fade).
Needs Python 3, Pillow, NumPy and ffmpeg.
"""
import argparse, json, math, os, re, shutil, subprocess, sys, tempfile
from multiprocessing import Pool

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
from PIL import Image, ImageOps
import dotfx

W, H, FPS = 1920, 1080, 24
VIDEO_EXT = (".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".gif")
# Loopscape's own styles: a file with one of these names would be taken for the official video
BUILT_IN = {"spectrum", "halftone", "bluesky", "pixelclouds", "sunset", "riso", "dunes", "lake", "pastel", "crt", "shore",
            "nightclouds", "aurora", "nebula", "moon", "embers", "deepsea", "rain", "bounce", "loop", "gradient", "rotate"}
# same settings as Loopscape's own videos (wallpaper/styles/Render.cs)
ENCODERS = {
    "h264": ["-c:v", "libx264", "-preset", "medium", "-crf", "21", "-g", str(FPS * 2)],
    "hevc": ["-c:v", "libx265", "-preset", "slow", "-crf", "23",
             "-x265-params", f"log-level=error:keyint={FPS * 2}:min-keyint={FPS * 2}", "-tag:v", "hvc1"],
}


def fail(msg):
    sys.exit("make_style: " + msg)


def loopscape_dir():
    return os.path.join(os.path.expanduser("~"), "Loopscape")


def find_tool(name, given=None):
    if given:
        if not os.path.isfile(given):
            fail(f"can't find {given}")
        return given
    p = shutil.which(name)
    if p:
        return p
    # winget's ffmpeg (Gyan.FFmpeg) isn't always on PATH for programs started before it was installed
    root = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "WinGet", "Packages")
    for dirpath, _, files in os.walk(root) if os.path.isdir(root) else []:
        if name + ".exe" in files:
            return os.path.join(dirpath, name + ".exe")
    fail(f"{name} not found. Install it (winget install Gyan.FFmpeg) or pass --ffmpeg C:\\path\\to\\ffmpeg.exe")


def check_name(name, styles_dir, force):
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,39}", name):
        fail("--name: use lowercase letters, digits and dashes, e.g. harbour-night")
    if re.fullmatch(r"con|prn|aux|nul|com[0-9]|lpt[0-9]", name):
        fail(f"--name: Windows doesn't allow '{name}' as a file name; pick another")
    taken = set(BUILT_IN)
    manifest = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "Loopscape", "styles.json")
    try:
        with open(manifest, encoding="utf-8") as f:
            taken |= {s["name"] for s in json.load(f)["styles"]}
    except (OSError, ValueError, KeyError):
        pass
    if name in taken:
        fail(f"'{name}' is one of Loopscape's own styles; pick another name")
    if os.path.exists(os.path.join(styles_dir, name + ".mp4")) and not force:
        fail(f"a style called '{name}' already exists; pick another name, or add --force to replace it")


# ---------- frames ----------
# Workers get the settings once (initializer); jobs are frame numbers (photo) or raw RGB frames (video).
G = {}


def _init(cfg, pad, src):
    G["cfg"], G["pad"] = cfg, pad
    G["src"] = Image.open(src).convert("RGB") if src else None
    if G["src"] is not None:
        G["src"].load()


def styled(frame):
    """dotfx on a padded frame (a whole number of dot cells), cropped to the screen"""
    pw, ph = G["pad"]
    out = dotfx.apply(frame, **G["cfg"])
    x, y = (pw - W) // 2, (ph - H) // 2
    return out.crop((x, y, x + W, y + H)).tobytes()


def photo_frame(job):
    i, n, motion = job
    src, (pw, ph) = G["src"], G["pad"]
    a = 2 * math.pi * i / n
    if motion == "drift":       # zoomed in 12%, the view circles slowly
        z, ox, oy = 1.12, math.cos(a), math.sin(a)
    elif motion == "breathe":   # zooms 2% -> 10% -> 2%
        z, ox, oy = 1.06 - 0.04 * math.cos(a), 0.0, 0.0
    else:
        z, ox, oy = 1.0, 0.0, 0.0
    ww, wh = src.width / z, src.height / z
    cx = src.width / 2 + ox * (src.width - ww) / 2 * 0.9
    cy = src.height / 2 + oy * (src.height - wh) / 2 * 0.9
    box = (cx - ww / 2, cy - wh / 2, cx + ww / 2, cy + wh / 2)
    return styled(src.resize((pw, ph), Image.BICUBIC, box=box))


def video_frame(raw):
    return styled(Image.frombytes("RGB", G["pad"], raw))


def fit_filter(pw, ph):
    """ffmpeg filters: square pixels (some clips have stretched ones), then scale and centre-crop to pw x ph"""
    return ("scale=trunc(iw*sar/2)*2:ih,setsar=1,"
            f"scale={pw}:{ph}:force_original_aspect_ratio=increase:flags=lanczos,crop={pw}:{ph}")


def cover(img, pw, ph):
    """scale and centre-crop to exactly pw x ph"""
    return ImageOps.fit(img, (pw, ph), Image.LANCZOS)


def video_source(path, ff, pw, ph, seconds, fade):
    """Frames of the clip at 24 fps and pw x ph, made to loop: the last `fade` seconds blend into the first ones.
    Plays frames fade..end-fade, then the end crossfading into frame fade-1, which leads back to the start."""
    size = pw * ph * 3
    dec = subprocess.Popen([ff, "-v", "error", "-i", path, "-t", str(seconds), "-an",
                            "-vf", f"fps={FPS}," + fit_filter(pw, ph),
                            "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], stdout=subprocess.PIPE)
    nf = max(1, int(round(fade * FPS)))
    head, tail = [], []
    try:
        while True:
            raw = dec.stdout.read(size)
            if len(raw) < size:
                break
            if len(head) < nf:
                head.append(raw)
                continue
            tail.append(raw)
            if len(tail) > nf:
                yield tail.pop(0)
        if dec.wait() != 0:
            fail("ffmpeg couldn't read the whole video (the message above says why)")
    finally:
        if dec.poll() is None:      # stopped early (an error or Ctrl+C): don't leave ffmpeg running
            dec.kill()
            dec.wait()
        dec.stdout.close()
    if len(head) < nf or len(tail) < nf:
        fail(f"the clip is too short to loop; it needs to be longer than {2 * fade:g} seconds (or use a smaller --fade)")
    for i in range(nf):
        k = (i + 1) / (nf + 1)    # mostly the end at first, mostly the start at the last frame
        a = np.frombuffer(tail[i], np.uint8).astype(np.float32)
        b = np.frombuffer(head[i], np.uint8).astype(np.float32)
        yield (a * (1 - k) + b * k + 0.5).astype(np.uint8).tobytes()


def batched(it, n):
    batch = []
    for x in it:
        batch.append(x)
        if len(batch) == n:
            yield batch
            batch = []
    if batch:
        yield batch


def main():
    a = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    a.add_argument("input", help="a photo (jpg, png, ...) or a video (mp4, mov, webm, gif, ...)")
    a.add_argument("--name", required=True, help="style name shown in Loopscape, e.g. harbour (lowercase, dashes)")
    a.add_argument("--preset", default="led", choices=list(dotfx.PRESETS), help="dotfx look (default led)")
    a.add_argument("--motion", default="drift", choices=["drift", "breathe", "none"], help="photos only")
    a.add_argument("--seconds", type=float, help="loop length (photo default 40; video: the first 60 s by default, minus --fade)")
    a.add_argument("--fade", type=float, default=1.0, help="video only: seconds of crossfade at the loop point (0.1-5)")
    a.add_argument("--format", default="h264", choices=list(ENCODERS), help="h264 plays everywhere (default)")
    a.add_argument("--preview", metavar="PNG", help="only render the first frame to this file, to check the look")
    a.add_argument("--force", action="store_true", help="replace an existing style with the same name")
    a.add_argument("--styles-dir", default=os.path.join(loopscape_dir(), "styles"))
    a.add_argument("--ffmpeg")
    a.add_argument("--workers", type=int, default=max(1, (os.cpu_count() or 2) - 1))
    # dotfx overrides (see SKILL.md)
    for k in ("cell", "levels", "bayer"):
        a.add_argument("--" + k, type=int)
    for k in ("dot", "blur", "sat", "bright", "contrast", "glow"):
        a.add_argument("--" + k, type=float)
    a.add_argument("--shape", choices=["circle", "square", "line"])
    a.add_argument("--bg", help="black, white, soft or #hex")
    a.add_argument("--duo", nargs=2, metavar=("DARK", "LIGHT"))
    o = a.parse_args()

    if not os.path.isfile(o.input):
        fail(f"can't find {o.input}")
    if not 0.1 <= o.fade <= 5:      # each second of fade keeps 48 full frames in memory
        fail("--fade must be between 0.1 and 5 seconds")
    if o.seconds is not None and o.seconds <= 0:
        fail("--seconds must be more than 0")
    is_video = o.input.lower().endswith(VIDEO_EXT)
    if not o.preview:
        check_name(o.name, o.styles_dir, o.force)
    ff = find_tool("ffmpeg", o.ffmpeg)

    cfg = {**dotfx.PRESETS[o.preset],
           **{k: getattr(o, k) for k in ("cell", "levels", "bayer", "dot", "blur", "sat", "bright", "contrast", "glow",
                                         "shape", "bg", "duo") if getattr(o, k) is not None}}
    cell = max(1, int(cfg.get("cell", dotfx.DEFAULTS["cell"])))
    pw, ph = math.ceil(W / cell) * cell, math.ceil(H / cell) * cell    # whole cells, cropped to 1920x1080 after
    cfg["width"] = pw

    tmp = tempfile.mkdtemp(prefix="loopscape-style-")
    enc = None
    try:
        if is_video and o.preview:
            # the loop starts --fade seconds into the clip
            r = subprocess.run([ff, "-v", "error", "-ss", str(o.fade), "-i", o.input, "-frames:v", "1", "-an",
                                "-vf", fit_filter(pw, ph), "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
                               capture_output=True)
            raw = r.stdout
            if len(raw) < pw * ph * 3:
                fail("couldn't read a frame from the video: " + r.stderr.decode(errors="replace").strip())
            frames, work, init_src = iter([raw]), video_frame, None
        elif is_video:
            seconds = min(o.seconds or 60.0, 600.0)
            frames = video_source(o.input, ff, pw, ph, seconds, o.fade)
            work, init_src = video_frame, None
        else:
            src = ImageOps.exif_transpose(Image.open(o.input)).convert("RGB")
            src = cover(src, pw, ph)
            init_src = os.path.join(tmp, "source.png")
            src.save(init_src)
            n = max(1, int(round((o.seconds or 40.0) * FPS)))
            frames = ((i, n, o.motion) for i in range(n))
            work = photo_frame

        if o.preview:       # one frame: no worker processes needed
            _init(cfg, (pw, ph), init_src)
            Image.frombytes("RGB", (W, H), work(next(iter(frames)))).save(o.preview)
            print(f"preview: {o.preview}")
            return

        video = os.path.join(tmp, o.name + ".mp4")
        with Pool(o.workers, initializer=_init, initargs=(cfg, (pw, ph), init_src)) as pool:
            enc = subprocess.Popen([ff, "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}",
                                    "-r", str(FPS), "-i", "-",
                                    "-vf", "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p",
                                    *ENCODERS[o.format],
                                    "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
                                    "-movflags", "+faststart", video], stdin=subprocess.PIPE)
            count, still = 0, None
            try:
                for batch in batched(frames, o.workers * 2):     # bounded, so long videos don't fill the memory
                    for out in pool.map(work, batch):
                        if still is None:
                            still = out
                        enc.stdin.write(out)
                        count += 1
                    print(f"\r{count} frames", end="", file=sys.stderr, flush=True)
                print(file=sys.stderr)
                enc.stdin.close()
            except OSError:         # the encoder quit early; its own message is above
                pass
            finally:
                if hasattr(frames, "close"):
                    frames.close()  # stops the video decoder if we stopped early
            if enc.wait() != 0:
                fail("ffmpeg failed while encoding" + (" (does this ffmpeg have libx265? try --format h264)"
                                                       if o.format == "hevc" else ""))

        # move into place last, so Loopscape never sees a half-written video
        os.makedirs(o.styles_dir, exist_ok=True)
        dest = os.path.join(o.styles_dir, o.name + ".mp4")
        png = os.path.join(tmp, o.name + ".png")
        Image.frombytes("RGB", (W, H), still).save(png)
        for f, d in ((video, dest), (png, dest[:-4] + ".png")):     # the video first, then its picture
            shutil.move(f, d + ".new")
            os.replace(d + ".new", d)
        mb = os.path.getsize(dest) / 1e6
        print(f"saved {dest} ({count / FPS:.0f} s loop, {mb:.1f} MB). "
              f"Pick '{o.name}' in the Loopscape tray icon's menu > Change wallpaper.")
    finally:
        if enc is not None and enc.poll() is None:     # stopped early: don't leave ffmpeg running
            enc.kill()
            enc.wait()
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()
