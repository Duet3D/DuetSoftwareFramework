using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// <para>
/// What fills <c>boards[]</c>: an expansion board's announcement and status report for every entry
/// past the first, and the controller's own two reports for <c>boards[0]</c>.
/// </para>
/// <para>
/// The two halves are asserted together because they answer the same question from opposite ends.
/// An expansion board announces a type and a version and nothing else, so its name and the firmware
/// binary it takes are derived here exactly as <c>ExpressionValue::ExtractRequestedPart</c> derives
/// them in RepRapFirmware. The controller is not on the CAN bus at all, so it has no announcement
/// and no board status report to broadcast, and says both over the link instead.
/// </para>
/// </summary>
[TestFixture]
public class BoardIdentityTests : SystemTests.Host.BenchFixture
{
    /// <summary>CAN address of the board the bench's configuration already creates</summary>
    private const byte ConfiguredBoard = 1;

    /// <summary>A CAN address nothing in the bench's configuration mentions</summary>
    private const byte NewBoard = 3;

    /// <summary>A unique id to announce, so that the formatting it arrives in can be asserted</summary>
    private static readonly byte[] UniqueId =
        [0x69, 0x49, 0xf3, 0x58, 0x59, 0x4d, 0x32, 0x53, 0x20, 0x20, 0x20, 0x46, 0x38, 0x42, 0x0e, 0xff];

    /// <summary>
    /// A board that announces itself gets the full name and firmware binary its type implies, not
    /// just the type it named
    /// </summary>
    /// <remarks>
    /// Duet3Expansion sends <c>"&lt;type&gt;|&lt;version&gt;|&lt;date&gt;"</c> and nothing else.
    /// RepRapFirmware turns the type into <c>name</c> and <c>firmwareFileName</c> on the way out of
    /// its object model; here they are stored, which is the same derivation in a different place.
    /// <c>firmwareFileName</c> is not cosmetic: it is what <c>FirmwareUpdater</c> looks a board up by,
    /// so a board without one is a board that can never be found to be out of date
    /// </remarks>
    [Test]
    public async Task AnAnnouncementFillsTheNameAndTheFirmwareBinary()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.InjectAnnounce(NewBoard, "EXP3HC", "3.7.0-rc.1", "2026-09-08 15:41",
                                       numDrivers: 3, UniqueId);

