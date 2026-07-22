"""Convert the TinyCLIP auto-pruned checkpoint used by Gluj Drive to ncnn.

Run this from the repository root with the conversion-only virtual environment:

    conversion/.venv/Scripts/python.exe tools/semantic/convert_tinyclip.py \
        conversion/TinyCLIP-auto-ViT-22M-32-Text-10M-LAION400M.pt \
        conversion/model

Microsoft's TinyCLIP source must be importable as ``open_clip`` and the pnnx
package must be installed. The repository's conversion directory is ignored by
Git so checkpoint weights and generated artifacts are never committed by
accident.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
from pathlib import Path
from types import SimpleNamespace

import pnnx
import torch
import torch.nn.functional as functional
from torch import nn

import open_clip
from open_clip.model import load_pruned_model
from open_clip.tokenizer import _tokenizer


MODEL_ID = "TinyCLIP-auto-ViT-22M-32-Text-10M-LAION400M"
ARCHITECTURE = "ViT-B-32"


class BatchlessAttention(nn.Module):
    """Pruned batch-first attention that never moves pnnx's batch axis."""

    def __init__(self, attention: nn.MultiheadAttention) -> None:
        super().__init__()
        self.in_proj_weight = nn.Parameter(attention.in_proj_weight.detach().clone())
        self.in_proj_bias = nn.Parameter(attention.in_proj_bias.detach().clone())
        self.out_proj = attention.out_proj
        self.heads = attention.num_heads
        self.head_dimension = attention.head_dim

    def forward(
        self,
        value: torch.Tensor,
        attention_mask: torch.Tensor | None = None,
    ) -> torch.Tensor:
        query, key, projected_value = functional.linear(
            value, self.in_proj_weight, self.in_proj_bias).chunk(3, dim=-1)
        batch = query.shape[0]
        sequence = query.shape[1]
        query = query.reshape(batch, sequence, self.heads, self.head_dimension).permute(0, 2, 1, 3)
        key = key.reshape(batch, sequence, self.heads, self.head_dimension).permute(0, 2, 1, 3)
        projected_value = projected_value.reshape(
            batch, sequence, self.heads, self.head_dimension).permute(0, 2, 1, 3)
        similarity = torch.matmul(query * (self.head_dimension ** -0.5), key.transpose(2, 3))
        if attention_mask is not None:
            similarity = similarity + attention_mask
        probabilities = torch.softmax(similarity, dim=-1)
        result = torch.matmul(probabilities, projected_value)
        result = result.permute(0, 2, 1, 3).reshape(
            batch, sequence, self.heads * self.head_dimension)
        return self.out_proj(result)


class BatchlessBlock(nn.Module):
    def __init__(self, block: nn.Module) -> None:
        super().__init__()
        self.norm_attention = block.ln_1
        self.attention = BatchlessAttention(block.attn) if block.ln_1 is not None else None
        self.norm_mlp = block.ln_2
        self.mlp_input = block.mlp.c_fc if block.mlp is not None else None
        self.mlp_activation = block.mlp.gelu if block.mlp is not None else None
        self.mlp_output = block.mlp.c_proj if block.mlp is not None else None

    def forward(
        self,
        value: torch.Tensor,
        attention_mask: torch.Tensor | None = None,
    ) -> torch.Tensor:
        if self.attention is not None:
            value = value + self.attention(self.norm_attention(value), attention_mask)
        if self.mlp_input is not None:
            residual = self.norm_mlp(value)
            residual = self.mlp_input(residual)
            residual = self.mlp_activation(residual)
            value = value + self.mlp_output(residual)
        return value


class ImageEncoder(nn.Module):
    def __init__(self, encoder: nn.Module) -> None:
        super().__init__()
        visual = encoder.visual
        self.convolution = visual.conv1
        self.class_embedding = nn.Parameter(visual.class_embedding.detach().clone())
        self.positional_embedding = nn.Parameter(visual.positional_embedding.detach().clone())
        self.norm_before = visual.ln_pre
        self.blocks = nn.ModuleList(BatchlessBlock(block) for block in visual.transformer.resblocks)
        self.norm_after = visual.ln_post
        self.projection = nn.Parameter(visual.proj.detach().clone())

    def forward(self, image: torch.Tensor) -> torch.Tensor:
        value = self.convolution(image)
        value = value.flatten(2).transpose(1, 2)
        class_token = self.class_embedding.reshape(1, 1, -1).expand(value.shape[0], 1, -1)
        value = torch.cat((class_token, value), dim=1)
        value = self.norm_before(value + self.positional_embedding)
        for block in self.blocks:
            value = block(value)
        value = self.norm_after(value[:, 0, :])
        return torch.matmul(value, self.projection)


