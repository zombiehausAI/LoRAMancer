#!/usr/bin/env python3
"""
Civitai NSFW Image Scraper Plugin for LoRAMancer Image Harvester
Harvests explicit, mature, and soft-NSFW AI generations and prompt metadata from Civitai.
"""

import sys
import os
import json
import argparse
import urllib.request
import urllib.parse
from pathlib import Path


def get_configured_api_key() -> str:
    # 1. Environment variables set by LoRAMancer PluginManagerService
    env_key = os.environ.get("LORAMANCER_CONFIG_APIKEY") or os.environ.get("PLUGIN_APIKEY")
    if env_key and env_key.strip():
        return env_key.strip()

    # 2. Check user profile plugin configuration file (~/.loramancer/plugin_configs/civitai-nsfw-scraper.json)
    try:
        user_home = Path.home()
        config_path = user_home / ".loramancer" / "plugin_configs" / "civitai-nsfw-scraper.json"
        if config_path.exists():
            data = json.loads(config_path.read_text(encoding="utf-8"))
            if isinstance(data, dict):
                key = data.get("apiKey") or data.get("api_key")
                if key and key.strip():
                    return key.strip()
    except Exception:
        pass

    return ""


def scrape_civitai_nsfw(query: str, limit: int = 40, api_key: str = ""):
    token = api_key.strip() if api_key and api_key.strip() else get_configured_api_key()

    safe_q = urllib.parse.quote(query.strip()) if query and query.strip() else ""
    per_page = min(max(limit, 5), 100)

    headers = {
        "User-Agent": "LoRAMancer/1.0 (Civitai NSFW Harvester)",
        "Accept": "application/json"
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"

    candidates = []

    # When query is provided, query Civitai models API to retrieve keyword-matched models and their sample images
    if safe_q:
        model_url = f"https://civitai.com/api/v1/models?query={safe_q}&limit=15&nsfw=true"
        try:
            req = urllib.request.Request(model_url, headers=headers)
            with urllib.request.urlopen(req, timeout=15) as resp:
                data = json.loads(resp.read().decode("utf-8", errors="replace"))
                items = data.get("items", [])
                for model in items:
                    model_name = model.get("name", "Civitai NSFW Model")
                    for mv in model.get("modelVersions", []):
                        for img in mv.get("images", []):
                            img_url = img.get("url")
                            if not img_url:
                                continue
                            hires_url = img_url.replace("/width=450/", "/original=true/").replace("/width=850/", "/original=true/")
                            thumb_url = img_url.replace("/original=true/", "/width=450/")
                            meta = img.get("meta") or {}
                            prompt = meta.get("prompt", "")
                            title = f"{model_name} - {prompt[:50]}..." if prompt else model_name

                            candidates.append({
                                "sourceUrl": hires_url,
                                "thumbnailUrl": thumb_url,
                                "title": title[:90],
                                "width": img.get("width", 0),
                                "height": img.get("height", 0)
                            })
                            if len(candidates) >= limit:
                                return candidates
        except Exception as e:
            sys.stderr.write(f"Civitai NSFW model search error: {e}\n")

        return candidates

    # Explicit and mature rated images fallback when no query specified
    url = f"https://civitai.com/api/v1/images?limit={per_page}&nsfw=X&sort=Most+Reactions"

    req = urllib.request.Request(url, headers=headers)

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read().decode("utf-8", errors="replace"))
            items = data.get("items", [])

            for item in items:
                img_url = item.get("url")
                if not img_url:
                    continue

                # Obtain high-res image URL by replacing medium/small dimension tokens
                hires_url = img_url.replace("/width=450/", "/original=true/").replace("/width=850/", "/original=true/")
                thumb_url = img_url.replace("/original=true/", "/width=450/")

                meta = item.get("meta") or {}
                prompt = meta.get("prompt", "")
                title = f"Civitai #{item.get('id', '')}"
                if prompt:
                    title = f"{title} - {prompt[:50]}..."

                candidates.append({
                    "sourceUrl": hires_url,
                    "thumbnailUrl": thumb_url,
                    "title": title[:90],
                    "width": int(item.get("width", 0) or 0),
                    "height": int(item.get("height", 0) or 0)
                })

                if len(candidates) >= limit:
                    break
    except Exception as e:
        sys.stderr.write(f"Civitai NSFW scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="Civitai NSFW Image Scraper Plugin for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search query filter")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--api-key", type=str, default="", help="Optional Civitai personal API token")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_civitai_nsfw(args.query, args.limit, args.api_key)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
