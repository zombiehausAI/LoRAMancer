#!/usr/bin/env python3
"""
Pixabay Image Scraper Plugin for LoRAMancer Image Harvester
Harvests photography, vector art, and 3D graphics from Pixabay API.
"""

import sys
import os
import json
import argparse
import urllib.request
import urllib.parse
from pathlib import Path


def get_plugin_api_key():
    api_key = os.environ.get("LORAMANCER_CONFIG_APIKEY") or os.environ.get("PLUGIN_APIKEY") or ""
    if not api_key:
        try:
            user_home = Path.home()
            config_path = user_home / ".loramancer" / "plugin_configs" / "pixabay-scraper.json"
            if config_path.exists():
                data = json.loads(config_path.read_text(encoding="utf-8"))
                if isinstance(data, dict):
                    api_key = data.get("apiKey") or data.get("api_key") or ""
        except Exception:
            pass

    return api_key.strip()


def scrape_pixabay(query: str, limit: int = 40, api_key: str = ""):
    token = api_key.strip() if api_key and api_key.strip() else get_plugin_api_key()

    if not token:
        sys.stderr.write(
            "Pixabay scraper requires a free API key.\n"
            "Please click the gear icon (⚙️) on the Pixabay plugin card in LoRAMancer to set your API Key.\n"
        )
        return []

    raw_query = query.strip() if query and query.strip() else "scenery"
    safe_q = urllib.parse.quote(raw_query)

    per_page = min(max(limit, 5), 100)
    url = f"https://pixabay.com/api/?key={urllib.parse.quote(token)}&q={safe_q}&per_page={per_page}&image_type=all&safesearch=false"

    headers = {
        "User-Agent": "LoRAMancer/1.0 (Pixabay Harvester)",
        "Accept": "application/json"
    }

    req = urllib.request.Request(url, headers=headers)
    candidates = []

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read().decode("utf-8", errors="replace"))
            hits = data.get("hits", [])

            for item in hits:
                source_url = item.get("largeImageURL") or item.get("webformatURL")
                if not source_url:
                    continue

                thumb_url = item.get("webformatURL") or item.get("previewURL") or source_url
                tags = item.get("tags", "")
                author = item.get("user", "")

                title = f"Pixabay #{item.get('id', '')}"
                if author and tags:
                    title = f"{tags[:45]} - by {author}"
                elif author:
                    title = f"Photo by {author}"
                elif tags:
                    title = tags[:65]

                candidates.append({
                    "sourceUrl": source_url,
                    "thumbnailUrl": thumb_url,
                    "title": title[:90],
                    "width": int(item.get("imageWidth", 0) or 0),
                    "height": int(item.get("imageHeight", 0) or 0)
                })

                if len(candidates) >= limit:
                    break
    except Exception as e:
        sys.stderr.write(f"Pixabay scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="Pixabay Creative Scraper for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search query (e.g. landscapes, vehicles, animals)")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--api-key", type=str, default="", help="Pixabay API key")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_pixabay(args.query, args.limit, args.api_key)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
