# Native C# Image Harvester 🌐

## Overview

The **Image Harvester** (`/harvester`) is LoRAMancer's native, multi-source reference image harvester and dataset discovery suite. Located under **Studio Workshop**, it enables creators to search, batch harvest, audit, and download candidate training images across public search engines and media feeds directly into their LoRAMancer project datasets.

Unlike traditional external Python/Streamlit plugins, the Image Harvester runs entirely in native **C# (.NET 10)** on asynchronous background threads—delivering fast, non-blocking searches, real-time downloads, and seamless Catppuccin-styled UI integration.

---

## Key Features

### 1. Multi-Engine Search Providers
The Harvester supports a comprehensive suite of built-in search providers, with dynamic toggling and custom provider support:
- **Selective Multi-Provider Pill Search**: Click any provider pill to highlight and select it for custom multi-source harvesting. Quick-select links (**Select All**, **Clear**, **General**, **Boorus**, **Scrapers**) allow instant filtering. The primary Harvest button dynamically displays the active count (e.g. `Harvest (3 Selected)` or `Harvest (All)`) and concurrently queries all selected providers in parallel (`Task.WhenAll`) with individual 12s timeouts.
- **Search All or Custom Subsets**: Concurrently scrape candidate imagery across chosen providers in parallel, merging and deduplicating results into a single unified gallery.
- **DuckDuckGo Image Search**: Queries DuckDuckGo's high-speed `/i.js` image endpoint. Supports custom queries, max results, minimum image dimensions, and safe search toggling.
- **Reddit Public Feeds**: Extracts high-resolution imagery directly from public art, photography, and subject subreddits (e.g. `r/wallpaper`, `r/itookapicture`, `r/EarthPorn`, `r/Art`) using clean JSON feeds.
- **Wikimedia Commons**: Queries the Wikimedia Commons search API for high-resolution public domain and Creative Commons art, architecture, and historical reference imagery.
- **Unsplash Photography**: Curated, royalty-free high-resolution photography and aesthetic references.
- **Safebooru (Anime & Tags)**: High-resolution anime, illustrations, and stylized concept art indexed by tags via Safebooru's public JSON API.
- **Danbooru (Tags)**: Tag-based anime, character, and art database queries.
- **Openverse (Creative Commons Index)**: Searches over 700 million openly licensed Creative Commons artworks and photographs.
- **Flickr Public Feed**: Keyword and tag-based photography streams from Flickr's public media feeds.
- **Direct URL Batch Importer**: Paste lists of direct image URLs separated by spaces or newlines for instant ingestion into the harvesting pipeline.
- **Python Scraper Plugins (`uiSlot: "HarvesterScraper"`)**: Community-extensible scrapers written in Python. To maximize efficiency and prevent disk bloat, all scraper plugins share a single consolidated virtual environment (`~/.loramancer/scraper_venv` pre-configured with `requests`, `beautifulsoup4`, `cloudscraper`, and `urllib3`), avoiding duplicate multi-gigabyte virtual environments. Scrapers are excluded from the main navigation sidebar to prevent menu clutter and are managed in a dedicated **Image Scrapers** tab in the Plugin Manager (`/plugins`). Each scraper stores its settings and credentials in its own JSON file at `~/.loramancer/scrapers/<id>.json`, configured via the gear icon (⚙️) on its plugin card. LoRAMancer invokes scrapers via standardized CLI flags (`--query "..." --limit ... --json`), and ingests stdout JSON into the unified Harvester candidate pool. Built-in scraper plugins include:
  - **Civitai Community Showcase** (`plugins/civitai_scraper`): Harvests generated community images and model showcases from Civitai.
  - **Wallhaven 4K/HD Wallpapers** (`plugins/wallhaven_scraper`): Ultra high-resolution digital art, anime, fantasy, and nature wallpapers from the Wallhaven API.
  - **ArtStation Digital Concepts** (`plugins/artstation_scraper`): Industry concept art, character designs, and 3D renders from ArtStation.
  - **DeviantArt Community Feed** (`plugins/deviantart_scraper`): Digital illustrations and fanart via DeviantArt Media RSS.
  - **Yande.re High-Res Anime Art** (`plugins/yandere_scraper`): High-resolution anime scans and digital paintings from Yande.re.
  - **Konachan Anime Wallpapers** (`plugins/konachan_scraper`): Widescreen anime wallpapers and high-definition illustrations from Konachan.
  - **Art Institute of Chicago** (`plugins/artic_scraper`): Master paintings, classical fine art, and museum artifacts via ArtIC IIIF API.
  - **Library of Congress Archive** (`plugins/loc_scraper`): Vintage photography, architectural archives, and historical portraits from the US Library of Congress.
  - **e621 Anthro & Creature Art** (`plugins/e621_scraper`): Anthropomorphic, creature, and fantasy character artwork with rich metadata.
  - **Xbooru Explicit Art** (`plugins/xbooru_scraper`): Explicit anime, 3D renders, and adult art from Xbooru API with booru tag filtering.
  - **Konachan Adult Wallpapers** (`plugins/konachan_adult_scraper`): Explicit and adult widescreen anime artwork and wallpapers from Konachan.com.
  - **Civitai NSFW Generations** (`plugins/civitai_nsfw_scraper`): Explicit AI artwork and mature generation feeds from Civitai with optional personal API key support.
  - **TBIB Cosplay & Gravure** (`plugins/tbib_scraper`): Real cosplay, gravure, swimwear, and Asian idol photography from TBIB.
  - **Rule34.xxx Community Archive** (`plugins/rule34_scraper`): Explicit artwork and character tags from Rule34.xxx API (disabled by default; requires API Key & User ID configured via gear icon).
  - **Danbooru Authenticated Archive** (`plugins/danbooru_scraper`): Access full post ratings and original resolutions on Danbooru (disabled by default; requires API Key & Username).
  - **Gelbooru Authenticated Archive** (`plugins/gelbooru_scraper`): Millions of anime and game fanart illustrations via official Gelbooru API (disabled by default; requires API Key & User ID).
  - **Pexels 4K Photography** (`plugins/pexels_scraper`): Free-to-use ultra-high-resolution real-world stock and portrait photography (disabled by default; requires free Pexels API Key).
  - **Pixabay Creative Archive** (`plugins/pixabay_scraper`): Millions of royalty-free photographs, vectors, and 3D illustrations (disabled by default; requires free Pixabay API Key).
