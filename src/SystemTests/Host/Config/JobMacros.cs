namespace SystemTests.Host.Config;

/// <summary>The job system macros, instrumented so a scenario can tell which of them ran</summary>
internal static class JobMacros
{
    /// <summary>
    /// The globals the instrumented macros count their runs in, created by config.g so a scenario
    /// can read them before any macro ran
    /// </summary>
    private const string Counters = """
        global startRan = 0
        global stopRan = 0
        global pauseRan = 0
        global resumeRan = 0
        global cancelRan = 0
        global filChangeRan = 0
        global macroRuns = 0
        """;

    /// <summary>
    /// start.g, stop.g, pause.g, resume.g, cancel.g and filament-change.g, each counting its runs in a
    /// global. pause.g also parks at X0 Y0, so a scenario can tell the restore point (taken before
    /// the park) from where pause.g leaves the machine
    /// </summary>
    public static readonly ConfigPart Instrumented = new(Counters, files: sd =>
    {
        sd.WriteSys("start.g", "set global.startRan = global.startRan + 1\n");
        sd.WriteSys("stop.g", "set global.stopRan = global.stopRan + 1\n");
        sd.WriteSys("pause.g", "set global.pauseRan = global.pauseRan + 1\nG90\nG1 X0 Y0 F6000\n");
        sd.WriteSys("resume.g", "set global.resumeRan = global.resumeRan + 1\n");
        sd.WriteSys("cancel.g", "set global.cancelRan = global.cancelRan + 1\n");
        sd.WriteSys("filament-change.g", "set global.filChangeRan = global.filChangeRan + 1\n");
    });
}
