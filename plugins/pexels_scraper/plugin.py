#!/usr/bin/env python3
"""
Pexels Photography Scraper Plugin for LoRAMancer Image Harvester
Harvests ultra high-resolution, free-to-use photography from Pexels API.
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
            config_path = user_home / ".loramancer" / "plugin_configs" / "pexels-scraper.json"
            if config_path.exists():
                data = json.loads(config_path.read_text(encoding="utf-8"))
                if isinstance(data, dict):
                    api_key = data.get("apiKey") or data.get("api_key") or ""
        except Exception:
            pass

    return api_key.strip()


def scrape_pexels(query: str, limit: int = 40, api_key: str = ""):
    token = api_key.strip() if api_key and api_key.strip() else get_plugin_api_key()

    if not token:
        sys.stderr.write(
            "Pexels scraper requires a free API key.\n"
            "Please click the gear icon (⚙️) on the Pexels plugin card in LoRAMancer to set your API Key.\n"
        )
        return []

    raw_query = query.strip() if query and query.strip() else "portrait"
    safe_q = urllib.parse.quote(raw_query)

    per_page = min(max(limit, 5), 80)
    url = f"https://api.pexels.com/v1/search?query={safe_q}&per_page={per_page}"

    headers = {
        "User-Agent": "LoRAMancer/1.0 (Pexels Harvester)",
        "Authorization": token,
        "Accept": "application/json"
    }

    req = urllib.request.Request(url, headers=headers)
    candidates = []

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read().decode("utf-8", errors="replace"))
            photos = data.get("photos", [])

            for item in photos:
                src = item.get("src", {})
                source_url = src.get("original") or src.get("large2x")
                if not source_url:
                    continue

                thumb_url = src.get("medium") or src.get("tiny") or source_url
                photographer = item.get("photographer", "")
                alt = item.get("alt", "")

                title = f"Pexels #{item.get('id', '')}"
                if photographer and alt:
                    title = f"{alt[:50]} - by {photographer}"
                elif photographer:
                    title = f"Photo by {photographer}"
                elif alt:
                    title = alt[:70]

                candidates.append({
                    "sourceUrl": source_url,
                    "thumbnailUrl": thumb_url,
                    "title": title[:90],
                    "width": int(item.get("width", 0) or 0),
                    "height": int(item.get("height", 0) or 0)
                })

                if len(candidates) >= limit:
                    break
    except Exception as e:
        sys.stderr.write(f"Pexels scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="Pexels Photography Scraper for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search query (e.g. portrait, studio lighting, landscape)")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--api-key", type=str, default="", help="Pexels API key")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_pexels(args.query, args.limit, args.api_key)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
