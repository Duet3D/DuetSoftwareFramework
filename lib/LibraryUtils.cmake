# Argument names every lib/<Name>/<Name>.cmake module accepts, referenced by each module's
# cmake_parse_arguments call. They live here rather than in a consumer so that a project can use one
# library module on its own - DuetSbcInterface wants RRFLibraries and nothing else - without having
# to replicate a convention it does not otherwise participate in.
set(DEFAULT_INTERFACE_ARGS
    "MCU"       # MCU to compile library for
)
set(DEFAULT_LIBRARY_ARGS
    ${DEFAULT_INTERFACE_ARGS}
    "ARCH"      # Default interface target for the library (from duet_arch_target)
)

function(get_enabled_features OUT)
    foreach(feature IN LISTS ARGN)
        if(ARG_${feature})
            list(APPEND _enabled_features ${feature})
        endif()
    endforeach()
    set(${OUT} "${_enabled_features}" PARENT_SCOPE)
endfunction()

function(make_library_name OUT BASE LIB_TYPE MCU)
    # Omitting LIB_TYPE shifts every positional argument along and silently yields a plausible-looking
    # but wrong target name, so reject anything that isn't a type we know.
    if(NOT LIB_TYPE STREQUAL "INTERFACE" AND NOT LIB_TYPE STREQUAL "STATIC")
        message(FATAL_ERROR "make_library_name: LIB_TYPE must be INTERFACE or STATIC, got '${LIB_TYPE}'")
    endif()

    string(TOUPPER ${MCU} _tag)

    if (LIB_TYPE STREQUAL "INTERFACE")
        set(_target "I_${BASE}_${_tag}")
    else()
        set(_target "${BASE}_${_tag}")
    endif()
    
    foreach(feature IN LISTS ARGN)
        string(APPEND _target "_${feature}")
    endforeach()

    set(${OUT} "${_target}" PARENT_SCOPE)
endfunction()