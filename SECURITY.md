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
  game also listens on a UDP port (default 27015). These connections are accepted without Steam authentication: the
  remote side's Steam identity is not verified by Steam, and the connection may be unencrypted.
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
  needs admission; presence and other messages need a fully joined player. The host never trusts the sender id a
  message claims.
- Per-player rate limits apply to proposals (120 per second, burst 240), presence updates and integrity repair
  requests.

## Passwords

The session password is never sent over the network. The joining player's game proves that it knows it with
HMAC-SHA256 over a challenge (random salt and nonces from a cryptographic random generator); the host compares the
answer in constant time.

## Known limitations

These are known and accepted for now; reports about them are welcome only if you see a practical improvement.

- **Trust the people you play with.** A builder can do anything a player can do in the city, including demolishing it.
  Use a password, and make players you do not know viewers (chat only). There is no ban list yet; a kicked player can
  join again unless a password keeps them out.
- **Joining loads the host's save.** A joining player's game loads a city save sent by the host, through the game's own
  loader, which was not designed for hostile files. Only join hosts you trust.
- **No limit on password attempts.** A refused player can reconnect and try again at once.
- **The password is checked last.** Someone who can connect, even without the password, learns from the refusal
  whether their game version and code mods differ from the host's.
- **Passwords are stored in plain text** in the game's settings file on each PC (the host password and the last join
  password).
- **Visible identifiers.** Players in a session see each other's Steam IDs. With direct IP connections, joining players
  know the host's IP address. A hosted session creates a friends-only Steam lobby whose data includes the session name
  and whether a password is set.
- **Logs and diagnostics** contain player names and Steam IDs (not passwords or chat). Think before attaching them to a
  public issue.
