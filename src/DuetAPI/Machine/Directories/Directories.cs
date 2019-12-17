using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the machine state
    /// </summary>
    public sealed class Directories : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Path to the Filaments directory
        /// </summary>
        public string Filaments
        {
            get => _filaments;
            set
            {
                if (_filaments != value)
                {
                    _filaments = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _filaments = "0:/filaments";

        /// <summary>
        /// Path to the G-Codes directory
        /// </summary>
        public string GCodes
        {
            get => _gcodes;
            set
            {
                if (_gcodes != value)
                {
                    _gcodes = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _gcodes = "0:/gcodes";

        /// <summary>
        /// Path to the macros directory
        /// </summary>
        public string Macros
        {
            get => _macros;
            set
            {
                if (_macros != value)
                {
                    _macros = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _macros = "0:/macros";

        /// <summary>
        /// Path to the system directory
        /// </summary>
        public string System
        {
            get => _system;
            set
            {
                if (_system != value)
                {
                    _system = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _system = "0:/sys";

        /// <summary>
        /// Path to the web directory
        /// </summary>
        public string WWW
        {
            get => _www;
            set
            {
                if (_www != value)
                {
                    _www = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _www = "0:/www";

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
            if (!(from is Directories other))
            {
                throw new ArgumentException("Invalid type");
            }

            Filaments = other.Filaments;
            GCodes = other.GCodes;
            Macros = other.Macros;
            System = other.System;
            WWW = other.WWW;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Directories
            {
                Filaments = Filaments,
                GCodes = GCodes,
                Macros = Macros,
                System = System,
                WWW = WWW
            };
        }
    }
}