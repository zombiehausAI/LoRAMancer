"""
Lora Updater Plugin for LoRAMancer
Scans directories for LoRA models (.safetensors), checks Civitai.com for newer versions using SHA256 hashes,
verifies base model architecture compatibility, creates organized backups, and downloads updates with hash verification.
"""
import argparse
from datetime import datetime
import hashlib
import json
import logging
import os
from pathlib import Path
import shutil
import sys
from typing import Any, Dict, List, Optional
import urllib.parse

import requests

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("LoraUpdater")

CIVITAI_BASE_URL = "https://civitai.com/api/v1"
DEFAULT_USER_AGENT = "LoRAMancer-LoraUpdater/1.0 (Windows NT 10.0; Win64; x64)"


def calculate_sha256(filepath: str, block_size: int = 1024 * 1024) -> str:
    """Calculates the SHA256 hash of a file using 1MB chunk streaming."""
    hasher = hashlib.sha256()
    try:
        with open(filepath, "rb") as f:
            while True:
                chunk = f.read(block_size)
                if not chunk:
                    break
                hasher.update(chunk)
        return hasher.hexdigest().upper()
    except Exception as e:
        logger.error(f"Error calculating hash for {filepath}: {e}")
        return ""


def get_headers(api_key: Optional[str] = None) -> Dict[str, str]:
    headers = {"User-Agent": DEFAULT_USER_AGENT}
    if api_key and api_key.strip():
        headers["Authorization"] = f"Bearer {api_key.strip()}"
    return headers


def get_model_info_by_hash(
    file_hash: str,
    session: Optional[requests.Session] = None,
    api_key: Optional[str] = None
) -> Optional[Dict[str, Any]]:
    """Fetches model version information from Civitai using file hash."""
    clean_hash = file_hash.strip().upper()
    url = f"{CIVITAI_BASE_URL}/model-versions/by-hash/{clean_hash}"
    if api_key and api_key.strip():
        url += f"?token={urllib.parse.quote(api_key.strip())}"

    client = session or requests
    headers = get_headers(api_key)

    try:
        response = client.get(url, timeout=20, headers=headers)
        if response.status_code == 404:
            logger.debug(f"Hash {clean_hash} not found on Civitai.")
            return None
        response.raise_for_status()
        return response.json()
    except requests.exceptions.RequestException as e:
        logger.error(f"Failed to query Civitai for hash {clean_hash}: {e}")
        return None


def get_model_details(
    model_id: int,
    session: Optional[requests.Session] = None,
    api_key: Optional[str] = None
) -> Optional[Dict[str, Any]]:
    """Fetches full model details including all modelVersions."""
    url = f"{CIVITAI_BASE_URL}/models/{model_id}"
    if api_key and api_key.strip():
        url += f"?token={urllib.parse.quote(api_key.strip())}"

    client = session or requests
    headers = get_headers(api_key)

    try:
        response = client.get(url, timeout=20, headers=headers)
        response.raise_for_status()
        return response.json()
    except requests.exceptions.RequestException as e:
        logger.error(f"Failed to query model details for model {model_id}: {e}")
        return None


def download_file(
    url: str,
    destination: str,
    expected_hash: Optional[str] = None,
    session: Optional[requests.Session] = None,
    api_key: Optional[str] = None
) -> bool:
    """Downloads a file in streaming chunks and verifies SHA256 integrity."""
    client = session or requests
    headers = get_headers(api_key)
    download_url = url
    if api_key and api_key.strip() and "civitai.com" in download_url and "token=" not in download_url:
        separator = "&" if "?" in download_url else "?"
        download_url += f"{separator}token={urllib.parse.quote(api_key.strip())}"

    try:
        with client.get(download_url, stream=True, timeout=60, headers=headers) as r:
            r.raise_for_status()
            with open(destination, "wb") as f:
                for chunk in r.iter_content(chunk_size=1024 * 1024):
                    if chunk:
                        f.write(chunk)

        if expected_hash:
            actual_hash = calculate_sha256(destination)
            if actual_hash.upper() != expected_hash.strip().upper():
                logger.error(f"SHA256 mismatch for downloaded file! Expected {expected_hash}, got {actual_hash}")
                if os.path.exists(destination):
                    os.remove(destination)
                return False

        return True
    except Exception as e:
        logger.error(f"Download failed for {url}: {e}")
        if os.path.exists(destination):
            os.remove(destination)
        return False


