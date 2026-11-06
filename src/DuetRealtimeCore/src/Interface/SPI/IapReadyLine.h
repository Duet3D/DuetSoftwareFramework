// The transfer ready line as the IAP program drives it.
//
// DuetCANMaster holds TfrRdy high while it is armed and drops it when a transfer starts, so a rising
// edge means ready. DuetIAP does not: it inverts the line each time it arms a transfer, because
// nothing drops it in between, so a falling edge says ready just as a rising one does. The line
// starts low and the first transfer is signalled by taking it high.
//
// What is tracked is therefore the level that means the next transfer is armed, which alternates.
// A level rather than an edge, because the line may already have moved by the time it is waited for:
// VerifyFirmwareChecksum sleeps through the IAP arming the verification request.
#pragma once

namespace Duet::Sbc
{

	class IapReadyLine
	{
	  public:
		// The IAP has just started, or is about to
		void Restart() noexcept { m_readyLevel = true; }

		[[nodiscard]] bool IsReady(bool level) const noexcept { return level == m_readyLevel; }

		// The transfer that was signalled is going ahead, so the next one takes the opposite level
		void Consume() noexcept { m_readyLevel = !m_readyLevel; }

	  private:
		bool m_readyLevel = true;
	};

} // namespace Duet::Sbc
