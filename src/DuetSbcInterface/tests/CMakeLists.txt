# Host-side unit tests. These need no hardware, so they run in CI/the devcontainer.
#
# The motion tests link duet_motion rather than duet_sbc: the motion engine is deliberately
# independent of the SPI interface, so it can be exercised entirely offline against a clock and a
# scheduling sink the test controls.
function(duet_sbc_add_test name)
    add_executable(${name} ${ARGN})
    target_include_directories(${name} PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
    add_test(NAME ${name} COMMAND ${name})
endfunction()

duet_sbc_add_test(ring_buffer_tests RingBufferTests.cpp)
target_link_libraries(ring_buffer_tests PRIVATE duet_sbc)

duet_sbc_add_test(step_timer_tests StepTimerTests.cpp)
target_link_libraries(step_timer_tests PRIVATE duet_motion)

duet_sbc_add_test(move_segment_tests MoveSegmentTests.cpp)
target_link_libraries(move_segment_tests PRIVATE duet_motion)

duet_sbc_add_test(drive_tracker_tests DriveTrackerTests.cpp)
target_link_libraries(drive_tracker_tests PRIVATE duet_motion)

duet_sbc_add_test(motion_system_tests MotionSystemTests.cpp)
target_link_libraries(motion_system_tests PRIVATE duet_motion)

duet_sbc_add_test(schedule_move_builder_tests ScheduleMoveBuilderTests.cpp)
target_link_libraries(schedule_move_builder_tests PRIVATE duet_motion)

duet_sbc_add_test(move_params_layout_tests MoveParamsLayoutTests.cpp)
target_link_libraries(move_params_layout_tests PRIVATE duet_motion)
