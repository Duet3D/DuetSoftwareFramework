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

# The socket transport against an in-process peer speaking the SocketLinkFormats framing. That peer
# is the executable specification the C# fake endpoint in src/SystemTests follows.
duet_sbc_add_test(socket_transport_tests SocketTransportTests.cpp)
target_link_libraries(socket_transport_tests PRIVATE duet_sbc)

duet_sbc_add_test(step_timer_tests StepTimerTests.cpp)
target_link_libraries(step_timer_tests PRIVATE duet_motion)

duet_sbc_add_test(move_segment_tests MoveSegmentTests.cpp)
target_link_libraries(move_segment_tests PRIVATE duet_motion)

duet_sbc_add_test(drive_tracker_tests DriveTrackerTests.cpp)
target_link_libraries(drive_tracker_tests PRIVATE duet_motion)

duet_sbc_add_test(motion_system_tests MotionSystemTests.cpp)
target_link_libraries(motion_system_tests PRIVATE duet_motion)

duet_sbc_add_test(dda_ring_tests DdaRingTests.cpp)
target_link_libraries(dda_ring_tests PRIVATE duet_motion)

duet_sbc_add_test(schedule_move_builder_tests ScheduleMoveBuilderTests.cpp)
target_link_libraries(schedule_move_builder_tests PRIVATE duet_motion)

duet_sbc_add_test(move_params_layout_tests MoveParamsLayoutTests.cpp)
target_link_libraries(move_params_layout_tests PRIVATE duet_motion)

duet_sbc_add_test(machine_config_layout_tests MachineConfigLayoutTests.cpp)
target_link_libraries(machine_config_layout_tests PRIVATE duet_motion)

# The rule DuetCANMaster matches an input change against. It lives in the shared protocol headers
# rather than in the controller so that it can be tested at all: the controller is built for an ARM
# target and reaches the rule through a firmware task. Links the protocol headers alone - the rule
# depends on nothing else, which is what makes it testable here.
duet_sbc_add_test(stop_rules_tests StopRulesTests.cpp)
target_link_libraries(stop_rules_tests PRIVATE duet_spi_protocol)
