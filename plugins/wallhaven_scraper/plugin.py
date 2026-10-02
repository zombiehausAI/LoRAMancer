#!/usr/bin/env python3
"""
Wallhaven Image Scraper Plugin for LoRAMancer Image Harvester
Harvests high-resolution digital art, anime, and photography from the Wallhaven REST API.
"""

import sys
import json
import argparse
import urllib.request
import urllib.parse


def scrape_wallhaven(query: str, limit: int = 40):
    safe_q = urllib.parse.quote(query.strip()) if query and query.strip() else ""
    sort_mode = "relevance" if safe_q else "toplist"
    url = f"https://wallhaven.cc/api/v1/search?q={safe_q}&categories=111&purity=100&sorting={sort_mode}&order=desc"

    headers = {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36",
        "Accept": "application/json"
    }

    req = urllib.request.Request(url, headers=headers)
    candidates = []

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            items = data.get("data", [])

            for item in items:
                img_url = item.get("path", "")
                if not img_url:
                    continue

                thumbs = item.get("thumbs", {})
                thumb_url = thumbs.get("large") or thumbs.get("small") or img_url

                width = item.get("dimension_x", 0)
                height = item.get("dimension_y", 0)
                category = item.get("category", "General")
                wallpaper_id = item.get("id", "wallhaven")

                title = f"Wallhaven {wallpaper_id} ({category})"

                candidates.append({
                    "sourceUrl": img_url,
                    "thumbnailUrl": thumb_url,
                    "title": title,
                    "width": width,
                    "height": height
                })

                if len(candidates) >= limit:
                    break
    except Exception as e:
        sys.stderr.write(f"Wallhaven scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="Wallhaven Image Scraper Plugin for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search query filter")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_wallhaven(args.query, args.limit)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
