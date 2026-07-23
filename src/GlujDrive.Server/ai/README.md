# Bundled semantic-search installer

Release builds can place these two generated files in this directory:

- `TinyCLIP-ncnn-win-x64.zip`
- `TinyCLIP-ncnn-win-x64.zip.sha256`

They are copied into published server builds. The **Install semantic search** button, available on the host PC, starts a background task that verifies and installs the package without administrator access. Source checkouts intentionally do not contain the large generated model archive.

Use `tools/semantic/package-ai-release.ps1` to build both files from a converted model directory and compiled native runtime.
