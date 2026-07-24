// Standalone SBC-side SPI jitter test.
//
// Connects to a Duet running the device-side firmware (DuetCANMaster), runs the ported SBC transfer
// loop on a pinned real-time thread, and measures the latency between RequestTransfer() and the
// completion of the SPI transfer that serves it -- the same metric quoted for the C# implementation.
// Because no .NET runtime (and therefore no GC) is involved, comparing this histogram against the C#
// one isolates whether the 40 ms outliers come from the kernel/hardware or from the managed runtime.
//
#include <Config/Configuration.h>
#include <Platform/ProcessHelpers.h>
#include <SBC/SbcInterface.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

using namespace Duet::Sbc;

namespace
{

	std::atomic<bool> gRunning{true};
	void OnSignal(int /*unused*/)
	{
		gRunning.store(false);
	}

	// Lock-free sample store written from the interface thread.
	constexpr size_t kMaxSamples = 8'000'000;
	std::vector<int64_t> gSamples;
	std::atomic<size_t> gSampleIndex{0};

	void RecordSample(int64_t latencyNs)
	{
		const size_t i = gSampleIndex.fetch_add(1, std::memory_order_relaxed);
		if (i < kMaxSamples)
		{
			gSamples[i] = latencyNs;
		}
	}

	int64_t Percentile(const std::vector<int64_t>& sorted, double pct)
	{
		if (sorted.empty())
			return 0;
		const double rank = (pct / 100.0) * static_cast<double>(sorted.size() - 1);
		const auto idx = static_cast<size_t>(std::llround(rank));
		return sorted[std::min(idx, sorted.size() - 1)];
	}

	void PrintUsage(const char* argv0)
	{
		std::printf("Usage: %s [options]\n"
					"  --spi-dev PATH        spidev node (default /dev/spidev0.0)\n"
					"  --spi-hz HZ           SPI frequency (default 8000000)\n"
					"  --spi-mode 0-3        SPI mode (default 0)\n"
					"  --gpiochip PATH       GPIO chip device (default /dev/gpiochip0)\n"
					"  --tfr-pin N           TfrRdy line offset (default 25)\n"
					"  --dap-pin N           DataAvailable line offset (default 24)\n"
					"  --out-pin N           scope-trigger output line: high while data is staged,\n"
					"                        low when the transfer completes (default: disabled)\n"
					"  --core N              isolated core for the interface thread (default 3)\n"
					"  --no-isolate          do not pin threads\n"
					"  --no-rt               do not use SCHED_FIFO real-time scheduling\n"
					"  --if-prio N           interface RT priority (default 50)\n"
					"  --rate HZ             producer cycle rate (default 1000)\n"
					"  --msgs-per-cycle N    movement messages queued per cycle (default 4, like MotionService)\n"
					"  --dst N               CAN destination address (default 2)\n"
					"  --producer-core N     pin the producer thread to this core (default: unpinned)\n"
					"  --producer-prio N     producer SCHED_FIFO priority when real-time (default 30)\n"
					"  --seconds S           run duration, 0 = until Ctrl-C (default 0)\n"
					"  --drop-last N         discard the last N samples from the report to avoid shutdown\n"
					"                        artifacts (default 16)\n"
					"  -h, --help            show this help\n",
					argv0);
	}

} // namespace

