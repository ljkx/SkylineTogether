using System;
using System.Collections.Generic;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// A text the host sends that the receiver shows in its own language: a locale key with named parameters, plus the
    /// English text as a fallback for a key the receiver does not know (ARCHITECTURE.md#ui-module). Used for system chat
    /// lines, kick, ban, end and drop reasons and refusal details (<see cref="NetText"/>).
    /// <code>
    /// text (≤ 1024 chars) | string key (≤ 128 B, empty: none) | varint argCount (≤ 8) | argCount × (string name ≤ 32 B, text value ≤ 256 chars)
    /// </code>
    /// </summary>
    public sealed class LocalText
    {
        public const int MaxTextChars = 1024;
        public const int MaxKeyBytes = 128;
        public const int MaxArgs = 8;
        public const int MaxArgNameBytes = 32;
        public const int MaxArgValueChars = 256;

        /// <summary>The English text (shown when the key is unknown here).</summary>
        public string Text = "";

        /// <summary>The locale key, or null for a plain text.</summary>
        public string Key;

        /// <summary>The parameters as name, value, name, value, ... (each <c>{NAME}</c> of the key's text is replaced).</summary>
        public string[] Args = Array.Empty<string>();

        public static LocalText Plain(string text) => new LocalText { Text = text ?? "" };

        public override string ToString() => Text ?? "";

        /// <summary>
        /// The text in the receiver's language: <paramref name="lookup"/> returns the template of a key (null when unknown),
        /// whose <c>{NAME}</c> placeholders are filled from <see cref="Args"/>; else the English <see cref="Text"/>. Only
        /// keys of the <c>st.net.</c> family are looked up (a host cannot make a joiner show any other string).
        /// </summary>
        public string Render(Func<string, string> lookup)
        {
            if (string.IsNullOrEmpty(Key) || lookup == null || !Key.StartsWith(NetText.KeyPrefix, StringComparison.Ordinal)) return Text ?? "";
            string template;
            try
            {
                template = lookup(Key);
            }
            catch (Exception)
            {
                template = null;
            }
            if (string.IsNullOrEmpty(template)) return Text ?? "";
            return NetText.Format(template, NetText.WithWait(Args, k => SafeLookup(lookup, k)));
        }

        private static string SafeLookup(Func<string, string> lookup, string key)
        {
            try
            {
                string template = lookup(key);
                return string.IsNullOrEmpty(template) ? null : template;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Write(ByteWriter w, LocalText t)
        {
            t = t ?? Plain("");
            MessageFields.WriteText(w, t.Text, MaxTextChars);
            MessageFields.WriteString(w, t.Key ?? "", MaxKeyBytes, "LocalText.Key");
            string[] args = t.Args ?? Array.Empty<string>();
            int pairs = Math.Min(MaxArgs, args.Length / 2);
            w.WriteVarUInt32((uint)pairs);
            for (int i = 0; i < pairs; i++)
            {
                MessageFields.WriteString(w, Ascii(args[2 * i], MaxArgNameBytes), MaxArgNameBytes, "LocalText.ArgName");
                MessageFields.WriteText(w, args[2 * i + 1] ?? "", MaxArgValueChars);
            }
        }

        public static LocalText Read(ByteReader r)
        {
            var t = new LocalText { Text = MessageFields.ReadText(r, MaxTextChars, "LocalText.Text") };
            string key = MessageFields.ReadString(r, MaxKeyBytes, "LocalText.Key");
            t.Key = string.IsNullOrEmpty(key) ? null : key;
            uint pairs = r.ReadVarUInt32();
            if (pairs > MaxArgs) throw new DecodeException("LocalText: " + pairs + " parameters");
            var args = new string[pairs * 2];
            for (int i = 0; i < pairs; i++)
            {
                args[2 * i] = MessageFields.ReadString(r, MaxArgNameBytes, "LocalText.ArgName");
                args[2 * i + 1] = MessageFields.ReadText(r, MaxArgValueChars, "LocalText.ArgValue");
            }
            t.Args = args;
            return t;
        }

        private static string Ascii(string name, int max)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
            {
                if (sb.Length >= max) break;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_') sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// The keys and English texts of everything the host tells its players in words (<see cref="LocalText"/>): system chat
    /// lines, the reasons of kicks, bans, ends and drops, and refusal details. The game layer's locale tables
    /// (<c>Data/locale.*.json</c>) translate them; a key a receiver lacks falls back to the English text sent along.
    /// </summary>
    public static class NetText
    {
        /// <summary>Every key starts with it (and a receiver looks up no other key).</summary>
        public const string KeyPrefix = "st.net.";

        // system chat lines
        public const string ChatJoining = "st.net.chat.joining";
        public const string ChatJoined = "st.net.chat.joined";
        public const string ChatLeft = "st.net.chat.left";
        public const string ChatRemoved = "st.net.chat.removed";
        public const string ChatBanned = "st.net.chat.banned";
        public const string ChatCancelled = "st.net.chat.cancelled";
        public const string ChatLostJoining = "st.net.chat.lostJoining";
        public const string ChatLoadFailed = "st.net.chat.loadFailed";
        public const string ChatTookTooLong = "st.net.chat.tookTooLong";
        public const string ChatJoinStalled = "st.net.chat.joinStalled";
        public const string ChatNotResponding = "st.net.chat.notResponding";
        public const string ChatNotRespondingJoining = "st.net.chat.notRespondingJoining";
        public const string ChatTooSlow = "st.net.chat.tooSlow";
        public const string ChatRoleBuilder = "st.net.chat.role.builder";
        public const string ChatRoleViewer = "st.net.chat.role.viewer";
        public const string ChatRoleCoHost = "st.net.chat.role.cohost";
        public const string ChatDropped = "st.net.chat.dropped";

        // kick, ban, end and drop reasons
        public const string KickByHost = "st.net.kick.host";
        public const string KickByCoHost = "st.net.kick.cohost";
        public const string BanByHost = "st.net.ban.host";
        public const string EndByHost = "st.net.end.host";
        public const string EndHostLeftCity = "st.net.end.hostLeftCity";
        public const string EndHostError = "st.net.end.hostError";
        public const string DropTookTooLong = "st.net.drop.tookTooLong";
        public const string DropNotResponding = "st.net.drop.notResponding";
        public const string DropFailing = "st.net.drop.failing";
        public const string DropTooSlow = "st.net.drop.tooSlow";
        public const string DropDownloadStalled = "st.net.drop.downloadStalled";
        public const string DropLoadStalled = "st.net.drop.loadStalled";
        public const string DropLoadTooLong = "st.net.drop.loadTooLong";
        public const string DropResends = "st.net.drop.resends";
        public const string DropSnapshotFailed = "st.net.drop.snapshotFailed";
        public const string DropApplyFailed = "st.net.drop.applyFailed";
        public const string DropNotApplying = "st.net.drop.notApplying";
        public const string DropTooFast = "st.net.drop.tooFast";

        // refusal details
        public const string RefuseShuttingDown = "st.net.refuse.shuttingDown";
        public const string RefuseProtocol = "st.net.refuse.protocol";
        public const string RefuseProtocolVersions = "st.net.refuse.protocolVersions";
        public const string RefuseBanned = "st.net.refuse.banned";
        public const string RefuseNotInvited = "st.net.refuse.notInvited";
        public const string RefuseDirectNeedsPassword = "st.net.refuse.directNeedsPassword";
        public const string RefuseLockout = "st.net.refuse.lockout";
        public const string RefuseWrongPassword = "st.net.refuse.wrongPassword";
        public const string RefuseWrongPasswordLocked = "st.net.refuse.wrongPasswordLocked";
        public const string RefuseTooManyAttempts = "st.net.refuse.tooManyAttempts";
        public const string RefuseBuild = "st.net.refuse.build";
        public const string RefuseGame = "st.net.refuse.game";
        public const string RefuseFull = "st.net.refuse.full";
        public const string RefuseStillJoining = "st.net.refuse.stillJoining";
        public const string RefuseCooldown = "st.net.refuse.cooldown";

        // a wait (the {WAIT} of a text whose SECONDS the receiver words in its own language)
        public const string TimeSeconds = "st.net.time.seconds";
        public const string TimeMinutes = "st.net.time.minutes";
        public const string TimeHours = "st.net.time.hours";

        // what a joiner says about the host (game layer)
        public const string ClientKicked = "st.net.client.kicked";
        public const string ClientBanned = "st.net.client.banned";
        public const string ClientKickedFor = "st.net.client.kickedFor";
        public const string ClientBannedFor = "st.net.client.bannedFor";
        public const string ClientHostNotProven = "st.net.client.hostProof";

        /// <summary>The English text of every key (the fallback sent along, and the reference for the locale tables).</summary>
        public static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChatJoining] = "{NAME} is joining…",
            [ChatJoined] = "{NAME} joined the city",
            [ChatLeft] = "{NAME} left",
            [ChatRemoved] = "{NAME} was removed",
            [ChatBanned] = "{NAME} was banned",
            [ChatCancelled] = "{NAME} cancelled joining.",
            [ChatLostJoining] = "{NAME} lost the connection while joining.",
            [ChatLoadFailed] = "{NAME} could not load the city.",
            [ChatTookTooLong] = "{NAME} took too long to join.",
            [ChatJoinStalled] = "{NAME} could not finish joining.",
            [ChatNotResponding] = "{NAME} stopped responding.",
            [ChatNotRespondingJoining] = "{NAME} stopped responding while joining.",
            [ChatTooSlow] = "{NAME} couldn't keep up with the session and was disconnected; they can join again to catch up.",
            [ChatRoleBuilder] = "{NAME} is now a builder.",
            [ChatRoleViewer] = "{NAME} is now a viewer.",
            [ChatRoleCoHost] = "{NAME} is now a co-host.",
            [ChatDropped] = "{NAME} was disconnected because of a technical problem; they can join again to catch up.",
            [KickByHost] = "removed by the host",
            [KickByCoHost] = "removed by {NAME}",
            [BanByHost] = "banned by the host",
            [EndByHost] = "The host ended the session.",
            [EndHostLeftCity] = "The host left the city.",
            [EndHostError] = "The host's game hit an error ({FEATURE}), so the session ended. The host can start it again.",
            [DropTookTooLong] = "Joining took too long (more than {MINUTES} minutes). Try joining again.",
            [DropNotResponding] = "The host received nothing from your game for {SECONDS} s. Join again to continue.",
            [DropFailing] = "The host could not handle the data your game sends. Join again to get a fresh copy of the city.",
            [DropTooSlow] = "Your connection could not keep up with the session. Join again to catch up.",
            [DropDownloadStalled] = "The city download made no progress for {SECONDS} s. Try joining again.",
            [DropLoadStalled] = "Loading the city made no progress for {SECONDS} s. Try joining again.",
            [DropLoadTooLong] = "Loading the city took more than {MINUTES} minutes. Try joining again.",
            [DropResends] = "The city download kept failing. Try joining again.",
            [DropSnapshotFailed] = "The host could not save the city ({DETAIL}). Try joining again.",
            [DropApplyFailed] = "The host could not process your changes. Join again to get a fresh copy of the city.",
            [DropNotApplying] = "Your game stopped processing the host's updates. Join again to get a fresh copy of the city.",
            [DropTooFast] = "Your game sent changes faster than the host could apply them. Join again to continue.",
            [RefuseShuttingDown] = "The host is ending the session.",
            [RefuseProtocol] = "Skyline Together versions differ. Install the same Skyline Together version on both PCs.",
            [RefuseProtocolVersions] = "Skyline Together versions differ (the host uses network protocol {HOST}). Install the same Skyline Together version on both PCs.",
            [RefuseBanned] = "You are banned from this session.",
            [RefuseNotInvited] = "This session is open to the host's Steam friends and invited players only.",
            [RefuseDirectNeedsPassword] = "This host accepts direct connections only with a session password. Join through Steam instead.",
            [RefuseLockout] = "Too many wrong passwords. Try again in {WAIT}.",
            [RefuseWrongPassword] = "Wrong password.",
            [RefuseWrongPasswordLocked] = "Wrong password. That was too many wrong tries; try again in {WAIT}.",
            [RefuseTooManyAttempts] = "Too many join attempts at once. Try again in a few seconds.",
            [RefuseBuild] = "Skyline Together builds differ (host build {HOST}, yours {YOU}). You and the host need exactly the same Skyline Together version.",
            [RefuseGame] = "Game versions differ (host {HOST}, you {YOU}). Update the game on the PC with the older version.",
            [RefuseFull] = "The session is full.",
            [RefuseStillJoining] = "You are still joining this session from an earlier attempt. Wait for it to finish, or restart the game and join again.",
            [RefuseCooldown] = "Your last attempts to join this session didn't finish. You can join again in {WAIT}.",
            [TimeSeconds] = "{N} s",
            [TimeMinutes] = "about {N} minutes",
            [TimeHours] = "about {N} hours",
            [ClientKicked] = "You were {REASON}.",
            [ClientBanned] = "You were removed from the session and {REASON}.",
            [ClientKickedFor] = "You were removed from the session: {REASON}",
            [ClientBannedFor] = "You were removed from the session and banned: {REASON}",
            [ClientHostNotProven] = "The host couldn't prove that it knows the session password.",
        };

        /// <summary>A text for <paramref name="key"/> with its English fallback filled in.</summary>
        public static LocalText Of(string key, params string[] args)
        {
            English.TryGetValue(key ?? "", out string template);
            return new LocalText { Key = key, Args = args ?? Array.Empty<string>(), Text = Format(template ?? key ?? "", WithWait(args, null)) };
        }

        /// <summary>
        /// True for the reasons a kick or ban carries as a phrase ("removed by the host"), which the joiner's screen puts
        /// into a sentence (<see cref="ClientKicked"/>, <see cref="ClientBanned"/>); any other reason is a sentence of its own
        /// and follows a colon (<see cref="ClientKickedFor"/>, <see cref="ClientBannedFor"/>).
        /// </summary>
        public static bool IsRemovalPhrase(string key) => key == KickByHost || key == KickByCoHost || key == BanByHost;

        /// <summary>
        /// <paramref name="args"/> plus <c>WAIT</c>, the <c>SECONDS</c> argument worded as a wait (<see cref="Wait"/>).
        /// Unchanged without <c>SECONDS</c> or with a <c>WAIT</c> of its own. The host sends only the seconds, so each
        /// receiver words the wait in its own language.
        /// </summary>
        public static string[] WithWait(string[] args, Func<string, string> lookup)
        {
            if (args == null) return null;
            string seconds = null;
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                if (string.Equals(args[i], "WAIT", StringComparison.Ordinal)) return args;
                if (string.Equals(args[i], "SECONDS", StringComparison.Ordinal)) seconds = args[i + 1];
            }
            if (seconds == null) return args;
            var extended = new string[(args.Length / 2) * 2 + 2];
            Array.Copy(args, extended, extended.Length - 2);
            extended[extended.Length - 2] = "WAIT";
            extended[extended.Length - 1] = Wait(seconds, lookup);
            return extended;
        }

        /// <summary>
        /// A wait in words: under 2 minutes in seconds ("40 s"), under 2 hours in minutes ("about 10 minutes"), else in
        /// hours ("about 4 hours"); the templates of <see cref="TimeSeconds"/>, <see cref="TimeMinutes"/> and
        /// <see cref="TimeHours"/> come from <paramref name="lookup"/>, else from <see cref="English"/>.
        /// </summary>
        public static string Wait(string secondsText, Func<string, string> lookup)
        {
            if (!int.TryParse(secondsText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int seconds))
            {
                return (secondsText ?? "?") + " s";
            }
            string key;
            int n;
            if (seconds < 120)
            {
                key = TimeSeconds;
                n = seconds;
            }
            else if (seconds < 2 * 3600)
            {
                key = TimeMinutes;
                n = (int)Math.Floor(seconds / 60.0 + 0.5);
            }
            else
            {
                key = TimeHours;
                n = (int)Math.Floor(seconds / 3600.0 + 0.5);
            }
            string template = lookup?.Invoke(key);
            if (string.IsNullOrEmpty(template)) English.TryGetValue(key, out template);
            return Format(template, new[] { "N", n.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        /// <summary>Replaces each <c>{NAME}</c> of <paramref name="template"/> with its value from <paramref name="args"/> (name, value, ...).</summary>
        public static string Format(string template, string[] args)
        {
            if (template == null || args == null || args.Length < 2) return template;
            // one pass over the template: a value is never searched for placeholders again (a player named "{REASON}"
            // got the next argument spliced into the name)
            var sb = new StringBuilder(template.Length + 32);
            int at = 0;
            while (at < template.Length)
            {
                int open = template.IndexOf('{', at);
                if (open < 0) break;
                int close = template.IndexOf('}', open + 1);
                if (close < 0) break;
                string key = template.Substring(open + 1, close - open - 1);
                string value = null;
                for (int i = 0; i + 1 < args.Length; i += 2)
                {
                    if (string.Equals(args[i], key, StringComparison.Ordinal))
                    {
                        value = args[i + 1] ?? "";
                        break;
                    }
                }
                sb.Append(template, at, open - at);
                if (value != null)
                {
                    sb.Append(value);
                    at = close + 1;
                }
                else
                {
                    sb.Append('{');
                    at = open + 1;
                }
            }
            sb.Append(template, at, template.Length - at);
            return sb.ToString();
        }
    }
}
