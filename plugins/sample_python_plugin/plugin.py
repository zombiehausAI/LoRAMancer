"""
LoRAMancer Sample Python Plugin
Executes inside dedicated isolated .venv inside this plugin's folder.
"""
import argparse
import json
import sys


def handle_ping(data: dict) -> dict:
    return {
        "status": "success",
        "message": "Pong from Python plugin running in isolated .venv!",
        "python_version": sys.version,
        "received_data": data,
    }


def handle_sanitize_captions(data: dict) -> dict:
    trigger_word = data.get("trigger_word", "").strip()
    raw_captions = data.get("captions", [])
    sanitized = []
    for cap in raw_captions:
        cleaned = ", ".join([tag.strip() for tag in cap.split(",") if tag.strip()])
        if trigger_word and not cleaned.startswith(trigger_word):
            cleaned = f"{trigger_word}, {cleaned}"
        sanitized.append(cleaned)
    return {
        "status": "success",
        "sanitized_captions": sanitized,
    }


def main():
    parser = argparse.ArgumentParser(description="LoRAMancer Python Plugin")
    parser.add_argument("--cmd", type=str, required=True, help="Command to execute")
    parser.add_argument("--data", type=str, default="{}", help="JSON payload")

    args = parser.parse_args()

    try:
        data = json.loads(args.data)
    except Exception:
        data = {}

    if args.cmd == "ping":
        result = handle_ping(data)
    elif args.cmd == "sanitize_captions":
        result = handle_sanitize_captions(data)
    else:
        result = {"status": "error", "message": f"Unknown command: {args.cmd}"}

    print(json.dumps(result))


if __name__ == "__main__":
    main()