int main(int argc, char** argv)
{
	Config config;
	double rateHz = 1000.0;
	int seconds = 0;
	int msgsPerCycle = 1;
	int dstAddress = 2;
	int dropFirst = 16;
	int dropLast = 16;
	int producerCore = -1;
	int producerPrio = 30;
	int msgType = 8;
	bool throwOnError = false;

	auto needArg = [&](int& i) -> const char*
	{
		if (i + 1 >= argc)
		{
			std::fprintf(stderr, "Missing argument for %s\n", argv[i]);
			std::exit(2);
		}
		return argv[++i];
	};

	for (int i = 1; i < argc; i++)
	{
		const std::string a = argv[i];
		if (a == "--spi-dev")
			config.spiDevice = needArg(i);
		else if (a == "--spi-hz")
			config.spiFrequency = static_cast<uint32_t>(std::stoul(needArg(i)));
		else if (a == "--spi-mode")
			config.spiTransferMode = std::stoi(needArg(i));
		else if (a == "--gpiochip")
			config.gpioChipDevice = needArg(i);
		else if (a == "--tfr-pin")
			config.transferReadyPin = std::stoi(needArg(i));
		else if (a == "--dap-pin")
			config.dataAvailablePin = std::stoi(needArg(i));
		else if (a == "--out-pin")
			config.sbcDataAvailablePin = std::stoi(needArg(i));
		else if (a == "--core")
			config.isolatedCoreId = std::stoi(needArg(i));
		else if (a == "--no-isolate")
			config.isolateInterfaceThread = false;
		else if (a == "--no-rt")
			config.useRealtimeScheduling = false;
		else if (a == "--if-prio")
			config.interfaceRtPriority = std::stoi(needArg(i));
		else if (a == "--rate")
			rateHz = std::stod(needArg(i));
		else if (a == "--msgs-per-cycle")
			msgsPerCycle = std::stoi(needArg(i));
		else if (a == "--dst")
			dstAddress = std::stoi(needArg(i));
		else if (a == "--producer-core")
			producerCore = std::stoi(needArg(i));
		else if (a == "--producer-prio")
			producerPrio = std::stoi(needArg(i));
		else if (a == "--seconds")
			seconds = std::stoi(needArg(i));
		else if (a == "--drop-first")
			dropFirst = std::stoi(needArg(i));
		else if (a == "--drop-last")
			dropLast = std::stoi(needArg(i));
		else if (a == "--msg-type")
			msgType = std::stoi(needArg(i));
		else if (a == "--throw-on-error")
			throwOnError = true;
		else if (a == "-h" || a == "--help")
		{
			PrintUsage(argv[0]);
			return 0;
		}
		else
		{
			std::fprintf(stderr, "Unknown option: %s\n", a.c_str());
			PrintUsage(argv[0]);
			return 2;
		}
	}

	gSamples.assign(kMaxSamples, 0);

	std::signal(SIGINT, OnSignal);
	std::signal(SIGTERM, OnSignal);

	std::printf("SBC SPI jitter test\n");
	std::printf("  spi=%s @ %u Hz mode %d\n", config.spiDevice.c_str(), config.spiFrequency, config.spiTransferMode);
	std::printf("  gpio=%s tfr=%d dap=%d out=%d\n",
				config.gpioChipDevice.c_str(),
				config.transferReadyPin,
				config.dataAvailablePin,
				config.sbcDataAvailablePin);
	std::printf("  isolate=%d core=%d rt=%d if-prio=%d\n",
				config.isolateInterfaceThread,
				config.isolatedCoreId,
				config.useRealtimeScheduling,
				config.interfaceRtPriority);
	std::printf("  rate=%.0f Hz msgs/cycle=%d dst=%d producer-core=%d producer-prio=%d drop-last=%d\n\n",
				rateHz,
				msgsPerCycle,
				dstAddress,
				producerCore,
				producerPrio,
				dropLast);

	try
	{
		SbcInterface interface(config);
		interface.SetRequestServedCallback(RecordSample);

		std::printf("Connecting to firmware...\n");
		interface.Connect();
		std::printf("Connected (protocol version %d). Running -- Ctrl-C to stop.\n\n",
					interface.Transfer().ProtocolVersion());

		interface.Start();

		// Drain the inbound ring. DuetControlServer does the same from a managed dispatcher thread;
		// here it just reports messages and recovery events. Deliberately a separate, normal-priority
		// thread so printing never runs on the real-time interface thread.
		std::thread reporter(
			[&]
			{
				Duet::Sbc::RingBuffer& inbound = interface.Inbound();
				while (gRunning.load(std::memory_order_relaxed))
				{
					const uint8_t* record = nullptr;
					uint32_t length = 0;
					if (!inbound.Peek(record, length))
					{
						std::this_thread::sleep_for(std::chrono::milliseconds(2));
						continue;
					}

					Duet::Sbc::InboundEventHeader header{};
					if (length >= sizeof(header))
					{
						std::memcpy(&header, record, sizeof(header));
						switch (static_cast<Duet::Sbc::InboundEventType>(header.type))
						{
						case Duet::Sbc::InboundEventType::Message:
						{
							Duet::Sbc::MessageEvent event{};
							std::memcpy(&event, record, sizeof(event));
							std::printf("[msg 0x%08x] %.*s\n",
										event.flags,
										static_cast<int>(length - sizeof(event)),
										reinterpret_cast<const char*>(record) + sizeof(event));
							break;
						}
						case Duet::Sbc::InboundEventType::Log:
						{
							Duet::Sbc::LogEvent event{};
							std::memcpy(&event, record, sizeof(event));
							std::fprintf(stderr,
										 "[recover] %.*s\n",
										 static_cast<int>(length - sizeof(event)),
										 reinterpret_cast<const char*>(record) + sizeof(event));
							if (throwOnError)
							{
								gRunning.store(false);
							}
							break;
						}
						case Duet::Sbc::InboundEventType::ConnectionLost:
							std::fprintf(stderr, "[link] connection lost\n");
							break;
						case Duet::Sbc::InboundEventType::ConnectionEstablished:
							std::fprintf(stderr, "[link] connection established\n");
							break;
						default:
							break;
						}
					}
					inbound.Consume();
				}
			});

		// Producer: queue a batch of CanMessageMovementLinearShaped per cycle, like MotionService.cs.
		std::thread producer(
			[&]
			{
				// Make the producer reliable so it does not stall and open a keep-alive-sized gap between
				// transfers: pin it (if requested) and run it real-time below the interface priority
				if (IsRaspberryPi())
				{
					if (producerCore >= 0)
					{
						PinCurrentThreadToCore(producerCore);
					}
					if (config.useRealtimeScheduling)
					{
						SetCurrentThreadRealtimePriority(producerPrio);
					}
				}

				const auto period =
					std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::duration<double>(1.0 / rateHz));
				auto next = std::chrono::steady_clock::now();
				while (gRunning.load(std::memory_order_relaxed))
				{
					for (int k = 0; k < msgsPerCycle; k++)
					{
						switch (msgType)
						{
						case 8:
						{
							static constexpr char kGreeting[] = "Hello from SBC harness";
							interface.QueueMessage(0, kGreeting, sizeof(kGreeting) - 1);
							break;
						}
						default:
							std::fprintf(stderr, "Unknown message type %d\n", msgType);
							gRunning.store(false);
							break;
						}
					}
					next += period;
					std::this_thread::sleep_until(next);
				}
			});

		const auto startTime = std::chrono::steady_clock::now();
		while (gRunning.load(std::memory_order_relaxed))
		{
			std::this_thread::sleep_for(std::chrono::milliseconds(100));
			if (seconds > 0 && std::chrono::steady_clock::now() - startTime >= std::chrono::seconds(seconds))
			{
				gRunning.store(false);
			}
		}

		// Clean shutdown: stop producing first, let in-flight transfers drain, then stop the loop.
		// The last few transfers can be perturbed by teardown, so their samples are dropped below.
		producer.join();
		std::this_thread::sleep_for(std::chrono::milliseconds(50));
		interface.Stop();
		reporter.join();

		// ---- Report ----
		const size_t count = std::min(gSampleIndex.load(), kMaxSamples);
		const size_t drop = (dropLast > 0) ? static_cast<size_t>(dropLast) : 0;
		const size_t dropF = (dropFirst > 0) ? static_cast<size_t>(dropFirst) : 0;
		const size_t used = (count > drop + dropF) ? (count - drop - dropF) : 0;
		std::vector<int64_t> samples(gSamples.begin() + dropF, gSamples.begin() + dropF + used);
		std::sort(samples.begin(), samples.end());

		std::printf("\n==== Results (%zu request-driven transfers, last %zu dropped) ====\n", used, count - used);
		if (!samples.empty())
		{
			double sum = 0;
			for (const int64_t s : samples)
				sum += static_cast<double>(s);
			const double meanUs = sum / static_cast<double>(samples.size()) / 1000.0;
			auto us = [](int64_t ns) { return static_cast<double>(ns) / 1000.0; };
			std::printf("  RequestTransfer -> transfer complete latency:\n");
			std::printf("    mean   : %10.2f us\n", meanUs);
			std::printf("    min    : %10.2f us\n", us(samples.front()));
			std::printf("    p50    : %10.2f us\n", us(Percentile(samples, 50)));
			std::printf("    p90    : %10.2f us\n", us(Percentile(samples, 90)));
			std::printf("    p99    : %10.2f us\n", us(Percentile(samples, 99)));
			std::printf("    p99.9  : %10.2f us\n", us(Percentile(samples, 99.9)));
			std::printf("    p99.99 : %10.2f us\n", us(Percentile(samples, 99.99)));
			std::printf("    max    : %10.2f us\n", us(samples.back()));
		}
		std::printf("  Max pin wait during a transfer : %.3f ms\n", interface.Transfer().MaxPinWaitDurationMs());
		std::printf("  Max delay between transfers    : %.3f ms\n", interface.Transfer().MaxFullTransferDelayMs());
		std::printf("  TfrRdy pin glitches            : %d\n", interface.Transfer().TfrPinGlitches());
		std::printf("  Missed GPIO edges              : %d\n", interface.Transfer().MissedEdges());
		std::printf("  Connection resyncs (recoveries): %d\n", interface.Transfer().ResyncCount());
		if (gSampleIndex.load() > kMaxSamples)
		{
			std::printf("  NOTE: sample buffer capped at %zu; increase kMaxSamples for longer runs.\n", kMaxSamples);
		}
	}
	catch (const std::exception& e)
	{
		std::fprintf(stderr, "Fatal: %s\n", e.what());
		return 1;
	}

	return 0;
}
