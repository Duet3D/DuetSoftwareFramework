using System;
using System.Threading.Tasks;
using DuetAPI.Connection;

namespace DuetControlServer.IPC.Processors
{
    public abstract class Base
    {
        protected Connection Connection { get; }

        public Base(Connection conn, ClientInitMessage initMessage)
        {
             Connection = conn;   
        }

        public virtual Task Process()
        {
            throw new NotImplementedException("Processor not implemented");
        }
    }
}