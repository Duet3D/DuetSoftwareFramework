# Run clang-tidy on each C++ translation unit of every project and library as it is compiled, with
# every finding an error.
#
# CMake's CXX_CLANG_TIDY runs the linter immediately after the compiler for the same source, so a
# finding is reported against the file that caused it. The checks are those of the .clang-tidy
# nearest the source: the shared ones at the root of the repository, plus a project's own where it
# has a .clang-tidy that inherits them (DuetCANMaster's naming rules, and lib/.clang-tidy's checks the
# libraries do not pass yet).
#
# clang-tidy is not invoked directly: the firmware is GCC-built for bare-metal ARM, so the command
# line CMake would hand it has to be rewritten first. scripts/internal/clang_tidy_wrapper.py does that
# and skips third-party code; see its docstring for the details.
#
#   DUET_CLANG_TIDY  lint while building (default ON); OFF for a plain, faster compile.

include_guard(GLOBAL)

option(DUET_CLANG_TIDY "Run clang-tidy on the C++ sources during the build" ON)

if(DUET_CLANG_TIDY)
    find_program(CLANG_TIDY_EXE NAMES clang-tidy DOC "clang-tidy executable used during the build")
    find_package(Python3 COMPONENTS Interpreter)
    if(NOT CLANG_TIDY_EXE OR NOT Python3_Interpreter_FOUND)
        message(WARNING "DUET_CLANG_TIDY is ON but clang-tidy or python3 was not found; not linting")
    endif()
endif()
set(_duet_clang_tidy_wrapper "${CMAKE_CURRENT_LIST_DIR}/../scripts/internal/clang_tidy_wrapper.py")

# duet_clang_tidy(<directory>)
#
# Lint the targets defined in <directory> and below.
function(duet_clang_tidy DIRECTORY)
    if(NOT DUET_CLANG_TIDY OR NOT CLANG_TIDY_EXE OR NOT Python3_Interpreter_FOUND)
        return()
    endif()
    duet_compiled_targets("${DIRECTORY}" _targets)
    foreach(_target IN LISTS _targets)
        get_target_property(_existing ${_target} CXX_CLANG_TIDY)
        if(_existing)
            continue()
        endif()
        set_target_properties(${_target} PROPERTIES CXX_CLANG_TIDY
            "${Python3_EXECUTABLE};${_duet_clang_tidy_wrapper};--clang-tidy-binary=${CLANG_TIDY_EXE};--cross-compile=${CROSS_COMPILE}")
    endforeach()
endfunction()