def find_lora_files(folder: str, recursive: bool = True) -> List[str]:
    """Finds all .safetensors files in a folder."""
    found_files = []
    if not os.path.isdir(folder):
        return found_files

    if recursive:
        for root, _, files in os.walk(folder):
            for file in files:
                if file.lower().endswith(".safetensors"):
                    found_files.append(os.path.join(root, file))
    else:
        for entry in os.scandir(folder):
            if entry.is_file() and entry.name.lower().endswith(".safetensors"):
                found_files.append(entry.path)

    return sorted(found_files)


def check_updates_for_files(
    lora_files: List[str],
    api_key: Optional[str] = None
) -> Dict[str, Any]:
    """Checks Civitai for updates across a list of files without downloading (Dry Run)."""
    results: List[Dict[str, Any]] = []
    summary = {
        "total_files": len(lora_files),
        "updates_available": 0,
        "up_to_date": 0,
        "not_found": 0,
        "skipped_wrong_type": 0,
        "skipped_base_mismatch": 0
    }

    with requests.Session() as session:
        for lora_path in lora_files:
            filename = os.path.basename(lora_path)
            local_hash = calculate_sha256(lora_path)
            if not local_hash:
                continue

            info = get_model_info_by_hash(local_hash, session=session, api_key=api_key)
            if not info:
                summary["not_found"] += 1
                results.append({
                    "file_path": lora_path,
                    "file_name": filename,
                    "status": "not_found",
                    "message": "Not found on Civitai by hash"
                })
                continue

            model_type = info.get("model", {}).get("type", "Unknown")
            if model_type.upper() != "LORA":
                summary["skipped_wrong_type"] += 1
                results.append({
                    "file_path": lora_path,
                    "file_name": filename,
                    "status": "wrong_type",
                    "model_type": model_type,
                    "message": f"Found as '{model_type}', not a LoRA"
                })
                continue

            local_version_id = info.get("id")
            model_id = info.get("modelId")
            local_version_name = info.get("name", "Unknown")
            local_base_model = info.get("baseModel", "Unknown")
            model_name = info.get("model", {}).get("name", filename)

            model_details = get_model_details(model_id, session=session, api_key=api_key)
            if not model_details or not model_details.get("modelVersions"):
                results.append({
                    "file_path": lora_path,
                    "file_name": filename,
                    "status": "error",
                    "message": f"Could not fetch versions for model {model_id}"
                })
                continue

            latest_version = model_details["modelVersions"][0]
            latest_version_id = latest_version.get("id")
            latest_version_name = latest_version.get("name", "Unknown")
            latest_base_model = latest_version.get("baseModel", "Unknown")

            if local_version_id == latest_version_id:
                summary["up_to_date"] += 1
                results.append({
                    "file_path": lora_path,
                    "file_name": filename,
                    "model_name": model_name,
                    "version_name": local_version_name,
                    "base_model": local_base_model,
                    "status": "up_to_date",
                    "message": "Already latest version"
                })
                continue

            # Check base model compatibility
            if local_base_model.lower() != latest_base_model.lower():
                summary["skipped_base_mismatch"] += 1
                results.append({
                    "file_path": lora_path,
                    "file_name": filename,
                    "model_name": model_name,
                    "local_version": local_version_name,
                    "latest_version": latest_version_name,
                    "local_base_model": local_base_model,
                    "latest_base_model": latest_base_model,
                    "status": "base_mismatch",
                    "message": f"Base model mismatch ({local_base_model} vs {latest_base_model})"
                })
                continue

            # Find matching safetensors file in latest version
            latest_file = next(
                (f for f in latest_version.get("files", []) if f.get("name", "").lower().endswith(".safetensors")),
                None
            )

            if not latest_file:
                results.append({
                    "file_path": lora_path,
                    "file_name": filename,
                    "status": "no_safetensors",
                    "message": "Latest version does not contain a .safetensors file"
                })
                continue

            summary["updates_available"] += 1
            results.append({
                "file_path": lora_path,
                "file_name": filename,
                "model_name": model_name,
                "model_id": model_id,
                "local_version": local_version_name,
                "latest_version": latest_version_name,
                "base_model": latest_base_model,
                "status": "update_available",
                "download_url": latest_file.get("downloadUrl"),
                "new_filename": latest_file.get("name"),
                "file_size_kb": latest_file.get("sizeKB"),
                "expected_hash": latest_file.get("hashes", {}).get("SHA256")
            })

    return {
        "status": "success",
        "summary": summary,
        "results": results
    }


