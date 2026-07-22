#pragma once

#if defined(_WIN32)
#define GD_API extern "C" __declspec(dllexport)
#else
#define GD_API extern "C"
#endif

// Stable ABI consumed by NativeTinyClipInference.cs. All functions return zero
// on success. Error text is thread-local and remains valid until the next call.
GD_API int gd_api_version();
GD_API int gd_vulkan_device_count();
GD_API int gd_vulkan_device_name(int index, char* buffer, int capacity);
GD_API int gd_create(const char* model_path_utf8, int device_index, void** context);
GD_API void gd_destroy(void* context);
GD_API int gd_embedding_dimensions(void* context);
GD_API int gd_embed_image_rgb(
    void* context,
    const unsigned char* pixels,
    int width,
    int height,
    int stride,
    float* output,
    int dimensions);
GD_API int gd_embed_text_tokens(
    void* context,
    const int* tokens,
    int token_count,
    float* output,
    int dimensions);
GD_API const char* gd_last_error();
