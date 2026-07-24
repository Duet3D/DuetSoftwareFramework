# Host-side unit tests. These need no hardware, so they run in CI/the devcontainer.
add_executable(ring_buffer_tests RingBufferTests.cpp)
target_link_libraries(ring_buffer_tests PRIVATE duet_sbc)

add_test(NAME ring_buffer_tests COMMAND ring_buffer_tests)
