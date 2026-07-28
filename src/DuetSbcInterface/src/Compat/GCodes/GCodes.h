/*
 * GCodes.h - compatibility shim
 *
 * The imported motion sources ask reprap.GetGCodes() for three things: how many axes there are, how
 * many of those the user can see, and how many extruders. That is all they use of a class that in
 * the firmware also parses G-code, runs macros, homes the machine and owns the movement systems -
 * all of which is DuetControlServer's job here.
 *
 * So this is not a port of GCodes. It is a view onto the same MotionConfig that DCS pushed down,
 * shaped to answer those three questions at the call sites that expect to ask GCodes them.
 */

#ifndef SRC_COMPAT_GCODES_GCODES_H_
#define SRC_COMPAT_GCODES_GCODES_H_

#include <RepRapFirmware.h>

namespace Duet::Sbc::Motion
{
	class MotionSystem;
}

class GCodesShim
{
public:
	explicit GCodesShim(const Duet::Sbc::Motion::MotionSystem& p_move) noexcept : move(p_move) { }

	[[nodiscard]] size_t GetTotalAxes() const noexcept;
	[[nodiscard]] size_t GetVisibleAxes() const noexcept;
	[[nodiscard]] size_t GetNumExtruders() const noexcept;

private:
	const Duet::Sbc::Motion::MotionSystem& move;
};

#endif /* SRC_COMPAT_GCODES_GCODES_H_ */
