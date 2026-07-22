cmake_minimum_required(VERSION 3.24)
project(GlujDriveInferenceNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS OFF)

find_package(ncnn CONFIG REQUIRED)

add_library(GlujDrive.Inference.Native SHARED
    src/glujdrive_inference.cpp
    include/glujdrive_inference.h)

target_include_directories(GlujDrive.Inference.Native PRIVATE include)
target_link_libraries(GlujDrive.Inference.Native PRIVATE ncnn)
target_compile_definitions(GlujDrive.Inference.Native PRIVATE GD_NCNN_RUNTIME=1)

if(MSVC)
    target_compile_options(GlujDrive.Inference.Native PRIVATE /W4 /permissive-)
else()
    target_compile_options(GlujDrive.Inference.Native PRIVATE -Wall -Wextra -Wpedantic)
endif()
