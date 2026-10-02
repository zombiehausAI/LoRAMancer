#!/usr/bin/env python3
"""
Xbooru Image Scraper Plugin for LoRAMancer Image Harvester
Harvests explicit anime, 3D, and cartoon artwork from Xbooru API.
"""

import sys
import json
import argparse
import urllib.request
import urllib.parse


def scrape_xbooru(query: str, limit: int = 40):
    raw_query = query.strip() if query and query.strip() else ""
    tag_tokens = raw_query.split() if raw_query else []
    normalized_tags = [t.strip().replace(" ", "_") for t in tag_tokens if t.strip()]
    safe_tags = urllib.parse.quote(" ".join(normalized_tags))

    per_page = min(max(limit, 5), 100)
    url = f"https://xbooru.com/index.php?page=dapi&s=post&q=index&json=1&limit={per_page}"
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
            data = json.loads(resp.read().decode("utf-8", errors="replace"))

            posts = data if isinstance(data, list) else data.get("post", [])
            for post in posts:
                file_url = post.get("file_url") or post.get("sample_url")
                if not file_url:
                    continue

                if file_url.startswith("//"):
                    file_url = f"https:{file_url}"

                thumb_url = post.get("preview_url") or file_url
                if thumb_url.startswith("//"):
                    thumb_url = f"https:{thumb_url}"

                tags = post.get("tags", "")
                title = f"Xbooru #{post.get('id', '')}"
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
        sys.stderr.write(f"Xbooru scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="Xbooru Scraper Plugin for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search booru tags query")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_xbooru(args.query, args.limit)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
