using System.IO;
using System.Threading.Tasks;

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

		/// <summary>
		/// Write the calibration parameters to config-override.g
		/// </summary>
		/// <param name="writer">Stream writer for config-override.g</param>
		/// <returns>Asynchronous task</returns>
        public override async Task WriteCalibrationParameters(StreamWriter writer)
        {
			await writer.WriteLineAsync("; Hangprinter parameters");
			await writer.WriteLineAsync("M669 K6 " +
				$"A{Anchors[0][0]:F3}:{Anchors[0][1]:F3}:{Anchors[0][2]:F3} " +
				$"B{Anchors[1][0]:F3}:{Anchors[1][1]:F3}:{Anchors[1][2]:F3} " +
				$"C{Anchors[2][0]:F3}:{Anchors[2][1]:F3}:{Anchors[2][2]:F3} " +
				$"D{Anchors[3][0]:F3}:{Anchors[3][1]:F3}:{Anchors[3][2]:F3} " +
				$"P{PrintRadius:F2}");
        }
    }
}
