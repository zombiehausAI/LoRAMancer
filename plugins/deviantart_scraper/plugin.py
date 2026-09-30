#!/usr/bin/env python3
"""
DeviantArt Media Scraper Plugin for LoRAMancer Image Harvester
Harvests artwork and digital illustrations from DeviantArt media feeds.
"""

import sys
import json
import argparse
import urllib.request
import urllib.parse
import xml.etree.ElementTree as ET


def scrape_deviantart(query: str, limit: int = 40):
    raw_query = query.strip() if query and query.strip() else "digital art"
    safe_q = urllib.parse.quote(raw_query)
    url = f"https://backend.deviantart.com/rss.xml?q={safe_q}&type=deviation"

    headers = {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36",
        "Accept": "application/rss+xml, application/xml, text/xml"
    }

    req = urllib.request.Request(url, headers=headers)
    candidates = []

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            xml_data = resp.read()
            root = ET.fromstring(xml_data)

            # Namespace map for media RSS
            ns = {
                "media": "http://search.yahoo.com/mrss/",
                "atom": "http://www.w3.org/2005/Atom"
            }

            for item in root.findall(".//item"):
                title_elem = item.find("title")
                title = title_elem.text if title_elem is not None and title_elem.text else "DeviantArt Piece"

                # Extract author credit if present
                author_elem = item.find("media:credit", ns)
                if author_elem is not None and author_elem.text:
                    title = f"{title} - by {author_elem.text}"

                # Extract highest resolution media:content
                contents = item.findall("media:content", ns)
                source_url = ""
                width = 0
                height = 0

                for content in contents:
                    c_url = content.attrib.get("url", "")
                    c_medium = content.attrib.get("medium", "")
                    if c_medium == "image" or not c_medium:
                        w = int(content.attrib.get("width", 0) or 0)
                        h = int(content.attrib.get("height", 0) or 0)
                        if w * h >= width * height or not source_url:
                            source_url = c_url
                            width = w
                            height = h

                # Extract thumbnail
                thumb_elem = item.find("media:thumbnail", ns)
                thumb_url = thumb_elem.attrib.get("url", "") if thumb_elem is not None else source_url

                if not source_url:
                    continue

                candidates.append({
                    "sourceUrl": source_url,
                    "thumbnailUrl": thumb_url,
                    "title": title[:90],
                    "width": width,
                    "height": height
                })

                if len(candidates) >= limit:
                    break
    except Exception as e:
        sys.stderr.write(f"DeviantArt scraper error: {e}\n")

    return candidates


def main():
    parser = argparse.ArgumentParser(description="DeviantArt Image Scraper Plugin for LoRAMancer")
    parser.add_argument("--query", type=str, default="", help="Search query filter")
    parser.add_argument("--limit", type=int, default=40, help="Maximum number of candidates")
    parser.add_argument("--json", action="store_true", help="Output JSON result list to stdout")

    args = parser.parse_args()
    results = scrape_deviantart(args.query, args.limit)

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
