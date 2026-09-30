#!/usr/bin/env python3
"""
Civitai Image Scraper Plugin for LoRAMancer Image Harvester
Harvests public generation showcase images and prompts via the Civitai API.
"""

import sys
import json
import argparse
import urllib.request
import urllib.parse


def scrape_civitai(query: str, limit: int = 40):
    candidates = []
    headers = {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36",
        "Accept": "application/json"
    }

    clean_query = query.strip() if query and query.strip() else ""

    # When a search query is given, query Civitai models API to retrieve keyword-matched models and their sample images
    if clean_query:
        safe_q = urllib.parse.quote(clean_query)
        url = f"https://civitai.com/api/v1/models?query={safe_q}&limit=15&nsfw=false"
        try:
            req = urllib.request.Request(url, headers=headers)
            with urllib.request.urlopen(req, timeout=15) as resp:
                data = json.loads(resp.read().decode("utf-8", errors="replace"))
                items = data.get("items", [])
                for model in items:
                    model_name = model.get("name", "Civitai Model")
                    for mv in model.get("modelVersions", []):
                        for img in mv.get("images", []):
                            img_url = img.get("url", "")
                            if not img_url:
                                continue
                            width = img.get("width", 0)
                            height = img.get("height", 0)
                            meta = img.get("meta") or {}
                            prompt = meta.get("prompt", "")
                            title = f"{model_name} - {prompt[:60]}".strip() if prompt else model_name
                            thumb_url = img_url.replace("/original=true", "/width=450") if "/original=true" in img_url else img_url

                            candidates.append({
                                "sourceUrl": img_url,
                                "thumbnailUrl": thumb_url,
                                "title": title[:90],
                                "width": width,
                                "height": height
                            })
                            if len(candidates) >= limit:
                                return candidates
        except Exception as e:
            sys.stderr.write(f"Civitai model search error: {e}\n")

        return candidates

    # If no query was specified, fall back to general showcase
    params = f"limit={min(max(limit, 5), 100)}&sort=Most+Reactions&nsfw=None"
    url = f"https://civitai.com/api/v1/images?{params}"
    req = urllib.request.Request(url, headers=headers)

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read().decode("utf-8", errors="replace"))
            items = data.get("items", [])

            for item in items:
                img_url = item.get("url", "")
                if not img_url:
                    continue

                width = item.get("width", 0)
                height = item.get("height", 0)

                # Extract title/prompt from generation metadata if present
                meta = item.get("meta") or {}
                prompt = meta.get("prompt", "")
                title = prompt[:80].strip() if prompt else "Civitai Community Showcase"

                # Generate thumbnail url with downscale parameter
                thumb_url = img_url.replace("/original=true", "/width=450") if "/original=true" in img_url else img_url

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
        sys.stderr.write(f"Scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="Civitai Image Scraper Plugin for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search query filter")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_civitai(args.query, args.limit)

    # Output standard JSON array to stdout for ImageHarvesterService consumption
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
