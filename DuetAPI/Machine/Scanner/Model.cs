using System;
using System.Runtime.Serialization;

namespace DuetAPI.Machine.Scanner
{
    public enum ScannerStatus
    {
        Disconnected = 'D',
        Idle = 'I',
        Scanning = 'S',
        PostProcessing = 'P',
        Calibrating = 'C',
        Uploading = 'U'
    }

    public class Model : ICloneable
    {
        public double Progress { get; set; }
        public ScannerStatus Status { get; set; } = ScannerStatus.Disconnected;

        public object Clone()
        {
            return new Model
            {
                Progress = Progress,
                Status = Status
            };
        }
    }
}