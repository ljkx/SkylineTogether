using System;
using System.Collections.Generic;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Modding
{
    /// <summary>Where the host sends one <see cref="ModChannelMessage"/> (<see cref="ModChannelRouter.Route"/>).</summary>
    public struct ModChannelRoute
    {
        /// <summary>The host's own mod receives it.</summary>
        public bool DeliverToHost;
        /// <summary>The clients the host forwards it to.</summary>
        public List<PeerId> Forward;
    }

    /// <summary>
    /// The host's routing of mod channel messages (docs/MOD-INTEGRATION.md): clients only talk to the host, which relays.
    /// A client's <see cref="ModChannelTarget.Everyone"/> reaches the host and every other live client;
    /// <see cref="ModChannelTarget.Host"/> the host; <see cref="ModChannelTarget.Player"/> that player (the host when it
    /// names peer 0). A message never goes back to its sender, and players that are not live are skipped. The host's own
    /// messages go to the clients the same way. Pure.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class ModChannelRouter
    {
        public static ModChannelRoute Route(PeerId sender, ModChannelTarget target, PeerId targetPeer, IEnumerable<PeerId> livePeers)
        {
            var route = new ModChannelRoute { Forward = new List<PeerId>() };
            switch (target)
            {
                case ModChannelTarget.Everyone:
                    route.DeliverToHost = !sender.IsHost;
                    foreach (PeerId p in livePeers ?? Array.Empty<PeerId>())
                    {
                        if (!p.IsHost && p != sender && !route.Forward.Contains(p)) route.Forward.Add(p);
                    }
                    break;
                case ModChannelTarget.Host:
                    route.DeliverToHost = !sender.IsHost;
                    break;
                case ModChannelTarget.Player:
                    if (targetPeer == sender) break;
                    if (targetPeer.IsHost)
                    {
                        route.DeliverToHost = true;
                        break;
                    }
                    foreach (PeerId p in livePeers ?? Array.Empty<PeerId>())
                    {
                        if (p == targetPeer)
                        {
                            route.Forward.Add(p);
                            break;
                        }
                    }
                    break;
            }
            return route;
        }
    }

    /// <summary>
    /// A send budget for the mod channel (bytes and messages per second, token buckets with one second of burst): the
    /// sending side refuses what is over it (the mod's <c>Send</c> returns false), and the host drops what a client sends
    /// beyond it before relaying, so one mod cannot flood the session. Not thread-safe (main thread).
    /// </summary>
    public sealed class ModChannelBudget
    {
        /// <summary>Default sustained bytes per second (per sender).</summary>
        public const int DefaultBytesPerSecond = 64 * 1024;

        /// <summary>Default sustained messages per second (per sender).</summary>
        public const int DefaultMessagesPerSecond = 30;

        private readonly double _bytesPerSecond;
        private readonly double _messagesPerSecond;
        private double _bytes;
        private double _messages;
        private double _last = double.NaN;

        public ModChannelBudget(int bytesPerSecond = DefaultBytesPerSecond, int messagesPerSecond = DefaultMessagesPerSecond)
        {
            _bytesPerSecond = Math.Max(1, bytesPerSecond);
            _messagesPerSecond = Math.Max(1, messagesPerSecond);
            _bytes = _bytesPerSecond;
            _messages = _messagesPerSecond;
        }

        /// <summary>Messages refused so far.</summary>
        public long Refused { get; private set; }

        /// <summary>True (and the budget charged) when a message of <paramref name="bytes"/> may go now.</summary>
        public bool TrySpend(int bytes, double now)
        {
            if (!double.IsNaN(_last) && now > _last)
            {
                double elapsed = now - _last;
                _bytes = Math.Min(_bytesPerSecond, _bytes + elapsed * _bytesPerSecond);
                _messages = Math.Min(_messagesPerSecond, _messages + elapsed * _messagesPerSecond);
            }
            if (double.IsNaN(_last) || now > _last) _last = now;
            if (bytes < 0 || _messages < 1 || _bytes < bytes)
            {
                Refused++;
                return false;
            }
            _bytes -= bytes;
            _messages -= 1;
            return true;
        }
    }
}
