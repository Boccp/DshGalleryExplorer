# -*- coding: utf-8 -*-
"""
Full-Bleed Folder Cover Icon Generator with Subtle Folder Frame & Corner Badge (`generate_folder_cover_icons.py`).

Design balance:
1. Shows ~94% of the first image inside the folder (no bulky Windows yellow folder shell covering half the art).
2. Clearly distinguishes folders from loose images at a glance via:
   - A sleek dark/gold top folder tab ear + subtle stacked-album border around the cover;
   - A crisp, high-contrast semi-transparent folder mini-badge in the bottom-right corner.
"""

from __future__ import annotations
import ctypes
from ctypes import wintypes
import os
import re
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from PIL import Image, ImageDraw, ImageFilter

IMAGE_EXTS = (".jpg", ".jpeg", ".png", ".webp", ".bmp")


class SHFOLDERCUSTOMSETTINGS(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("dwMask", wintypes.DWORD),
        ("pvid", ctypes.c_void_p),
        ("pszWebViewTemplate", wintypes.LPWSTR),
        ("cchWebViewTemplate", wintypes.DWORD),
        ("pszWebViewTemplateVersion", wintypes.LPWSTR),
        ("pszInfoTip", wintypes.LPWSTR),
        ("cchInfoTip", wintypes.DWORD),
        ("pclsid", ctypes.c_void_p),
        ("dwFlags", wintypes.DWORD),
        ("pszIconFile", wintypes.LPWSTR),
        ("cchIconFile", wintypes.DWORD),
        ("iIconIndex", ctypes.c_int),
        ("pszLogo", wintypes.LPWSTR),
        ("cchLogo", wintypes.DWORD),
    ]


FCSM_ICONFILE = 0x00000010
FCS_FORCEWRITE = 0x00000002
FILE_ATTRIBUTE_HIDDEN = 0x02
FILE_ATTRIBUTE_SYSTEM = 0x04


def natural_sort_key(name: str) -> list:
    return [int(t) if t.isdigit() else t.lower() for t in re.split(r"(\d+)", name)]


def find_first_image(folder: str) -> str | None:
    """Find the first image file inside `folder` (or inside its first child subfolder if none at top level)."""
    try:
        entries = sorted(os.listdir(folder), key=natural_sort_key)
    except Exception:
        return None

    for name in entries:
        if name.startswith("."):
            continue
        full = os.path.join(folder, name)
        if os.path.isfile(full) and name.lower().endswith(IMAGE_EXTS):
            return full

    for name in entries:
        if name.startswith("."):
            continue
        full = os.path.join(folder, name)
        if os.path.isdir(full):
            sub_img = find_first_image(full)
            if sub_img:
                return sub_img
    return None


def render_folder_cover_image(img_path: str, size: int = 256) -> Image.Image:
    """
    Compose a 256x256 folder thumbnail that displays ~94% of the cover art
    while clearly looking like a folder via:
    1. A sleek top-left folder tab ear + thin warm-gold folder backplate rim (top 14px);
    2. Full-bleed cover image filling (4, 16) -> (252, 252) with a crisp 2px inner frame;
    3. A high-visibility dark-glass pill in the bottom-right corner with a crisp golden folder glyph.
    """
    img = Image.open(img_path).convert("RGBA")
    w, h = img.size
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)

    # 1. Draw the sleek folder backplate tab ear at top-left so the silhouette itself says "Folder"
    # Tab ear: (6, 3) to (104, 24), Backplate top bar: (4, 11) to (251, 251)
    tab_fill = (245, 179, 36, 255)      # Warm folder amber-gold
    tab_border = (186, 122, 12, 255)
    draw.rounded_rectangle((6, 2, 98, 28), radius=9, fill=tab_fill, outline=tab_border, width=2)
    draw.rounded_rectangle((3, 11, size - 4, size - 3), radius=15, fill=(34, 36, 42, 255), outline=tab_fill, width=3)

    # 2. Prepare the cover image inside the main card area: (7, 15, size - 8, size - 7) -> 242 x 234 (94% of canvas!)
    box_l, box_t, box_r, box_b = 7, 15, size - 7, size - 7
    box_w = box_r - box_l
    box_h = box_b - box_t

    scale = max(box_w / w, box_h / h)
    nw, nh = max(1, int(round(w * scale))), max(1, int(round(h * scale)))
    resized = img.resize((nw, nh), Image.Resampling.LANCZOS)
    left = (nw - box_w) // 2
    top = int((nh - box_h) * 0.20)  # Keep faces & top titles in frame
    cropped = resized.crop((left, top, left + box_w, top + box_h))

    mask = Image.new("L", (box_w, box_h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, box_w - 1, box_h - 1), radius=12, fill=255)
    canvas.paste(cropped, (box_l, box_t), mask=mask)

    # Re-draw crisp outer golden-amber folder frame line so the border is super clean
    draw = ImageDraw.Draw(canvas)
    draw.rounded_rectangle((box_l, box_t, box_r - 1, box_b - 1), radius=12, outline=(250, 190, 50, 235), width=2)

    # 3. Draw bottom-right folder badge (dark frosted pill + bright golden folder icon)
    badge_w, badge_h = 54, 44
    bx1 = size - 12 - badge_w
    by1 = size - 12 - badge_h
    bx2 = size - 12
    by2 = size - 12

    # Badge dark background with white/gold rim for 100% visibility over both light and dark manga covers
    draw.rounded_rectangle(
        (bx1, by1, bx2, by2),
        radius=11,
        fill=(20, 22, 28, 228),
        outline=(255, 200, 60, 245),
        width=2,
    )

    # Draw crisp vector folder glyph inside badge
    fx1, fy1 = bx1 + 11, by1 + 13
    fx2, fy2 = bx2 - 11, by2 - 9
    # Folder mini tab
    draw.rounded_rectangle((fx1, fy1 - 4, fx1 + 14, fy1 + 4), radius=2, fill=(255, 196, 45, 255))
    # Folder body
    draw.rounded_rectangle((fx1, fy1, fx2, fy2), radius=3, fill=(255, 210, 70, 255), outline=(180, 120, 10, 255), width=1)
    # Folder front flap highlight line
    draw.line((fx1 + 2, fy1 + 5, fx2 - 2, fy1 + 5), fill=(255, 242, 180, 255), width=2)

    return canvas


