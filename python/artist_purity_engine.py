# -*- coding: utf-8 -*-
"""
Standardized Artist Library Purity, Deduplication, Edition Selection & Double-Page Split Engine.
Prevents homonym artist pollution, low-res thumbnails, foreign (Thai/Spanish/Russian) mislabeling,
duplicate images across sources, and unsplit wide double-page manga spreads.
"""

from __future__ import annotations
import hashlib
import io
import json
import os
import re
import sqlite3
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Any, Callable, Iterable

from PIL import Image
import numpy as np

DEFAULT_DB_PATH = r"C:\Users\Administrator\.dsh\gallery_cache\_library_ledger.sqlite3"

# Strict language classification rules for manga / doujinshi galleries
FORBIDDEN_LANGUAGE_TAGS = frozenset({
    "language:thai",
    "language:spanish",
    "language:russian",
    "language:korean",
    "language:french",
    "language:german",
    "language:portuguese",
    "language:italian",
    "language:vietnamese",
    "language:indonesian",
    "language:polish",
    "language:turkish",
})

FORBIDDEN_TITLE_PATTERNS = re.compile(
    r"(\b(?:thai|spanish|español|russian|русский|korean|한국어|french|français|portuguese|português|vietnamese|tiếng\s*việt|indonesian)\b"
    r"|[\u0e00-\u0e7f]{2,}"  # Thai script
    r"|[\uac00-\ud7af]{2,}"  # Hangul script
    r"|[\u0400-\u04ff]{3,})",  # Cyrillic script
    re.IGNORECASE,
)

CN_TITLE_PATTERNS = re.compile(
    r"(\[[^\]]*(?:汉化|漢化|中文|中国翻訳|中国語|翻译|翻譯|精校|嵌字|个人汉化|野良汉化|频率汉化|基德汉化|熊爪汉化)[^\]]*\]"
    r"|\b(?:chinese|zh-cn|zh-tw)\b)",
    re.IGNORECASE,
)

EN_TITLE_PATTERNS = re.compile(
    r"(\[[^\]]*(?:english|eng|en)[^\]]*\]|\b(?:english\s+translated|english\s+version|english)\b)",
    re.IGNORECASE,
)


def classify_manga_language(title: str, tags: Iterable[str] = ()) -> tuple[str | None, int]:
    """
    Classify gallery language strictly into ('中文版', 3), ('英文版', 2), ('日文原版', 1), or (None, 0) if forbidden.
    Priority: 中文版 (3) > 英文版 (2) > 日文原版 (1) > Forbidden (0).
    """
    tag_set = {t.strip().lower() for t in tags}
    if tag_set & FORBIDDEN_LANGUAGE_TAGS:
        return None, 0
    if FORBIDDEN_TITLE_PATTERNS.search(title):
        return None, 0

    if "language:chinese" in tag_set or CN_TITLE_PATTERNS.search(title):
        return "中文版", 3
    if "language:english" in tag_set or EN_TITLE_PATTERNS.search(title):
        return "英文版", 2
    if "language:translated" in tag_set or "language:rewrite" in tag_set:
        # Translated into an unknown non-CN/non-EN language
        return None, 0
    return "日文原版", 1


def compute_dhash(img: Image.Image, hash_size: int = 8) -> str:
    """Compute 64-bit difference hash (dHash) for visual duplicate detection across resolutions."""
    gray = img.convert("L").resize((hash_size + 1, hash_size), Image.Resampling.LANCZOS)
    pixels = np.asarray(gray, dtype=np.int16)
    diff = pixels[:, 1:] > pixels[:, :-1]
    bit_string = "".join("1" if b else "0" for b in diff.flatten())
    return f"{int(bit_string, 2):016x}"


def hamming_distance(hash1: str, hash2: str) -> int:
    """Return Hamming distance between two 16-char hex dHashes."""
    return (int(hash1, 16) ^ int(hash2, 16)).bit_count()


def has_video_letterbox_bars(img: Image.Image, min_bar_px: int = 24) -> bool:
    """Detect top/bottom solid black video screenshot letterbox bars (e.g., Bilibili captures)."""
    rgb = np.asarray(img.convert("RGB"), dtype=np.uint8)
    h, w, _ = rgb.shape
    if h < min_bar_px * 3:
        return False
    row_max = rgb.max(axis=(1, 2))
    top_black = 0
    for val in row_max:
        if val <= 12:
            top_black += 1
        else:
            break
    bot_black = 0
    for val in row_max[::-1]:
        if val <= 12:
            bot_black += 1
        else:
            break
    return top_black >= min_bar_px or bot_black >= min_bar_px


def validate_loose_illustration(
    raw_bytes: bytes,
    min_short_side: int = 500,
    min_long_side: int = 700,
    reject_letterbox: bool = True,
) -> tuple[bool, str, tuple[int, int], str]:
    """
    Validate a loose illustration or cover before saving.
    Returns (is_valid, reason, (width, height), dhash).
    """
    try:
        img = Image.open(io.BytesIO(raw_bytes))
        img.load()
    except Exception as exc:
        return False, f"decode_error:{exc}", (0, 0), ""

    w, h = img.size
    short_side, long_side = min(w, h), max(w, h)
    if short_side < min_short_side or long_side < min_long_side:
        return False, f"low_resolution:{w}x{h}", (w, h), ""

    if reject_letterbox and has_video_letterbox_bars(img):
        return False, f"video_letterbox_bar:{w}x{h}", (w, h), ""

    return True, "ok", (w, h), compute_dhash(img)


