using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using DuetControlServer;
using DuetControlServer.Files;
using DuetControlServer.Link.Protocol;
using DuetControlServer.Link.Protocol.SbcRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using SpiAdapter = DuetControlServer.Link.Adapter.SPI;

namespace UnitTests.SPI;

/// <summary>
/// Tests for the SPI adapter when the connection to RRF is lost
/// </summary>
/// <remarks>
/// The adapter opens spidev and the GPIO chip in its constructor, so it is created without one and gets its fields set here.
/// The transfer ready pin has no event fd, so every wait for TfrRdy times out like it does while RRF restarts
/// </remarks>
[TestFixture]
public class LostConnection
{
    private ServiceProvider _provider = null!;
    private InputGpioPin _transferReadyPin = null!;

    /// <summary>
    /// Logger that stops the transfer once the adapter has handled the lost connection
    /// </summary>
    private sealed class CancelOnLostConnection(CancellationTokenSource cts) : ILogger<SpiAdapter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception) == "Lost connection to Duet")
            {
                cts.Cancel();
            }
        }
    }

    [SetUp]
    public void SetUp()
    {
        ServiceCollection services = new();
        services
            .AddLogging()
            .AddSingleton(Options.Create(new Settings()))
            .AddSingleton<IHostApplicationLifetime, ApplicationLifetime>()
            .AddFiles()
            .AddModel()
            .AddUtility();
        _provider = services.BuildServiceProvider();

        // Without fds of -1 the finalizer would close stdin
        _transferReadyPin = (InputGpioPin)RuntimeHelpers.GetUninitializedObject(typeof(InputGpioPin));
        SetField(_transferReadyPin, "_chipFd", -1);
        SetField(_transferReadyPin, "_reqFd", -1);
    }

    [TearDown]
    public void TearDown()
    {
        _transferReadyPin.Dispose();
        _provider.Dispose();
    }

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    [TestCase(true, TestName = "BadProtocolVersionOnFirstLoss")]
    [TestCase(false, TestName = "BadProtocolVersionOnLaterRetry")]
    public void HeaderRewrittenAfterBadProtocolVersion(bool connected)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Settings settings = new() { SbcConnectTimeout = 10, SbcConnectionTimeout = 10 };

        SpiAdapter spi = (SpiAdapter)RuntimeHelpers.GetUninitializedObject(typeof(SpiAdapter));
        SetField(spi, "_eventLogger", _provider.GetRequiredService<EventLogger>());
        SetField(spi, "_model", _provider.GetRequiredService<ObjectModel>());
        SetField(spi, "_logger", new CancelOnLostConnection(cts));
        SetField(spi, "_settings", settings);
        SetField(spi, "_transferReadyPin", _transferReadyPin);
        SetField(spi, "_rxHeaderBuffer", (Memory<byte>)new byte[Marshal.SizeOf<TransferHeader>()]);
        SetField(spi, "_txHeaderBuffer", (Memory<byte>)new byte[Marshal.SizeOf<TransferHeader>()]);
        SetField(spi, "_txBuffer", new LinkedList<Memory<byte>>([new byte[settings.SbcBufferSize]]).First);
        SetField(spi, "_packetsBeingResent", new List<Request>());
        SetField(spi, "_fullTransferStopwatch", new Stopwatch());
        if (connected)
        {
            // RRF restarts while the link is up, so TfrRdy never goes high for the next header
            SetField(spi, "_connected", true);
            SetField(spi, "_expectedTfrRdyPinValue", true);
        }
        else
        {
            // The connection was already lost and DSF keeps retrying
            SetField(spi, "_hadTimeout", true);
            SetField(spi, "_waitingForFirstTransfer", true);
        }

        // ExchangeHeader has adopted the protocol version of a bad header from the booting RRF
        TransferHeader txHeader = new();
        Writer.InitTransferHeader(ref txHeader);
        txHeader.ProtocolVersion = 3;
        SetField(spi, "_txHeader", txHeader);

        // The next wait for TfrRdy times out and the adapter handles the lost connection once
        spi.PerformFullTransfer(false, cts.Token);

        // RRF must receive a header with the current protocol version and a valid checksum, else it rejects every retry
        Memory<byte> txHeaderBuffer = (Memory<byte>)GetField(spi, "_txHeaderBuffer")!;
        TransferHeader sentHeader = MemoryMarshal.Read<TransferHeader>(txHeaderBuffer.Span);
        Assert.That(sentHeader.ProtocolVersion, Is.EqualTo(Consts.ProtocolVersion));
        Assert.That(sentHeader.ChecksumHeader32, Is.EqualTo(CRC32.Calculate(txHeaderBuffer[..12].Span)));
        Assert.That(sentHeader, Is.EqualTo(GetField(spi, "_txHeader")));
    }
}
