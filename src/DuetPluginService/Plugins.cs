using DuetAPI.ObjectModel;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DuetPluginService
{
    /// <summary>
    /// Helper functions for plugin management
    /// </summary>
    public static class Plugins
    {
        /// <summary>
        /// Internal lock for the plugin list
        /// </summary>
        private static readonly AsyncLock _lock = new();

        /// <summary>
        /// Lock access to the plugins
        /// </summary>
        /// <returns>Lock instance</returns>
        public static IDisposable Lock() => _lock.Lock();

        /// <summary>
        /// Lock access to the plugins asynchronously
        /// </summary>
        /// <returns>Lock instance</returns>
        public static AwaitableDisposable<IDisposable> LockAsync() => _lock.LockAsync();

        /// <summary>
        /// List of plugins
        /// </summary>
        public static List<Plugin> List { get; } = new List<Plugin>();

        /// <summary>
        /// Plugin names vs processes
        /// </summary>
        public static Dictionary<string, Process> Processes { get; } = new Dictionary<string, Process>();
    }
}
