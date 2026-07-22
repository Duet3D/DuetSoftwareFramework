function(get_enabled_features OUT)
    foreach(feature IN LISTS ARGN)
        if(ARG_${feature})
            list(APPEND _enabled_features ${feature})
        endif()
    endforeach()
    set(${OUT} "${_enabled_features}" PARENT_SCOPE)
endfunction()

function(make_library_name OUT BASE LIB_TYPE MCU)
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