- **Custom REST Providers**: Creators can add arbitrary search endpoints with custom URL templates (`https://api.example.com/search?q={query}&limit={limit}`), optional JSON results array paths, and customizable key mappings for image URL, thumbnail, and title.

### 2. Search Provider Manager & Dynamic Controls
- **Provider Toggling**: Enable or disable any search provider directly from the quick chips bar or via the dedicated **Configure Providers** manager dialog.
- **Settings Persistence**: Active and custom providers are saved directly in `~/.loramancer/settings.json` (`HarvestDisabledProviderIds`, `HarvestCustomProviders`).
- **Source Badges**: Every card in the candidate gallery clearly displays the originating provider badge (e.g., *DuckDuckGo*, *Unsplash*, *Safebooru*, *Reddit*).

### 3. High-Performance Background Execution
- **Asynchronous & Non-Blocking**: All network queries and downloads execute on background worker threads using .NET 10 `HttpClient` with connection pooling and automated gzip/brotli decompression.
- **Concurrent Download Queue**: Parallelized downloading throttled with `SemaphoreSlim(4)` ensures maximum throughput without overloading local disks or remote hosts.
- **Real-Time Progress Reporting**: Live progress banner tracking active index, current file name, and overall progress percentage.

### 4. Native Ollama Vision Dataset Cleanse
Audit candidate images with local Ollama vision models (`llama3.2-vision`, `llava`, `qwen2-vl`) before downloading:
- **Quality Presets**:
  - 🔤 **Watermarks & Overlays**: Flags text, stamps, subtitles, brand logos, or UI overlays.
  - 🌫️ **Blurry / Low Quality**: Identifies out-of-focus, motion-blurred, heavily pixelated, or degraded images.
  - 🎨 **Non-Photographic Art / CGI**: Identifies anime, 2D cartoons, and 3D renderings when building photorealistic models.
  - 🖼️ **Memes & Letterbox**: Detects memes, top/bottom caption text, and black letterbox bars.
- **Auto-Deselection**: Flagged images display a visual warning badge with reason tooltips and are automatically deselected from the download set.

### 5. Interactive Gallery & Full-Resolution Lightbox
- **Responsive Card Grid**: Displays candidate thumbnails with selection checkboxes, dimension badges, and source engine chips.
- **Bulk Selection Tools**: 1-click **Select All**, **Deselect All**, and **Invert Selection**.
- **Full-Resolution Lightbox**: Click any thumbnail to inspect high-resolution imagery, dimensions, source URLs, and Ollama audit details in an overlay modal.

### 6. Catalogs & Saved Searches (Continuous Dataset Harvester)
Inspired by the `imagetaker` workflow, the Image Harvester allows creators to save searches as permanent **Catalogs**:
- **Persistent Catalogs**: Save search queries, target provider selections, and destination folders into persistent catalogs stored in `~/.loramancer/harvest_catalogs.json`.
- **Automatic History Tracking & Deduplication**: Each catalog folder maintains a `download_history.json` tracking all previously downloaded image URLs and scanned local filenames.
- **Omit Existing Images**: When searching or syncing against a catalog (or any chosen destination folder), the harvester automatically checks history and local files on disk. Previously downloaded images are automatically omitted so you only see newly discovered candidates.
- **Continuous 1-Click Sync**: Click **"Sync New"** on any catalog in the Catalogs Manager to immediately scrape fresh images across your saved providers, omit duplicates, download the new additions directly into the catalog folder, and update the catalog count.
- **100% ImageTaker Compatibility**: Uses standard `download_history.json` array format compatible with existing `imagetaker` project folders.

### 7. Seamless Stage 1 (Curate & Caption) Integration
- **Direct Dataset Delivery**: Download selected files straight into the default `~/.loramancer/harvested_datasets/` cache, custom folders, or organized catalog directories.
- **1-Click Stage 1 Transfer**: Click **"Send to Stage 1 (Curate)"** from the Harvester header (or from any catalog card) to jump directly to `/curate` with the downloaded images ready for cropping, auto-captioning, and training prep.
- **Quick-Launch from Stage 1**: Click the **"📥 Harvest Images"** button in Stage 1's header toolbar to jump straight to the Harvester at any point during dataset preparation.