        Board board = await bench.Host.WaitForModelAsync(model => model.FindBoard(NewBoard)!,
                                                         found => found?.State == BoardState.Running,
                                                         $"board {NewBoard} announced itself");
        Assert.Multiple(() =>
        {
            Assert.That(board.ShortName, Is.EqualTo("EXP3HC"), "the type the board named");
            Assert.That(board.Name, Is.EqualTo("Duet 3 Expansion EXP3HC"),
                        "ExpansionDetail::longName prefixes the type with 'Duet 3 Expansion '");
            Assert.That(board.FirmwareFileName, Is.EqualTo("Duet3Firmware_EXP3HC.bin"),
                        "ExpansionDetail::firmwareFileNameBin, which is what the firmware updater looks the board up by");
            Assert.That(board.FirmwareVersion, Is.EqualTo("3.7.0-rc.1"));
            Assert.That(board.FirmwareDate, Is.EqualTo("2026-09-08 15:41"));
            Assert.That(board.MaxMotors, Is.EqualTo(3), "the driver count the announcement carried");
            Assert.That(board.Drivers, Has.Count.EqualTo(3), "one drivers[] entry per driver announced");
            Assert.That(board.UniqueId, Is.EqualTo("6949f358594d32532020204638420eff"),
                        "the raw id as lower-case hex, the same spelling every other board's id is shown in");
        });
    }

    /// <summary>
    /// A board that says its firmware is a .uf2 gets a .uf2 file name
    /// </summary>
    /// <remarks>
    /// <c>usesUf2Binary</c> rides in <c>CanMessageAnnounceV1</c>. Getting this wrong points the
    /// firmware updater at a file that does not exist for the boards it applies to, which is every
    /// RP2040-based one
    /// </remarks>
    [Test]
    public async Task ABoardThatTakesAUf2BinaryIsNamedAsOne()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.InjectAnnounce(NewBoard, "EXP1HCL", "3.7.0-rc.1", "2026-09-08",
                                       numDrivers: 1, uniqueId: null, usesUf2Binary: true);

        Board board = await bench.Host.WaitForModelAsync(model => model.FindBoard(NewBoard)!,
                                                         found => found?.State == BoardState.Running,
                                                         $"board {NewBoard} announced itself");
        Assert.That(board.FirmwareFileName, Is.EqualTo("Duet3Firmware_EXP1HCL.uf2"));
    }

    /// <summary>
    /// The Mini 5+ takes a .uf2 even though its announcement does not say so
    /// </summary>
    /// <remarks>
    /// It predates the <c>usesUf2Binary</c> flag, and <c>ExpressionValue::ExtractRequestedPart</c>
    /// special-cases the same name for the same reason
    /// </remarks>
    [Test]
    public async Task TheMini5PlusTakesAUf2WithoutClaimingTo()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.InjectAnnounce(NewBoard, "Mini5plus", "3.7.0-rc.1", "2026-09-08",
                                       numDrivers: 5, uniqueId: null, usesUf2Binary: false);

        Board board = await bench.Host.WaitForModelAsync(model => model.FindBoard(NewBoard)!,
                                                         found => found?.State == BoardState.Running,
                                                         $"board {NewBoard} announced itself");
        Assert.That(board.FirmwareFileName, Is.EqualTo("Duet3Firmware_Mini5plus.uf2"));
    }

    /// <summary>
    /// A board with no type to announce gets no name and no firmware binary, rather than ones built
    /// out of nothing
    /// </summary>
    /// <remarks>
    /// A board announces an empty type while its firmware is being replaced, which RepRapFirmware
    /// allows for as well (<c>ExtractRequestedPart</c> guards against a null type name). The failure
    /// this guards against is <c>Duet3Firmware_.bin</c> reaching the firmware updater as a file to
    /// look for
    /// </remarks>
    [Test]
    public async Task ABoardThatNamesNoTypeGetsNoDerivedNames()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.InjectAnnounce(NewBoard, string.Empty, "3.7.0-rc.1", "2026-09-08", numDrivers: 0);

        Board board = await bench.Host.WaitForModelAsync(model => model.FindBoard(NewBoard)!,
                                                         found => found?.State == BoardState.Running,
                                                         $"board {NewBoard} announced itself");
        Assert.Multiple(() =>
        {
            Assert.That(board.Name, Is.Empty);
            Assert.That(board.FirmwareFileName, Is.Empty);
        });
    }

    /// <summary>
    /// Boards discovered out of order still sit in <c>boards[]</c> in CAN address order
    /// </summary>
    /// <remarks>
    /// An index into <c>boards[]</c> only means something if the order does: a client reading
    /// <c>boards[2]</c> has to be reading the same board on every run, and on a machine with no gaps
    /// in its addresses it is the board at address 2, which is what a reference recorded against
    /// RepRapFirmware compares against. Boards announce themselves in whatever order they finish
    /// booting in, so discovery order is not that
    /// </remarks>
    [Test]
    public async Task BoardsSitInCanAddressOrderHoweverTheyWereDiscovered()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        // Address 1 is already there from the bench's configuration, so announcing 3 before 2 is a
        // board arriving behind one that sorts after it
        bench.CanMaster.InjectAnnounce(3, "EXP3HC", "3.7.0-rc.1", "2026-09-08", numDrivers: 3);
        bench.CanMaster.InjectAnnounce(2, "EXP1HCL", "3.7.0-rc.1", "2026-09-08", numDrivers: 1);

        int[] addresses = await bench.Host.WaitForModelAsync(
            model => model.Boards.Select(board => board.CanAddress ?? -1).ToArray(),
            found => found.Length == 4, "both boards announced themselves");
        Assert.That(addresses, Is.EqualTo(new[] { 0, 1, 2, 3 }),
                    "the controller first, then the expansion boards in ascending CAN address order");
    }

    /// <summary>
    /// The fields that belong to the main board alone are absent on an expansion board, and the one
    /// that belongs to an expansion board alone is absent on the main board
    /// </summary>
    /// <remarks>
    /// RepRapFirmware serves <c>boards[]</c> from two object model tables that share no keys:
    /// <c>Platform</c>'s for <c>boards[0]</c>, which has <c>firmwareName</c>, <c>maxHeaters</c> and
    /// <c>supportsDirectDisplay</c>, and <c>ExpansionManager</c>'s for the rest, which has
    /// <c>state</c> and <c>timeout</c>. One <see cref="Board"/> class serves both here, so the fields
    /// only one of them has must be null on the other; the <c>om-default-boards</c> regression case
    /// caught all four publishing their defaults on every board
    /// </remarks>
    [Test]
    public async Task EachBoardReportsOnlyTheFieldsItsKindHas()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareController: controller =>
            controller.InjectControllerBoardInfo(name: "Duet 3 MB6HC", shortName: "MB6HC",
                                                 firmwareName: "RepRapFirmware for Duet 3 MB6HC"));

        bench.CanMaster.InjectAnnounce(NewBoard, "EXP3HC", "3.7.0-rc.1", "2026-09-08", numDrivers: 3);

        Board controllerBoard = await bench.Host.WaitForModelAsync(model => model.FindBoard(0)!,
                                                                    found => !string.IsNullOrEmpty(found?.ShortName),
                                                                    "the controller said what board it is");
        Board expansion = await bench.Host.WaitForModelAsync(model => model.FindBoard(NewBoard)!,
                                                              found => found?.State == BoardState.Running,
                                                              $"board {NewBoard} announced itself");
        Assert.Multiple(() =>
        {
            Assert.That(controllerBoard.FirmwareName, Is.EqualTo("RepRapFirmware for Duet 3 MB6HC"));
            Assert.That(controllerBoard.MaxHeaters, Is.Zero, "the main board has the field, and drives no heaters");
            Assert.That(controllerBoard.SupportsDirectDisplay, Is.False, "it has the field, and drives no display");
            Assert.That(controllerBoard.Timeout, Is.Null,
                        "the connection timeout belongs to a board on the CAN bus, which this is not");

            Assert.That(expansion.FirmwareName, Is.Null, "an expansion board never names its firmware");
            Assert.That(expansion.MaxHeaters, Is.Null, "nor says how many heaters it could drive");
            Assert.That(expansion.SupportsDirectDisplay, Is.Null, "nor whether it could drive a display");
            Assert.That(expansion.Timeout, Is.EqualTo(Board.DefaultConnectionTimeoutSeconds),
                        "but it does have a connection timeout, which is what M959 sets");
        });
    }

    /// <summary>
    /// <c>boards[0].state</c> is the state of the link to the controller, and follows it down
    /// </summary>
    /// <remarks>
    /// RepRapFirmware reports no state for <c>boards[0]</c> because there the object model and the
    /// main board are one program, so it cannot be absent. Here the controller is a separate board
    /// across SPI and can be, which is the whole reason the field is worth having: pinned to
    /// <c>running</c> it would say the controller is there while the link is down
    /// </remarks>
    [Test]
    [Category("LongRunning")]
    public async Task TheControllerStateFollowsTheLink()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Assert.That(await bench.Host.ReadModelAsync(model => model.FindBoard(0)!.State),
                    Is.EqualTo(BoardState.Running), "the link is up");

        // Withholding readiness is a controller that has stopped answering, which is what the link
        // times out on
        bench.CanMaster.PauseArming();
        try
        {
            await bench.Host.WaitForModelAsync(model => model.FindBoard(0)?.State,
                                               state => state == BoardState.TimedOut,
                                               "the controller stopped answering");
        }
        finally
        {
            bench.CanMaster.ResumeArming();
        }

        await bench.Host.WaitForModelAsync(model => model.FindBoard(0)?.State,
                                           state => state == BoardState.Running,
                                           "the controller started answering again");
    }

    /// <summary>
    /// A board status report publishes the readings the board has and the features it claims
    /// </summary>
    /// <remarks>
    /// The three feature flags are what put <c>accelerometer</c>, <c>closedLoop</c> and
    /// <c>inductiveSensor</c> in <c>boards[]</c>; their presence is the whole of what tells a client
    /// the board has one, because nothing else in the object model says so
    /// </remarks>
    [Test]
    public async Task ABoardStatusReportPublishesTheReadingsAndTheFeatures()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.InjectBoardStatus(ConfiguredBoard, neverUsedRam: 167852,
                                          vIn: 24.0f, v12: 12.0f, mcuTemp: 33.5f,
                                          hasAccelerometer: true, hasClosedLoop: true);

        Board board = await bench.Host.WaitForModelAsync(model => model.FindBoard(ConfiguredBoard)!,
                                                         found => found?.McuTemp is not null,
                                                         $"board {ConfiguredBoard} reported its status");
        Assert.Multiple(() =>
        {
            Assert.That(board.FreeRam, Is.EqualTo(167852));
            Assert.That(board.VIn!.Current, Is.EqualTo(24.0f).Within(0.05f));
            Assert.That(board.V12!.Current, Is.EqualTo(12.0f).Within(0.05f));
            Assert.That(board.McuTemp!.Current, Is.EqualTo(33.5f).Within(0.05f));
            Assert.That(board.Accelerometer, Is.Not.Null, "the board said it has one");
            Assert.That(board.ClosedLoop, Is.Not.Null, "and that it supports closed loop control");
            Assert.That(board.InductiveSensor, Is.Null, "and said nothing about an inductive sensor");
        });
    }

    /// <summary>
    /// A feature the board stops claiming is taken away again
    /// </summary>
    /// <remarks>
    /// <c>ExpansionManager::ProcessBoardStatusReport</c> assigns all three flags on every report
    /// rather than only setting them. A board that is flashed with firmware built without the
    /// accelerometer, or whose sensor fails to initialise, stops claiming it, and an entry that
    /// stayed would tell a client to offer a feature that is no longer there
    /// </remarks>
    [Test]
    public async Task AFeatureTheBoardStopsClaimingGoesAway()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.InjectBoardStatus(ConfiguredBoard, mcuTemp: 30.0f, hasAccelerometer: true);
        await bench.Host.WaitForModelAsync(model => model.FindBoard(ConfiguredBoard)?.Accelerometer,
                                           accelerometer => accelerometer is not null,
                                           $"board {ConfiguredBoard} claimed an accelerometer");

        bench.CanMaster.InjectBoardStatus(ConfiguredBoard, mcuTemp: 31.0f, hasAccelerometer: false);
        await bench.Host.WaitForModelAsync(model => model.FindBoard(ConfiguredBoard)?.Accelerometer,
                                           accelerometer => accelerometer is null,
                                           $"board {ConfiguredBoard} stopped claiming an accelerometer");
    }

    /// <summary>
    /// A reading a board has no hardware for stays null rather than reading zero
    /// </summary>
    /// <remarks>
    /// The readings are packed in a fixed order and only the present ones take a slot, so a report
    /// with no 12V rail puts the MCU temperature where the 12V reading would have been. Reading them
    /// by position instead of by flag publishes 12 volts as an MCU temperature
    /// </remarks>
    [Test]
    public async Task AReadingTheBoardCannotTakeStaysNull()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.InjectBoardStatus(ConfiguredBoard, vIn: 24.0f, mcuTemp: 40.0f);

        Board board = await bench.Host.WaitForModelAsync(model => model.FindBoard(ConfiguredBoard)!,
                                                         found => found?.McuTemp is not null,
                                                         $"board {ConfiguredBoard} reported its status");
        Assert.Multiple(() =>
        {
            Assert.That(board.V12, Is.Null, "the board reported no 12V rail");
            Assert.That(board.VIn!.Current, Is.EqualTo(24.0f).Within(0.05f));
            Assert.That(board.McuTemp!.Current, Is.EqualTo(40.0f).Within(0.05f),
                        "and its MCU temperature is still its own, not the reading that would have followed VIN");
        });
    }

    /// <summary>
    /// The controller says what board it is when the link comes up, and that is what fills
    /// <c>boards[0]</c>
    /// </summary>
    /// <remarks>
    /// <c>boards[0]</c> is DuetCANMaster, which is not on the CAN bus and so can neither announce
    /// itself nor broadcast a board status report. Without this packet the entry carries its CAN
    /// address and nothing else, and every client that reads <c>boards[0]</c> to find out what
    /// machine it is talking to finds an empty name and an empty firmware version
    /// </remarks>
    [Test]
    public async Task TheControllerSaysWhatBoardItIs()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareController: controller =>
            controller.InjectControllerBoardInfo(
                name: "Duet 3 MB6HC", shortName: "MB6HC",
                firmwareName: "RepRapFirmware for Duet 3 MB6HC", firmwareVersion: "4.0.0-alpha.1",
                firmwareDate: "2026-09-18", firmwareFileName: "Duet3Firmware_MB6HC.bin",
                iapFileNameSbc: "Duet3_SBCiap32_MB6HC.bin", iapFileNameSd: "Duet3_SDiap32_MB6HC.bin",
                uniqueId: UniqueId));

        Board board = await bench.Host.WaitForModelAsync(model => model.FindBoard(0)!,
                                                         found => !string.IsNullOrEmpty(found?.ShortName),
                                                         "the controller said what board it is");
        Assert.Multiple(() =>
        {
            Assert.That(board.Name, Is.EqualTo("Duet 3 MB6HC"));
            Assert.That(board.ShortName, Is.EqualTo("MB6HC"));
            Assert.That(board.FirmwareName, Is.EqualTo("RepRapFirmware for Duet 3 MB6HC"));
            Assert.That(board.FirmwareVersion, Is.EqualTo("4.0.0-alpha.1"));
            Assert.That(board.FirmwareDate, Is.EqualTo("2026-09-18"));
            Assert.That(board.FirmwareFileName, Is.EqualTo("Duet3Firmware_MB6HC.bin"));
            Assert.That(board.IapFileNameSBC, Is.EqualTo("Duet3_SBCiap32_MB6HC.bin"));
            Assert.That(board.IapFileNameSD, Is.EqualTo("Duet3_SDiap32_MB6HC.bin"));
            Assert.That(board.UniqueId, Is.EqualTo("6949f358594d32532020204638420eff"));
            Assert.That(board.State, Is.EqualTo(BoardState.Running));
        });
    }

    /// <summary>
    /// What the controller can drive is stated on this side, not taken from what it said
    /// </summary>
    /// <remarks>
    /// DuetCANMaster bridges SPI to CAN-FD and keeps the master step clock. It will never drive a
    /// motor, a heater or a display, so these three are not on the wire at all and a controller that
    /// says nothing about itself still reports them correctly
    /// </remarks>
    [Test]
    public async Task TheControllerDrivesNoMotorHeaterOrDisplay()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareController: controller =>
            controller.InjectControllerBoardInfo(name: "Duet 3 MB6HC", shortName: "MB6HC"));

        Board board = await bench.Host.WaitForModelAsync(model => model.FindBoard(0)!,
                                                         found => !string.IsNullOrEmpty(found?.ShortName),
                                                         "the controller said what board it is");
        Assert.Multiple(() =>
        {
            Assert.That(board.MaxMotors, Is.Zero, "every driver of the machine is on an expansion board");
            Assert.That(board.MaxHeaters, Is.Zero, "and so is every heater");
            Assert.That(board.SupportsDirectDisplay, Is.False, "and there is no display to drive");
        });
    }

    /// <summary>
    /// A file the controller has no in-application programmer for is null rather than empty
    /// </summary>
    /// <remarks>
    /// The two are different answers to "can this board be updated that way": the object model reads
    /// null as no, and an empty string as a file whose name is nothing
    /// </remarks>
    [Test]
    public async Task AnUpdatePathTheControllerDoesNotHaveIsNull()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareController: controller =>
            controller.InjectControllerBoardInfo(name: "Duet 3 MB6HC", shortName: "MB6HC",
                                                 iapFileNameSbc: "Duet3_SBCiap32_MB6HC.bin"));

        Board board = await bench.Host.WaitForModelAsync(model => model.FindBoard(0)!,
                                                         found => !string.IsNullOrEmpty(found?.ShortName),
                                                         "the controller said what board it is");
        Assert.Multiple(() =>
        {
            Assert.That(board.IapFileNameSBC, Is.EqualTo("Duet3_SBCiap32_MB6HC.bin"));
            Assert.That(board.IapFileNameSD, Is.Null, "there is no SD card to flash this board from");
        });
    }

    /// <summary>
    /// The controller reports its own voltages, MCU temperature and free memory the way every other
    /// board broadcasts them
    /// </summary>
    [Test]
    public async Task TheControllerReportsItsOwnHealth()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.InjectControllerBoardStatus(neverUsedRam: 83652, mcuTemp: 46.3f, vIn: 23.5f, v12: 12.0f);

        Board board = await bench.Host.WaitForModelAsync(model => model.FindBoard(0)!,
                                                         found => found?.McuTemp is not null,
                                                         "the controller reported its health");
        Assert.Multiple(() =>
        {
            Assert.That(board.FreeRam, Is.EqualTo(83652));
            Assert.That(board.McuTemp!.Current, Is.EqualTo(46.3f).Within(0.01f));
            Assert.That(board.VIn!.Current, Is.EqualTo(23.5f).Within(0.01f));
            Assert.That(board.V12!.Current, Is.EqualTo(12.0f).Within(0.01f));
        });
    }

    /// <summary>
    /// A reading the controller has no hardware for stays null, as it does for an expansion board
    /// </summary>
    [Test]
    public async Task AReadingTheControllerCannotTakeStaysNull()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.InjectControllerBoardStatus(neverUsedRam: 83652, mcuTemp: 46.3f, vIn: 23.5f);

        Board board = await bench.Host.WaitForModelAsync(model => model.FindBoard(0)!,
                                                         found => found?.McuTemp is not null,
                                                         "the controller reported its health");
        Assert.That(board.V12, Is.Null, "the controller reported no 12V rail");
    }
}