def update_lora_file(
    lora_path: str,
    latest_file_info: Dict[str, Any],
    session: requests.Session,
    api_key: Optional[str] = None,
    run_backup_folder: Optional[str] = None,
    base_model: str = "Unknown"
) -> Dict[str, Any]:
    """Downloads, verifies, creates backups, and replaces an individual LoRA file."""
    download_url = latest_file_info.get("downloadUrl")
    new_filename = latest_file_info.get("name") or os.path.basename(lora_path)
    final_path = os.path.join(os.path.dirname(lora_path), new_filename)
    temp_path = final_path + ".tmp"
    expected_hash = latest_file_info.get("hashes", {}).get("SHA256")

    if not download_url:
        return {"success": False, "error": "No download URL provided in file info"}

    logger.info(f"Downloading update '{new_filename}'...")
    if os.path.exists(temp_path):
        os.remove(temp_path)

    if not download_file(download_url, temp_path, expected_hash=expected_hash, session=session, api_key=api_key):
        return {"success": False, "error": "Download failed or hash verification failed"}

    # Prepare backup path
    backup_path = None
    if run_backup_folder:
        sanitized_base = "".join(c for c in base_model if c.isalnum() or c in (" ", ".", "_", "-")).strip() or "Unknown"
        model_backup_dir = os.path.join(run_backup_folder, sanitized_base)
        os.makedirs(model_backup_dir, exist_ok=True)
        backup_path = os.path.join(model_backup_dir, os.path.basename(lora_path))
    else:
        backup_path = lora_path + ".bak"

    # Backup existing file
    try:
        shutil.move(lora_path, backup_path)
    except Exception as e:
        if os.path.exists(temp_path):
            os.remove(temp_path)
        return {"success": False, "error": f"Failed to backup original file: {e}"}

    # Move new file into place
    try:
        shutil.move(temp_path, final_path)
        return {
            "success": True,
            "original_file": lora_path,
            "updated_file": final_path,
            "backup_file": backup_path
        }
    except Exception as e:
        # Rollback backup
        if os.path.exists(backup_path):
            shutil.move(backup_path, lora_path)
        if os.path.exists(temp_path):
            os.remove(temp_path)
        return {"success": False, "error": f"Failed to move downloaded file into place: {e}. Restored backup."}


def scan_and_update_loras(
    folder: str,
    recursive: bool = True,
    backup_folder: Optional[str] = None,
    api_key: Optional[str] = None,
    allow_base_mismatch: bool = False
) -> Dict[str, Any]:
    """Scans folder, verifies with Civitai, and downloads updates."""
    if not os.path.isdir(folder):
        return {"status": "error", "message": f"Directory not found: {folder}"}

    lora_files = find_lora_files(folder, recursive=recursive)
    if not lora_files:
        return {"status": "success", "message": "No .safetensors files found in target folder", "summary": {}}

    run_backup_folder = None
    if backup_folder:
        try:
            timestamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
            run_backup_folder = os.path.join(backup_folder, f"LoraUpdater_{timestamp}")
            os.makedirs(run_backup_folder, exist_ok=True)
        except Exception as e:
            logger.warning(f"Could not create backup folder {backup_folder}: {e}. Falling back to .bak")
            run_backup_folder = None

    updated_count = 0
    up_to_date_count = 0
    not_found_count = 0
    skipped_type_count = 0
    skipped_mismatch_count = 0
    updates_log: List[Dict[str, Any]] = []

    with requests.Session() as session:
        for lora_path in lora_files:
            filename = os.path.basename(lora_path)
            local_hash = calculate_sha256(lora_path)
            if not local_hash:
                continue

            info = get_model_info_by_hash(local_hash, session=session, api_key=api_key)
            if not info:
                not_found_count += 1
                continue

            model_type = info.get("model", {}).get("type", "Unknown")
            if model_type.upper() != "LORA":
                skipped_type_count += 1
                continue

            local_version_id = info.get("id")
            model_id = info.get("modelId")
            local_base_model = info.get("baseModel", "Unknown")

            model_details = get_model_details(model_id, session=session, api_key=api_key)
            if not model_details or not model_details.get("modelVersions"):
                continue

            latest_version = model_details["modelVersions"][0]
            if local_version_id == latest_version.get("id"):
                up_to_date_count += 1
                continue

            latest_base_model = latest_version.get("baseModel", "Unknown")
            if not allow_base_mismatch and local_base_model.lower() != latest_base_model.lower():
                skipped_mismatch_count += 1
                continue

            latest_file = next(
                (f for f in latest_version.get("files", []) if f.get("name", "").lower().endswith(".safetensors")),
                None
            )
            if not latest_file:
                continue

            result = update_lora_file(
                lora_path,
                latest_file,
                session=session,
                api_key=api_key,
                run_backup_folder=run_backup_folder,
                base_model=local_base_model
            )

            if result["success"]:
                updated_count += 1
                updates_log.append({
                    "model_name": info.get("model", {}).get("name", filename),
                    "old_file": filename,
                    "new_file": latest_file.get("name"),
                    "old_version": info.get("name"),
                    "new_version": latest_version.get("name"),
                    "base_model": local_base_model,
                    "backup_path": result.get("backup_file")
                })

    return {
        "status": "success",
        "summary": {
            "total_files": len(lora_files),
            "updated": updated_count,
            "up_to_date": up_to_date_count,
            "not_found": not_found_count,
            "skipped_wrong_type": skipped_type_count,
            "skipped_base_mismatch": skipped_mismatch_count,
            "backup_directory": run_backup_folder
        },
        "updates": updates_log
    }


