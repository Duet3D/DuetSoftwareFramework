using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.IPC.Worker
{
    public class Subscription : Base
    {
        public Subscription(Socket socket) : base(socket) { }

        // Handle model subscriptions
        public override async Task<Base> Work()
        {
            // TODO
            await Task.CompletedTask;
            return this;
        }
    }
}
