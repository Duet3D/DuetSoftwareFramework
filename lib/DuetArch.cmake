# Shared MCU -> ARM architecture flag mapping for the Duet3D firmware libraries and firmwares.
#
# It lives in lib/ (a sibling of the library submodules) because both the libraries and the
# firmware projects that consume them need the identical -mcpu/-mfpu/-mfloat-abi triple for a
# given MCU - the same sibling-path assumption the old Makefiles already relied on
# (../RRFLibraries, ../FreeRTOS, ...). Keeping it in one place stops the firmware and its
# libraries from drifting onto incompatible ABIs.
#
# duet_arch_flags(<mcu-token> <out-compile-options-var> <out-link-options-var>)
#   <mcu-token> is the leading component of a library config name, e.g. SAME70, SAME5x, SAME51,
#   SAM4E, SAMC21, RP2040, STM32H743, STM32H523.

include_guard(GLOBAL)

function(_duet_mcu_define MCU OUT)
    if(MCU STREQUAL "SAME70")
        set(_part_define "__SAME70Q20B__") # FreeRTOS and RRFLibraries used to select `__SAME70Q21__`
    elseif(MCU STREQUAL "SAME51")
        set(_part_define "__SAME51N19A__")
    elseif(MCU STREQUAL "SAM4E")
        set(_part_define "__SAM4E8E__")
    elseif(MCU STREQUAL "SAMC21")
        set(_part_define "__SAMC21G18A__")
    elseif(MCU STREQUAL "RP2040")
        set(_part_define "__RP2040__")
    elseif(MCU STREQUAL "STM32H743")
        set(_part_define "STM32H743xx")
    elseif(MCU STREQUAL "STM32H523")
        set(_part_define "STM32H523xx")
    else()
        message(FATAL_ERROR "duet_mcu_define: unsupported MCU '${MCU}'")
    endif()
    set(${OUT} "${_part_define}" PARENT_SCOPE)
endfunction()

function(_duet_arch_flags MCU OUT_COMPILE_OPTIONS OUT_COMPILE_DEFINITIONS OUT_LINK)
    if(MCU MATCHES "^(SAME70|STM32H743)$")
        set(_compile_options -mcpu=cortex-m7 -mfpu=fpv5-d16 -mfloat-abi=hard -mno-unaligned-access)
    elseif(MCU MATCHES "^(SAME5x|SAME51|SAM4E)$")
        set(_compile_options -mcpu=cortex-m4 -mfpu=fpv4-sp-d16 -mfloat-abi=hard -mno-unaligned-access)
    elseif(MCU STREQUAL "STM32H523")
        set(_compile_options -mcpu=cortex-m33 -mfpu=fpv5-sp-d16 -mfloat-abi=hard -mno-unaligned-access)
    elseif(MCU MATCHES "^(SAMC21|RP2040)$")
        set(_compile_options -mcpu=cortex-m0plus)          # Cortex-M0+ has no FPU -> soft float
    else()
        message(FATAL_ERROR "duet_arch_flags: unknown MCU token '${MCU}'")
    endif()
    # Flags shared by every Duet target regardless of core.
    list(APPEND _compile_options -mthumb -mfp16-format=ieee -fno-math-errno)
    _duet_mcu_define(${MCU} _part_define)
    set(${OUT_COMPILE_DEFINITIONS} "${_part_define}" PARENT_SCOPE)
    # The link driver only needs the machine-selection flags, not the codegen ones.
    set(_link_options)
    foreach(f IN LISTS _compile_options)
        if(f MATCHES "^-m(cpu|fpu|float-abi|thumb)")
            list(APPEND _link_options ${f})
        endif()
    endforeach()
    set(${OUT_COMPILE_OPTIONS} "${_compile_options}" PARENT_SCOPE)
    set(${OUT_LINK} "${_link_options}" PARENT_SCOPE)
endfunction()

# Convenience: create (once) an INTERFACE target duet_arch_<mcu> carrying those flags as usage
# requirements, so libraries/executables can just link it.
function(duet_arch_target MCU OUT_TARGET)
    string(TOLOWER ${MCU} _tag)
    set(_target "duet_arch_${_tag}")
    if(TARGET ${_target})
        return()  # already created, don't create again
    endif()
    _duet_arch_flags(${MCU} _compile_options _compile_definitions _link_options)
    add_library(${_target} INTERFACE)
    target_compile_options(${_target} INTERFACE ${_compile_options})
    target_compile_definitions(${_target} INTERFACE ${_compile_definitions})
    target_link_options(${_target} INTERFACE ${_link_options})
    set(${OUT_TARGET} ${_target} PARENT_SCOPE)
endfunction()
