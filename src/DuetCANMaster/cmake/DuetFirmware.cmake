# Helpers that turn the DuetCANMaster board definitions in CMakeLists.txt into firmware targets.
#
#   duet_provide_libraries(<MCU>) -> builds the six shared libraries once for that MCU (into this
#                                    project's build tree) and records them in DUET_LIBS_<MCU>.
#   duet_add_firmware(...)        -> one add_executable() firmware image linking those libraries.
#
# A board picks an MCU; boards that share an MCU share the library builds. Each library is a
# self-contained CMake component (lib/<Name>/<Name>.cmake) selected by a config name, so the whole
# board->MCU->library-config mapping lives here in one readable place.

set(_duet_lib_root "${CMAKE_CURRENT_LIST_DIR}/../../../lib")

set(DEFAULT_INTERFACE_ARGS
    "MCU"       # MCU to compile library for
)
set(DEFAULT_LIBRARY_ARGS
    ${DEFAULT_INTERFACE_ARGS}
    "ARCH"      # Default interface target for the library (from duet_arch_target)
)

include("${_duet_lib_root}/DuetArch.cmake")
include("${_duet_lib_root}/CoreN2G/CoreN2G.cmake")
include("${_duet_lib_root}/FreeRTOS/FreeRTOS.cmake")
include("${_duet_lib_root}/RRFLibraries/RRFLibraries.cmake")
include("${_duet_lib_root}/CANlib/CANlib.cmake")
include("${_duet_lib_root}/LibTinyusb/LibTinyusb.cmake")
include("${_duet_lib_root}/LibMbedTls/LibMbedTls.cmake")

# Which library config each MCU's firmware links against, and the firmware's own MCU-specific bits.
# Adding a board on a new MCU is a matter of adding one block here.
function(_duet_target_library_profile TARGET OUT_DEPS OUT_ARGS)
    if(${TARGET} STREQUAL "Duet3Firmware_MB6HC")

        set(_deps CANLIB COREN2G FREERTOS RRFLIBRARIES LIBTINYUSB)
        set(_args
            COREN2G         "CAN;USB;SDHC;RTOS"
            FREERTOS        ""
            RRFLIBRARIES    "RTOS"
            CANLIB          "RTOS"
            LIBTINYUSB      ""
            LIBMBEDTLS      ""
            HARDWARE_DIR    "SAME70"
        )
    elseif(${TARGET} STREQUAL "Duet3Firmware_MB6XD")
        set(_deps CANLIB COREN2G FREERTOS RRFLIBRARIES LIBTINYUSB)
        set(_args
            COREN2G         "CAN;USB;SDHC;RTOS"
            FREERTOS        ""
            RRFLIBRARIES    "RTOS"
            CANLIB          "RTOS"
            LIBTINYUSB      ""
            LIBMBEDTLS      ""
            HARDWARE_DIR    "SAME70"
        )
    else()
        message(FATAL_ERROR "_duet_target_library_profile: unsupported TARGET '${TARGET}'")
    endif()
    set(${OUT_DEPS} "${_deps}" PARENT_SCOPE)
    set(${OUT_ARGS} "${_args}" PARENT_SCOPE)
endfunction()

