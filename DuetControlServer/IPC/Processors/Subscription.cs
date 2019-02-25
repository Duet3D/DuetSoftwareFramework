using System.Threading.Tasks;
using DuetAPI.Connection;

namespace DuetControlServer.IPC.Processors
{
    public class Subscription : Base
    {
        public Subscription(Connection conn, ClientInitMessage initMessage) : base(conn, initMessage)
        {
        }
        
        // Handle model subscriptions
        public override async Task Process()
        {
            // TODO
            await Task.CompletedTask;
        }
    }
}
