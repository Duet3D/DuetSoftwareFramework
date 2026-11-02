# Run clang-tidy on each C++ translation unit of every project and library as it is compiled.
#
# CMake's CXX_CLANG_TIDY runs the linter immediately after the compiler for the same source, so a
# finding is reported against the file that caused it. The checks are those of the .clang-tidy
# nearest the source: the shared ones at the root of the repository, plus a project's own (such as
# its naming rules) where it has a .clang-tidy that inherits them.
#
# clang-tidy is not invoked directly: the firmware is GCC-built for bare-metal ARM, so the command
# line CMake would hand it has to be rewritten first. scripts/clang_tidy_wrapper.py does that, skips
# third-party code, and turns findings into warnings or errors; see its docstring for the details.
#
#   DUET_CLANG_TIDY         lint while building (default ON); OFF for a plain, faster compile
#   DUET_CLANG_TIDY_ERRORS  the projects (src/<name>) and libraries (lib/<name>) whose findings fail
#                           the build; the others' are warnings. A project goes on the list once it
#                           has no findings left.

include_guard(GLOBAL)

option(DUET_CLANG_TIDY "Run clang-tidy on the C++ sources during the build" ON)
set(DUET_CLANG_TIDY_ERRORS "DuetCANMaster" CACHE STRING
    "Projects and libraries whose clang-tidy findings fail the build; the others' are warnings")

if(DUET_CLANG_TIDY)
    find_program(CLANG_TIDY_EXE NAMES clang-tidy DOC "clang-tidy executable used during the build")
    find_package(Python3 COMPONENTS Interpreter)
    if(NOT CLANG_TIDY_EXE OR NOT Python3_Interpreter_FOUND)
        message(WARNING "DUET_CLANG_TIDY is ON but clang-tidy or python3 was not found; not linting")
    endif()
endif()
set(_duet_clang_tidy_wrapper "${CMAKE_CURRENT_LIST_DIR}/../scripts/clang_tidy_wrapper.py")

# duet_clang_tidy(<directory>)
#
# Lint the targets defined in <directory> and below, each as part of the project or library whose
# directory, src/<name> or lib/<name>, holds it. A target that already sets CXX_CLANG_TIDY is left
# as it is.
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
        get_target_property(_dir ${_target} SOURCE_DIR)
        file(RELATIVE_PATH _relative "${CMAKE_SOURCE_DIR}" "${_dir}")
        string(REGEX REPLACE "^(src|lib)/([^/]+).*" "\\2" _owner "${_relative}")
        set(_command
            "${Python3_EXECUTABLE}" "${_duet_clang_tidy_wrapper}"
            "--clang-tidy-binary=${CLANG_TIDY_EXE}" "--cross-compile=${CROSS_COMPILE}")
        if(_owner IN_LIST DUET_CLANG_TIDY_ERRORS)
            list(APPEND _command --errors)
        endif()
        set_target_properties(${_target} PROPERTIES CXX_CLANG_TIDY "${_command}")
    endforeach()
endfunction()
