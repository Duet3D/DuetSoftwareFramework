// Minimal shared test harness.
//
// These tests deliberately have no framework dependency: they are built for three targets, two of
// them cross-compiled, and CI runs them straight out of the build tree. What is actually needed is
// a check that reports where it failed and a main() that returns the right exit code, which is
// small enough to keep here rather than take on gtest.

#ifndef TESTS_TESTSUPPORT_H_
#define TESTS_TESTSUPPORT_H_

#include <cmath>
#include <cstdio>

namespace TestSupport
{
	inline int failures = 0;

	// Report a failure and keep going: one broken invariant usually implicates several checks, and
	// seeing all of them says more about what went wrong than seeing only the first.
	inline void Fail(const char *message, int line) noexcept
	{
		std::printf("FAIL: %s (line %d)\n", message, line);
		++failures;
	}

	inline int Summarise(const char *suiteName) noexcept
	{
		if (failures == 0)
		{
			std::printf("All %s tests passed.\n", suiteName);
			return 0;
		}
		std::printf("%d check(s) failed in %s.\n", failures, suiteName);
		return 1;
	}
}

#define CHECK(cond, msg)                                                                                               \
	do                                                                                                                 \
	{                                                                                                                  \
		if (!(cond))                                                                                                   \
		{                                                                                                              \
			TestSupport::Fail(msg, __LINE__);                                                                          \
		}                                                                                                              \
	} while (0)

// Absolute tolerance rather than relative: every use here compares a physical quantity against an
// expected value in known units, where "within this many ticks/steps" is the meaningful statement.
#define CHECK_NEAR(actual, expected, tolerance, msg)                                                                   \
	do                                                                                                                 \
	{                                                                                                                  \
		const double _a = (double)(actual);                                                                            \
		const double _e = (double)(expected);                                                                          \
		if (!(std::fabs(_a - _e) <= (double)(tolerance)))                                                               \
		{                                                                                                              \
			std::printf("FAIL: %s (line %d): got %g, expected %g +/- %g\n", msg, __LINE__, _a, _e,                     \
						(double)(tolerance));                                                                          \
			++TestSupport::failures;                                                                                   \
		}                                                                                                              \
	} while (0)

#endif /* TESTS_TESTSUPPORT_H_ */
