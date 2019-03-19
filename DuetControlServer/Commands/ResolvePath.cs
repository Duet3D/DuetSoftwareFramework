using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    public class ResolvePath : DuetAPI.Commands.ResolvePath
    {
        protected override Task<string> Run() => Task.FromResult(File.ResolvePath(Path));
    }
}