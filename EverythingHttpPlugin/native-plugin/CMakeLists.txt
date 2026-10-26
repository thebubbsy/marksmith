cmake_minimum_required(VERSION 3.15)
project(omnisight_http64 C)

set(CMAKE_C_STANDARD 11)

add_library(omnisight_http64 SHARED
    omnisight_plugin.c
    omnisight_plugin.def
)

target_link_libraries(omnisight_http64 PRIVATE
    kernel32
    user32
    ws2_32
)

set_target_properties(omnisight_http64 PROPERTIES
    PREFIX ""
    OUTPUT_NAME "omnisight_http64"
)
