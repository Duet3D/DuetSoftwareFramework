#!/usr/bin/env python3
"""Regenerate the CMake component modules for the glob-based Duet3D libraries from their existing
Makefiles/*.mk build configs.

    python3 lib/gen-cmake-configs.py

Emits CoreN2G/CoreN2G.cmake, RRFLibraries/RRFLibraries.cmake, CANlib/CANlib.cmake and
LibTinyusb/LibTinyusb.cmake. FreeRTOS and LibMbedTls use fixed (non-globbed) source lists and are
maintained by hand.

For every config the script verifies that a CMake-style glob('src/**/*.<ext>') minus the translated
exclude regexes reproduces the *exact* file set the Makefile's `find` command selects, so the
generated modules stay faithful to the Makefiles they replace. Re-run it after the library source
tree or a Makefile config changes.
"""
import re, os, subprocess, sys, glob as globmod
from pathlib import Path

LIBROOT = os.path.dirname(os.path.abspath(__file__))

# ---------------------------------------------------------------------------------------------
# Extraction from a Makefiles/*.mk config
# ---------------------------------------------------------------------------------------------
def _find_block(text, ext):
    """The raw `find ...` shell command for the given source extension, $(SRC_DIR) -> src."""
    for m in re.finditer(r":?=\s*\$\(shell\s+(find\b.*?\))\s*$", text, re.S | re.M):
        blk = m.group(1)
        if re.search(r"-name\s+['\"]\*\." + re.escape(ext) + r"['\"]", blk):
            depth = 0
            for i, ch in enumerate(blk):
                if ch == '(':
                    depth += 1
                elif ch == ')':
                    if depth == 0:
                        blk = blk[:i]; break
                    depth -= 1
            return re.sub(r"\$\([A-Z0-9x_]*SRC_DIR\)", "src", re.sub(r"\\\s*\n", " ", blk))
    return None

def _run_find(libdir, cmd):
    out = subprocess.run(cmd, cwd=libdir, shell=True, capture_output=True, text=True).stdout
    return sorted(f[2:] if f.startswith("./") else f for f in out.split("\n") if f.strip())

def _exclude_regexes(block):
    """Translate the `! -path '<glob>'` patterns to substring regexes for list(FILTER EXCLUDE)."""
    regs = []
    for p in re.findall(r"!\s*-path\s+['\"]([^'\"]+)['\"]", block):
        p = re.sub(r"\$\([A-Z0-9x_]*SRC_DIR\)", "src", p).strip()
        if p.startswith("*/"): p = p[1:]        # */X/*  -> /X/*
        if p.startswith("src/"): p = "/" + p    # src/X  -> /src/X
        if p.endswith("/*"): p = p[:-1]         # .../X/* -> .../X/
        elif p.endswith("*"): p = p[:-1]
        regs.append(p)
    return regs

def _cmake_equivalent(libdir, ext, regs):
    files = [os.path.relpath(str(f), libdir) for f in Path(libdir, "src").rglob(f"*.{ext}")]
    return sorted(f for f in files if not any(r in "/" + f for r in regs))

def _includes(text):
    m = re.search(r"[A-Z0-9x_]*INCLUDES\s*:?=\s*(.*?)(?=\n[A-Za-z0-9_]+\s*:?=|\n\n|\Z)", text, re.S)
    if not m: return []
    return [re.sub(r"\$\([A-Z0-9x_]*SRC_DIR\)", "src", i) for i in re.findall(r"-I(\S+)", m.group(1))]

def _defs(text, names):
    for n in names:
        m = re.search(r"[A-Z0-9x_]*" + n + r"\s*:?\+?=\s*(.*?)(?=\n[A-Z]|\n\n|\Z)", text, re.S)
        if m:
            ds = re.findall(r'-D(?:"[^"]*"|\S)+', m.group(1))
            if ds: return [d[2:].rstrip("'") for d in ds]
    return []

def _arch(text):
    a = {}
    for k, p in [("mcpu", r"-mcpu=[a-z0-9+.-]+"), ("mfpu", r"-mfpu=[a-z0-9-]+"), ("mfloat", r"-mfloat-abi=[a-z]+")]:
        v = sorted(set(re.findall(p, text)))
        a[k] = v[0] if v else None
    return a

