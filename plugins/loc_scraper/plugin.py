#!/usr/bin/env python3
"""
Library of Congress Scraper Plugin for LoRAMancer Image Harvester
Harvests vintage historical photography, portraits, architectural archives, and retro prints.
"""

import sys
import json
import argparse
import urllib.request
import urllib.parse


def scrape_loc(query: str, limit: int = 40):
    raw_query = query.strip() if query and query.strip() else "vintage portrait"
    safe_q = urllib.parse.quote(raw_query)

    per_page = min(max(limit, 5), 100)
    url = f"https://www.loc.gov/photos/?q={safe_q}&fo=json&c={per_page}"

    headers = {
        "User-Agent": "LoRAMancer/1.0 (Historical Archive Harvester; mailto:contact@loramancer.local)",
        "Accept": "application/json"
    }

    req = urllib.request.Request(url, headers=headers)
    candidates = []

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read().decode("utf-8", errors="replace"))
            results = data.get("results", [])

            for item in results:
                image_urls = item.get("image_url", [])
                if not image_urls:
                    continue

                if isinstance(image_urls, str):
                    image_urls = [image_urls]

                # Strip URL fragment (#h=...&w=...)
                clean_urls = [u.split("#")[0] for u in image_urls if u]
                if not clean_urls:
                    continue

                # The highest resolution preview is typically the last in the array
                source_url = clean_urls[-1]
                thumb_url = clean_urls[0]

                # Check if there is an uncompressed / larger rendition in resources
                title = item.get("title", "Library of Congress Photo")
                date = item.get("date", "")
                if date:
                    title = f"{title} ({date})"

                candidates.append({
                    "sourceUrl": source_url,
                    "thumbnailUrl": thumb_url,
                    "title": title[:90],
                    "width": 0,
                    "height": 0
                })

                if len(candidates) >= limit:
                    break
    except Exception as e:
        sys.stderr.write(f"Library of Congress scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="Library of Congress Scraper Plugin for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search query (e.g. vintage portraits, locomotives, 1920s)")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_loc(args.query, args.limit)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
