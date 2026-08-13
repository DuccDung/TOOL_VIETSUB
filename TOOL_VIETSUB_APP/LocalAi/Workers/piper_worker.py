import json
import pathlib
import sys
import wave

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


def main() -> None:
    with open(sys.argv[1], "r", encoding="utf-8") as request_file:
        request = json.load(request_file)
    from piper import PiperVoice, SynthesisConfig

    model_path = pathlib.Path(request["modelPath"]).resolve(strict=True)
    config_path = pathlib.Path(request["configPath"]).resolve(strict=True)
    voice = PiperVoice.load(str(model_path), config_path=str(config_path), use_cuda=False)
    synthesis_config = SynthesisConfig(
        volume=float(request.get("volume", 1.0)),
        length_scale=float(request.get("lengthScale", 1.0)),
        normalize_audio=True,
    )
    written = []
    for item in request.get("items", []):
        output_path = pathlib.Path(item["outputPath"]).resolve()
        output_path.parent.mkdir(parents=True, exist_ok=True)
        with wave.open(str(output_path), "wb") as wave_file:
            voice.synthesize_wav(str(item["text"]), wave_file, syn_config=synthesis_config)
        written.append(str(output_path))

    json.dump({"written": written}, sys.stdout, ensure_ascii=False)


if __name__ == "__main__":
    main()
