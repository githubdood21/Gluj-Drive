"""Compare reference PyTorch and converted ncnn TinyCLIP fixture outputs.

Both JSON inputs must contain float arrays named image_embeddings and
text_embeddings in the same fixture order. This script intentionally performs
no model inference; release automation first records outputs from each runtime,
then uses this deterministic gate before packaging.
"""

from __future__ import annotations

import argparse
import json
import math
import sys


def normalized(values: list[list[float]]) -> list[list[float]]:
    result: list[list[float]] = []
    for vector in values:
        length = math.sqrt(sum(value * value for value in vector))
        if length == 0:
            raise ValueError("an embedding fixture contains a zero-length vector")
        result.append([value / length for value in vector])
    return result


def dot(left: list[float], right: list[float]) -> float:
    return sum(a * b for a, b in zip(left, right))


def nearest(values: list[list[float]], row: int, k: int) -> list[int]:
    scores = [(dot(values[row], candidate), index) for index, candidate in enumerate(values) if index != row]
    scores.sort(reverse=True)
    return [index for _, index in scores[:k]]


def top_k_overlap(left: list[list[float]], right: list[list[float]], k: int) -> float:
    overlaps = [
        len(set(nearest(left, row, k)).intersection(nearest(right, row, k))) / k
        for row in range(len(left))
    ]
    return sum(overlaps) / len(overlaps)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("reference", help="JSON produced by the official PyTorch checkpoint")
    parser.add_argument("candidate", help="JSON produced by the converted ncnn runtime")
    parser.add_argument("--max-absolute-error", type=float, default=0.02)
    parser.add_argument("--minimum-top-k-overlap", type=float, default=0.90)
    parser.add_argument("--top-k", type=int, default=5)
    args = parser.parse_args()

    with open(args.reference, encoding="utf-8") as reference_file:
        reference = json.load(reference_file)
    with open(args.candidate, encoding="utf-8") as candidate_file:
        candidate = json.load(candidate_file)
    failures: list[str] = []

    for name in ("image_embeddings", "text_embeddings"):
        expected = normalized(reference[name])
        actual = normalized(candidate[name])
        expected_shape = (len(expected), len(expected[0]) if expected else 0)
        actual_shape = (len(actual), len(actual[0]) if actual else 0)
        if expected_shape != actual_shape:
            failures.append(f"{name}: shape {actual_shape} does not match {expected_shape}")
            continue
        if len(expected) < 2 or expected_shape[1] == 0:
            failures.append(f"{name}: at least two non-empty fixture vectors are required")
            continue
        error = max(abs(a - b) for left, right in zip(expected, actual) for a, b in zip(left, right))
        overlap = top_k_overlap(expected, actual, min(args.top_k, len(expected) - 1))
        print(f"{name}: max_abs_error={error:.6f}, top_k_overlap={overlap:.3f}")
        if error > args.max_absolute_error:
            failures.append(f"{name}: maximum absolute error exceeded")
        if overlap < args.minimum_top_k_overlap:
            failures.append(f"{name}: nearest-neighbour overlap was too low")

    if failures:
        for failure in failures:
            print(f"FAILED: {failure}", file=sys.stderr)
        return 1

    print("Parity verification passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
