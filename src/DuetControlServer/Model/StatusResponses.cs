using System.Collections.Generic;

#pragma warning disable IDE1006 // Naming Styles
#pragma warning disable CS1591

namespace DuetControlServer.Model
{
    public class Coords
    {
        public List<int> axesHomed { get; set; }
        public int wpl { get; set; }
        public List<float> xyz { get; set; }
        public List<float> machine { get; set; }
        public List<float> extr { get; set; }
    }

    public class Speeds
    {
        public float requested { get; set; }
        public float top { get; set; }
    }

    public class MsgBox
    {
        public string msg { get; set; }
        public string title { get; set; }
        public int mode { get; set; }
        public int seq { get; set; }
        //public float timeout { get; set; }
        public int controls { get; set; }
    }

    public class Output
    {
        public int beepDuration { get; set; }
        public int beepFrequency { get; set; }
        public string message { get; set; } = string.Empty;
        public MsgBox msgBox { get; set; }
    }

    public class Params
    {
        public int atxPower { get; set; }
        public List<int> fanPercent { get; set; }
        public List<string> fanNames { get; set; }
        public float speedFactor { get; set; }
        public List<float> extrFactors { get; set; }
        public float babystep { get; set; }
    }

    public class Sensors
    {
        public int probeValue { get; set; }
        public List<int> probeSecondary { get; set; }
        public List<int> fanRPM { get; set; }
    }

    public class SlowHeater
    {
        public float current { get; set; }
        public float active { get; set; }
        public float standby { get; set; }
        public int state { get; set; }
        public int heater { get; set; }
    }

    public class Tools
    {
        public List<float> active { get; set; }
        public List<float> standby { get; set; }
    }

    public class Extra
    {
        public string name { get; set; } = string.Empty;
        public float temp { get; set; }
    }

    public class ToolTemps
    {
        public List<List<float>> active { get; set; }
        public List<List<float>> standby { get; set; }
    }

    public class Temps
    {
        public SlowHeater bed { get; set; }
        public SlowHeater chamber { get; set; }
        public List<float> current { get; set; }
        public List<int> state { get; set; }
        public List<string> names { get; set; }
        public ToolTemps tools { get; set; }
        public List<Extra> extra { get; set; }
    }

    public class StatusResponse
    {
        public char status { get; set; }
        public Coords coords { get; set; }
        public Speeds speeds { get; set; }
        public int currentTool { get; set; }
        public Output output { get; set; }
        public Params @params { get; set; }
        public Sensors sensors { get; set; }
        public Temps temps { get; set; }
        //public float time { get; set; }
    }

    public class ProbeItem
    {
        public int threshold { get; set; }
        public float height { get; set; }
        public int type { get; set; }
    }

    public class Mcutemp
    {
        public float min { get; set; }
        public float cur { get; set; }
        public float max { get; set; }
    }

    public class Vin
    {
        public float min { get; set; }
        public float cur { get; set; }
        public float max { get; set; }
    }

    public class ToolItem
    {
        public int number { get; set; }
        public string name { get; set; } = string.Empty;
        public List<int> heaters { get; set; }
        public List<int> drives { get; set; }
        public List<List<int>> axisMap { get; set; }
        public int fans { get; set; }
        public string filament { get; set; }
        public List<float> offsets { get; set; }
    }

    public class AdvancedStatusResponse : StatusResponse
    {
        public float coldExtrudeTemp { get; set; }
        public float coldRetractTemp { get; set; }
        public string compensation { get; set; }
        public int controllableFans { get; set; }
        public float tempLimit { get; set; }
        public int endstops { get; set; }
        public string firmwareName { get; set; }
        public string geometry { get; set; }
        public int axes { get; set; }
        public int totalAxes { get; set; }
        public string axisNames { get; set; }
        public string mode { get; set; }
        public string name { get; set; }
        public ProbeItem probe { get; set; }
        public List<ToolItem> tools { get; set; }
        public Mcutemp mcutemp { get; set; }
        public Vin vin { get; set; }
    }

    public class TimesLeft
    {
        public float file { get; set; }
        public float filament { get; set; }
        public float layer { get; set; }
    }

    public class PrintStatusResponse : StatusResponse
    {
        public int currentLayer { get; set; }
        public float currentLayerTime { get; set; }
        public List<float> extrRaw { get; set; }
        public float fractionPrinted { get; set; }
        public uint filePosition { get; set; }
        public float firstLayerDuration { get; set; }
        public float firstLayerHeight { get; set; }
        public float printDuration { get; set; }
        public float warmUpDuration { get; set; }
        public TimesLeft timesLeft { get; set; }
    }

    public class ConfigResponse
    {
        public List<float> axisMins { get; set; }
        public List<float> axisMaxes { get; set; }
        public List<float> accelerations { get; set; }
        public List<int> currents { get; set; }
        public string firmwareElectronics { get; set; }
        public string firmwareName { get; set; }
        public string boardName { get; set; }
        public string firmwareVersion { get; set; }
        public string firmwareDate { get; set; }
        public float idleCurrentFactor { get; set; }
        public float idleTimeout { get; set; }
        public List<float> minFeedrates { get; set; }
        public List<float> maxFeedrates { get; set; }
    }
}