def detect_and_split_double_page(
    img: Image.Image,
    min_long_side: int = 1500,
    min_aspect_ratio: float = 1.22,
    max_aspect_ratio: float = 1.65,
    rtl_reading_order: bool = True,
) -> list[tuple[str, Image.Image]]:
    """
    Inspect a manga page and split into two single pages if it is a two-page spread
    with a center gutter (white/black margin seam or page boundary).
    Returns [("", img)] if single page or full-bleed illustration without a center gutter,
    or [("_a", first_page), ("_b", second_page)] in reading order (RTL default: right page _a, left page _b).
    """
    w, h = img.size
    if max(w, h) <= min_long_side or w <= h * min_aspect_ratio or w > h * max_aspect_ratio:
        return [("", img)]

    gray = np.asarray(img.convert("L"), dtype=np.float32)
    mid = w // 2
    band_half = max(8, int(w * 0.035))
    band = gray[:, mid - band_half : mid + band_half + 1]

    # Measure column-wise pixel standard deviation and extreme luminance (white >= 242 or black <= 15)
    col_std = band.std(axis=0)
    white_ratio = (band >= 242).mean(axis=0)
    black_ratio = (band <= 15).mean(axis=0)
    uniform_ratio = np.maximum(white_ratio, black_ratio)

    # Also check vertical edge gradient (sharp transition between left and right pages at center)
    col_diff = np.abs(np.diff(band, axis=1)).mean(axis=0)

    best_offset = int(np.argmax(uniform_ratio))
    max_uniform = float(uniform_ratio[best_offset])
    min_std = float(col_std.min())
    center_jump = float(col_diff.max()) if col_diff.size > 0 else 0.0

    is_two_page_gutter = (max_uniform >= 0.68) or (min_std <= 18.0) or (max_uniform >= 0.48 and center_jump >= 14.0)
    if not is_two_page_gutter:
        return [("", img)]

    split_x = mid - band_half + best_offset
    # Clamp split_x close to the geometric center if uniform region is wide
    if abs(split_x - mid) > int(w * 0.02):
        split_x = mid

    left_page = img.crop((0, 0, split_x, h))
    right_page = img.crop((split_x, 0, w, h))

    if rtl_reading_order:
        return [("_a", right_page), ("_b", left_page)]
    return [("_a", left_page), ("_b", right_page)]


class LibraryLedger:
    """SQLite-backed deduplication and URL cache ledger for fast incremental runs."""

    def __init__(self, db_path: str = DEFAULT_DB_PATH) -> None:
        self.db_path = db_path
        os.makedirs(os.path.dirname(db_path), exist_ok=True)
        self.conn = sqlite3.connect(db_path)
        self._init_tables()

    def _init_tables(self) -> None:
        with self.conn:
            self.conn.execute(
                """
                CREATE TABLE IF NOT EXISTS visited_urls (
                    url TEXT PRIMARY KEY,
                    artist TEXT,
                    status INTEGER,
                    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )
                """
            )
            self.conn.execute(
                """
                CREATE TABLE IF NOT EXISTS image_hashes (
                    sha256 TEXT PRIMARY KEY,
                    artist TEXT,
                    dhash TEXT,
                    width INTEGER,
                    height INTEGER,
                    file_path TEXT
                )
                """
            )
            self.conn.execute("CREATE INDEX IF NOT EXISTS idx_image_artist_dhash ON image_hashes(artist, dhash)")

    def is_url_visited(self, url: str) -> bool:
        row = self.conn.execute("SELECT 1 FROM visited_urls WHERE url = ?", (url,)).fetchone()
        return row is not None

    def mark_url_visited(self, url: str, artist: str, status: int = 200) -> None:
        with self.conn:
            self.conn.execute(
                "INSERT OR REPLACE INTO visited_urls(url, artist, status) VALUES (?, ?, ?)",
                (url, artist, status),
            )

    def has_visual_duplicate(self, artist: str, dhash: str, width: int, height: int, max_hamming: int = 4) -> str | None:
        """
        Check if an image with the same visual content already exists for this artist.
        Returns existing file_path if existing image is >= resolution, else None.
        """
        rows = self.conn.execute(
            "SELECT dhash, width, height, file_path FROM image_hashes WHERE artist = ?",
            (artist,),
        ).fetchall()
        for ex_dhash, ex_w, ex_h, ex_path in rows:
            if ex_dhash and hamming_distance(dhash, ex_dhash) <= max_hamming:
                if ex_w * ex_h >= width * height and os.path.exists(ex_path):
                    return ex_path
        return None

    def register_image(self, artist: str, sha256: str, dhash: str, width: int, height: int, file_path: str) -> None:
        with self.conn:
            self.conn.execute(
                "INSERT OR REPLACE INTO image_hashes(sha256, artist, dhash, width, height, file_path) VALUES (?, ?, ?, ?, ?, ?)",
                (sha256, artist, dhash, width, height, file_path),
            )

    def close(self) -> None:
        self.conn.close()
