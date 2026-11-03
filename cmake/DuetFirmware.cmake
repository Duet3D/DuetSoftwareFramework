# Helpers shared by the firmware projects.

include_guard(GLOBAL)

# Run by duet_firmware_images to write an image's disassembly
set(_duet_disassemble_script "${CMAKE_CURRENT_LIST_DIR}/../scripts/internal/disassemble.py")
if(DUET_DISASSEMBLY)
    find_package(Python3 REQUIRED COMPONENTS Interpreter)
endif()

# Searched by duet_firmware_images for the SVD file describing the MCU's peripherals
set(_duet_svd_dir "${CMAKE_CURRENT_LIST_DIR}/../lib/CoreN2G/src")

# CrcAppender stamps a CRC on a firmware image (and can append a filesystem image to it). It is a
# prebuilt host tool kept in scripts/internal, taken from PATH if it is there.
string(TOLOWER "${CMAKE_HOST_SYSTEM_NAME}" _duet_host_os)
string(REPLACE "darwin" "macos" _duet_host_os "${_duet_host_os}")
find_program(CRC_APPENDER CrcAppender
    PATHS "${CMAKE_CURRENT_LIST_DIR}/../scripts/internal/CrcAppender/${_duet_host_os}-${CMAKE_HOST_SYSTEM_PROCESSOR}"
    REQUIRED)

# duet_compiled_targets(<directory> <out-var>)
#
# The targets defined in <directory> and below that compile sources: executables and static and
# object libraries.
function(duet_compiled_targets DIRECTORY OUT)
    set(_compiled)
    get_directory_property(_targets DIRECTORY "${DIRECTORY}" BUILDSYSTEM_TARGETS)
    foreach(_target IN LISTS _targets)
        get_target_property(_type ${_target} TYPE)
        if(_type MATCHES "^(EXECUTABLE|STATIC_LIBRARY|OBJECT_LIBRARY)$")
            list(APPEND _compiled ${_target})
        endif()
    endforeach()
    get_directory_property(_subdirectories DIRECTORY "${DIRECTORY}" SUBDIRECTORIES)
    foreach(_subdirectory IN LISTS _subdirectories)
        duet_compiled_targets("${_subdirectory}" _below)
        list(APPEND _compiled ${_below})
    endforeach()
    set(${OUT} "${_compiled}" PARENT_SCOPE)
endfunction()

# duet_relative_prefix_maps(<directory>)
#
# ccache's base_dir hands the compiler each source under it as a path relative to the build tree
# (../../lib/CoreN2G/src/Cache.cpp), which the -fmacro-prefix-map=<absolute directory>/= options that
# make __FILE__ relative to a project's root no longer match. This gives every target in <directory>
# and below such a map for the relative path too, so __FILE__ is the same with ccache as without.
function(duet_relative_prefix_maps DIRECTORY)
    duet_compiled_targets("${DIRECTORY}" _targets)
    foreach(_target IN LISTS _targets)
        get_target_property(_options ${_target} COMPILE_OPTIONS)
        foreach(_option IN LISTS _options)
            if(_option MATCHES "^-fmacro-prefix-map=(/.+)/=$")
                # Ninja compiles in the top of the build tree, which ccache makes the paths relative to
                file(RELATIVE_PATH _relative "${CMAKE_BINARY_DIR}" "${CMAKE_MATCH_1}")
                target_compile_options(${_target} PRIVATE "-fmacro-prefix-map=${_relative}/=")
            endif()
        endforeach()
    endforeach()
endfunction()

