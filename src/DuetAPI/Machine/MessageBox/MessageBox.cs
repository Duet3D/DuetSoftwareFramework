using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the message box to show
    /// </summary>
    public sealed class MessageBox : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Mode of the message box to display or null if none is shown
        /// </summary>
        public MessageBoxMode? Mode
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
        private MessageBoxMode? _mode;
        
        /// <summary>
        /// Title of the message box
        /// </summary>
        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _title;
        
        /// <summary>
        /// Content of the message box
        /// </summary>
        public string Message
        {
            get => _message;
            set
            {
                if (_message != value)
                {
                    _message = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _message;

        /// <summary>
        /// Optional axis movement controls to show (axis indices)
        /// </summary>
        public ObservableCollection<int> AxisControls { get; } = new ObservableCollection<int>();

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
            if (!(from is MessageBox other))
            {
                throw new ArgumentException("Invalid type");
            }

            Mode = other.Mode;
            Title = (other.Title != null) ? string.Copy(other.Title) : null;
            Message = (other.Message != null) ? string.Copy(other.Message) : null;
            ListHelpers.SetList(AxisControls, other.AxisControls);
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            MessageBox clone = new MessageBox
            {
                Mode = Mode,
                Title = (Title != null) ? string.Copy(Title) : null,
                Message = (Message != null) ? string.Copy(Message) : null
            };

            ListHelpers.AddItems(clone.AxisControls, AxisControls);

            return clone;
        }
    }
}