using System;
using System.Collections.Concurrent;
using SkylineTogether.Core;

namespace SkylineTogether.Session
{
    /// <summary>Runs callbacks from async continuations on the main thread, at the next driver update.</summary>
    public sealed class MainThreadQueue
    {
        private readonly ConcurrentQueue<Action> _actions = new ConcurrentQueue<Action>();
        private readonly ILog _log;

        public MainThreadQueue(ILog log)
        {
            _log = log;
        }

        public void Post(Action action)
        {
            if (action != null) _actions.Enqueue(action);
        }

        public void Drain()
        {
            int budget = 256;
            while (budget-- > 0 && _actions.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _log.Error(LogCategory.Session, "main-thread callback failed", ex);
                }
            }
        }
    }
}
