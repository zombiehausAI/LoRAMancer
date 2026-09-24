"""
Ollama Vision LoRA Tagger & Captioner Plugin for LoRAMancer
Auto-captions training images using local Ollama vision models (e.g. llama3.2-vision, llava, minicpm-v, qwen2-vl).
Accepts folders or ZIP archives, applies trigger words, inclusions, and blacklists, and outputs ready-to-train datasets or ZIPs.
"""
import argparse
import base64
import io
import json
import os
import shutil
import sys
import tempfile
import urllib.error
import urllib.request
import zipfile

IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".bmp"}

VISION_MODEL_HINTS = [
    "vision", "llava", "minicpm", "qwen2-vl", "moondream", "bakllava", "cogvlm"
]

DEFAULT_TAG_PROMPT = (
    "Analyze this image in detail for machine learning training. "
    "Provide a concise, comma-separated list of visual tags describing: "
    "the subject, attire, hair, expression, pose, background, lighting, and artistic style. "
    "Do not include conversational filler, introductory words, or markdown. Output ONLY comma-separated tags."
)

DEFAULT_NATURAL_PROMPT = (
    "Describe this image thoroughly in 1-2 detailed sentences for training a text-to-image AI model. "
    "Focus on subject appearance, posture, clothing, colors, setting, and lighting. "
    "Do not say 'This is an image of' or 'The picture shows'. Be descriptive and direct."
)


def ping_ollama(url: str = "http://localhost:11434") -> dict:
    url = url.rstrip("/")
    try:
        req = urllib.request.Request(f"{url}/api/tags", headers={"User-Agent": "LoRAMancer-Tagger/1.0"})
        with urllib.request.urlopen(req, timeout=5) as response:
            if response.status == 200:
                data = json.loads(response.read().decode("utf-8"))
                models = [m.get("name", "") for m in data.get("models", [])]
                vision_models = [
                    m for m in models if any(hint in m.lower() for hint in VISION_MODEL_HINTS)
                ]
                return {
                    "reachable": True,
                    "all_models": models,
                    "vision_models": vision_models,
                    "default_model": vision_models[0] if vision_models else (models[0] if models else "llama3.2-vision")
                }
    except Exception as e:
        return {
            "reachable": False,
            "error": str(e),
            "all_models": [],
            "vision_models": [],
            "default_model": "llama3.2-vision"
        }
    return {
        "reachable": False,
        "error": "Unknown connection state",
        "all_models": [],
        "vision_models": [],
        "default_model": "llama3.2-vision"
    }


def query_ollama_vision(
    image_bytes: bytes,
    model: str,
    prompt: str,
    ollama_url: str = "http://localhost:11434",
    timeout: int = 60
) -> str:
    url = f"{ollama_url.rstrip('/')}/api/generate"
    b64_image = base64.b64encode(image_bytes).decode("utf-8")

    payload = {
        "model": model,
        "prompt": prompt,
        "images": [b64_image],
        "stream": False,
        "options": {
            "temperature": 0.2,
            "num_predict": 256
        }
    }

    req_data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=req_data,
        headers={"Content-Type": "application/json", "User-Agent": "LoRAMancer-Tagger/1.0"}
    )

    with urllib.request.urlopen(req, timeout=timeout) as resp:
        if resp.status == 200:
            result = json.loads(resp.read().decode("utf-8"))
            return result.get("response", "").strip()
        else:
            raise RuntimeError(f"Ollama returned HTTP {resp.status}")


