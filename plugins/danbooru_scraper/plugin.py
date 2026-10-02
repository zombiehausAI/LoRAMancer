#!/usr/bin/env python3
"""
Danbooru Authenticated Image Scraper Plugin for LoRAMancer Image Harvester
Harvests anime art and character designs across all ratings from Danbooru using user credentials.
"""

import sys
import os
import json
import base64
import argparse
import urllib.request
import urllib.parse
from pathlib import Path


def get_plugin_credentials():
    api_key = os.environ.get("LORAMANCER_CONFIG_APIKEY") or os.environ.get("PLUGIN_APIKEY") or ""
    username = os.environ.get("LORAMANCER_CONFIG_USERNAME") or os.environ.get("PLUGIN_USERNAME") or ""

    if not (api_key and username):
        try:
            user_home = Path.home()
            config_path = user_home / ".loramancer" / "plugin_configs" / "danbooru-scraper.json"
            if config_path.exists():
                data = json.loads(config_path.read_text(encoding="utf-8"))
                if isinstance(data, dict):
                    if not api_key:
                        api_key = data.get("apiKey") or data.get("api_key") or ""
                    if not username:
                        username = data.get("username") or data.get("login") or ""
        except Exception:
            pass

    return api_key.strip(), username.strip()


def scrape_danbooru(query: str, limit: int = 40, api_key: str = "", username: str = ""):
    conf_key, conf_user = get_plugin_credentials()
    final_key = api_key.strip() if api_key and api_key.strip() else conf_key
    final_user = username.strip() if username and username.strip() else conf_user

    if not (final_key and final_user):
        sys.stderr.write(
            "Danbooru authenticated scraper requires an API Key and Username.\n"
            "Please click the gear icon (⚙️) on the Danbooru plugin card in LoRAMancer to configure credentials.\n"
        )
        return []

    raw_query = query.strip() if query and query.strip() else ""
    tag_tokens = raw_query.split() if raw_query else []
    normalized_tags = [t.strip().replace(" ", "_") for t in tag_tokens if t.strip()]
    safe_tags = urllib.parse.quote(" ".join(normalized_tags))

    per_page = min(max(limit, 5), 100)
    url = f"https://danbooru.donmai.us/posts.json?limit={per_page}"
    if safe_tags:
        url += f"&tags={safe_tags}"

    # Basic auth header
    credentials = f"{final_user}:{final_key}"
    auth_header = "Basic " + base64.b64encode(credentials.encode("utf-8")).decode("utf-8")

    headers = {
        "User-Agent": f"LoRAMancer/1.0 (by {final_user} on Danbooru)",
        "Authorization": auth_header,
        "Accept": "application/json"
    }

    req = urllib.request.Request(url, headers=headers)
    candidates = []

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            posts = json.loads(resp.read().decode("utf-8", errors="replace"))
            if not isinstance(posts, list):
                return []

            for post in posts:
                file_url = post.get("file_url") or post.get("large_file_url")
                if not file_url:
                    continue

                thumb_url = post.get("preview_file_url") or post.get("large_file_url") or file_url
                tags = post.get("tag_string_character") or post.get("tag_string_general") or ""
                artist = post.get("tag_string_artist") or ""

                title = f"Danbooru #{post.get('id', '')}"
                if artist:
                    title = f"{title} - by {artist}"
                elif tags:
                    title = f"{title} ({tags[:40]}...)"

                candidates.append({
                    "sourceUrl": file_url,
                    "thumbnailUrl": thumb_url,
                    "title": title[:90],
                    "width": int(post.get("image_width", 0) or 0),
                    "height": int(post.get("image_height", 0) or 0)
                })

                if len(candidates) >= limit:
                    break
    except Exception as e:
        sys.stderr.write(f"Danbooru scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="Danbooru Authenticated Scraper for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search booru tags query")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--api-key", type=str, default="", help="Danbooru API key")
    parser.add_argument("--username", type=str, default="", help="Danbooru username")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_danbooru(args.query, args.limit, args.api_key, args.username)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
