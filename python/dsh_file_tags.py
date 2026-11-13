# -*- coding: utf-8 -*-
"""
DSH Agent File Tags & Archive Manager (`dsh_file_tags.py`).

Provides zero-latency lookup, full-disk ADS rescan, and one-time task tag auto-clearing for DSH Agents.

Seamless Sync Directory (`F:\Openclow\画师素材库\_标签索引`):
Whenever the user toggles any tag (`Ctrl + Q/W/E/R + Left Click`) in DSH Gallery Explorer or an Agent
calls `set_tag()` / `clear_tag()` / `scan_and_sync_all()`, the following files are updated in real time:
  - `F:\Openclow\画师素材库\_标签索引\红.txt` & `红.json`
  - `F:\Openclow\画师素材库\_标签索引\橙.txt` & `橙.json`
  - `F:\Openclow\画师素材库\_标签索引\黄.txt` & `黄.json`
  - `F:\Openclow\画师素材库\_标签索引\绿.txt` & `绿.json`
  - `F:\Openclow\画师素材库\_标签索引\全部标记.txt` & `全部标记.json`
  - `F:\Openclow\画师素材库\_dsh_tags_ledger.json`
"""

from __future__ import annotations
import ctypes
import json
import os
import sys
from datetime import datetime

LEDGER_PATH = r"C:\Users\Administrator\.dsh\gallery_cache\_dsh_tags_ledger.json"
TAG_INDEX_DIR = r"C:\Users\Administrator\.dsh\gallery_cache\tags_index"
ARCHIVE_ROOT = r"V:\归档"

TAG_DEFINITIONS = {
    "红": {"key": "red", "color": "#E81123", "uid": "f7e0e137-2eb5-4fa4-a50d-ddd65df17c34"},
    "橙": {"key": "orange", "color": "#F7630C", "uid": "c84a8131-c4de-47d9-9440-26e859d14b3d"},
    "黄": {"key": "yellow", "color": "#FFB900", "uid": "d4b8d4bd-ceaf-4e58-ac61-a185fcf96c5d"},
    "绿": {"key": "green", "color": "#107C10", "uid": "79376daf-c44a-4fe4-aa3b-8b30baea453e"},
}

ORDERED_NAMES = ["红", "橙", "黄", "绿"]
UID_TO_NAME = {v["uid"]: k for k, v in TAG_DEFINITIONS.items()}
NAME_TO_UID = {k: v["uid"] for k, v in TAG_DEFINITIONS.items()}
FILE_ATTRIBUTE_READONLY = 0x01


def read_ads_tags(path: str) -> list[str]:
    """Read tag names ('红', '橙', '黄', '绿') from NTFS `:files` ADS stream."""
    ads_path = f"{path}:files"
    try:
        with open(ads_path, "r", encoding="utf-8") as f:
            uids = json.load(f)
        if isinstance(uids, list) and uids:
            return [UID_TO_NAME[u] for u in uids if u in UID_TO_NAME]
    except Exception:
        pass
    return []


def write_ads_tags(path: str, tag_names: list[str]) -> None:
    """Write tag names ('红', '橙', '黄', '绿') to NTFS `:files` ADS stream."""
    norm_p = os.path.abspath(path)
    next_uids = [NAME_TO_UID[n] for n in ORDERED_NAMES if n in tag_names]
    attrs = ctypes.windll.kernel32.GetFileAttributesW(norm_p)
    had_readonly = (attrs != 0xFFFFFFFF) and ((attrs & FILE_ATTRIBUTE_READONLY) != 0)
    if had_readonly:
        ctypes.windll.kernel32.SetFileAttributesW(norm_p, attrs & ~FILE_ATTRIBUTE_READONLY)
    try:
        ads_path = f"{norm_p}:files"
        if not next_uids:
            try:
                os.remove(ads_path)
            except Exception:
                pass
        else:
            with open(ads_path, "w", encoding="utf-8") as f:
                json.dump(next_uids, f)
    except Exception:
        pass
    finally:
        if had_readonly:
            ctypes.windll.kernel32.SetFileAttributesW(norm_p, attrs)