# duet_firmware_executable(<target> OUTPUT_NAME <name> LINKER_SCRIPT <path> SOURCES <source>...)
#
# A firmware image: <name>.elf and its <name>.map at the top of the build tree, linked with the
# linker script <path>. Its sources are compiled as C99 and C++20, with __FILE__ relative to the
# calling project's root so that the image does not depend on where the tree is checked out.
function(duet_firmware_executable TARGET)
    cmake_parse_arguments(PARSE_ARGV 1 ARG "" "OUTPUT_NAME;LINKER_SCRIPT" "SOURCES")
    if(ARG_UNPARSED_ARGUMENTS)
        message(FATAL_ERROR "duet_firmware_executable: unknown arguments: ${ARG_UNPARSED_ARGUMENTS}")
    endif()

    add_executable(${TARGET} ${ARG_SOURCES})
    set(_map "${CMAKE_BINARY_DIR}/${ARG_OUTPUT_NAME}.map")
    set_target_properties(${TARGET} PROPERTIES
        OUTPUT_NAME "${ARG_OUTPUT_NAME}"
        SUFFIX ".elf"
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}"
        LINK_DEPENDS "${ARG_LINKER_SCRIPT}"
        ADDITIONAL_CLEAN_FILES "${_map}"
        C_STANDARD 99
        C_EXTENSIONS ON
        CXX_STANDARD 20
        CXX_STANDARD_REQUIRED ON
        CXX_EXTENSIONS OFF)
    target_compile_options(${TARGET} PRIVATE "-fmacro-prefix-map=${CMAKE_CURRENT_SOURCE_DIR}/=")
    # -static: there are no shared libraries. It also makes ld check each archive as it opens it, as
    # it does for libraries found with -l; that changes the order of the veneers it generates for
    # calls to code in RAM, so the image depends on it.
    target_link_options(${TARGET} PRIVATE
        -static
        "-T${ARG_LINKER_SCRIPT}"
        "LINKER:-Map,${_map}"
        LINKER:--cref
        LINKER:--gc-sections
        LINKER:--entry=Reset_Handler
        LINKER:--fatal-warnings)
endfunction()

# Link order for all boards. DUET_LIBRARIES defines which libraries to link. See duet_link_libraries()
set(DUET_LINK_ORDER
    LibMbedTls LibTinyusb CANlib CoreN2G RRFLibraries FreeRTOS Qfplib WiFiSocketServerRTOS DuetSpiInterface)

# duet_link_libraries(<target> [<library>...])
#
# Link <target> against the board's libraries (DUET_LIBRARIES) in DUET_LINK_ORDER, followed by the
# toolchain <library>s. As a group, so that each archive appears once on the command line although
# they call each other. Header-only libraries (WiFiSocketServerRTOS, DuetSpiInterface) add nothing
# to the link line, only their include directories, so they are linked outside the group, which
# CMake does not apply to them.
function(duet_link_libraries TARGET)
    foreach(_lib IN LISTS DUET_LIBRARIES)
        if(NOT _lib IN_LIST DUET_LINK_ORDER)
            message(FATAL_ERROR "duet_link_libraries: ${_lib} is not in DUET_LINK_ORDER")
        endif()
    endforeach()
    set(_group)
    set(_headers)
    foreach(_lib IN LISTS DUET_LINK_ORDER)
        if(_lib IN_LIST DUET_LIBRARIES)
            get_target_property(_type Duet::${_lib} TYPE)
            if(_type STREQUAL "INTERFACE_LIBRARY")
                list(APPEND _headers Duet::${_lib})
            else()
                list(APPEND _group Duet::${_lib})
            endif()
        endif()
    endforeach()
    list(APPEND _group ${ARGN})
    list(JOIN _group "," _group)
    target_link_libraries(${TARGET} PRIVATE "$<LINK_GROUP:RESCAN,${_group}>" ${_headers})
endfunction()

# duet_build_date(<target> <source>)
#
# Recompile <source>, which holds the build date, on every build of <target>.
function(duet_build_date TARGET SOURCE)
    add_custom_target(${TARGET}_build_date
        COMMAND "${CMAKE_COMMAND}" -E touch build_date.stamp
        BYPRODUCTS build_date.stamp)
    set_source_files_properties(${SOURCE} PROPERTIES
        OBJECT_DEPENDS "${CMAKE_CURRENT_BINARY_DIR}/build_date.stamp")
    add_dependencies(${TARGET} ${TARGET}_build_date)
endfunction()

