import contextlib
import json
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


def main() -> None:
    with open(sys.argv[1], "r", encoding="utf-8") as request_file:
        request = json.load(request_file)

    # Giữ stdout chỉ dành cho JSON protocol của desktop host.
    with contextlib.redirect_stdout(sys.stderr):
        import soundfile
        from vieneu import Vieneu

        tts = Vieneu(
            mode=request.get("mode", "v3turbo"),
            backend=request.get("backend", "onnx"),
            precision=request.get("precision", "int8"),
        )

        written = []
        if not bool(request.get("prepareOnly", False)):
            for item in request.get("items", []):
                output_path = pathlib.Path(item["outputPath"]).resolve()
                output_path.parent.mkdir(parents=True, exist_ok=True)
                waveform = tts.infer(str(item["text"]), voice=str(item["voice"]))
                soundfile.write(
                    str(output_path),
                    waveform,
                    int(tts.sample_rate),
                    subtype="PCM_16",
                )
                written.append(str(output_path))

    json.dump({"written": written}, sys.stdout, ensure_ascii=False)


if __name__ == "__main__":
    main()