def set_tag(path: str, tag_name: str) -> list[str]:
    """Add `tag_name` ('红'/'橙'/'黄'/'绿') to `path` and sync index files."""
    if tag_name not in NAME_TO_UID:
        raise ValueError(f"Unknown tag: {tag_name}")
    norm_p = os.path.abspath(path)
    cur = read_ads_tags(norm_p)
    if tag_name not in cur:
        cur.append(tag_name)
    ordered = [n for n in ORDERED_NAMES if n in cur]
    write_ads_tags(norm_p, ordered)
    ledger = load_ledger()
    ledger.setdefault("items", {})[norm_p] = {
        "tags": ordered,
        "uids": [NAME_TO_UID[n] for n in ordered],
        "updatedAt": datetime.utcnow().isoformat() + "Z",
    }
    save_ledger(ledger)
    return ordered


def clear_tag(path: str, tag_name: str | None = None) -> tuple[str, list[str]]:
    """
    Remove a specific tag (e.g. '红') or all tags (when tag_name is None) from `path`.
    Updates NTFS `:files` ADS stream, `_dsh_tags_ledger.json`, and `_标签索引/*`.
    Returns `(path, remaining_tag_names)`.
    """
    norm_p = os.path.abspath(path)
    current_names = read_ads_tags(norm_p)
    if not current_names:
        ledger_tmp = load_ledger()
        current_names = ledger_tmp.get("items", {}).get(norm_p, {}).get("tags", [])

    if tag_name is None:
        next_names: list[str] = []
    else:
        next_names = [n for n in current_names if n != tag_name]

    next_uids = [NAME_TO_UID[n] for n in ORDERED_NAMES if n in next_names]
    write_ads_tags(norm_p, next_names)

    ledger = load_ledger()
    if not next_names:
        ledger.get("items", {}).pop(norm_p, None)
    else:
        ledger.setdefault("items", {})[norm_p] = {
            "tags": next_names,
            "uids": next_uids,
            "updatedAt": datetime.utcnow().isoformat() + "Z",
        }
    save_ledger(ledger)
    return norm_p, next_names


def load_ledger() -> dict:
    try:
        if os.path.exists(LEDGER_PATH):
            with open(LEDGER_PATH, "r", encoding="utf-8") as f:
                return json.load(f)
    except Exception:
        pass
    return {"items": {}}