# duet_firmware_images(<target> [CRC [FILES <dir>]] [HEX])
#
# After linking <target>, write the flashable images next to its .elf: <name>.bin, with a CRC
# appended if CRC is given (after the files in <dir>, embedded as a filesystem image, if FILES is),
# and <name>.hex if HEX is given, and its disassembly <name>.lst if DUET_DISASSEMBLY is on. Then print
# the image's size. Also link them as vscode/firmware.elf and vscode/firmware.bin in the build tree,
# the fixed paths the VS Code launch configurations debug and flash whichever board it builds, and
# the SVD file for DUET_PART_DEFINE as vscode/device.svd, which they give the peripheral viewer.
function(duet_firmware_images TARGET)
    cmake_parse_arguments(PARSE_ARGV 1 ARG "CRC;HEX" "FILES" "")
    if(ARG_UNPARSED_ARGUMENTS)
        message(FATAL_ERROR "duet_firmware_images: unknown arguments: ${ARG_UNPARSED_ARGUMENTS}")
    endif()

    set(_elf "$<TARGET_FILE:${TARGET}>")
    get_target_property(_dir ${TARGET} RUNTIME_OUTPUT_DIRECTORY)
    get_target_property(_name ${TARGET} OUTPUT_NAME)
    set(_base "${_dir}/${_name}")
    file(MAKE_DIRECTORY "${CMAKE_BINARY_DIR}/vscode")
    foreach(_ext elf bin)
        file(RELATIVE_PATH _target "${CMAKE_BINARY_DIR}/vscode" "${_base}.${_ext}")
        file(CREATE_LINK "${_target}" "${CMAKE_BINARY_DIR}/vscode/firmware.${_ext}" SYMBOLIC)
    endforeach()
    # The device packs name the SVD files after the part (__SAME51G19A__ is ATSAME51G19A.svd), and
    # STM32CubeMX after the line. CoreN2G has none for the SAME70
    if(DUET_PART_DEFINE STREQUAL "STM32H523xx")
        set(_svd_name STM32H523.svd)
    else()
        string(REGEX REPLACE "^__(.+)__$" "AT\\1.svd" _svd_name "${DUET_PART_DEFINE}")
    endif()
    file(GLOB_RECURSE _svd "${_duet_svd_dir}/${_svd_name}")
    if(_svd)
        list(GET _svd 0 _svd)
        file(RELATIVE_PATH _target "${CMAKE_BINARY_DIR}/vscode" "${_svd}")
        file(CREATE_LINK "${_target}" "${CMAKE_BINARY_DIR}/vscode/device.svd" SYMBOLIC)
    else()
        file(REMOVE "${CMAKE_BINARY_DIR}/vscode/device.svd")
    endif()
    set(_commands COMMAND "${CMAKE_OBJCOPY}" -O binary "${_elf}" "${_base}.bin")
    set(_images "${_base}.bin")
    if(ARG_HEX)
        list(APPEND _commands COMMAND "${CMAKE_OBJCOPY}" -O ihex "${_elf}" "${_base}.hex")
        list(APPEND _images "${_base}.hex")
    endif()
    if(DUET_DISASSEMBLY)
        # With the source interleaved in Debug builds, which have the line information for it
        list(APPEND _commands COMMAND "${Python3_EXECUTABLE}" "${_duet_disassemble_script}"
            "${CMAKE_OBJDUMP}" "${_elf}" "${_base}.lst")
        if(NOT DUET_DISASSEMBLY_LABELS)
            list(APPEND _commands --no-labels)
        endif()
        list(APPEND _images "${_base}.lst")
    endif()
    if(ARG_CRC)
        list(APPEND _commands COMMAND "${CRC_APPENDER}" "${_base}.bin" ${ARG_FILES})
        if(ARG_FILES)
            # Relink when an embedded file changes, so the image is rebuilt with it
            file(GLOB_RECURSE _files CONFIGURE_DEPENDS "${ARG_FILES}/*")
            set_property(TARGET ${TARGET} APPEND PROPERTY LINK_DEPENDS ${_files})
        endif()
    endif()
    add_custom_command(TARGET ${TARGET} POST_BUILD
        ${_commands}
        COMMAND "${CMAKE_SIZE}" "${_elf}"
        BYPRODUCTS ${_images}
        COMMENT "Creating ${TARGET} images"
        VERBATIM)
endfunction()
