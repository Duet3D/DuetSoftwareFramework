/*
 * RepRap.h - compatibility shim
 *
 * In RepRapFirmware `reprap` is the global object through which every subsystem finds every other.
 * The imported motion sources use it about thirty times - reprap.GetMove(), reprap.GetGCodes(),
 * reprap.Debug() - and this facade exists so that those call sites need no edits, which keeps the
 * files diffable against upstream.
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

	[[nodiscard]] Duet::Sbc::Motion::MotionSystem& GetMove() const noexcept { return move; }
	[[nodiscard]] const GCodesShim& GetGCodes() const noexcept { return gCodes; }
	[[nodiscard]] Platform& GetPlatform() const noexcept { return platform; }

	// Debug topic selection. The firmware sets these from M111; nothing sets them here, so every
	// `if (reprap.Debug(Module::Move))` in the imported code folds away. Kept as functions rather
	// than removed so that those branches still have to compile, which is what stops their contents
	// bit-rotting into something that no longer builds.
	[[nodiscard]] static bool Debug(Module module) noexcept;
	[[nodiscard]] static AxesBitmap GetDebugFlags(Module module) noexcept;

private:
	static Duet::Sbc::Motion::MotionSystem move;
	static Platform platform;
	GCodesShim gCodes;
};

extern RepRapShim reprap;

#endif /* SRC_COMPAT_PLATFORM_REPRAP_H_ */
