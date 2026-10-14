using System;

namespace SkylineTogether.Core.Session
{
    /// <summary>Where the recovery after an unexpected end of a client session stands.</summary>
    public enum RejoinPhase
    {
        /// <summary>Nothing to recover (no session, a live session, or the player decided).</summary>
        Idle,
        /// <summary>Waiting for the next automatic attempt (<see cref="RejoinController.NextAttemptAt"/>).</summary>
        Waiting,
        /// <summary>An attempt (a complete join with a fresh snapshot) is running.</summary>
        Attempting,
        /// <summary>No more automatic attempts: the player chooses (reconnect, main menu, or stay offline).</summary>
        NeedsChoice,
    }

    /// <summary>Backoff and attempt limit of the automatic reconnects of a <see cref="RejoinController"/>.</summary>
    public sealed class RejoinOptions
    {
        /// <summary>Delay before attempt n (1-based) is <c>BackoffSeconds[min(n, length) - 1]</c>.</summary>
        public double[] BackoffSeconds = { 2, 5, 10, 20, 30, 30 };

        /// <summary>Automatic attempts before the player is asked.</summary>
        public int MaxAttempts = 6;
    }

    /// <summary>
    /// Recovery of a client whose session ended while it was in the host's city (ARCH §7.8, §12.2, simplified): the
    /// world is the host's city without a session, which must not quietly turn into a single-player game. Retryable
    /// ends (lost connection, failed join) schedule automatic rejoins with backoff, each through the normal join path
    /// (connect, handshake, fresh snapshot, load); everything else (the host ended the session, a kick, a refusal, a
    /// repeated failure) asks the player. The game layer holds the city paused while <see cref="Recovering"/> and acts
    /// on <see cref="Tick"/> returning true. Pure logic: the clock is passed in. Main thread only.
    /// </summary>
    public sealed class RejoinController
    {
        private readonly RejoinOptions _options;

        public RejoinController(RejoinOptions options = null)
        {
            _options = options ?? new RejoinOptions();
            if (_options.BackoffSeconds == null || _options.BackoffSeconds.Length == 0) _options.BackoffSeconds = new double[] { 5 };
            if (_options.MaxAttempts < 1) _options.MaxAttempts = 1;
        }

        public RejoinPhase Phase { get; private set; }

        /// <summary>The number of the running or next attempt (1-based; 0 before the first).</summary>
        public int Attempt { get; private set; }

        public int MaxAttempts => _options.MaxAttempts;

        /// <summary>When the next automatic attempt starts (<see cref="RejoinPhase.Waiting"/>).</summary>
        public double NextAttemptAt { get; private set; }

        /// <summary>Why recovery started, or why the last attempt failed (shown to the player).</summary>
        public string Reason { get; private set; }

        /// <summary>How the session that started the recovery ended.</summary>
        public ClientEndKind StartKind { get; private set; }

        /// <summary>True when "Reconnect" makes sense (the host may still be hosting).</summary>
        public bool CanReconnect { get; private set; }

        /// <summary>Successful automatic or manual rejoins so far (diagnostics).</summary>
        public int Rejoins { get; private set; }

        /// <summary>True while the city must stay held: waiting, attempting or asking the player.</summary>
        public bool Recovering => Phase != RejoinPhase.Idle;

        /// <summary>
        /// A client session ended. <paramref name="enteredCity"/>: the client had started loading the host's city (the
        /// local world is the host's). A first join that fails before that is no recovery case: the player is still
        /// where they were. Returns true when recovery is (still) active.
        /// </summary>
        public bool OnClientEnded(ClientEndKind kind, string reason, bool enteredCity, double now)
        {
            bool retryable = SessionClient.IsRetryable(kind);
            if (Phase == RejoinPhase.Idle || Phase == RejoinPhase.NeedsChoice)
            {
                if (Phase == RejoinPhase.Idle && !enteredCity) return false; // a first join that failed early
                if (kind == ClientEndKind.Left)
                {
                    Reset();
                    return false;
                }
                StartKind = kind;
                Reason = reason;
                Attempt = 0;
                if (retryable)
                {
                    CanReconnect = true;
                    Schedule(now);
                }
                else
                {
                    CanReconnect = kind == ClientEndKind.ProtocolError || kind == ClientEndKind.LoadFailed || kind == ClientEndKind.LocalFault || kind == ClientEndKind.Failed;
                    Phase = RejoinPhase.NeedsChoice;
                }
                return true;
            }

            // an attempt (or a session resumed by one) ended
            if (kind == ClientEndKind.Left)
            {
                Reset();
                return false;
            }
            Reason = reason;
            if (kind == ClientEndKind.SessionEnded || kind == ClientEndKind.Kicked)
            {
                StartKind = kind;
                CanReconnect = false;
                Phase = RejoinPhase.NeedsChoice;
                return true;
            }
            if (retryable && Attempt < _options.MaxAttempts)
            {
                Schedule(now);
                return true;
            }
            CanReconnect = retryable || kind == ClientEndKind.LoadFailed || kind == ClientEndKind.LocalFault || kind == ClientEndKind.Failed;
            Phase = RejoinPhase.NeedsChoice;
            return true;
        }

        /// <summary>
        /// Once per frame; true when an attempt must start now (the phase is then <see cref="RejoinPhase.Attempting"/>).
        /// <paramref name="canStart"/> false postpones a due attempt without spending it (e.g. the game is still loading).
        /// </summary>
        public bool Tick(double now, bool canStart = true)
        {
            if (Phase != RejoinPhase.Waiting || now < NextAttemptAt) return false;
            if (!canStart)
            {
                NextAttemptAt = now + 1.0;
                return false;
            }
            Attempt++;
            Phase = RejoinPhase.Attempting;
            return true;
        }

        /// <summary>The attempt's client went live: recovery is over.</summary>
        public void OnLive()
        {
            if (Phase == RejoinPhase.Attempting) Rejoins++;
            Reset();
        }

        /// <summary>The player asked to reconnect now (from the waiting or the choice state).</summary>
        public void ReconnectNow(double now)
        {
            if (Phase == RejoinPhase.Attempting) return;
            if (Phase == RejoinPhase.NeedsChoice || Phase == RejoinPhase.Idle) Attempt = 0;
            CanReconnect = true;
            Phase = RejoinPhase.Waiting;
            NextAttemptAt = now;
        }

        /// <summary>Stops automatic attempts and asks the player (e.g. they cancelled a running attempt).</summary>
        public void AskPlayer(string reason)
        {
            Phase = RejoinPhase.NeedsChoice;
            CanReconnect = true;
            if (reason != null) Reason = reason;
        }

        /// <summary>The player decided (main menu, stay offline) or a new session started: no recovery any more.</summary>
        public void Reset()
        {
            Phase = RejoinPhase.Idle;
            Attempt = 0;
            NextAttemptAt = 0;
            CanReconnect = false;
            StartKind = ClientEndKind.None;
        }

        /// <summary>Seconds until the next attempt (0 when due or not waiting).</summary>
        public double SecondsUntilAttempt(double now) => Phase == RejoinPhase.Waiting ? Math.Max(0, NextAttemptAt - now) : 0;

        private void Schedule(double now)
        {
            double[] b = _options.BackoffSeconds;
            int index = Math.Min(Attempt, b.Length - 1);
            NextAttemptAt = now + Math.Max(0, b[index]);
            Phase = RejoinPhase.Waiting;
        }
    }
}
