namespace DuetAPI.Machine
{
    /// <summary>
    /// Class holding information about a user variable
    /// </summary>
    public sealed class UserVariable : ModelObject
    {
        /// <summary>
        /// Name of the user variable
        /// </summary>
        public string Name
        {
            get => _name;
			set => SetPropertyValue(ref _name, value);
        }
        private string _name = string.Empty;

        /// <summary>
        /// Value of the user variable
        /// </summary>
        public object Value
        {
            get => _value;
			set => SetPropertyValue(ref _value, value);
        }
        private object _value;
    }
}
