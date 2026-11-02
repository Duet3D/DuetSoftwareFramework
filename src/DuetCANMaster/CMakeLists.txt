# DuetCANMaster, the RepRapFirmware-derived SPI-to-CAN-FD bridge for the Duet 3 main boards, built
# as part of the top-level firmware build against the shared libraries in lib/. A build tree builds
# one board, DUET_BOARD, as an executable target of the same name; Boards.cmake describes the
# boards. Its image goes to the top of the build tree: the .elf, the .bin with its CRC appended,
# and the .map.
#
# Its sources are linted by clang-tidy as they are compiled (see cmake/ClangTidy.cmake), with every
# finding an error: DuetCANMaster is on DUET_CLANG_TIDY_ERRORS.

cmake_minimum_required(VERSION 3.25)
project(DuetCANMaster LANGUAGES C CXX)

# Sources, in link order: the replacement C++ and C runtime pieces come first so that theirs are
# the definitions used, then the firmware itself.
file(GLOB_RECURSE _libcpp CONFIGURE_DEPENDS src/libcpp/*.cpp src/libcpp/*.cc)
file(GLOB_RECURSE _libc   CONFIGURE_DEPENDS src/libc/*.c src/libc/*.cpp)
file(GLOB_RECURSE _cxx    CONFIGURE_DEPENDS src/*.cpp)
file(GLOB_RECURSE _c      CONFIGURE_DEPENDS src/*.c)
list(SORT _libcpp)
list(SORT _libc)
list(FILTER _cxx EXCLUDE REGEX "/src/(libcpp|libc)/")
list(FILTER _c   EXCLUDE REGEX "/src/libc/")

duet_firmware_executable(${DUET_BOARD}
    OUTPUT_NAME ${CANMASTER_OUTPUT}
    LINKER_SCRIPT "${CMAKE_CURRENT_SOURCE_DIR}/src/${CANMASTER_LINKER_SCRIPT}"
    SOURCES ${_libcpp} ${_libc} ${_cxx} ${_c})
# Version.cpp holds the build date
duet_build_date(${DUET_BOARD} src/Version.cpp)
# The C++ runtime is libsupc++ plus the pieces in src/libcpp, so the image is linked by the C driver
# rather than g++, which would add libstdc++
set_target_properties(${DUET_BOARD} PROPERTIES LINKER_LANGUAGE C)

target_include_directories(${DUET_BOARD} PRIVATE
    src
    src/Hardware/${DUET_MCU})

target_compile_definitions(${DUET_BOARD} PRIVATE
    ${DUET_PART_DEFINE}
    ${CANMASTER_DEFINES}
    "MBEDTLS_CONFIG_FILE=\"config-same70.h\""
    $<$<COMPILE_LANGUAGE:C>:noexcept=>
    $<$<COMPILE_LANGUAGE:CXX>:_XOPEN_SOURCE>)

set(_c_options
    -Werror=implicit
    -Wwrite-strings
    -fstack-usage)
set(_cxx_options
    -fno-threadsafe-statics
    -fno-rtti
    -fexceptions
    -Wfloat-conversion
    -Wsuggest-override
    -Wnoexcept
    -Wshadow
    -Wsign-promo
    -fstack-usage)

target_compile_options(${DUET_BOARD} PRIVATE
    -fno-math-errno
    -fsingle-precision-constant
    -Wall
    -Wundef
    -Wdouble-promotion
    -Werror=return-type
    -Werror
    "$<$<COMPILE_LANGUAGE:C>:${_c_options}>"
    "$<$<COMPILE_LANGUAGE:CXX>:${_cxx_options}>")

target_link_options(${DUET_BOARD} PRIVATE
    --specs=nosys.specs
    LINKER:--check-sections
    LINKER:--no-warn-rwx-segment
    LINKER:--unresolved-symbols=report-all
    LINKER:--warn-common
    LINKER:--warn-section-align
    LINKER:--warn-unresolved-symbols)

duet_link_libraries(${DUET_BOARD} supc++ m)

duet_firmware_images(${DUET_BOARD} CRC)
