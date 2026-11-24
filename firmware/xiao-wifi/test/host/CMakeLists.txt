# Unit tests for components/mouthtrap_stream, built for the linux target and run on the build machine:
#   idf.py -C firmware/xiao-wifi/test/host -B <build folder> build && <build folder>/mouthtrap_stream_test.elf
cmake_minimum_required(VERSION 3.16)
set(EXTRA_COMPONENT_DIRS "${CMAKE_CURRENT_LIST_DIR}/../../components")
set(COMPONENTS main)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(mouthtrap_stream_test)
