namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about hangprinter kinematics
    /// </summary>
    public sealed class HangprinterKinematics : Kinematics
    {
		/// <summary>
		/// Anchor configurations for A, B, C, Dz
		/// </summary>
		public ModelCollection<float[]> Anchors { get; } = new ModelCollection<float[]> {
			new float[] {     0F, -2000F, -100F },
			new float[] {  2000F,  1000F, -100F },
			new float[] { -2000F,  1000F, -100F },
			new float[] {     0F,  0F,    3000F }
		};

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