# Build (once) the six shared libraries for an MCU, into this project's build tree.
function(duet_provide_libraries TARGET MCU)
    # Create a tag for the library targets using the executable target name
    string(TOLOWER ${TARGET} _tag)
    _duet_target_library_profile(${TARGET} _deps _args)
    cmake_parse_arguments(P "" "HARDWARE_DIR" "COREN2G;FREERTOS;RRFLIBRARIES;CANLIB;LIBTINYUSB;LIBMBEDTLS" ${_args})


    # Create interface targets for libraries that need to be linked into other libraries (e.g. FreeRTOS for CANlib and CoreN2G)
    # It is important to create the interface targets first because otherwise the static libraries have circular dependencies
    # when all they actually need from each other is the include paths and sometimes compile definitions.
    # The interface targets are linked into the static libraries, and the static libraries are linked into the firmware executable.
    if(CANLIB IN_LIST _deps)
        canlib_add_interface(       _canlib_interface_target        MCU ${MCU} ${P_CANLIB})
    endif()
    if(COREN2G IN_LIST _deps)
        coren2g_add_interface(      _coren2g_interface_target       MCU ${MCU} ${P_COREN2G})
    endif()
    if(FREERTOS IN_LIST _deps)
        freertos_add_interface(     _freertos_interface_target      MCU ${MCU} ${P_FREERTOS})
    endif()
    if(RRFLIBRARIES IN_LIST _deps)
        rrflibraries_add_interface( _rrflibraries_interface_target  MCU ${MCU} ${P_RRFLIBRARIES})
    endif()
    if(LIBTINYUSB IN_LIST _deps)
        libtinyusb_add_interface(   _libtinyusb_interface_target    MCU ${MCU} ${P_LIBTINYUSB})
    endif()

    # Create actual static library targets
    if(CANLIB IN_LIST _deps)
        canlib_add_library(
            _canlib_target
            MCU ${MCU}
            ${P_CANLIB}
            ARCH ${_arch}
            COREN2G_INTERFACE ${_coren2g_interface_target}
            FREERTOS_INTERFACE ${_freertos_interface_target}
            RRFLIBRARIES_INTERFACE ${_rrflibraries_interface_target}
        )
    endif()
    
    if(COREN2G IN_LIST _deps)
        coren2g_add_library(
            _coren2g_target
            MCU ${MCU}
            ${P_COREN2G}
            ARCH ${_arch}
            CANLIB_INTERFACE ${_canlib_interface_target}
            FREERTOS_INTERFACE ${_freertos_interface_target}
            RRFLIBRARIES_INTERFACE ${_rrflibraries_interface_target}
            LIBTINYUSB_INTERFACE ${_libtinyusb_interface_target}
        )
    endif()
    
    if(FREERTOS IN_LIST _deps)
        freertos_add_library(
            _freertos_target
            MCU ${MCU}
            ${P_FREERTOS}
            ARCH ${_arch}
        )
    endif()

    if(RRFLIBRARIES IN_LIST _deps)
        rrflibraries_add_library(
            _rrflibraries_target
            MCU ${MCU}
            ${P_RRFLIBRARIES}
            ARCH ${_arch}
            FREERTOS_INTERFACE ${_freertos_interface_target}
        )
    endif()

    if(LIBTINYUSB IN_LIST _deps)
        libtinyusb_add_library(
            _libtinyusb_target
            MCU ${MCU}
            ${P_LIBTINYUSB}
            ARCH ${_arch}
            COREN2G_INTERFACE ${_coren2g_interface_target}
            FREERTOS_INTERFACE ${_freertos_interface_target}
        )
    endif()

    if(LIBMBEDTLS IN_LIST _deps)
        libmbedtls_add_library(
            _libmbedtls_target
            MCU ${MCU}
            ${P_LIBMBEDTLS}
            ARCH ${_arch}
        )
    endif()

    set(DUET_LIBS_${TARGET}
        ${_coren2g_target}
        ${_rrflibraries_target}
        ${_freertos_target}
        ${_canlib_target}
        ${_libtinyusb_target}
        ${_libmbedtls_target}
        PARENT_SCOPE
    )
    set(DUET_HARDWARE_DIR_${TARGET} "${P_HARDWARE_DIR}" PARENT_SCOPE)
endfunction()