def sanitize_and_format_caption(
    raw_caption: str,
    trigger_word: str = "",
    included_phrases: list = None,
    blacklist_words: list = None,
    caption_style: str = "tags"
) -> str:
    included_phrases = included_phrases or []
    blacklist_words = [w.strip().lower() for w in (blacklist_words or []) if w.strip()]

    # Strip conversational prefixes
    prefixes = [
        "here is a description:", "here are the tags:", "tags:", "description:",
        "the image shows", "this image features", "an image of", "a photo of"
    ]
    cleaned_lower = raw_caption.lower()
    for prefix in prefixes:
        if cleaned_lower.startswith(prefix):
            raw_caption = raw_caption[len(prefix):].strip(": ,")
            cleaned_lower = raw_caption.lower()

    if caption_style == "tags":
        # Split on commas, newlines, or semicolons
        parts = [p.strip() for p in raw_caption.replace("\n", ",").replace(";", ",").split(",") if p.strip()]
        
        filtered = []
        for tag in parts:
            tag_clean = tag.strip(" .-\t").lower()
            if not tag_clean:
                continue
            if any(b in tag_clean for b in blacklist_words):
                continue
            if tag_clean not in [f.lower() for f in filtered]:
                filtered.append(tag.strip())

        # Inject included phrases if not already present
        for inc in included_phrases:
            inc_clean = inc.strip()
            if inc_clean and inc_clean.lower() not in [f.lower() for f in filtered]:
                filtered.append(inc_clean)

        # Prepend trigger word
        if trigger_word:
            trigger_clean = trigger_word.strip()
            if trigger_clean and trigger_clean.lower() in [f.lower() for f in filtered]:
                filtered = [f for f in filtered if f.lower() != trigger_clean.lower()]
            filtered.insert(0, trigger_clean)

        return ", ".join(filtered)
    else:
        # Natural language style
        text = raw_caption.strip()
        for b in blacklist_words:
            if b in text.lower():
                # Case-insensitive removal
                start = text.lower().find(b)
                while start != -1:
                    text = text[:start] + text[start + len(b):]
                    start = text.lower().find(b)

        text = " ".join(text.split())
        if trigger_word and not text.lower().startswith(trigger_word.lower()):
            text = f"{trigger_word.strip()}, {text}"

        return text.strip()


