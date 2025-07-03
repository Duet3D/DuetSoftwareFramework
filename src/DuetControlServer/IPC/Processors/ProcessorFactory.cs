using System;
using DuetAPI.Connection.InitMessages;
using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.IPC.Processors;

/// <summary>
/// Factory to create new IPC processor instances
/// </summary>
/// <param name="serviceProvider">Service provider</param>
public class ProcessorFactory(IServiceProvider serviceProvider)
{
    /// <summary>
    /// Create a new processor instance for the given connection and initialization message
    /// </summary>
    /// <typeparam name="T">Processor type</typeparam>
    /// <param name="conn">Connection to use for the processor</param>
    /// <param name="initMessage">Initialization message for the processor</param>
    /// <returns>Processor instance</returns>
    public T Create<T>(Connection conn, ClientInitMessage initMessage) where T : IProcessor
    {
        return ActivatorUtilities.CreateInstance<T>(serviceProvider, conn, initMessage);
    }
}
