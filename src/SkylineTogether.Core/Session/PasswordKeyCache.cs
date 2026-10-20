using System;
using System.Threading.Tasks;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// The password key of one salt (<see cref="SessionCrypto.DeriveKey"/>), derived once per password: on a worker
    /// thread when <c>background</c> is set (200 000 PBKDF2 rounds take a noticeable fraction of a second, which the game's
    /// main thread must not wait for), else at once on the caller's thread. Main thread only (the worker only derives).
    /// </summary>
    public sealed class PasswordKeyCache
    {
        private readonly byte[] _salt;
        private readonly bool _background;
        private string _keyFor;
        private byte[] _key;
        private Task<byte[]> _task;
        private string _taskFor;

        public PasswordKeyCache(byte[] salt, bool background)
        {
            _salt = salt ?? Array.Empty<byte>();
            _background = background;
        }

        /// <summary>True while a derivation runs on the worker thread.</summary>
        public bool Deriving => _task != null && !_task.IsCompleted;

        /// <summary>
        /// The key of <paramref name="password"/>, or null while it is still being derived in the background (ask again
        /// next frame). Starts the derivation when the password is new.
        /// </summary>
        public byte[] TryGet(string password)
        {
            password = password ?? "";
            if (_key != null && string.Equals(_keyFor, password, StringComparison.Ordinal)) return _key;
            if (!_background)
            {
                _key = SessionCrypto.DeriveKey(password, _salt);
                _keyFor = password;
                return _key;
            }
            if (_task == null || !string.Equals(_taskFor, password, StringComparison.Ordinal))
            {
                byte[] salt = _salt;
                string p = password;
                _taskFor = password;
                _task = Task.Run(() => SessionCrypto.DeriveKey(p, salt));
            }
            if (!_task.IsCompleted) return null;
            Task<byte[]> done = _task;
            _task = null;
            _key = done.Status == TaskStatus.RanToCompletion ? done.Result : SessionCrypto.DeriveKey(password, _salt);
            _keyFor = password;
            return _key;
        }
    }
}
