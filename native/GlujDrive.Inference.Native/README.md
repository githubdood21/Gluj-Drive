# Gluj Drive native inference runtime

This optional Windows DLL is the stable boundary between ASP.NET and ncnn. It is never loaded during server startup, scanning, thumbnail generation, or normal browsing.

## Build

Install a Vulkan-enabled ncnn build, then configure with its CMake package directory:

```powershell
cmake -S native/GlujDrive.Inference.Native -B native/build -Dncnn_DIR=C:/path/to/ncnn/lib/cmake/ncnn
cmake --build native/build --config Release
```

Copy `GlujDrive.Inference.Native.dll` and the ncnn/Vulkan runtime dependencies beside `GlujDrive.Server.exe`. The managed app checks ABI version 1 before exposing the runtime as available.

## Converted model contract

The bundled or separately downloadable ZIP must contain `manifest.json` at its root and these generated files:

- `image.param` / `image.bin`, with input `image` and output `embedding`.
- `text.param` / `text.bin`, with an int32 input named `tokens` and float output named `embedding`.
- `vocab.json` and `merges.txt` for the managed CLIP BPE tokenizer.
- `embedding-dimensions.txt`.
- `runtime/win-x64/GlujDrive.Inference.Native.dll`, preferably with ncnn linked statically.
- TinyCLIP's MIT license and attribution.

`manifest.json` declares `modelId`, `version`, `fingerprint`, `embeddingDimensions`, `imageWidth`, `imageHeight`, `contextLength`, `startTokenId`, `endTokenId`, `vocabularyFile`, `mergesFile`, and a `files` object mapping every packaged relative path to its SHA-256. The ZIP itself is also pinned by `SemanticSearch:ModelPackageSha256`.

The release conversion pipeline must compare normalized PyTorch and ncnn embeddings on fixed image/text fixtures and reject the artifact when nearest-neighbour ordering changes or numeric tolerances are exceeded. Save both runs as JSON objects with `image_embeddings` and `text_embeddings`, then run `python tools/semantic/verify_parity.py reference.json candidate.json`.

For the auto-pruned ViT-22M/32 checkpoint, create an isolated Python environment, install TinyCLIP, `pnnx`, and `ncnn`, then run:

```powershell
conversion\.venv\Scripts\python.exe tools\semantic\convert_tinyclip.py `
  conversion\TinyCLIP-auto-ViT-22M-32-Text-10M-LAION400M.pt `
  conversion\model

conversion\.venv\Scripts\python.exe tools\semantic\validate_tinyclip_conversion.py `
  conversion\TinyCLIP-auto-ViT-22M-32-Text-10M-LAION400M.pt `
  conversion\model

conversion\.venv\Scripts\python.exe tools\semantic\verify_parity.py `
  conversion\model\parity\reference.json `
  conversion\model\parity\candidate.json
```

The converter reconstructs the full ViT-B/32 architecture, fuses the learned image and text pruning masks, exports batch-first ncnn graphs, and writes the tokenizer and manifest. The native text wrapper selects the end-of-text position from the encoder's 77 output rows because a dynamic argmax/gather is not portable through ncnn's graph converter.

After parity passes, run `tools/semantic/package-ai-release.ps1 -ModelDirectory <converted-model> -RuntimeDll <compiled-dll>`. It adds the runtime to the manifest, calculates all hashes, produces the ZIP and sidecar archive hash, and places them where `dotnet publish` can bundle them.
