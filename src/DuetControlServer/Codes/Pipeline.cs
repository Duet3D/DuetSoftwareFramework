using DuetAPI.ObjectModel;
using DuetControlServer.Commands;
using System.Threading.Channels;

namespace DuetControlServer.Codes
{
    public static class Pipeline
    {
        /// <summary>
        /// Initialize the code pipelines
        /// </summary>
        public static void Init()
        {

        }

        /// <summary>
        /// Enqueue a code to be executed
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        /// <param name="stage">Stage level to enqueue it at</param>
        public static void Enqueue(Code code, Pipelines.Stage stage = Pipelines.Stage.Input)
        {

        }
    }
}
