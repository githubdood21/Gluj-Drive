#include "glujdrive_inference.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include <net.h>
#include <gpu.h>

namespace {
thread_local std::string last_error;
std::once_flag gpu_initialization;

struct context_state {
    ncnn::Net image;
    ncnn::Net text;
    int dimensions = 0;
    int device_index = -1;
};

int fail(std::string message) {
    last_error = std::move(message);
    return -1;
}

std::filesystem::path model_file(const char* root, const char* name) {
    return std::filesystem::u8path(root) / name;
}

bool normalize(float* values, int count) {
    double length_squared = 0.0;
    for (int index = 0; index < count; ++index) {
        length_squared += static_cast<double>(values[index]) * values[index];
    }
    const auto length = std::sqrt(length_squared);
    if (length <= 0.0) return false;
    for (int index = 0; index < count; ++index) {
        values[index] = static_cast<float>(values[index] / length);
    }
    return true;
}

int run_text(context_state& state, const int* tokens, int token_count, float* output, int dimensions) {
    auto extractor = state.text.create_extractor();
    ncnn::Mat token_tensor(token_count, static_cast<size_t>(4u), 1);
    if (token_tensor.empty()) return fail("Could not allocate the text input tensor.");
    std::memcpy(token_tensor.data, tokens, static_cast<size_t>(token_count) * sizeof(int));
    if (extractor.input("tokens", token_tensor) != 0) return fail("The text model has no 'tokens' input.");
    ncnn::Mat embedding;
    if (extractor.extract("embedding", embedding) != 0) return fail("Text inference failed.");
    if (embedding.total() != static_cast<size_t>(dimensions)) return fail("Text embedding dimensions do not match the manifest.");
    std::memcpy(output, embedding.data, static_cast<size_t>(dimensions) * sizeof(float));
    return normalize(output, dimensions) ? 0 : fail("Text inference returned an empty vector.");
}
} // namespace

int gd_api_version() { return 1; }

int gd_vulkan_device_count() {
    std::call_once(gpu_initialization, [] { ncnn::create_gpu_instance(); });
    return ncnn::get_gpu_count();
}

int gd_vulkan_device_name(int index, char* buffer, int capacity) {
    if (!buffer || capacity <= 0 || index < 0 || index >= gd_vulkan_device_count()) {
        return fail("The Vulkan device index is invalid.");
    }
    const std::string name = "Vulkan GPU " + std::to_string(index + 1);
    const auto count = std::min(static_cast<int>(name.size()), capacity - 1);
    std::memcpy(buffer, name.data(), static_cast<size_t>(count));
    buffer[count] = '\0';
    return 0;
}

int gd_create(const char* model_path_utf8, int device_index, void** output_context) {
    if (!model_path_utf8 || !output_context) return fail("A model path and output context are required.");
    *output_context = nullptr;
    try {
        auto state = std::make_unique<context_state>();
        state->device_index = device_index;

        if (device_index >= 0) {
            if (device_index >= gd_vulkan_device_count()) return fail("The requested Vulkan device is unavailable.");
            state->image.opt.use_vulkan_compute = true;
            state->text.opt.use_vulkan_compute = true;
            state->image.set_vulkan_device(device_index);
            state->text.set_vulkan_device(device_index);
        }

        const auto dimensions_path = model_file(model_path_utf8, "embedding-dimensions.txt");
        std::ifstream dimensions_stream(dimensions_path);
        dimensions_stream >> state->dimensions;
        if (state->dimensions <= 0) return fail("embedding-dimensions.txt is missing or invalid.");

        const auto image_param = model_file(model_path_utf8, "image.param").string();
        const auto image_bin = model_file(model_path_utf8, "image.bin").string();
        const auto text_param = model_file(model_path_utf8, "text.param").string();
        const auto text_bin = model_file(model_path_utf8, "text.bin").string();
        if (state->image.load_param(image_param.c_str()) != 0 || state->image.load_model(image_bin.c_str()) != 0) {
            return fail("Could not load the TinyCLIP image encoder.");
        }
        if (state->text.load_param(text_param.c_str()) != 0 || state->text.load_model(text_bin.c_str()) != 0) {
            return fail("Could not load the TinyCLIP text encoder.");
        }

        *output_context = state.release();
        last_error.clear();
        return 0;
    } catch (const std::exception& error) {
        return fail(error.what());
    }
}

void gd_destroy(void* context) { delete static_cast<context_state*>(context); }

int gd_embedding_dimensions(void* context) {
    const auto* state = static_cast<context_state*>(context);
    return state ? state->dimensions : 0;
}

int gd_embed_image_rgb(void* context, const unsigned char* pixels, int width, int height, int stride, float* output, int dimensions) {
    auto* state = static_cast<context_state*>(context);
    if (!state || !pixels || !output || width <= 0 || height <= 0 || stride < width * 3 || dimensions != state->dimensions) {
        return fail("The image inference arguments are invalid.");
    }

    ncnn::Mat image = ncnn::Mat::from_pixels(pixels, ncnn::Mat::PIXEL_RGB, width, height, stride);
    const float mean[3] = {0.48145466f * 255.f, 0.4578275f * 255.f, 0.40821073f * 255.f};
    const float norm[3] = {1.f / (0.26862954f * 255.f), 1.f / (0.26130258f * 255.f), 1.f / (0.27577711f * 255.f)};
    image.substract_mean_normalize(mean, norm);
    auto extractor = state->image.create_extractor();
    if (extractor.input("image", image) != 0) return fail("The image model has no 'image' input.");
    ncnn::Mat embedding;
    if (extractor.extract("embedding", embedding) != 0) return fail("Image inference failed.");
    if (embedding.total() != static_cast<size_t>(dimensions)) return fail("Image embedding dimensions do not match the manifest.");
    std::memcpy(output, embedding.data, static_cast<size_t>(dimensions) * sizeof(float));
    return normalize(output, dimensions) ? 0 : fail("Image inference returned an empty vector.");
}

int gd_embed_text_tokens(void* context, const int* tokens, int token_count, float* output, int dimensions) {
    auto* state = static_cast<context_state*>(context);
    if (!state || !tokens || token_count <= 0 || !output || dimensions != state->dimensions) return fail("The text inference arguments are invalid.");
    return run_text(*state, tokens, token_count, output, dimensions);
}

const char* gd_last_error() { return last_error.c_str(); }
