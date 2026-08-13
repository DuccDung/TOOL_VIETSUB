# TOOL_VIETSUB - Third-party notices for the local AI pipeline

This inventory records the engines and models selected for V1. It is not a
substitute for a legal review of the final installer. A release that bundles
third-party binaries or models must also bundle the corresponding full license
texts and satisfy their source/notice obligations.

## Speech recognition

- `Whisper.net` / `Whisper.net.Runtime` 1.9.1: MIT, .NET bindings and the CPU
  runtime based on `whisper.cpp`.
- `whisper.cpp`: MIT.
- OpenAI Whisper `ggml-base.bin`: model converted for `whisper.cpp`; use is
  governed by the OpenAI Whisper repository license (MIT).
- Sources: [Whisper.net](https://github.com/sandrohanea/whisper.net),
  [whisper.cpp](https://github.com/ggml-org/whisper.cpp),
  [OpenAI Whisper](https://github.com/openai/whisper).

## OCR

- `Sdcb.PaddleOCR` 3.3.1 and its local model/runtime packages: Apache-2.0.
- PaddleOCR/Paddle Inference: Apache-2.0.
- OpenCvSharp/OpenCV runtime: retain the license notices shipped by the NuGet
  packages when publishing.
- Sources: [Sdcb.PaddleOCR on NuGet](https://www.nuget.org/packages/Sdcb.PaddleOCR/3.3.1),
  [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR).

## Offline translation

- Transformers, PyTorch and SentencePiece are used by the isolated local Python
  worker for direct Chinese-to-Vietnamese translation.
- The official `Helsinki-NLP/opus-mt-zh-vi` artifact is pinned at commit
  `67ea2dbfbaf13a16772a40346d3d72b59e591443` and licensed Apache-2.0.
- Sources: [pinned OPUS-MT model](https://huggingface.co/Helsinki-NLP/opus-mt-zh-vi/tree/67ea2dbfbaf13a16772a40346d3d72b59e591443),
  [Transformers](https://github.com/huggingface/transformers),
  [PyTorch](https://github.com/pytorch/pytorch),
  [SentencePiece](https://github.com/google/sentencepiece).
- Argos Translate 1.11.0: dual licensed MIT or CC0.
- `translate-en_vi-1_9.argosmodel`: derived from the OPUS-MT English to
  Vietnamese model and marked CC BY 4.0 in the package README.
- Model authors/citation recorded by the package: Jörg Tiedemann and Santhosh
  Thottingal, “OPUS-MT — Building open translation services for the World”,
  EAMT 2020.
- Sources: [Argos Translate](https://github.com/argosopentech/argos-translate),
  [Argos model index](https://www.argosopentech.com/argospm/index/).

## Vietnamese text-to-speech

- Piper 1.6.0 (`piper-tts`): GPL-3.0. TOOL_VIETSUB starts Piper as a separate,
  isolated Python process and exchanges JSON/file paths with it; Piper is not
  linked into the .NET process.
- A distributor must still comply with GPL-3.0 for every Piper copy it conveys,
  including the complete license and corresponding-source obligations.
- Voice `vi_VN-vais1000-medium`: its model card identifies the VAIS-1000
  dataset as CC BY 4.0. Attribution: Quoc Truong Do and Chi Mai Luong,
  “VAIS-1000: A Vietnamese Speech Synthesis Corpus”, IEEE DataPort, 2018,
  DOI `10.21227/H2B887`.
- Sources: [Piper GPL repository](https://github.com/OHF-Voice/piper1-gpl),
  [voice model card](https://huggingface.co/rhasspy/piper-voices/blob/375a0fe641dea077c2a47b4e9a056d6da521eed3/vi/vi_VN/vais1000/medium/MODEL_CARD).

## Media runtime

- FFmpeg/FFprobe are discovered as external executables. Their effective
  license depends on the exact build options and included codecs. Before
  bundling an FFmpeg build in an installer, record that build's configuration
  and comply with its LGPL/GPL notices and source obligations.
- Source: [FFmpeg legal information](https://ffmpeg.org/legal.html).

## Runtime downloader

- `uv` 0.12.3 is downloaded from the pinned official GitHub release and
  verified by SHA-256 before execution. Retain the license included with the
  selected uv release when it is redistributed.
- Source: [Astral uv](https://github.com/astral-sh/uv).
