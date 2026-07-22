"""Execute PyTorch and ncnn TinyCLIP encoders on identical fixtures."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import ncnn
import numpy as np
import torch

import open_clip
from convert_tinyclip import ImageEncoder, TextEncoder, load_fused_model


TEXT_FIXTURES = (
    "a sunset at the beach",
    "a red car",
    "a scanned document",
    "a dog playing outside",
    "a plate of food",
    "a screenshot of a computer application",
)


def normalize(value: np.ndarray) -> list[float]:
    value = value.astype(np.float32).reshape(-1)
    length = np.linalg.norm(value)
    if not np.isfinite(length) or length == 0:
        raise RuntimeError("Inference returned an invalid embedding.")
    return (value / length).tolist()


def load_ncnn(param: Path, weights: Path) -> ncnn.Net:
    network = ncnn.Net()
    if network.load_param(str(param)) != 0 or network.load_model(str(weights)) != 0:
        raise RuntimeError(f"Could not load ncnn model {param}.")
    return network


def infer(network: ncnn.Net, input_name: str, output_name: str, value: np.ndarray) -> np.ndarray:
    with network.create_extractor() as extractor:
        if extractor.input(input_name, ncnn.Mat(value).clone()) != 0:
            raise RuntimeError(f"ncnn rejected input {input_name}.")
        result, output = extractor.extract(output_name)
        if result != 0:
            raise RuntimeError(f"ncnn could not extract {output_name}.")
        return np.asarray(output).copy()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("model_directory", type=Path)
    parser.add_argument("--results-directory", type=Path)
    arguments = parser.parse_args()

    model_directory = arguments.model_directory.resolve()
    results = (arguments.results_directory or model_directory / "parity").resolve()
    results.mkdir(parents=True, exist_ok=True)
    torch.manual_seed(20260722)

    model = load_fused_model(arguments.checkpoint.resolve())
    image_encoder = ImageEncoder(model.image_encoder_without_ddp).eval()
    text_encoder = TextEncoder(model.text_encoder_without_ddp).eval()
    images = torch.rand(len(TEXT_FIXTURES), 3, 224, 224) * 4.0 - 2.0
    tokens = open_clip.tokenize(list(TEXT_FIXTURES))

    image_network = load_ncnn(model_directory / "image.param", model_directory / "image.bin")
    text_network = load_ncnn(model_directory / "text.param", model_directory / "text.bin")
    reference_images: list[list[float]] = []
    candidate_images: list[list[float]] = []
    reference_text: list[list[float]] = []
    candidate_text: list[list[float]] = []

    with torch.no_grad():
        for image in images:
            reference_images.append(normalize(image_encoder(image.unsqueeze(0)).numpy()))
            candidate_images.append(normalize(infer(
                image_network, "image", "embedding", image.numpy().astype(np.float32))))

        for token_row in tokens:
            reference_text.append(normalize(
                model.text_encoder_without_ddp(token_row.unsqueeze(0)).numpy()))
            all_positions = infer(
                text_network, "tokens", "embedding", token_row.numpy().astype(np.int32))
            end_position = int(token_row.argmax().item())
            candidate_text.append(normalize(all_positions[end_position]))

    reference = {
        "image_embeddings": reference_images,
        "text_embeddings": reference_text,
    }
    candidate = {
        "image_embeddings": candidate_images,
        "text_embeddings": candidate_text,
    }
    (results / "reference.json").write_text(json.dumps(reference), encoding="utf-8")
    (results / "candidate.json").write_text(json.dumps(candidate), encoding="utf-8")
    print(f"Parity fixtures written to {results}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