def parse_config(libname, cfg):
    mk = os.path.join(LIBROOT, libname, "Makefiles", cfg + ".mk")
    text = open(mk).read()
    libdir = os.path.join(LIBROOT, libname)
    excl = {}
    for ext in ("cpp", "c"):
        blk = _find_block(text, ext)
        if blk is None:
            excl[ext] = []
            continue
        regs = _exclude_regexes(blk)
        want, got = _run_find(libdir, blk), _cmake_equivalent(libdir, ext, regs)
        if want != got:
            raise SystemExit(f"VERIFY FAILED {libname}/{cfg} .{ext}: glob+exclude != find\n"
                             f"  only in find: {set(want)-set(got)}\n  only in glob: {set(got)-set(want)}")
        excl[ext] = regs
    return dict(excl_cpp=excl["cpp"], excl_c=excl["c"], includes=_includes(text),
                c_defs=_defs(text, ["_C_DEFS", "_C_DEFINES", "_DEFINES"]),
                cxx_defs=_defs(text, ["_CXX_DEFS", "_DEFINES"]), arch=_arch(text))

# ---------------------------------------------------------------------------------------------
# CMake emission
# ---------------------------------------------------------------------------------------------
def _cmlist(items):
    return ";".join(items).replace('"', '\\"')

HEADER = ("# AUTO-GENERATED from Makefiles/*.mk by lib/gen-cmake-configs.py - do not edit by hand.\n"
          "# {desc}\n#   {func}(TARGET <name> CONFIG <one of {var}_CONFIGS>)\n\n")

def builder(var, func, glob_cpp, glob_c, opt, common, cflags, cxxflags):
    g = []
    g.append(f'    file(GLOB_RECURSE _cpp CONFIGURE_DEPENDS "${{{var}_DIR}}/{glob_cpp}")' if glob_cpp else "    set(_cpp)")
    g.append(f'    file(GLOB_RECURSE _c CONFIGURE_DEPENDS "${{{var}_DIR}}/{glob_c}")' if glob_c else "    set(_c)")
    return f'''
function({func})
    cmake_parse_arguments(PARSE_ARGV 0 ARG "" "TARGET;CONFIG" "")
    if(NOT DEFINED _{var}_${{ARG_CONFIG}}_MCU)
        message(FATAL_ERROR "{func}: unknown CONFIG '${{ARG_CONFIG}}' (see {var}_CONFIGS)")
    endif()
    set(_p _{var}_${{ARG_CONFIG}})
{chr(10).join(g)}
    foreach(_r IN LISTS ${{_p}}_EXCL_CPP)
        list(FILTER _cpp EXCLUDE REGEX "${{_r}}")
    endforeach()
    foreach(_r IN LISTS ${{_p}}_EXCL_C)
        list(FILTER _c EXCLUDE REGEX "${{_r}}")
    endforeach()
    add_library(${{ARG_TARGET}} STATIC ${{_cpp}} ${{_c}})
    foreach(_i IN LISTS ${{_p}}_INC)
        target_include_directories(${{ARG_TARGET}} PRIVATE "${{{var}_DIR}}/${{_i}}")
        target_include_directories(${{ARG_TARGET}} INTERFACE "${{{var}_DIR}}/${{_i}}")
    endforeach()
    target_compile_definitions(${{ARG_TARGET}} PRIVATE
        "$<$<COMPILE_LANGUAGE:C>:${{${{_p}}_CDEF}}>"
        "$<$<COMPILE_LANGUAGE:CXX>:${{${{_p}}_CXXDEF}}>")
    duet_arch_flags(${{${{_p}}_MCU}} _arch _lnk)
    target_compile_options(${{ARG_TARGET}} PRIVATE
        ${{_arch}}
        {common}
        "$<$<COMPILE_LANGUAGE:C>:{';'.join(cflags.split())}>"
        "$<$<COMPILE_LANGUAGE:CXX>:{';'.join(cxxflags.split())}>"
        $<$<NOT:$<CONFIG:Debug>>:{opt}>
        $<$<CONFIG:Debug>:-Og;-g3>)
endfunction()
'''