# --- One firmware image -----------------------------------------------------------------------
#   duet_add_firmware(<target> MCU <mcu> DEFINE <board-define> LINKER_SCRIPT <path-rel-to-src>)
function(duet_add_firmware TARGET)
    cmake_parse_arguments(PARSE_ARGV 1 ARG "" "MCU;DEFINE;LINKER_SCRIPT" "")

    if(ARG_UNPARSED_ARGUMENTS)
        message(FATAL_ERROR "duet_add_firmware: unknown arguments: ${ARG_UNPARSED_ARGUMENTS}")
    endif()

    if(${TARGET} STREQUAL "Duet3Firmware_MB6HC")
        set(_c_excludes)
        set(_cpp_excludes)
        set(_compile_definitions
            "MBEDTLS_CONFIG_FILE=\"config-same70.h\""
        )
        set(_lnk)
    elseif(${TARGET} STREQUAL "Duet3Firmware_MB6XD")
        set(_c_excludes)
        set(_cpp_excludes)
    else()
        message(FATAL_ERROR "duet_add_firmware: unknown target '${TARGET}'")
    endif()

    duet_arch_target(${ARG_MCU} _arch)
    duet_provide_libraries(${TARGET} ${ARG_MCU})
    set(_libs ${DUET_LIBS_${TARGET}})
    set(_hw "${DUET_HARDWARE_DIR_${TARGET}}")

    # Take every source under src/
    file(GLOB_RECURSE _srcs CONFIGURE_DEPENDS
        "${CMAKE_SOURCE_DIR}/src/*.cpp" "${CMAKE_SOURCE_DIR}/src/*.cc" "${CMAKE_SOURCE_DIR}/src/*.c")
    add_executable(${TARGET} ${_srcs})
    set_target_properties(${TARGET} PROPERTIES
        SUFFIX ".elf"
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/${TARGET}")

    target_link_libraries(${TARGET} PRIVATE ${_arch})

    # Library-provided include paths (all MCU-specific ones) arrive as usage requirements from the
    # linked library targets. The firmware only adds its own sources and the header-only helper
    # libraries that aren't built as targets here.
    target_include_directories(${TARGET} PRIVATE
        "${LIBRARIES_DIR}/DuetSpiInterface/include"
        "${LIBRARIES_DIR}/WiFiSocketServerRTOS/src/include"
        "${CMAKE_SOURCE_DIR}/src"
        "${CMAKE_SOURCE_DIR}/src/Hardware/${_hw}")

    target_compile_definitions(${TARGET} PRIVATE
        ${ARG_DEFINE}
        ${_compile_definitions}
        $<$<COMPILE_LANGUAGE:C>:noexcept=>
        $<$<COMPILE_LANGUAGE:CXX>:_XOPEN_SOURCE>)

    target_compile_options(${TARGET} PRIVATE
        -ffunction-sections
        -fdata-sections
        -nostdlib
        -Wall
        -Wundef
        -Wdouble-promotion
        -Werror=return-type
        -fsingle-precision-constant
        -Werror
        $<$<COMPILE_LANGUAGE:C>:-std=gnu99;-Werror=implicit;-Wwrite-strings>
        $<$<COMPILE_LANGUAGE:CXX>:-std=c++20;-fno-threadsafe-statics;-fno-rtti;-fexceptions;-Wfloat-conversion;-Wsuggest-override;-Wnoexcept;-Wshadow;-Wsign-promo>
        $<$<NOT:$<CONFIG:Debug>>:-O2>
        $<$<CONFIG:Debug>:-Og;-g3>
    )

    set(_map "$<TARGET_FILE_DIR:${TARGET}>/${TARGET}.map")
    target_link_options(${TARGET} PRIVATE
        ${_lnk}
        --specs=nosys.specs
        -Os
        -Wl,--gc-sections
        -Wl,--fatal-warnings
        -Wl,--no-warn-rwx-segment
        "-T${CMAKE_SOURCE_DIR}/src/${ARG_LINKER_SCRIPT}"
        "-Wl,-Map,${_map}"
        -Wl,--cref
        -Wl,--check-sections
        -Wl,--entry=Reset_Handler
        -Wl,--unresolved-symbols=report-all
        -Wl,--warn-common
        -Wl,--warn-section-align
        -Wl,--warn-unresolved-symbols
    )

    # The six static libraries reference each other's symbols cyclically, so they must be scanned
    # as a group; supc++ (the C++ runtime support that -nostdlib excludes) goes in the group too.
    target_link_libraries(${TARGET} PRIVATE "$<LINK_GROUP:RESCAN,${_libs},supc++>")

    find_program(CRCAPPENDER CrcAppender PATHS "${CRC_APPENDER_DIR}" NO_DEFAULT_PATH)
    if(NOT CRCAPPENDER)
        message(FATAL_ERROR "CrcAppender not found under ${CRC_APPENDER_DIR}")
    endif()
    set(_bin "$<TARGET_FILE_DIR:${TARGET}>/${TARGET}.bin")
    add_custom_command(TARGET ${TARGET} POST_BUILD
        COMMAND ${CMAKE_OBJCOPY} -O binary "$<TARGET_FILE:${TARGET}>" "${_bin}"
        COMMAND ${CRCAPPENDER} "${_bin}"
        COMMAND ${CMAKE_SIZE} "$<TARGET_FILE:${TARGET}>"
        COMMENT "Creating ${TARGET}.bin (+CRC)"
        VERBATIM)
    set_target_properties(${TARGET} PROPERTIES ADDITIONAL_CLEAN_FILES "${_bin};${_map}")
endfunction()
