#!/usr/bin/env python3
"""
Art Institute of Chicago Scraper Plugin for LoRAMancer Image Harvester
Harvests classical master paintings, fine art, and museum artifacts via ArtIC API.
"""

import sys
import json
import argparse
import urllib.request
import urllib.parse


def scrape_artic(query: str, limit: int = 40):
    raw_query = query.strip() if query and query.strip() else "painting"
    safe_q = urllib.parse.quote(raw_query)

    per_page = min(max(limit, 5), 100)
    fields = "id,title,artist_title,image_id,date_display,medium_display,_score"
    url = f"https://api.artic.edu/api/v1/artworks/search?q={safe_q}&fields={fields}&limit={per_page}"

    headers = {
        "User-Agent": "LoRAMancer/1.0 (Artwork Harvester; mailto:contact@loramancer.local)",
        "Accept": "application/json"
    }

    req = urllib.request.Request(url, headers=headers)
    candidates = []

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read().decode("utf-8", errors="replace"))
            artworks = data.get("data", [])

            for item in artworks:
                # If a specific query was provided, ensure it actually matched (avoid ArtIC's 0.00002 score fallback)
                score = float(item.get("_score") or 0.0)
                if query and query.strip() and score < 1.0:
                    continue

                image_id = item.get("image_id")
                if not image_id:
                    continue

                # IIIF image delivery URLs
                high_res_url = f"https://www.artic.edu/iiif/2/{image_id}/full/1686,/0/default.jpg"
                thumb_url = f"https://www.artic.edu/iiif/2/{image_id}/full/300,/0/default.jpg"

                title = item.get("title", "Artwork")
                artist = item.get("artist_title")
                date = item.get("date_display")
                if artist and date:
                    title = f"{title} - {artist} ({date})"
                elif artist:
                    title = f"{title} - {artist}"

                candidates.append({
                    "sourceUrl": high_res_url,
                    "thumbnailUrl": thumb_url,
                    "title": title[:90],
                    "width": 0,
                    "height": 0
                })

                if len(candidates) >= limit:
                    break
    except Exception as e:
        sys.stderr.write(f"ArtIC scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="Art Institute of Chicago Scraper Plugin for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search query (e.g. monet, oil on canvas, impressionism)")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_artic(args.query, args.limit)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
