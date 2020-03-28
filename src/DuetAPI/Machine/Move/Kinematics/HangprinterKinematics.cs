namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about core kinematics
    /// </summary>
    public sealed class HangprinterKinematics : Kinematics
    {
		/// <summary>
		/// A anchor
		/// </summary>
		public ModelCollection<float> AnchorA { get; } = new ModelCollection<float> { 0F, -2000F, -100F };

		/// <summary>
		/// B anchor
		/// </summary>
		public ModelCollection<float> AnchorB { get; } = new ModelCollection<float> { 2000F, 1000F, -100F };

		/// <summary>
		/// C anchor
		/// </summary>
		public ModelCollection<float> AnchorC { get; } = new ModelCollection<float> { -2000F, 1000F, -100F };

		/// <summary>
		/// Dz anchor
		/// </summary>
		public float AnchorDz
		{
			get => _anchorDz;
			set => SetPropertyValue(ref _anchorDz, value);
		}
		private float _anchorDz = 3000F;

		/// <summary>
		/// Print radius (in mm)
		/// </summary>
		public float PrintRadius
		{
			get => _printRadius;
			set => SetPropertyValue(ref _printRadius, value);
		}
		private float _printRadius = 1500F;
    }
}
