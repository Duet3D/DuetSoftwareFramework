// The transfer ready handshake with the IAP program.
//
// SpiTransfer needs a GPIO line and an SPI device, so it cannot run here, and the socket transport
// the system tests use has no line at all, which is how a handshake that failed on every real board
// passed all of them. This plays the IAP's side of the line against the same class SpiTransfer uses.
//
// Getting it wrong costs a board its firmware. The IAP has already erased the flash when the second
// segment is due, so a host that misses that signal leaves it to time out and fall back on the ROM.

#include <Interface/SPI/IapReadyLine.h>

#include <TestSupport.h>

using Duet::Sbc::IapReadyLine;

namespace
{

	// The line as iap_spi.cpp drives it: low from SpiInit, and inverted by every setupSpi
	class IapSide
	{
	  public:
		void Arm() noexcept { m_level = !m_level; }
		[[nodiscard]] bool Level() const noexcept { return m_level; }

	  private:
		bool m_level = false;
	};

	void AnArmedTransferIsSeenWhicheverWayTheLineMoved()
	{
		IapSide iap;
		IapReadyLine line;
		line.Restart();
		CHECK(!line.IsReady(iap.Level()), "not ready before IAP has armed anything: it is still erasing");

		for (int segment = 0; segment < 6; ++segment)
		{
			iap.Arm();
			CHECK(line.IsReady(iap.Level()), "every other segment is signalled by a falling edge");
			line.Consume();
			CHECK(!line.IsReady(iap.Level()), "a segment that has gone is not signalled again");
		}
	}

	// VerifyFirmwareChecksum: the segment that never comes, the request, and the verdict
	void TheVerificationIsSignalledTheSameWay()
	{
		IapSide iap;
		IapReadyLine line;
		line.Restart();

		iap.Arm(); // the last segment
		line.Consume();

		iap.Arm(); // the next segment, which will time out
		CHECK(line.IsReady(iap.Level()), "IAP is waiting for another segment");
		line.Consume();

		iap.Arm(); // arrives during FirmwareFinishedDelay, while nothing is watching the line
		CHECK(line.IsReady(iap.Level()), "the request is signalled by the level, so sleeping through the edge is fine");
		line.Consume();

		CHECK(!line.IsReady(iap.Level()), "the verdict is not ready until IAP has checked the flash");
		iap.Arm();
		CHECK(line.IsReady(iap.Level()), "the verdict");
	}

	void ARestartForgetsTheLastUpdate()
	{
		IapReadyLine line;
		line.Consume();
		line.Restart();
		CHECK(line.IsReady(true), "a new IAP takes the line high for its first transfer");
		CHECK(!line.IsReady(false), "whatever the last update left behind");
	}

} // namespace

int main()
{
	AnArmedTransferIsSeenWhicheverWayTheLineMoved();
	TheVerificationIsSignalledTheSameWay();
	ARestartForgetsTheLastUpdate();
	return TestSupport::Summarise("IapReadyLine");
}