def make_full_bleed_ico(img_path: str, ico_path: str) -> bool:
    try:
        canvas = render_folder_cover_image(img_path, size=256)
        if os.path.exists(ico_path):
            ctypes.windll.kernel32.SetFileAttributesW(ico_path, 0x80)
        canvas.save(ico_path, format="ICO", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32)])
        ctypes.windll.kernel32.SetFileAttributesW(ico_path, FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM)
        return True
    except Exception:
        return False


import hashlib

CENTRAL_ICO_CACHE_DIR = r"C:\Users\Administrator\.dsh\gallery_cache\folder_icons"


def apply_folder_icon(folder: str, force: bool = False) -> tuple[str, bool, str]:
    """
    Render folder cover icon strictly into `C:\\Users\\Administrator\\.dsh\\gallery_cache\\folder_icons\\`
    without creating any `.folder_cover.ico` or `desktop.ini` inside user library folders!
    """
    first_img = find_first_image(folder)
    if not first_img:
        return folder, False, "no_image"

    os.makedirs(CENTRAL_ICO_CACHE_DIR, exist_ok=True)
    digest = hashlib.md5(os.path.abspath(folder).lower().encode("utf-8")).hexdigest()
    ico_path = os.path.join(CENTRAL_ICO_CACHE_DIR, f"{digest}.ico")

    if not force and os.path.exists(ico_path):
        try:
            if os.path.getmtime(ico_path) >= os.path.getmtime(first_img):
                return folder, True, "already_up_to_date"
        except Exception:
            pass

    if not make_full_bleed_ico(first_img, ico_path):
        return folder, False, "ico_render_failed"

    return folder, True, f"ok:{os.path.basename(first_img)}"


def collect_target_folders(roots: list[str]) -> list[str]:
    folders = []
    for r in roots:
        if not os.path.exists(r):
            continue
        for dirpath, dirnames, filenames in os.walk(r):
            dirnames[:] = [d for d in dirnames if not d.startswith(".") and not d.startswith("_")]
            if dirpath == r:
                continue
            folders.append(dirpath)
    return folders


def main() -> None:
    roots = sys.argv[1:] or [r"F:\Openclow\画师素材库", r"V:\归档"]
    folders = collect_target_folders(roots)
    print(f"Scanning {len(folders)} folders across {roots}...")

    ok_count = 0
    skip_count = 0
    with ThreadPoolExecutor(max_workers=8) as pool:
        futures = [pool.submit(apply_folder_icon, f, True) for f in folders]
        for fut in as_completed(futures):
            folder, ok, msg = fut.result()
            if ok:
                ok_count += 1
            else:
                skip_count += 1

    SHCNE_ASSOCCHANGED = 0x08000000
    SHCNF_IDLIST = 0x0000
    ctypes.windll.shell32.SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, None, None)
    print(f"Completed framed+badged folder cover icons: {ok_count} folders updated, {skip_count} empty/skipped.")


if __name__ == "__main__":
    main()
