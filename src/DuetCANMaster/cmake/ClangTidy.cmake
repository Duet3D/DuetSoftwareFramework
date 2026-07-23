# Run clang-tidy on each first-party firmware translation unit as it is compiled.
#
# CMake's CXX_CLANG_TIDY runs the linter immediately after the compiler for the same source, so a
# diagnostic stops the build at the file that caused it. Every check that survives the filtering in
# .clang-tidy is treated as an error (--warnings-as-errors=*), matching the firmware's own -Werror.
#
# clang-tidy is not invoked directly: the firmware is GCC-built for bare-metal ARM, so the command
# line CMake would hand it has to be rewritten first. Scripts/clang-tidy-wrapper.py does that (and
# skips the vendored src/libc and src/libcpp sources); see its docstring for the details.
#
# Turn the whole thing off with -DDUET_CLANG_TIDY=OFF for a plain, faster compile.

option(DUET_CLANG_TIDY "Run clang-tidy on the firmware sources during the build" ON)

find_program(CLANG_TIDY_EXE NAMES clang-tidy DOC "clang-tidy executable used during the build")
find_package(Python3 COMPONENTS Interpreter)

# Attach the linter to one firmware target. A no-op unless DUET_CLANG_TIDY is on and both
# clang-tidy and a Python interpreter were found.
function(duet_enable_clang_tidy TARGET)
    if(NOT DUET_CLANG_TIDY)
        return()
    endif()
    if(NOT CLANG_TIDY_EXE OR NOT Python3_Interpreter_FOUND)
        message(WARNING
            "DUET_CLANG_TIDY is ON but clang-tidy or python3 was not found; "
            "not linting ${TARGET}")
        return()
    endif()

    set_target_properties(${TARGET} PROPERTIES
        CXX_CLANG_TIDY
            "${Python3_EXECUTABLE};${CMAKE_CURRENT_FUNCTION_LIST_DIR}/../Scripts/clang-tidy-wrapper.py;--clang-tidy-binary=${CLANG_TIDY_EXE};--cross-compile=${CROSS_COMPILE};--warnings-as-errors=*;--quiet")
endfunction()
