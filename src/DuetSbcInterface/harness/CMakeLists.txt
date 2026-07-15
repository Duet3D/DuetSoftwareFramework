# Standalone jitter test harness.
add_executable(sbc_jitter_test main.cpp)
target_link_libraries(sbc_jitter_test PRIVATE duet_sbc)

if(DUET_SBC_STATIC)
    # Fully static link: no runtime glibc/libstdc++ version dependency, so the binary is portable
    # across Pi OS releases. The program only uses syscalls, pthreads and libstdc++ (no NSS/dlopen),
    # so a static glibc link is safe here.
    target_link_options(sbc_jitter_test PRIVATE -static -static-libgcc -static-libstdc++)
endif()