class TextEncoder(nn.Module):
    def __init__(self, encoder: nn.Module) -> None:
        super().__init__()
        self.token_embedding = encoder.token_embedding
        self.positional_embedding = nn.Parameter(encoder.positional_embedding.detach().clone())
        self.blocks = nn.ModuleList(BatchlessBlock(block) for block in encoder.transformer.resblocks)
        self.norm = encoder.ln_final
        self.projection = nn.Parameter(encoder.text_projection.detach().clone())
        self.register_buffer("attention_mask", encoder.attn_mask.detach().clone(), persistent=False)

    def forward(self, tokens: torch.Tensor) -> torch.Tensor:
        value = self.token_embedding(tokens) + self.positional_embedding
        for block in self.blocks:
            value = block(value, self.attention_mask)
        # Return all positions. The native wrapper selects the EOT token before
        # normalization, avoiding a dynamic argmax/gather unsupported by ncnn.
        return torch.matmul(self.norm(value), self.projection)


class ExportLayerNorm(nn.LayerNorm):
    def forward(
        self,
        value: torch.Tensor,
        hidden_z: torch.Tensor | None = None,
    ) -> torch.Tensor:
        if hidden_z is not None:
            raise RuntimeError("Pruning masks must be fused before export.")
        return super().forward(value)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def load_fused_model(checkpoint_path: Path) -> nn.Module:
    args = SimpleNamespace(
        prune_image=True,
        prune_text=True,
        sparsity_warmup=1000,
        start_sparsity=0.0,
        target_sparsity=0.25,
    )
    model = open_clip.create_model(ARCHITECTURE, device=torch.device("cpu"), args=args)
    checkpoint = torch.load(checkpoint_path, map_location="cpu", weights_only=False)
    state = checkpoint["state_dict"] if "state_dict" in checkpoint else checkpoint
    state = {name.replace(".module", ""): value for name, value in state.items()}
    load_pruned_model(model, state)
    model.eval()

    # Evaluation-mode L0 masks are deterministic. A forward pass records the
    # masks on each tower, after which prune() physically removes unused heads,
    # channels, MLP dimensions, and blocks.
    # Use no_grad rather than inference_mode because prune() creates the new
    # Parameters that TorchScript subsequently traces.
    with torch.no_grad():
        model.image_encoder_without_ddp(torch.zeros(1, 3, 224, 224))
        model.text_encoder_without_ddp(torch.zeros(1, 77, dtype=torch.long))
        model.image_encoder_without_ddp = model.image_encoder_without_ddp.prune()
        # Upstream CLIPBase accidentally redeclares this property without its
        # setter, so update its registered module and non-DDP reference directly.
        model._text_encoder = model.text_encoder_without_ddp.prune()
        model._without_ddp[1] = model._text_encoder
        model.image_encoder_without_ddp.l0_module = None
        model.text_encoder_without_ddp.l0_module = None

    replace_custom_layer_norms(model)
    return model.eval()


def replace_custom_layer_norms(module: nn.Module) -> None:
    """Replace TinyCLIP's dtype-restoring LayerNorm with an exportable equivalent."""
    for name, child in list(module.named_children()):
        if child.__class__.__module__ == "open_clip.model" and child.__class__.__name__ == "LayerNorm":
            replacement = ExportLayerNorm(
                child.normalized_shape,
                eps=child.eps,
                elementwise_affine=child.elementwise_affine,
            )
            with torch.no_grad():
                if child.elementwise_affine:
                    replacement.weight.copy_(child.weight)
                    replacement.bias.copy_(child.bias)
            setattr(module, name, replacement)
        else:
            replace_custom_layer_norms(child)


def rename_ncnn_blobs(param_path: Path, input_name: str, output_name: str) -> None:
    lines = param_path.read_text(encoding="utf-8").splitlines()
    if len(lines) < 3:
        raise RuntimeError(f"pnnx produced an invalid parameter file: {param_path}")

    layer_lines = [line.split() for line in lines[2:] if line.strip()]
    produced: list[str] = []
    consumed: set[str] = set()
    for parts in layer_lines:
        bottom_count = int(parts[2])
        top_count = int(parts[3])
        blobs = parts[4:4 + bottom_count + top_count]
        consumed.update(blobs[:bottom_count])
        produced.extend(blobs[bottom_count:])

    graph_inputs = [blob for blob in produced if blob not in consumed]
    graph_outputs = [blob for blob in produced if blob not in consumed]
    # Input layer outputs are also consumed, so obtain the first blob directly.
    first_parts = layer_lines[0]
    first_bottoms = int(first_parts[2])
    first_tops = int(first_parts[3])
    input_blobs = first_parts[4 + first_bottoms:4 + first_bottoms + first_tops]
    output_blobs = graph_outputs
    if len(input_blobs) != 1 or len(output_blobs) != 1:
        raise RuntimeError(
            f"Expected one model input and output, found {input_blobs} and {output_blobs}.")

    replacements = {input_blobs[0]: input_name, output_blobs[0]: output_name}
    rewritten = []
    for line in lines:
        parts = line.split()
        rewritten.append(" ".join(replacements.get(part, part) for part in parts))
    param_path.write_text("\n".join(rewritten) + "\n", encoding="utf-8")


