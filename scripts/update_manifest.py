#!/usr/bin/env python3
"""Add or replace the current version in manifest.json.

Usage: update_manifest.py <zip> <download-url>
Reads plugin metadata from build.yaml (simple "key: value" and folded ">" fields).
"""
import datetime
import hashlib
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent


def read_build_yaml():
    meta, key, folded = {}, None, []
    for line in (ROOT / "build.yaml").read_text().splitlines():
        if line.startswith((" ", "\t", "-")) and key:
            if line.strip().startswith("- "):
                continue
            folded.append(line.strip())
            meta[key] = " ".join(folded).strip()
            continue
        if ":" in line:
            key, value = line.split(":", 1)
            key, value = key.strip(), value.strip()
            folded = []
            meta[key] = "" if value in (">", "|") else value.strip('"')
    return meta


def main():
    zip_path, url = pathlib.Path(sys.argv[1]), sys.argv[2]
    meta = read_build_yaml()
    manifest_path = ROOT / "manifest.json"
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else []
    plugin = next((p for p in manifest if p["guid"] == meta["guid"]), None)
    if plugin is None:
        plugin = {"guid": meta["guid"], "versions": []}
        manifest.append(plugin)
    plugin.update(
        name=meta["name"],
        description=meta["description"],
        overview=meta["overview"],
        owner=meta["owner"],
        category=meta["category"],
        imageUrl="",
    )
    entry = {
        "version": meta["version"],
        "changelog": meta.get("changelog", ""),
        "targetAbi": meta["targetAbi"],
        "sourceUrl": url,
        "checksum": hashlib.md5(zip_path.read_bytes()).hexdigest(),
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    plugin["versions"] = [entry] + [v for v in plugin["versions"] if v["version"] != meta["version"]]
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps(entry, indent=2))


if __name__ == "__main__":
    main()