def emit(libname, var, func, desc, glob_cpp, glob_c, opt, common, cflags, cxxflags, data):
    with open(os.path.join(LIBROOT, libname, libname + ".cmake"), "w") as f:
        f.write(HEADER.format(desc=desc, func=func, var=var))
        f.write(f'set({var}_DIR "${{CMAKE_CURRENT_LIST_DIR}}")\n')
        f.write(f'include("${{CMAKE_CURRENT_LIST_DIR}}/../DuetArch.cmake")\n\n')
        f.write(f'set({var}_CONFIGS {" ".join(data)})\n\n')
        for cfg, e in data.items():
            p = f"_{var}_{cfg}"
            f.write(f'set({p}_MCU {cfg.split("_")[0]})\n')
            f.write(f'set({p}_EXCL_CPP "{_cmlist(e["excl_cpp"])}")\n')
            f.write(f'set({p}_EXCL_C "{_cmlist(e["excl_c"])}")\n')
            f.write(f'set({p}_INC "{_cmlist(e["inc"])}")\n')
            f.write(f'set({p}_CDEF "{_cmlist(e["cdef"])}")\n')
            f.write(f'set({p}_CXXDEF "{_cmlist(e["cxxdef"])}")\n')
        f.write(builder(var, func, glob_cpp, glob_c, opt, common, cflags, cxxflags))

def configs(libname):
    return [os.path.basename(m)[:-3] for m in sorted(globmod.glob(f"{LIBROOT}/{libname}/Makefiles/*.mk"))]

def part_define(libname, cfg):
    text = open(os.path.join(LIBROOT, libname, "Makefiles", cfg + ".mk")).read()
    m = re.search(r"-D(__[A-Z0-9_]+__|STM32[A-Z0-9]+xx)", text)
    return m.group(1) if m else ""

COMMON = "-ffunction-sections -fdata-sections -nostdlib -Wall -Wundef -Wdouble-promotion -Werror=return-type -fsingle-precision-constant"
CXX_WARN = "-fno-threadsafe-statics -fno-rtti -fno-exceptions -Werror -Wnoexcept -Wshadow -Wsign-promo"

def main():
    # CoreN2G: C and C++ sources; only C-vs-C++ define difference across all configs is C adds noexcept=.
    d = {}
    for cfg in configs("CoreN2G"):
        e = parse_config("CoreN2G", cfg)
        cxxdef = e["cxx_defs"]
        d[cfg] = dict(excl_cpp=e["excl_cpp"], excl_c=e["excl_c"], inc=e["includes"],
                      cxxdef=cxxdef, cdef=cxxdef + ["noexcept="])
    emit("CoreN2G", "COREN2G", "coren2g_add_library", "CoreN2G (MCU hardware abstraction layer).",
         "src/*.cpp", "src/*.c", "-O3", COMMON, "-std=gnu99 -Werror=implicit",
         "-std=c++20 -Wsuggest-override " + CXX_WARN, d)

    # LibTinyusb: C only.
    d = {}
    for cfg in configs("LibTinyusb"):
        e = parse_config("LibTinyusb", cfg)
        d[cfg] = dict(excl_cpp=[], excl_c=e["excl_c"], inc=e["includes"], cdef=e["c_defs"], cxxdef=[])
    emit("LibTinyusb", "LIBTINYUSB", "libtinyusb_add_library", "LibTinyusb (TinyUSB device stack).",
         None, "src/tinyusb/src/*.c", "-O3", COMMON, "-Werror=implicit", "", d)

    # RRFLibraries and CANlib: C++ only; defines are part-define plus RTOS.
    for libname, var, func, desc in [
            ("RRFLibraries", "RRFLIBRARIES", "rrflibraries_add_library", "RRFLibraries."),
            ("CANlib", "CANLIB", "canlib_add_library", "CANlib.")]:
        d = {}
        for cfg in configs(libname):
            e = parse_config(libname, cfg)
            rtos = "RTOS" in cfg.split("_")[1:]
            defs = [part_define(libname, cfg)] + (["RTOS"] if rtos else [])
            d[cfg] = dict(excl_cpp=e["excl_cpp"], excl_c=e["excl_c"], inc=e["includes"], cdef=[], cxxdef=defs)
        emit(libname, var, func, desc, "src/*.cpp", None, "-O2", COMMON, "", "-std=c++20 " + CXX_WARN, d)

    print("Regenerated CoreN2G, LibTinyusb, RRFLibraries, CANlib (glob+exclude verified against Makefile find)")

if __name__ == "__main__":
    main()