def export_encoder(
    module: nn.Module,
    sample: torch.Tensor,
    output_directory: Path,
    name: str,
    input_name: str,
) -> None:
    work_path = output_directory / f"{name}.pt"
    ncnn_param = output_directory / f"{name}.ncnn.param"
    ncnn_bin = output_directory / f"{name}.ncnn.bin"
    # pnnx's generated Python verification file does not escape absolute
    # Windows paths. Convert from inside the output directory using basenames.
    previous_directory = Path.cwd()
    try:
        os.chdir(output_directory)
        pnnx.export(
            module.eval(),
            work_path.name,
            (sample,),
            ncnnparam=ncnn_param.name,
            ncnnbin=ncnn_bin.name,
            fp16=True,
            check_trace=True,
        )
    finally:
        os.chdir(previous_directory)
    rename_ncnn_blobs(ncnn_param, input_name, "embedding")
    shutil.move(ncnn_param, output_directory / f"{name}.param")
    shutil.move(ncnn_bin, output_directory / f"{name}.bin")


def write_tokenizer(output_directory: Path) -> tuple[int, int]:
    vocabulary_path = output_directory / "vocab.json"
    vocabulary_path.write_text(
        json.dumps(_tokenizer.encoder, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    merges = sorted(_tokenizer.bpe_ranks.items(), key=lambda item: item[1])
    (output_directory / "merges.txt").write_text(
        "#version: 0.2\n" + "\n".join(f"{left} {right}" for (left, right), _ in merges) + "\n",
        encoding="utf-8",
    )
    return (
        _tokenizer.encoder["<start_of_text>"],
        _tokenizer.encoder["<end_of_text>"],
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--tinyclip-license", type=Path)
    arguments = parser.parse_args()

    checkpoint = arguments.checkpoint.resolve()
    output = arguments.output.resolve()
    if not checkpoint.is_file():
        parser.error(f"checkpoint does not exist: {checkpoint}")
    output.mkdir(parents=True, exist_ok=True)

    print("Loading and fusing TinyCLIP pruning masks...")
    model = load_fused_model(checkpoint)
    image = ImageEncoder(model.image_encoder_without_ddp).eval()
    text = TextEncoder(model.text_encoder_without_ddp).eval()

    with torch.inference_mode():
        image_dimensions = image(torch.zeros(1, 3, 224, 224)).shape[-1]
        text_dimensions = text(torch.zeros(1, 77, dtype=torch.long)).shape[-1]
    if image_dimensions != text_dimensions:
        raise RuntimeError(
            f"Image and text embedding sizes differ: {image_dimensions} != {text_dimensions}")

    print("Exporting image encoder through pnnx...")
    export_encoder(image, torch.zeros(1, 3, 224, 224), output, "image", "image")
    print("Exporting text encoder through pnnx...")
    export_encoder(text, torch.zeros(1, 77, dtype=torch.long), output, "text", "tokens")

    start_token, end_token = write_tokenizer(output)
    (output / "embedding-dimensions.txt").write_text(str(image_dimensions), encoding="ascii")
    license_path = arguments.tinyclip_license
    if license_path is None:
        license_path = Path(open_clip.__file__).resolve().parents[2] / "LICENSE"
    if license_path.is_file():
        shutil.copy2(license_path, output / "TINYCLIP-LICENSE.txt")

    fingerprint = sha256(checkpoint)
    manifest = {
        "modelId": MODEL_ID,
        "version": "1",
        "fingerprint": f"sha256:{fingerprint}",
        "embeddingDimensions": image_dimensions,
        "imageWidth": 224,
        "imageHeight": 224,
        "contextLength": 77,
        "startTokenId": start_token,
        "endTokenId": end_token,
        "vocabularyFile": "vocab.json",
        "mergesFile": "merges.txt",
        "files": {},
    }
    (output / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"Converted model written to {output}")
    print("The release packager will populate manifest file hashes after adding the runtime DLL.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
