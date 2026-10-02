#!/usr/bin/env python3
"""
Yande.re Image Scraper Plugin for LoRAMancer Image Harvester
Harvests ultra high-resolution anime artwork and wallpapers from Yande.re.
"""

import sys
import json
import argparse
import urllib.request
import urllib.parse


def scrape_yandere(query: str, limit: int = 40):
    raw_query = query.strip() if query and query.strip() else ""
    tag_tokens = raw_query.split() if raw_query else []
    normalized_tags = [t.strip().replace(" ", "_") for t in tag_tokens if t.strip()]
    safe_tags = urllib.parse.quote(" ".join(normalized_tags))

    per_page = min(max(limit, 5), 100)
    url = f"https://yande.re/post.json?limit={per_page}"
    if safe_tags:
        url += f"&tags={safe_tags}"

    headers = {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36",
        "Accept": "application/json"
    }

    req = urllib.request.Request(url, headers=headers)
    candidates = []

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            posts = json.loads(resp.read().decode("utf-8", errors="replace"))

            for post in posts:
                # Prefer high-res jpeg_url or file_url
                file_url = post.get("jpeg_url") or post.get("file_url")
                if not file_url:
                    continue

                thumb_url = post.get("preview_url") or post.get("sample_url") or file_url
                tags = post.get("tags", "")
                author = post.get("author", "")
                title = f"Yande.re #{post.get('id', '')} - {author}" if author else f"Yande.re #{post.get('id', '')}"
                if tags:
                    title = f"{title} ({tags[:40]}...)"

                candidates.append({
                    "sourceUrl": file_url,
                    "thumbnailUrl": thumb_url,
                    "title": title[:90],
                    "width": int(post.get("width", 0) or 0),
                    "height": int(post.get("height", 0) or 0)
                })

                if len(candidates) >= limit:
                    break
    except Exception as e:
        sys.stderr.write(f"Yande.re scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="Yande.re Image Scraper Plugin for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search tags query")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_yandere(args.query, args.limit)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
