using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about hangprinter kinematics
    /// </summary>
    public partial class HangprinterKinematics : Kinematics
    {
		/// <summary>
		/// Anchor configurations for A, B, C, Dz
		/// </summary>
		public ObservableCollection<float[]> Anchors { get; } = [
			[0F, -2000F, -100F],
			[2000F,  1000F, -100F],
			[-2000F,  1000F, -100F],
			[0F,  0F,    3000F]
		];

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
