import json
import os
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


def main() -> None:
    with open(sys.argv[1], "r", encoding="utf-8") as request_file:
        request = json.load(request_file)
    package_path = pathlib.Path(request["packagePath"]).resolve(strict=True)
    package_dir = pathlib.Path(request["packageDirectory"]).resolve()
    package_dir.mkdir(parents=True, exist_ok=True)
    os.environ["ARGOS_PACKAGES_DIR"] = str(package_dir)
    os.environ["ARGOS_DEVICE_TYPE"] = "cpu"
    # Force MiniSBD so translation remains offline even when an older package
    # contains Stanza metadata without the Stanza tokenizer files.
    os.environ["ARGOS_CHUNK_TYPE"] = "MINISBD"
    os.environ["ARGOS_INTER_THREADS"] = "1"
    os.environ["ARGOS_INTRA_THREADS"] = str(max(2, min(8, os.cpu_count() or 2)))

    import argostranslate.package
    import argostranslate.translate

    marker = package_dir / ".en-vi-1.9-installed"
    if not marker.exists():
        argostranslate.package.install_from_path(package_path)
        marker.write_text("installed", encoding="utf-8")

    source = request.get("sourceLanguage", "en")
    target = request.get("targetLanguage", "vi")
    translations = [
        argostranslate.translate.translate(str(text), source, target)
        for text in request.get("texts", [])
    ]
    json.dump({"translations": translations}, sys.stdout, ensure_ascii=False)


if __name__ == "__main__":
    main()
