#!/usr/bin/env python3
"""
Gelbooru Authenticated Image Scraper Plugin for LoRAMancer Image Harvester
Harvests anime, manga, and character art from Gelbooru using user credentials.
"""

import sys
import os
import json
import argparse
import urllib.request
import urllib.parse
from pathlib import Path


def get_plugin_credentials():
    api_key = os.environ.get("LORAMANCER_CONFIG_APIKEY") or os.environ.get("PLUGIN_APIKEY") or ""
    user_id = os.environ.get("LORAMANCER_CONFIG_USERID") or os.environ.get("PLUGIN_USERID") or ""

    if not (api_key and user_id):
        try:
            user_home = Path.home()
            config_path = user_home / ".loramancer" / "plugin_configs" / "gelbooru-scraper.json"
            if config_path.exists():
                data = json.loads(config_path.read_text(encoding="utf-8"))
                if isinstance(data, dict):
                    if not api_key:
                        api_key = data.get("apiKey") or data.get("api_key") or ""
                    if not user_id:
                        user_id = data.get("userId") or data.get("user_id") or ""
        except Exception:
            pass

    return api_key.strip(), user_id.strip()


def scrape_gelbooru(query: str, limit: int = 40, api_key: str = "", user_id: str = ""):
    conf_key, conf_user = get_plugin_credentials()
    final_key = api_key.strip() if api_key and api_key.strip() else conf_key
    final_user = user_id.strip() if user_id and user_id.strip() else conf_user

    if not (final_key and final_user):
        sys.stderr.write(
            "Gelbooru API requires user authentication.\n"
            "Please click the gear icon (⚙️) on the Gelbooru plugin card in LoRAMancer to set your API Key and User ID.\n"
        )
        return []

    raw_query = query.strip() if query and query.strip() else ""
    tag_tokens = raw_query.split() if raw_query else []
    normalized_tags = [t.strip().replace(" ", "_") for t in tag_tokens if t.strip()]
    safe_tags = urllib.parse.quote(" ".join(normalized_tags))

    per_page = min(max(limit, 5), 100)
    url = f"https://gelbooru.com/index.php?page=dapi&s=post&q=index&json=1&limit={per_page}&api_key={urllib.parse.quote(final_key)}&user_id={urllib.parse.quote(final_user)}"
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

            posts = data.get("post", []) if isinstance(data, dict) else (data if isinstance(data, list) else [])
            for post in posts:
                file_url = post.get("file_url")
                if not file_url:
                    continue

                thumb_url = post.get("preview_url") or file_url
                tags = post.get("tags", "")
                title = f"Gelbooru #{post.get('id', '')}"
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
        sys.stderr.write(f"Gelbooru scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="Gelbooru Authenticated Scraper for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search booru tags query")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--api-key", type=str, default="", help="Gelbooru API key")
    parser.add_argument("--user-id", type=str, default="", help="Gelbooru user ID")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_gelbooru(args.query, args.limit, args.api_key, args.user_id)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
