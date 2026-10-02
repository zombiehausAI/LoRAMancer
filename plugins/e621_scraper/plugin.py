#!/usr/bin/env python3
"""
e621 Scraper Plugin for LoRAMancer Image Harvester
Harvests anthropomorphic, creature, and fantasy character art from e621 API.
"""

import sys
import json
import argparse
import urllib.request
import urllib.parse


def scrape_e621(query: str, limit: int = 40):
    raw_query = query.strip() if query and query.strip() else ""
    tag_tokens = raw_query.split() if raw_query else []
    normalized_tags = [t.strip().replace(" ", "_") for t in tag_tokens if t.strip()]

    # If no rating is specified, default to safe to ensure general suitability
    has_rating = any(t.startswith("rating:") or t.startswith("order:") for t in normalized_tags)
    if not has_rating:
        normalized_tags.insert(0, "rating:s")

    safe_tags = urllib.parse.quote(" ".join(normalized_tags))

    per_page = min(max(limit, 5), 100)
    url = f"https://e621.net/posts.json?tags={safe_tags}&limit={per_page}"

    headers = {
        "User-Agent": "LoRAMancerHarvester/1.0 (by loramancer_app on e621)",
        "Accept": "application/json"
    }

    req = urllib.request.Request(url, headers=headers)
    candidates = []

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read().decode("utf-8", errors="replace"))
            posts = data.get("posts", [])

            for post in posts:
                file_info = post.get("file", {})
                source_url = file_info.get("url")
                if not source_url:
                    continue

                preview_info = post.get("preview", {})
                sample_info = post.get("sample", {})
                thumb_url = preview_info.get("url") or sample_info.get("url") or source_url

                tags_dict = post.get("tags", {})
                artists = tags_dict.get("artist", [])
                artist_name = artists[0] if artists else ""
                species = tags_dict.get("species", [])
                species_name = species[0] if species else ""

                title = f"e621 #{post.get('id', '')}"
                if artist_name:
                    title = f"{title} - by {artist_name}"
                if species_name:
                    title = f"{title} ({species_name})"

                candidates.append({
                    "sourceUrl": source_url,
                    "thumbnailUrl": thumb_url,
                    "title": title[:90],
                    "width": int(file_info.get("width", 0) or 0),
                    "height": int(file_info.get("height", 0) or 0)
                })

                if len(candidates) >= limit:
                    break
    except Exception as e:
        sys.stderr.write(f"e621 scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="e621 Scraper Plugin for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search tags query (e.g. dragon, wolf, feline)")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_e621(args.query, args.limit)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
