namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about core kinematics
    /// </summary>
    public sealed class CoreKinematics : Kinematics
    {
		/// <summary>
		/// Constructor of this class
		/// </summary>
		public CoreKinematics()
		{
			Name = KinematicsName.Cartesian;
		}

		/// <summary>
		/// Forward matrix
		/// </summary>
		public ModelCollection<float[]> ForwardMatrix { get; } = new ModelCollection<float[]>
		{
			new float[] { 1, 0, 0 },
			new float[] { 0, 1, 0 },
			new float[] { 0, 0, 1 }
		};

		/// <summary>
		/// Inverse matrix
		/// </summary>
		public ModelCollection<float[]> InverseMatrix { get; } = new ModelCollection<float[]>
		{
			new float[] { 1, 0, 0 },
			new float[] { 0, 1, 0 },
			new float[] { 0, 0, 1 }
		};
    }
}
