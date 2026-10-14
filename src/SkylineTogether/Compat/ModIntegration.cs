using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Game.Modding;
using Game.SceneFlow;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Diagnostics;
using SkylineTogether.Session;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// The integration API for other mods (docs/MOD-INTEGRATION.md), matched by name so that no mod needs a reference to
    /// Skyline Together:
    /// <list type="bullet">
    /// <item>declarations: <c>[SkylineTogether("role")]</c> attributes on the mod's types and settings properties (any
    /// attribute class named <c>SkylineTogetherAttribute</c>; read as metadata, never constructed), assembly attributes
    /// with a role and a target, and the <c>Declarations</c> lines of a static class named
    /// <c>SkylineTogetherIntegration</c>;</item>
    /// <item>runtime: that class's delegate fields are set (<c>IsInSession</c>, <c>IsHost</c>, <c>IsClient</c>,
    /// <c>LocalPlayer</c>, <c>Send</c>) and its static methods called (<c>OnSessionStarted(bool)</c>,
    /// <c>OnSessionEnded()</c>, <c>OnMessage(int, byte[])</c>).</item>
    /// </list>
    /// The mod channel carries opaque bytes per mod (channel = assembly name, <see cref="ModChannelMessage"/>): clients
    /// talk to the host, which relays (<see cref="ModChannelRouter"/>), with a size limit and a send budget on both ends.
    /// Nothing here throws into the game or into the other mod: a bad declaration is reported, a callback that keeps
    /// failing is switched off. Main thread only.
    /// </summary>
    public static class ModIntegration
    {
        public const string AttributeName = "SkylineTogetherAttribute";
        public const string ClassName = "SkylineTogetherIntegration";

        /// <summary>Callback failures after which a mod's callbacks are switched off for the rest of the game.</summary>
        private const int MaxFailures = 3;

        private sealed class Bound
        {
            public string Channel;
            public string TypeName;
            public Action<bool> SessionStarted;
            public Action SessionEnded;
            public Action<int, byte[]> Message;
            public int Failures;
            public bool Off;
            public long Sent, Received, Refused;
            /// <summary>Message kinds viewers may send on this channel (the mod's <c>viewer-message</c> declarations).</summary>
            public ViewerMessageKinds ViewerKinds = ViewerMessageKinds.None;
            public readonly ModChannelBudget Budget = new ModChannelBudget();
            public readonly List<string> Callbacks = new List<string>();
        }

        private static readonly Dictionary<Assembly, ModDeclarations> Declared = new Dictionary<Assembly, ModDeclarations>();
        private static readonly Dictionary<string, Bound> Channels = new Dictionary<string, Bound>(StringComparer.Ordinal);
        private static readonly Dictionary<byte, ModChannelBudget> PeerBudgets = new Dictionary<byte, ModChannelBudget>();
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);
        private static readonly ByteWriter Out = new ByteWriter(1024);
        private static SessionManager _sessions;
        private static ILog _log;
        private static bool _discovered;
        private static bool _started;
        private static long _unknownChannel;
        private static long _refusedFromViewers;

        /// <summary>Subscribes to the session events (once, at load).</summary>
        public static void Attach(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            // nothing may throw into the session (a session failure ends it): every handler runs under the guard
            sessions.HostStarted += host => Guard.Run(Features.Mods, "mod integration host start", () =>
            {
                Discover();
                PeerBudgets.Clear();
                host.AppMessage += (peer, e) =>
                {
                    if (e.Kind == MessageKind.ModChannel) HostReceive(peer, e); // no closure for the other kinds
                };
                host.PeerLeft += peer => PeerBudgets.Remove(peer.Value);
                Started(true);
            });
            sessions.ClientStarted += client => Guard.Run(Features.Mods, "mod integration client start", () =>
            {
                Discover();
                client.AppMessage += e =>
                {
                    if (e.Kind == MessageKind.ModChannel) ClientReceive(e);
                };
                client.BecameLive += () => Guard.Run(Features.Mods, "mod integration session start", () => Started(false));
            });
            sessions.SessionEnded += () => Guard.Run(Features.Mods, "mod integration session end", Ended);
        }

        private static void HostReceive(PeerId peer, Envelope e) => Guard.Run(Features.Mods, "mod channel receive", () => OnHostMessage(peer, e));

        private static void ClientReceive(Envelope e) => Guard.Run(Features.Mods, "mod channel receive", () => OnClientMessage(e));

        /// <summary>Session driver, every frame: binds the integration classes once the game has loaded every mod.</summary>
        public static void Tick()
        {
            if (_discovered) return;
            ModManager manager = GameManager.instance?.modManager;
            if (manager != null && manager.isInitialized) Discover();
        }

        // ================================================================== discovery

        /// <summary>Reads (and binds) the integration of every loaded mod; idempotent.</summary>
        public static void Discover()
        {
            if (_discovered) return;
            _discovered = true;
            int bound = 0;
            foreach ((Assembly assembly, string _) in ModCompat.LoadedMods())
            {
                ModDeclarations d = DeclarationsFor(assembly);
                if (d.HasIntegrationClass) bound++;
            }
            if (bound > 0) _log?.Info(LogCategory.Compat, "mod integration: " + bound + " mod(s) use the Skyline Together integration API");
        }

        /// <summary>
        /// The declarations of a mod (attributes and its integration class), read once; the integration class is bound at
        /// the same time. Never throws: whatever cannot be read is a problem line of the result.
        /// </summary>
        public static ModDeclarations DeclarationsFor(Assembly assembly)
        {
            if (assembly == null) return new ModDeclarations();
            if (Declared.TryGetValue(assembly, out ModDeclarations cached)) return cached;
            var d = new ModDeclarations();
            Declared[assembly] = d;
            try
            {
                Read(assembly, d);
            }
            catch (Exception ex)
            {
                d.Problems.Add("reading the declarations failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return d;
        }

        private static void Read(Assembly assembly, ModDeclarations d)
        {
            foreach (CustomAttributeData a in Attributes(() => CustomAttributeData.GetCustomAttributes(assembly)))
            {
                Add(d, a, null, "assembly attribute");
            }
            Type integration = null;
            foreach (Type t in ModAssemblyScanner.LoadableTypes(assembly))
            {
                if (t == null) continue;
                if (t.Name == ClassName && t.IsClass)
                {
                    if (integration == null) integration = t;
                    else d.Problems.Add("more than one " + ClassName + " class (" + integration.FullName + ", " + t.FullName + "); the first is used");
                }
                foreach (CustomAttributeData a in Attributes(() => CustomAttributeData.GetCustomAttributes(t)))
                {
                    Add(d, a, t.FullName, "attribute on " + t.FullName);
                }
                if (!typeof(Game.Modding.ModSetting).IsAssignableFrom(t)) continue;
                foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    foreach (CustomAttributeData a in Attributes(() => CustomAttributeData.GetCustomAttributes(p)))
                    {
                        Add(d, a, t.FullName + "." + p.Name, "attribute on " + t.FullName + "." + p.Name);
                    }
                }
            }
            if (integration != null) ReadIntegrationClass(assembly, integration, d);
        }

        private static IList<CustomAttributeData> Attributes(Func<IList<CustomAttributeData>> read)
        {
            try
            {
                return read() ?? (IList<CustomAttributeData>)Array.Empty<CustomAttributeData>();
            }
            catch (Exception)
            {
                return Array.Empty<CustomAttributeData>(); // an attribute type that cannot load is not ours
            }
        }

        /// <summary>One <c>[SkylineTogether(role)]</c> or <c>[SkylineTogether(role, target)]</c>; other attributes are skipped.</summary>
        private static void Add(ModDeclarations d, CustomAttributeData a, string target, string source)
        {
            string name;
            try
            {
                name = a.Constructor?.DeclaringType?.Name;
            }
            catch (Exception)
            {
                return;
            }
            if (name != AttributeName) return;
            IList<CustomAttributeTypedArgument> args = a.ConstructorArguments;
            string role = args.Count > 0 ? args[0].Value as string : null;
            if (args.Count > 1) target = args[1].Value as string;
            if (args.Count == 0 || (args[0].ArgumentType != typeof(string)))
            {
                d.Problems.Add(source + ": the attribute's first argument must be the role (a string)");
                return;
            }
            d.Add(role, target ?? "", source);
        }

        private static void ReadIntegrationClass(Assembly assembly, Type type, ModDeclarations d)
        {
            const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            d.HasIntegrationClass = true;
            FieldInfo version = type.GetField("ApiVersion", Static);
            try
            {
                if (version != null && version.FieldType == typeof(int)) d.ApiVersion = version.IsLiteral ? (int)version.GetRawConstantValue() : (int)version.GetValue(null);
                else if (version != null) d.Problems.Add(type.FullName + ".ApiVersion must be an int constant");
            }
            catch (Exception ex)
            {
                d.Problems.Add(type.FullName + ".ApiVersion could not be read (" + Inner(ex).Message + ")");
            }
            object lines = null;
            try
            {
                FieldInfo f = type.GetField("Declarations", Static);
                PropertyInfo p = f == null ? type.GetProperty("Declarations", Static) : null;
                lines = f != null ? f.GetValue(null) : p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(null) : null;
            }
            catch (Exception ex)
            {
                d.Problems.Add(type.FullName + ".Declarations could not be read (" + Inner(ex).Message + ")");
            }
            if (lines is string[] array)
            {
                for (int i = 0; i < array.Length; i++) d.AddLine(array[i], "Declarations[" + i.ToString(CultureInfo.InvariantCulture) + "]");
            }
            else if (lines != null)
            {
                d.Problems.Add(type.FullName + ".Declarations must be a string[]");
            }
            Bind(assembly, type, d);
        }

        /// <summary>Sets the delegate fields and finds the callbacks of a mod's integration class.</summary>
        private static void Bind(Assembly assembly, Type type, ModDeclarations d)
        {
            var b = new Bound { Channel = assembly.GetName().Name, TypeName = type.FullName, ViewerKinds = ViewerMessageKinds.From(d) };
            Set(type, "IsInSession", new Func<bool>(() => InSession), d);
            Set(type, "IsHost", new Func<bool>(() => Available && _sessions?.Host != null), d);
            Set(type, "IsClient", new Func<bool>(() => IsLiveClient), d);
            Set(type, "LocalPlayer", new Func<int>(LocalPlayer), d);
            Set(type, "Send", new Func<byte[], int, bool>((payload, target) => Send(b, payload, target)), d);
            b.SessionStarted = Callback<Action<bool>>(type, "OnSessionStarted", d, b);
            b.SessionEnded = Callback<Action>(type, "OnSessionEnded", d, b);
            b.Message = Callback<Action<int, byte[]>>(type, "OnMessage", d, b);
            Channels[b.Channel] = b;
        }

        private static void Set(Type type, string name, Delegate value, ModDeclarations d)
        {
            FieldInfo f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) return; // every member is optional
            if (f.FieldType != value.GetType())
            {
                d.Problems.Add(type.FullName + "." + name + " must be a static field of type " + Readable(value.GetType()) + " (it is " + Readable(f.FieldType) + ")");
                return;
            }
            if (f.IsInitOnly || f.IsLiteral)
            {
                d.Problems.Add(type.FullName + "." + name + " must not be readonly");
                return;
            }
            try
            {
                f.SetValue(null, value);
            }
            catch (Exception ex)
            {
                d.Problems.Add(type.FullName + "." + name + " could not be set (" + Inner(ex).Message + ")");
            }
        }

        private static T Callback<T>(Type type, string name, ModDeclarations d, Bound b) where T : Delegate
        {
            MethodInfo m;
            try
            {
                m = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch (AmbiguousMatchException)
            {
                d.Problems.Add(type.FullName + "." + name + " is overloaded; keep one static method");
                return null;
            }
            if (m == null) return null;
            var created = Delegate.CreateDelegate(typeof(T), m, false) as T;
            if (created == null)
            {
                d.Problems.Add(type.FullName + "." + name + " has the wrong signature (expected " + Readable(typeof(T)) + ")");
                return null;
            }
            b.Callbacks.Add(name);
            return created;
        }

        private static string Readable(Type t)
        {
            if (!t.IsGenericType) return t.Name;
            var args = new List<string>();
            foreach (Type a in t.GetGenericArguments()) args.Add(Readable(a));
            return t.Name.Substring(0, t.Name.IndexOf('`')) + "<" + string.Join(", ", args) + ">";
        }

        private static Exception Inner(Exception ex) => ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;

        // ================================================================== session state for the delegates

        private static bool IsLiveClient => Available && _sessions?.Client != null && _sessions.Client.State == ClientState.Live;

        /// <summary>Off when the guard tripped the feature: mods then see no session and send nothing.</summary>
        private static bool Available => Features.Mods.IsAvailable;

        private static bool InSession => Available && (_sessions?.Host != null || IsLiveClient);

        private static int LocalPlayer()
        {
            if (!Available) return -1;
            if (_sessions?.Host != null) return 0;
            return IsLiveClient ? _sessions.Client.LocalPeer.Value : -1;
        }

        private static double Now => _sessions?.Clock.Seconds ?? 0;

        private static void Started(bool host)
        {
            _started = true;
            foreach (Bound b in Channels.Values) Invoke(b, "OnSessionStarted", () => b.SessionStarted?.Invoke(host));
        }

        private static void Ended()
        {
            PeerBudgets.Clear();
            if (!_started) return;
            _started = false;
            foreach (Bound b in Channels.Values) Invoke(b, "OnSessionEnded", () => b.SessionEnded?.Invoke());
        }

        private static void Invoke(Bound b, string what, Action call)
        {
            if (b.Off) return;
            try
            {
                call();
            }
            catch (Exception ex)
            {
                b.Failures++;
                _log?.Warn(LogCategory.Compat, "mod integration: " + b.TypeName + "." + what + " threw (" + b.Failures + " of " + MaxFailures + ")", ex);
                if (b.Failures >= MaxFailures)
                {
                    b.Off = true;
                    _log?.Warn(LogCategory.Compat, "mod integration: the callbacks of " + b.Channel + " are off for the rest of this game");
                }
            }
        }

        // ================================================================== the mod channel

        /// <summary>
        /// The mod's <c>Send(payload, target)</c>: target -1 = everyone, 0 = the host, n = player n. False when not in a
        /// session, over the size limit or over the send budget (nothing is queued then).
        /// </summary>
        private static bool Send(Bound b, byte[] payload, int target)
        {
            try
            {
                if (!InSession || target < -1 || target > byte.MaxValue) return false;
                payload = payload ?? Array.Empty<byte>();
                if (payload.Length > ModChannelMessage.MaxPayloadBytes)
                {
                    WarnOnce(b.Channel + ".size", "mod channel: " + b.Channel + " sent " + payload.Length + " bytes; the limit is " + ModChannelMessage.MaxPayloadBytes + " per message");
                    b.Refused++;
                    return false;
                }
                // a viewer receives but sends only the kinds the mod declared viewer-allowed (the host checks it too)
                SessionClient client = _sessions.Host == null ? _sessions.Client : null;
                if (client != null && !ModChannelPermissions.MaySend(client.LocalRole, payload, b.ViewerKinds))
                {
                    WarnOnce(b.Channel + ".viewer", "mod channel: " + b.Channel + " tried to send as a viewer; viewers only receive unless the mod declares the message kind with \"viewer-message\"");
                    b.Refused++;
                    return false;
                }
                if (!b.Budget.TrySpend(payload.Length, Now))
                {
                    WarnOnce(b.Channel + ".budget", "mod channel: " + b.Channel + " sends faster than " + ModChannelBudget.DefaultMessagesPerSecond + " messages or "
                                                    + ModChannelBudget.DefaultBytesPerSecond / 1024 + " KiB per second; the excess is refused");
                    b.Refused++;
                    return false;
                }
                var m = new ModChannelMessage
                {
                    Channel = b.Channel,
                    Target = target < 0 ? ModChannelTarget.Everyone : target == 0 ? ModChannelTarget.Host : ModChannelTarget.Player,
                    TargetPeer = new PeerId((byte)Math.Max(0, target)),
                    Payload = payload,
                };
                SessionHost host = _sessions.Host;
                bool sent;
                if (host != null)
                {
                    m.From = PeerId.Host;
                    sent = Forward(host, m, ModChannelRouter.Route(PeerId.Host, m.Target, m.TargetPeer, LivePeers(host)).Forward) > 0;
                }
                else
                {
                    m.From = _sessions.Client.LocalPeer;
                    Encode(m);
                    sent = _sessions.Client.SendToHost(MessageKind.ModChannel, Out.Buffer, 0, Out.Length);
                }
                if (sent) b.Sent++;
                return sent;
            }
            catch (Exception ex)
            {
                WarnOnce(b.Channel + ".send", "mod channel: sending for " + b.Channel + " failed (" + ex.Message + ")");
                return false;
            }
        }

        private static void OnHostMessage(PeerId from, Envelope e)
        {
            if (e.Kind != MessageKind.ModChannel) return;
            ModChannelMessage m;
            try
            {
                m = ModChannelMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
            }
            catch (DecodeException ex)
            {
                WarnOnce("decode." + from.Value, "mod channel: dropped an unreadable message from " + from + " (" + ex.Message + ")");
                return;
            }
            if (!PeerBudgets.TryGetValue(from.Value, out ModChannelBudget budget)) PeerBudgets[from.Value] = budget = new ModChannelBudget();
            if (!budget.TrySpend(m.Payload.Length, Now))
            {
                WarnOnce("budget." + from.Value + "." + m.Channel, "mod channel: " + from + " sends " + m.Channel + " messages faster than allowed; the excess is dropped");
                return;
            }
            m.From = from; // a client never speaks for another
            SessionHost host = _sessions?.Host;
            if (host == null) return;
            // role permissions: a viewer's message is relayed only when the mod lets viewers send its kind (the host's own
            // declarations of the mod decide; a mod the host lacks declares nothing)
            Channels.TryGetValue(m.Channel, out Bound declared);
            if (!ModChannelPermissions.MaySend(host.GetRole(from), m.Payload, declared?.ViewerKinds ?? ViewerMessageKinds.None))
            {
                _refusedFromViewers++;
                WarnOnce("viewer." + from.Value + "." + m.Channel, "mod channel: dropped a " + m.Channel + " message from " + from + ", a viewer (the mod does not declare that message kind viewer-allowed)");
                return;
            }
            ModChannelRoute route = ModChannelRouter.Route(from, m.Target, m.TargetPeer, LivePeers(host));
            if (route.Forward.Count > 0) Forward(host, m, route.Forward);
            if (route.DeliverToHost) Deliver(m);
        }

        private static void OnClientMessage(Envelope e)
        {
            if (e.Kind != MessageKind.ModChannel) return;
            try
            {
                Deliver(ModChannelMessage.Decode(e.Body, e.BodyOffset, e.BodyCount));
            }
            catch (DecodeException ex)
            {
                WarnOnce("decode.host", "mod channel: dropped an unreadable message from the host (" + ex.Message + ")");
            }
        }

        private static void Deliver(ModChannelMessage m)
        {
            if (!Channels.TryGetValue(m.Channel, out Bound b) || b.Message == null)
            {
                _unknownChannel++; // the mod is not installed here, or does not listen
                return;
            }
            b.Received++;
            byte[] payload = m.Payload;
            Invoke(b, "OnMessage", () => b.Message(m.From.Value, payload));
        }

        private static int Forward(SessionHost host, ModChannelMessage m, List<PeerId> peers)
        {
            if (peers.Count == 0) return 0;
            Encode(m);
            foreach (PeerId p in peers) host.SendTo(p, MessageKind.ModChannel, Out.Buffer, 0, Out.Length);
            return peers.Count;
        }

        private static void Encode(ModChannelMessage m)
        {
            Out.Reset();
            ModChannelMessage.Write(Out, m);
        }

        private static List<PeerId> LivePeers(SessionHost host)
        {
            var live = new List<PeerId>();
            foreach (PlayerInfo p in host.GetPlayers())
            {
                if (!p.Id.IsHost && p.State == PlayerState.Live) live.Add(p.Id);
            }
            return live;
        }

        private static void WarnOnce(string key, string text)
        {
            if (Warned.Add(key)) _log?.Warn(LogCategory.Compat, text);
        }

        /// <summary>The diagnostics lines: every mod using the integration class, its callbacks and channel counters.</summary>
        public static void Describe(List<string> lines)
        {
            if (Channels.Count == 0)
            {
                lines.Add("  integration API: no mod uses it");
                return;
            }
            foreach (Bound b in Channels.Values)
            {
                lines.Add("  integration " + b.Channel + " (" + b.TypeName + "): callbacks " + (b.Callbacks.Count == 0 ? "none" : string.Join(", ", b.Callbacks))
                          + (b.Off ? " (off after " + b.Failures + " failures)" : b.Failures > 0 ? " (" + b.Failures + " failures)" : "")
                          + "; channel sent " + b.Sent + ", received " + b.Received + ", refused " + b.Refused);
            }
            if (_unknownChannel > 0) lines.Add("  mod channel: " + _unknownChannel + " message(s) for mods not listening here");
            if (_refusedFromViewers > 0) lines.Add("  mod channel: " + _refusedFromViewers + " message(s) from viewers dropped (kind not viewer-allowed)");
        }
    }
}
