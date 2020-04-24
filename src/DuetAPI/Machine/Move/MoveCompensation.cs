﻿namespace DuetAPI.Machine
{
	/// <summary>
	/// Information about the configured compensation options
	/// </summary>
	public sealed class MoveCompensation : ModelObject
    {
        /// <summary>
        /// Effective height before the bed compensation is turned off (in mm) or null if not configured
        /// </summary>
		public float? FadeHeight
		{
			get => _fadeHeight;
			set => SetPropertyValue(ref _fadeHeight, value);
		}
		private float? _fadeHeight;

		/// <summary>
		/// Full path to the currently used height map file or null if none is in use
		/// </summary>
		[LinuxProperty]
		public string File
		{
			get => _file;
			set => SetPropertyValue(ref _file, value);
		}
		private string _file;

		/// <summary>
		/// Deviations of the mesh grid or null if not applicable
		/// </summary>
		public MoveDeviations MeshDeviation
		{
			get => _meshDeviation;
			set => SetPropertyValue(ref _meshDeviation, value);
		}
		private MoveDeviations _meshDeviation;

		/// <summary>
		/// Settings of the current probe grid
		/// </summary>
		public ProbeGrid ProbeGrid { get; } = new ProbeGrid();

		/// <summary>
		/// Type of the compensation in use
		/// </summary>
		public MoveCompensationType Type
		{
			get => _type;
			set => SetPropertyValue(ref _type, value);
		}
		private MoveCompensationType _type = MoveCompensationType.None;
	}
}
