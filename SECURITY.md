# Security policy

## Reporting a vulnerability

Please do **not** open a public issue for a security problem. Report it privately through GitHub's private
vulnerability reporting: the repository's **Security** tab → **Report a vulnerability**
([direct link](https://github.com/ljkx/SkylineTogether/security/advisories/new)). If that is not available to you,
open an issue that asks for a private contact, without any details.

Please include what an attacker needs (a player in the session, someone who knows the host's Steam ID or IP address,
...), what they can achieve, the build you tested and, if possible, steps to reproduce. You will get an answer within a
few days. Skyline Together is a hobby project in early testing; fixes go into the next build, and only the latest build
is supported.

## How Skyline Together uses the network

- **Peer to peer through Steam.** Sessions use Steam Networking Sockets: connections by Steam ID go through Valve's
  relay network and are encrypted by Steam. There is no central server run by this project.
- **Optional direct IP connections.** When the host turns on **Allow direct IP connections** (off by default), the host's
  game also listens on a UDP port (default 27015). Steam may accept these connections without authentication (the remote
  side's Steam identity is then not verified, and the connection may be unencrypted), so the host's game **requires the
  session password** for every direct connection and for any connection without a Steam-authenticated identity: the
  port only opens while a host password is set, and a direct joiner is refused ("not invited") when there is none.
  Steam P2P connections are identified by the Steam ID Steam authenticated; a Steam ID a message claims is never trusted.
- **Host-authoritative.** The host's game runs the simulation. Other players' games send proposals (what they built,
  changed or deleted); the host's game checks the sender's role, conflicts and rate limits before it applies them.

## Untrusted input

Everything received from the network is treated as untrusted:

- Decoders work against hard limits (`src/SkylineTogether.Core/Protocol/Limits.cs`): message, fragment and decompressed
  sizes, per-message-kind body sizes (checked before decompression), counts of parts, sections and fragments, proposal
  sizes, chat (500 characters) and name (64 characters) lengths. Malformed input raises `DecodeException`; the host
  disconnects the sender, a joined player's game leaves the session.
- Texts from other players are sanitized (control characters, bidirectional overrides and line separators are removed).
- Before a player is admitted, the host's game only handles the handshake messages, with a 30-second timeout. Chat
  needs admission; presence and other messages need a fully joined player. The host never trusts the sender id or the
  Steam ID a message claims.
- The handshake reveals nothing before authentication: bans and the join policy are checked first (on the
  transport's identity), then the password; only a player who passed both learns whether their build, game version or
  code mods differ (a protocol mismatch before the password is refused without versions).
- Per-player rate limits apply to proposals (120 per second, burst 240), presence updates and integrity repair
  requests.

## Passwords

The session password is never sent over the network. The joining player's game proves that it knows it with
`HMAC-SHA256(PBKDF2-HMAC-SHA256(password, salt, 10 000 rounds), hostNonce ‖ clientNonce ‖ sessionId)`; salt, nonces and
session id come from a cryptographic random generator. The host draws a fresh nonce for every challenge, accepts one
answer per challenge (a recorded answer is useless on another connection) and compares it in constant time.

Wrong answers are limited per Steam ID and per address (for direct and unauthenticated connections): three are free,
then the identity is locked out for 5 seconds, doubling with every further wrong answer up to 10 minutes; a right answer
clears the record. At most two password challenges may be open at once per identity.

## Moderation

- **Kick** removes a player; they may join again. **Ban** removes them and keeps them out: by Steam ID, or by address
  for a player who joined without a Steam identity. The ban list is stored with the host's settings and can be edited
  in the host options (unban).
- **Friends and invited only** (host option) refuses Steam players who are neither the host's Steam friends nor were
  invited from the panel ("not invited"). Direct joiners without a Steam identity still need the password.
- **Co-hosts** (appointed by the host) may kick builders and viewers and switch them between builder and viewer. They
  cannot touch the host or other co-hosts, cannot ban, and cannot change the host's options, password, join policy or
  ban list. The host's game checks every request against the sender's role.

## Known limitations

These are known and accepted for now; reports about them are welcome only if you see a practical improvement.

- **Trust the people you play with.** A builder can do anything a player can do in the city, including demolishing it.
  Use a password or **Friends and invited only**, and make players you do not know viewers (chat only).
- **Joining loads the host's save.** A joining player's game loads a city save sent by the host, through the game's own
  loader, which was not designed for hostile files. Only join hosts you trust.
- **Many addresses.** The password attempt limit is per Steam ID and per address; an attacker with many addresses gets
  three free guesses on each. Use a password that is not easy to guess when direct IP connections are on.
- **Address bans** are as good as the address: a player without a Steam identity who changes their IP address is not
  recognised again.
- **Passwords are stored in plain text** in the game's settings file on each PC (the host password and the last join
  password).
- **Visible identifiers.** Players in a session see each other's Steam IDs. With direct IP connections, joining players
  know the host's IP address. A hosted session creates a friends-only Steam lobby whose data includes the session name
  and whether a password is set.
- **Logs and diagnostics** contain player names and Steam IDs (not passwords or chat). Think before attaching them to a
  public issue.
