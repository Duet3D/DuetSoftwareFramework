namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Machine configuration limits
    /// </summary>
    public sealed class Limits : ModelObject
	{
		/// <summary>
		/// Maximum number of axes or null if unknown
		/// </summary>
		public int? Axes
		{
			get => _axes;
			set => SetPropertyValue(ref _axes, value);
		}
		private int? _axes;

		/// <summary>
		/// Maximum number of axes + extruders or null if unknown
		/// </summary>
		public int? AxesPlusExtruders
		{
			get => _axesPlusExtruders;
			set => SetPropertyValue(ref _axesPlusExtruders, value);
		}
		private int? _axesPlusExtruders;

		/// <summary>
		/// Maximum number of bed heaters or null if unknown
		/// </summary>
		public int? BedHeaters
		{
			get => _bedHeaters;
			set => SetPropertyValue(ref _bedHeaters, value);
		}
		private int? _bedHeaters;

		/// <summary>
		/// Maximum number of boards or null if unknown
		/// </summary>
		public int? Boards
		{
			get => _boards;
			set => SetPropertyValue(ref _boards, value);
		}
		private int? _boards;

		/// <summary>
		/// Maximum number of chamber heaters or null if unknown
		/// </summary>
		public int? ChamberHeaters
		{
			get => _chamberHeaters;
			set => SetPropertyValue(ref _chamberHeaters, value);
		}
		private int? _chamberHeaters;

		/// <summary>
		/// Maximum number of drivers or null if unknown
		/// </summary>
		public int? Drivers
		{
			get => _drivers;
			set => SetPropertyValue(ref _drivers, value);
		}
		private int? _drivers;

		/// <summary>
		/// Maximum number of drivers per axis or null if unknown
		/// </summary>
		public int? DriversPerAxis
		{
			get => _driversPerAxis;
			set => SetPropertyValue(ref _driversPerAxis, value);
		}
		private int? _driversPerAxis;

		/// <summary>
		/// Maximum number of extruders or null if unknown
		/// </summary>
		public int? Extruders
		{
			get => _extruders;
			set => SetPropertyValue(ref _extruders, value);
		}
		private int? _extruders;

		/// <summary>
		/// Maximum number of extruders per tool or null if unknown
		/// </summary>
		public int? ExtrudersPerTool
		{
			get => _extrudersPerTool;
			set => SetPropertyValue(ref _extrudersPerTool, value);
		}
		private int? _extrudersPerTool;

		/// <summary>
		/// Maximum number of fans or null if unknown
		/// </summary>
		public int? Fans
		{
			get => _fans;
			set => SetPropertyValue(ref _fans, value);
		}
		private int? _fans;

		/// <summary>
		/// Maximum number of general-purpose input ports or null if unknown
		/// </summary>
		public int? GpInPorts
		{
			get => _gpInPorts;
			set => SetPropertyValue(ref _gpInPorts, value);
		}
		private int? _gpInPorts;

		/// <summary>
		/// Maximum number of general-purpose output ports or null if unknown
		/// </summary>
		public int? GpOutPorts
		{
			get => _gpOutPorts;
			set => SetPropertyValue(ref _gpOutPorts, value);
		}
		private int? _gpOutPorts;

		/// <summary>
		/// Maximum number of heaters or null if unknown
		/// </summary>
		public int? Heaters
		{
			get => _heaters;
			set => SetPropertyValue(ref _heaters, value);
		}
		private int? _heaters;

		/// <summary>
		/// Maximum number of heaters per tool or null if unknown
		/// </summary>
		public int? HeatersPerTool
		{
			get => _heatersPerTool;
			set => SetPropertyValue(ref _heatersPerTool, value);
		}
		private int? _heatersPerTool;

		/// <summary>
		/// Maximum number of monitors per heater or null if unknown
		/// </summary>
		public int? MonitorsPerHeater
		{
			get => _monitorsPerHeater;
			set => SetPropertyValue(ref _monitorsPerHeater, value);
		}
		private int? _monitorsPerHeater;

		/// <summary>
		/// Maximum number of restore points or null if unknown
		/// </summary>
		public int? RestorePoints
		{
			get => _restorePoints;
			set => SetPropertyValue(ref _restorePoints, value);
		}
		private int? _restorePoints;

		/// <summary>
		/// Maximum number of sensors or null if unknown
		/// </summary>
		public int? Sensors
		{
			get => _sensors;
			set => SetPropertyValue(ref _sensors, value);
		}
		private int? _sensors;

		/// <summary>
		/// Maximum number of spindles or null if unknown
		/// </summary>
		public int? Spindles
		{
			get => _spindles;
			set => SetPropertyValue(ref _spindles, value);
		}
		private int? _spindles;

		/// <summary>
		/// Maximum number of tools or null if unknown
		/// </summary>
		public int? Tools
		{
			get => _tools;
			set => SetPropertyValue(ref _tools, value);
		}
		private int? _tools;

		/// <summary>
		/// Maximum number of tracked objects or null if unknown
		/// </summary>
		public int? TrackedObjects
		{
			get => _trackedObjects;
			set => SetPropertyValue(ref _trackedObjects, value);
		}
		private int? _trackedObjects;

		/// <summary>
		/// Maximum number of triggers or null if unknown
		/// </summary>
		public int? Triggers
		{
			get => _triggers;
			set => SetPropertyValue(ref _triggers, value);
		}
		private int? _triggers;

		/// <summary>
		/// Maximum number of volumes or null if unknown
		/// </summary>
		public int? Volumes
		{
			get => _volumes;
			set => SetPropertyValue(ref _volumes, value);
		}
		private int? _volumes;

		/// <summary>
		/// Maximum number of workplaces or null if unknown
		/// </summary>
		public int? Workplaces
		{
			get => _workplaces;
			set => SetPropertyValue(ref _workplaces, value);
		}
		private int? _workplaces;

		/// <summary>
		/// Maximum number of Z-probe programming bytes or null if unknown
		/// </summary>
		public int? ZProbeProgramBytes
		{
			get => _zProbeProgramBytes;
			set => SetPropertyValue(ref _zProbeProgramBytes, value);
		}
		private int? _zProbeProgramBytes;

		/// <summary>
		/// Maximum number of Z-probes or null if unknown
		/// </summary>
		public int? ZProbes
		{
			get => _zProbes;
			set => SetPropertyValue(ref _zProbes, value);
		}
		private int? _zProbes;
	}
}