def handle_tag_dataset(data: dict) -> dict:
    input_path = data.get("input_path", "").strip()
    output_path = data.get("output_path", "").strip()
    trigger_word = data.get("trigger_word", "").strip()
    ollama_url = data.get("ollama_url", "http://localhost:11434").strip()
    model = data.get("model", "llama3.2-vision").strip()
    caption_style = data.get("caption_style", "tags").strip()
    custom_prompt = data.get("custom_prompt", "").strip()
    create_zip = data.get("create_zip", False)

    # Process inclusions and blacklist
    raw_inclusions = data.get("included_phrases", [])
    if isinstance(raw_inclusions, str):
        included_phrases = [x.strip() for x in raw_inclusions.split(",") if x.strip()]
    else:
        included_phrases = list(raw_inclusions)

    raw_blacklist = data.get("blacklist_words", [])
    if isinstance(raw_blacklist, str):
        blacklist_words = [x.strip() for x in raw_blacklist.split(",") if x.strip()]
    else:
        blacklist_words = list(raw_blacklist)

    if not input_path or not os.path.exists(input_path):
        return {"status": "error", "message": f"Input path does not exist: {input_path}"}

    # Verify Ollama reachability
    ollama_status = ping_ollama(ollama_url)
    if not ollama_status["reachable"]:
        return {
            "status": "error",
            "message": f"Ollama is not running or unreachable at {ollama_url}. Please ensure Ollama is installed and started.",
            "details": ollama_status.get("error", "")
        }

    # Pick prompt
    if custom_prompt:
        prompt = custom_prompt
    elif caption_style == "natural":
        prompt = DEFAULT_NATURAL_PROMPT
    else:
        prompt = DEFAULT_TAG_PROMPT

    # Handle unzipping if input is a zip file
    temp_extract_dir = None
    source_dir = input_path
    if os.path.isfile(input_path) and input_path.lower().endswith(".zip"):
        temp_extract_dir = tempfile.mkdtemp(prefix="loramancer_tagger_in_")
        with zipfile.ZipFile(input_path, "r") as zf:
            zf.extractall(temp_extract_dir)
        source_dir = temp_extract_dir

    # Determine destination directory
    target_is_zip = output_path.lower().endswith(".zip")
    if target_is_zip or create_zip:
        work_out_dir = tempfile.mkdtemp(prefix="loramancer_tagger_out_")
    else:
        work_out_dir = output_path if output_path else source_dir
        os.makedirs(work_out_dir, exist_ok=True)

    # Collect image files
    image_files = []
    for root, _, files in os.walk(source_dir):
        for f in files:
            ext = os.path.splitext(f)[1].lower()
            if ext in IMAGE_EXTENSIONS:
                image_files.append(os.path.join(root, f))

    if not image_files:
        if temp_extract_dir and os.path.exists(temp_extract_dir):
            shutil.rmtree(temp_extract_dir, ignore_errors=True)
        return {"status": "error", "message": "No supported images (.png, .jpg, .jpeg, .webp) found."}

    processed = 0
    sample_captions = []
    errors = []

    for img_path in image_files:
        try:
            with open(img_path, "rb") as f:
                img_data = f.read()

            raw_caption = query_ollama_vision(img_data, model, prompt, ollama_url)
            final_caption = sanitize_and_format_caption(
                raw_caption,
                trigger_word=trigger_word,
                included_phrases=included_phrases,
                blacklist_words=blacklist_words,
                caption_style=caption_style
            )

            file_base = os.path.splitext(os.path.basename(img_path))[0]
            txt_filename = file_base + ".txt"

            dest_txt_path = os.path.join(work_out_dir, txt_filename)
            with open(dest_txt_path, "w", encoding="utf-8") as tf:
                tf.write(final_caption)

            # Copy image to output if distinct
            dest_img_path = os.path.join(work_out_dir, os.path.basename(img_path))
            if os.path.abspath(img_path) != os.path.abspath(dest_img_path):
                shutil.copy2(img_path, dest_img_path)

            processed += 1
            if len(sample_captions) < 5:
                sample_captions.append({
                    "image": os.path.basename(img_path),
                    "caption": final_caption
                })
        except Exception as ex:
            errors.append(f"{os.path.basename(img_path)}: {str(ex)}")

    final_zip_path = None
    if target_is_zip or create_zip:
        if target_is_zip:
            final_zip_path = output_path
        else:
            final_zip_path = os.path.join(
                os.path.dirname(output_path) if output_path else source_dir,
                f"tagged_dataset_{len(image_files)}.zip"
            )

        os.makedirs(os.path.dirname(os.path.abspath(final_zip_path)), exist_ok=True)
        with zipfile.ZipFile(final_zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
            for root, _, files in os.walk(work_out_dir):
                for f in files:
                    full_p = os.path.join(root, f)
                    arc_name = os.path.relpath(full_p, work_out_dir)
                    zf.write(full_p, arc_name)

        shutil.rmtree(work_out_dir, ignore_errors=True)

    if temp_extract_dir and os.path.exists(temp_extract_dir):
        shutil.rmtree(temp_extract_dir, ignore_errors=True)

    return {
        "status": "success",
        "processed_count": processed,
        "total_images": len(image_files),
        "output_directory": work_out_dir if not (target_is_zip or create_zip) else None,
        "output_zip": final_zip_path,
        "sample_captions": sample_captions,
        "errors": errors[:5]
    }


def main():
    parser = argparse.ArgumentParser(description="Ollama Vision LoRA Tagger Plugin")
    parser.add_argument("--cmd", type=str, required=True, help="Command to execute")
    parser.add_argument("--data", type=str, default="{}", help="JSON payload")

    args = parser.parse_args()

    try:
        data = json.loads(args.data)
    except Exception:
        data = {}

    if args.cmd == "ping":
        ollama_info = ping_ollama(data.get("ollama_url", "http://localhost:11434"))
        result = {
            "status": "success",
            "message": "Ollama LoRA Tagger plugin is active",
            "python_version": sys.version,
            "ollama": ollama_info
        }
    elif args.cmd == "check_ollama":
        result = ping_ollama(data.get("ollama_url", "http://localhost:11434"))
    elif args.cmd == "tag_dataset":
        result = handle_tag_dataset(data)
    else:
        result = {"status": "error", "message": f"Unknown command: {args.cmd}"}

    print(json.dumps(result))


if __name__ == "__main__":
    main()