def sync_tag_index_dir(ledger: dict) -> None:
    """Mirror `ledger` into `F:\\Openclow\\画师素材库\\_标签索引` for instant Agent file reads."""
    os.makedirs(TAG_INDEX_DIR, exist_ok=True)
    items = ledger.get("items", {})

    by_color: dict[str, list[str]] = {n: [] for n in ORDERED_NAMES}
    summary_lines: list[str] = []

    for path in sorted(items.keys(), key=lambda s: s.lower()):
        meta = items[path]
        tags = [n for n in ORDERED_NAMES if n in (meta.get("tags") or [])]
        if not tags:
            continue
        summary_lines.append(f"[{','.join(tags)}]\t{path}")
        for t in tags:
            by_color[t].append(path)

    with open(os.path.join(TAG_INDEX_DIR, "全部标记.json"), "w", encoding="utf-8") as f:
        json.dump(ledger, f, ensure_ascii=False, indent=2)

    with open(os.path.join(TAG_INDEX_DIR, "全部标记.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(summary_lines) + ("\n" if summary_lines else ""))

    for name in ORDERED_NAMES:
        paths = by_color[name]
        with open(os.path.join(TAG_INDEX_DIR, f"{name}.txt"), "w", encoding="utf-8") as f:
            f.write("\n".join(paths) + ("\n" if paths else ""))
        payload = {
            "tag": name,
            "color": TAG_DEFINITIONS[name]["color"],
            "count": len(paths),
            "items": paths,
        }
        with open(os.path.join(TAG_INDEX_DIR, f"{name}.json"), "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)


def save_ledger(ledger: dict) -> None:
    os.makedirs(os.path.dirname(LEDGER_PATH), exist_ok=True)
    with open(LEDGER_PATH, "w", encoding="utf-8") as f:
        json.dump(ledger, f, ensure_ascii=False, indent=2)
    sync_tag_index_dir(ledger)


def scan_and_sync_all(roots: list[str] | None = None) -> dict[str, list[str]]:
    """Scan NTFS `:files` ADS streams across all library roots and rebuild `_标签索引`."""
    if not roots:
        roots = [r"F:\Openclow\画师素材库", r"V:\归档"]
    ledger = load_ledger()
    items = ledger.setdefault("items", {})

    # Remove nonexistent paths
    for p in list(items.keys()):
        if not os.path.exists(p):
            items.pop(p, None)

    for root in roots:
        if not os.path.exists(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if not d.startswith(".") and d != "_标签索引"]
            for d in dirnames:
                full_d = os.path.join(dirpath, d)
                tags = read_ads_tags(full_d)
                if tags:
                    items[full_d] = {
                        "tags": tags,
                        "uids": [NAME_TO_UID[n] for n in tags],
                        "updatedAt": items.get(full_d, {}).get("updatedAt") or (datetime.utcnow().isoformat() + "Z"),
                    }
            for fn in filenames:
                if fn.startswith(".") or fn == "desktop.ini" or fn.startswith("_dsh_"):
                    continue
                full_f = os.path.join(dirpath, fn)
                tags = read_ads_tags(full_f)
                if tags:
                    items[full_f] = {
                        "tags": tags,
                        "uids": [NAME_TO_UID[n] for n in tags],
                        "updatedAt": items.get(full_f, {}).get("updatedAt") or (datetime.utcnow().isoformat() + "Z"),
                    }

    save_ledger(ledger)
    return {p: m["tags"] for p, m in items.items()}


def list_tagged_items(tag_filter: str | None = None, root_dir: str | None = None) -> dict[str, list[str]]:
    ledger = load_ledger()
    items = ledger.get("items", {})
    result: dict[str, list[str]] = {}
    dirty = False

    for path, meta in list(items.items()):
        if not os.path.exists(path):
            items.pop(path, None)
            dirty = True
            continue
        live_tags = read_ads_tags(path) or meta.get("tags", [])
        if not live_tags:
            items.pop(path, None)
            dirty = True
            continue
        if live_tags != meta.get("tags"):
            meta["tags"] = live_tags
            dirty = True
        if root_dir and not os.path.abspath(path).lower().startswith(os.path.abspath(root_dir).lower()):
            continue
        if tag_filter and tag_filter not in live_tags:
            continue
        result[path] = live_tags

    if dirty:
        save_ledger(ledger)
    else:
        sync_tag_index_dir(ledger)
    return result


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "list"
    if cmd == "list":
        flt = sys.argv[2] if len(sys.argv) > 2 else None
        tagged = list_tagged_items(flt)
        print(json.dumps(tagged, ensure_ascii=False, indent=2))
    elif cmd == "scan":
        tagged = scan_and_sync_all()
        print(json.dumps(tagged, ensure_ascii=False, indent=2))
    elif cmd == "set":
        target = sys.argv[2]
        tname = sys.argv[3]
        rem = set_tag(target, tname)
        print(f"Set tag {tname} -> {target}; tags={rem}")
    elif cmd == "clear":
        target = sys.argv[2]
        tname = sys.argv[3] if len(sys.argv) > 3 else None
        new_p, rem = clear_tag(target, tname)
        print(f"Cleared tag {tname or 'ALL'} -> {new_p}; remaining={rem}")
