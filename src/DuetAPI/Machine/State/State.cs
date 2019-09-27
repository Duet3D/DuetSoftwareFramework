using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the machine state
    /// </summary>
    public sealed class State : IAssignable, ICloneable, INotifyPropertyChanged
    {
        /// <summary>
        /// Event to trigger when a property has changed
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// State of the ATX power pin (if controlled)
        /// </summary>
        public bool? AtxPower
        {
            get => _atxPower;
            set
            {
                if (_atxPower != value)
                {
                    _atxPower = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool? _atxPower;

        /// <summary>
        /// Information about a requested beep
        /// </summary>
        public BeepDetails Beep { get; private set; } = new BeepDetails();
        
        /// <summary>
        /// Number of the currently selected tool or -1 if none is selected
        /// </summary>
        public int CurrentTool
        {
            get => _currentTool;
            set
            {
                if (_currentTool != value)
                {
                    _currentTool = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int _currentTool = -1;

        /// <summary>
        /// Persistent message to display (see M117)
        /// </summary>
        public string DisplayMessage
        {
            get => _displayMessage;
            set
            {
                if (_displayMessage != value)
                {
                    _displayMessage = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _displayMessage;

        /// <summary>
        /// Log file being written to or null if logging is disabled
        /// </summary>
        public string LogFile
        {
            get => _logFile;
            set
            {
                if (_logFile != value)
                {
                    _logFile = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _logFile;

        /// <summary>
        /// Current mode of operation
        /// </summary>
        public MachineMode Mode
        {
            get => _mode;
            set
            {
                if (_mode != value)
                {
                    _mode = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private MachineMode _mode = MachineMode.FFF;
        
        /// <summary>
        /// Current state of the machine
        /// </summary>
        public MachineStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private MachineStatus _status = MachineStatus.Idle;

        /// <summary>
        /// Assigns every property of another instance of this one
        /// </summary>
        /// <param name="from">Object to assign from</param>
        /// <exception cref="ArgumentNullException">other is null</exception>
        /// <exception cref="ArgumentException">Types do not match</exception>
        public void Assign(object from)
        {
            if (from == null)
            {
                throw new ArgumentNullException();
            }
            if (!(from is State other))
            {
                throw new ArgumentException("Invalid type");
            }

            AtxPower = other.AtxPower;
            Beep.Assign(other.Beep);
            CurrentTool = other.CurrentTool;
            DisplayMessage = other.DisplayMessage;
            LogFile = other.LogFile;
            Mode = other.Mode;
            Status = other.Status;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new State
            {
                AtxPower = AtxPower,
                Beep = (BeepDetails)Beep.Clone(),
                CurrentTool = CurrentTool,
                DisplayMessage = DisplayMessage,
                LogFile = LogFile,
                Mode = Mode,
                Status = Status
            };
        }
    }
}