def handle_ping(data: Dict[str, Any]) -> Dict[str, Any]:
    api_key = data.get("api_key", "").strip()
    civitai_ok = False
    try:
        headers = get_headers(api_key)
        res = requests.get(f"{CIVITAI_BASE_URL}/models?limit=1", headers=headers, timeout=8)
        civitai_ok = res.status_code == 200
    except Exception:
        civitai_ok = False

    return {
        "status": "success",
        "plugin": "Lora Updater",
        "version": "1.0.0",
        "python_version": sys.version,
        "civitai_reachable": civitai_ok
    }


def main():
    parser = argparse.ArgumentParser(description="Lora Updater Plugin")
    parser.add_argument("--cmd", type=str, required=True, help="Command to execute")
    parser.add_argument("--data", type=str, default="{}", help="JSON payload")

    args = parser.parse_args()

    try:
        data = json.loads(args.data)
    except Exception:
        data = {}

    cmd = args.cmd
    api_key = data.get("api_key", "").strip()

    if cmd == "ping":
        result = handle_ping(data)
    elif cmd == "check_updates":
        folder = data.get("folder", "").strip()
        recursive = data.get("recursive", True)
        if not folder or not os.path.isdir(folder):
            result = {"status": "error", "message": f"Invalid folder: {folder}"}
        else:
            files = find_lora_files(folder, recursive=recursive)
            result = check_updates_for_files(files, api_key=api_key)
    elif cmd == "scan_and_update":
        folder = data.get("folder", "").strip()
        recursive = data.get("recursive", True)
        backup_folder = data.get("backup_folder", "").strip() or None
        allow_mismatch = data.get("allow_base_mismatch", False)
        result = scan_and_update_loras(
            folder,
            recursive=recursive,
            backup_folder=backup_folder,
            api_key=api_key,
            allow_base_mismatch=allow_mismatch
        )
    elif cmd == "update_single":
        file_path = data.get("file_path", "").strip()
        backup_folder = data.get("backup_folder", "").strip() or None
        if not file_path or not os.path.isfile(file_path):
            result = {"status": "error", "message": f"File not found: {file_path}"}
        else:
            folder = os.path.dirname(file_path)
            res = check_updates_for_files([file_path], api_key=api_key)
            if res.get("status") == "success" and res.get("results"):
                item = res["results"][0]
                if item.get("status") == "update_available":
                    with requests.Session() as s:
                        up_res = update_lora_file(
                            file_path,
                            {
                                "downloadUrl": item.get("download_url"),
                                "name": item.get("new_filename"),
                                "hashes": {"SHA256": item.get("expected_hash")}
                            },
                            session=s,
                            api_key=api_key,
                            run_backup_folder=backup_folder,
                            base_model=item.get("base_model", "Unknown")
                        )
                        result = {"status": "success" if up_res["success"] else "error", "details": up_res}
                else:
                    result = {"status": "info", "message": item.get("message", "No update available")}
            else:
                result = {"status": "error", "message": "Failed to check file on Civitai"}
    else:
        result = {"status": "error", "message": f"Unknown command: {cmd}"}

    print(json.dumps(result))


if __name__ == "__main__":
    main()
