# Third-party notices

Team code is MIT (see `LICENSE`). The following third-party components are bundled and keep their own licences.

## sherpa-onnx (v2.0.1)
- Files: `Assets/Plugins/Android/sherpa-onnx/sherpa-onnx-jvm-2.0.1.jar`,
  `Assets/Plugins/Android/sherpa-onnx/libs/arm64-v8a/libsherpa-onnx-jni.so` (static-link onnxruntime build)
- Source: https://github.com/k2-fsa/sherpa-onnx (release v2.0.1)
- Copyright Xiaomi Corporation and the k2-fsa contributors.
- Licence: Apache License 2.0, full text in `Assets/Plugins/Android/sherpa-onnx/LICENSE.txt`.
- The `.so` statically links ONNX Runtime (Copyright Microsoft Corporation, MIT licence,
  https://github.com/microsoft/onnxruntime/blob/main/LICENSE).

## Keyword-spotting model: sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01
- Files: `Assets/StreamingAssets/kws/*.onnx`, `Assets/StreamingAssets/kws/tokens.txt` (int8 encoder/decoder/joiner, unmodified)
- Source: https://github.com/k2-fsa/sherpa-onnx/releases/tag/kws-models
  (also https://www.modelscope.cn/pkufool/sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01), trained with icefall on GigaSpeech.
- Licence: Apache License 2.0 (as stated in the model's README), same text as above.
- `Assets/StreamingAssets/kws/keywords.txt` is ours (MIT).
