#!/usr/bin/env python3
"""
ArtStation Image Scraper Plugin for LoRAMancer Image Harvester
Harvests digital artwork, character concept art, and 3D renders from ArtStation.
"""

import sys
import json
import argparse
import urllib.request
import urllib.parse


def scrape_artstation(query: str, limit: int = 40):
    raw_query = query.strip() if query and query.strip() else "concept art"
    safe_q = urllib.parse.quote(raw_query)
    url = f"https://www.artstation.com/api/v2/search/projects.json?query={safe_q}&page=1&per_page={min(max(limit, 10), 60)}"

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
                cover = item.get("cover") or {}
                img_url = cover.get("image_url") or item.get("smaller_square_cover_url", "")
                if not img_url:
                    continue

                # Obtain high-res image URL by replacing medium/small dimension tokens
                hires_url = img_url.replace("/smaller_square/", "/large/").replace("/small/", "/large/")
                thumb_url = cover.get("small_square_url") or img_url

                title = item.get("title", "ArtStation Project")
                user = item.get("user") or {}
                artist = user.get("full_name") or user.get("username", "")
                if artist:
                    title = f"{title} - by {artist}"

                candidates.append({
                    "sourceUrl": hires_url,
                    "thumbnailUrl": thumb_url,
                    "title": title[:90],
                    "width": 0,
                    "height": 0
                })

                if len(candidates) >= limit:
                    break
    except Exception as e:
        sys.stderr.write(f"ArtStation scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="ArtStation Image Scraper Plugin for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search query filter")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_artstation(args.query, args.limit)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
