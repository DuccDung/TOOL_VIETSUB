import json
import os
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")
os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")


def main() -> None:
    with open(sys.argv[1], "r", encoding="utf-8") as request_file:
        request = json.load(request_file)

    model_directory = pathlib.Path(request["modelDirectory"]).resolve(strict=True)
    texts = [str(text).strip() for text in request.get("texts", [])]
    if not texts:
        json.dump({"results": []}, sys.stdout, ensure_ascii=False)
        return

    import torch
    from transformers import MarianMTModel, MarianTokenizer

    torch.set_num_threads(max(2, min(8, os.cpu_count() or 2)))
    tokenizer = MarianTokenizer.from_pretrained(
        model_directory,
        local_files_only=True,
    )
    model = MarianMTModel.from_pretrained(
        model_directory,
        local_files_only=True,
    )
    model.eval()

    encoded = tokenizer(
        texts,
        return_tensors="pt",
        padding=True,
        truncation=True,
        max_length=384,
    )
    source_token_count = int(encoded["attention_mask"].sum(dim=1).max().item())
    max_generated_tokens = max(32, min(160, source_token_count * 4 + 16))
    with torch.inference_mode():
        sequences = model.generate(
            **encoded,
            num_beams=6,
            max_new_tokens=max_generated_tokens,
            no_repeat_ngram_size=3,
            renormalize_logits=True,
            early_stopping=True,
        )

    eos_token_id = tokenizer.eos_token_id
    pad_token_id = tokenizer.pad_token_id
    decoder_start_token_id = model.config.decoder_start_token_id
    results = []
    for sequence in sequences:
        token_ids = sequence.tolist()
        ended_with_eos = eos_token_id in token_ids
        generated_token_count = sum(
            1 for token_id in token_ids
            if token_id not in {pad_token_id, decoder_start_token_id}
        )
        results.append(
            {
                "text": tokenizer.decode(sequence, skip_special_tokens=True).strip(),
                "endedWithEos": ended_with_eos,
                "generatedTokenCount": generated_token_count,
                "maxGeneratedTokens": max_generated_tokens,
            }
        )

    json.dump({"results": results}, sys.stdout, ensure_ascii=False)


if __name__ == "__main__":
    main()
