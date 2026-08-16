import json
import os
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


def main() -> None:
    with open(sys.argv[1], "r", encoding="utf-8") as request_file:
        request = json.load(request_file)

    model_directory = pathlib.Path(request["modelDirectory"]).resolve(strict=True)
    source_model = pathlib.Path(request["sourceModelPath"]).resolve(strict=True)
    target_model = pathlib.Path(request["targetModelPath"]).resolve(strict=True)

    import ctranslate2
    import sentencepiece

    source_tokenizer = sentencepiece.SentencePieceProcessor(model_file=str(source_model))
    target_tokenizer = sentencepiece.SentencePieceProcessor(model_file=str(target_model))
    translator = ctranslate2.Translator(
        str(model_directory),
        device="cpu",
        compute_type="int8",
        inter_threads=1,
        intra_threads=max(2, min(8, os.cpu_count() or 2)),
    )

    texts = [str(text).strip() for text in request.get("texts", [])]
    token_batches = [source_tokenizer.encode(text, out_type=str) for text in texts]
    results = translator.translate_batch(
        token_batches,
        beam_size=4,
        max_decoding_length=256,
    )
    translations = []
    for result in results:
        tokens = [token for token in result.hypotheses[0] if token not in {"</s>", "<pad>"}]
        translations.append(target_tokenizer.decode(tokens).strip())

    json.dump({"translations": translations}, sys.stdout, ensure_ascii=False)


if __name__ == "__main__":
    main()
