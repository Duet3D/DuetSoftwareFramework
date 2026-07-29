/*
 * RepRap.h - compatibility shim
 *
 * In RepRapFirmware `reprap` is the global object through which every subsystem finds every other.
 * The imported motion sources use it about thirty times - reprap.GetMove(), reprap.GetGCodes(),
 * reprap.Debug() - and this facade exists so that those call sites keep working as written, rather
 * than each of them having to learn where the motion system actually lives here.
 *
 * It is a facade over much less than the name suggests: one MotionSystem, one view onto its config
 * shaped like GCodes, and debug flags that nothing sets.
 */

#ifndef SRC_COMPAT_PLATFORM_REPRAP_H_
#define SRC_COMPAT_PLATFORM_REPRAP_H_

#include <GCodes/GCodes.h>
#include <Motion/MotionSystem.h>
#include <Platform/Platform.h>
#include <RepRapFirmware.h>

// The imported sources declare locals as `const Move&`. On this side the motion system is what Move
// became, so the name is kept rather than edited out of every one of those files.
using Move = Duet::Sbc::Motion::MotionSystem;

// Likewise for the machine description types the imported code names unqualified.
using AxisDriversConfig = Duet::Sbc::Motion::AxisDriversConfig;

class RepRapShim
{
public:
	RepRapShim() noexcept;

	// Not static, although there is only ever one of each: the imported code reaches them as
	// `reprap.GetMove()`, and a static accessor called through an instance is a lint error at every
	// one of those call sites. `reprap` is the singleton; what hangs off it need not be.
	[[nodiscard]] Duet::Sbc::Motion::MotionSystem& GetMove() noexcept { return m_move; }
	[[nodiscard]] const GCodesShim& GetGCodes() const noexcept { return m_gCodes; }
	[[nodiscard]] Platform& GetPlatform() noexcept { return m_platform; }

	// Debug topic selection. The firmware sets these from M111; nothing sets them here, so every
	// `if (reprap.Debug(Module::Move))` in the imported code folds away. Kept as functions rather
	// than removed so that those branches still have to compile, which is what stops their contents
	// bit-rotting into something that no longer builds.
	[[nodiscard]] static bool Debug(Module module) noexcept;
	[[nodiscard]] static AxesBitmap GetDebugFlags(Module module) noexcept;

private:
	Duet::Sbc::Motion::MotionSystem m_move;
	Platform m_platform;
	GCodesShim m_gCodes;
};

extern RepRapShim reprap;

#endif /* SRC_COMPAT_PLATFORM_REPRAP_H_ */
