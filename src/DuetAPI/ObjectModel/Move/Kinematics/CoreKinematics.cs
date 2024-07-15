namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about core kinematics
    /// </summary>
    public sealed class CoreKinematics : ZLeadscrewKinematics
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
		public ModelCollection<float[]> ForwardMatrix { get; } =
        [
            [1, 0, 0],
			[0, 1, 0],
			[0, 0, 1]
		];

		/// <summary>
		/// Inverse matrix
		/// </summary>
		public ModelCollection<float[]> InverseMatrix { get; } =
        [
            [1, 0, 0],
			[0, 1, 0],
			[0, 0, 1]
		];
    }
}
