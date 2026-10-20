# Security policy

## Reporting a vulnerability

Please do **not** open a public issue for a security problem. Report it privately through GitHub's private
vulnerability reporting: the repository's **Security** tab → **Report a vulnerability**
([direct link](https://github.com/ljkx/SkylineTogether/security/advisories/new)). If that is not available to you,
open an issue that asks for a private contact, without any details.

Please include what an attacker needs (a player in the session, someone who knows the host's Steam ID or IP address,
...), what they can achieve, the version and build id you tested and, if possible, steps to reproduce. You will get an
answer within a few days. Fixes go into the next release.

## Supported versions

| Version | Supported |
|---|---|
| The latest release (Paradox Mods and GitHub) | Yes |
| Older releases | No; update to the latest release |

## Scope

In scope: Skyline Together itself (the mod, its network protocol, the GitHub installer and log collector). Out of
scope: bugs in Cities: Skylines II, Steam or Paradox Mods (report them to Paradox Interactive or Valve), other mods,
including the unrelated "CS2 Multiplayer Mod", and attacks that need control of a player's PC.

## How Skyline Together uses the network

- **Peer to peer through Steam.** Sessions use Steam Networking Sockets: connections by Steam ID go through Valve's
  relay network and are encrypted by Steam. There is no central server run by this project.
- **Optional direct IP connections.** When the host turns on **Allow direct IP connections** (off by default), the host's
  game also listens on a UDP port (default 27015). Steam may accept these connections without authentication (the remote
  side's Steam identity is then not verified, and the connection may be unencrypted), so the host's game **requires the
  session password** for every direct connection and for any connection without a Steam-authenticated identity: the
  port only opens while a host password is set, and a direct joiner is refused ("not invited") when there is none.
  Only the direct-IP socket accepts connections without authentication, and a connection Steam flags as
  unauthenticated is identified by its address, whatever Steam ID it presents.
  Steam P2P connections are identified by the Steam ID Steam authenticated; a Steam ID a message claims is never trusted.
- **Host-authoritative.** The host's game runs the simulation. Other players' games send proposals (what they built,
  changed or deleted); the host's game checks the sender's role, conflicts and rate limits before it applies them.

## Untrusted input

Everything received from the network is treated as untrusted:

- Decoders work against hard limits (`src/SkylineTogether.Core/Protocol/Limits.cs`): message, fragment and decompressed
  sizes, per-message-kind body sizes (checked before decompression), counts of parts, sections and fragments, proposal
  sizes, chat (500 characters) and name (64 characters) lengths. Malformed input raises `DecodeException`; the host
  disconnects the sender, a joined player's game leaves the session.
- Each side reads only the message kinds the other side sends, judged on the envelope's first byte before anything is
  decompressed or reassembled; other kinds are dropped unread. Each player's messages may decompress to 8 MiB per frame
  (the rest waits for the next frames; a player that keeps sending more is disconnected), the host's network poll
  checks its time budget after every message, and fragmented messages that stop half-way are dropped after two minutes.
- Texts from other players are sanitized (control characters, bidirectional overrides and line separators are removed).
- Before a player is admitted, the host's game only accepts hello, control and ping messages (any other kind ends the
  connection unread), at most 64 KiB each until the password is proven and 1 MiB after it, and 4 MiB of decompressed
  handshake data in total. A connection must send its hello within 10 seconds and finish the handshake within 30;
  the Steam transport allows 8 handshakes at once (2 per Steam ID or address) besides the players, and gives a
  connection its large receive buffer only once it is admitted. Pings are not answered before admission. Chat needs
  admission; presence and other messages need a fully joined player. The host never trusts the sender id or the
  Steam ID a message claims.
- The handshake reveals nothing before authentication: bans and the join policy are checked first (on the
  transport's identity), then the protocol version, then the password; only a player who passed them learns whether
  their build, game version, code mods, DLC or asset packs differ (a protocol mismatch before the password is refused
  without versions). The joining player's game sends its own lists (code mods, DLC, asset packs with their Paradox Mods
  ids, the values of the mod settings a host pushes) only after the host accepted its password (or said none is
  needed) and proved the password itself. A second connection of a player who is already in the session replaces the
  old one only once it is fully admitted.
- A joining player's watchdogs run on the host: a download that makes no progress for 3 minutes, a load whose reported
  progress does not grow for 5 minutes or that takes more than 20 minutes, and a joiner that keeps asking for the
  city again are dropped. Joins that do not finish make the same Steam ID or address wait before the next one (the
  first is free, then 30 seconds, 2, 10 and 30 minutes). A player's "Join anyway" (when the host lets
  players join with different mods) only skips the comparison of code mods and asset packs; bans, the join policy, the
  password, the build, game version, DLC and the player limit are checked as always.
- Per-player rate limits apply to proposals (120 per second, burst 240), presence updates and integrity repair
  requests.

## Passwords

The session password is never sent over the network. Both games derive `key = PBKDF2-HMAC-SHA256(password, salt,
200 000 rounds)` (on a worker thread) and prove it with `HMAC-SHA256(key, label ‖ sessionId ‖ hostNonce ‖ clientNonce ‖
salt ‖ host Steam ID)`, where the label names the direction and the host Steam ID is the one the joining player dialled
(0 for a direct connection); salt, nonces and session id come from a cryptographic random generator. The joining
player's game answers first; the host draws a fresh nonce for every challenge, accepts one answer per challenge (a
recorded answer is useless on another connection) and compares it in constant time, then proves the password back. The
joining player's game checks the host's proof before it sends anything else, so a host that does not know the password
is detected and learns no mod lists. An answer relayed through another Steam ID fails, because it names the Steam ID
the player dialled.

Wrong answers are limited per Steam ID and per address (for direct and unauthenticated connections; an IPv6 address
counts as its /64 network): three are free, then the identity is locked out for 5 seconds, doubling with every further
wrong answer up to 10 minutes; a right answer clears the record. At most two password challenges may be open at once per
identity. A locked-out player is told how long to wait (not "wrong password").

## Moderation

- **Kick** removes a player; they may join again. **Ban** removes them and keeps them out: by Steam ID, and by address
  for a player who joined over direct IP (an IPv6 address by its /64 network). The ban list is stored with the host's
  settings and can be edited in the host options (unban).
- **Roles are remembered** for the session: a player the host made a viewer (or co-host) gets the same role when they
  join again. With **New players join as viewers** (host option, off by default) players who join for the first time in
  a session start as viewers.
- A player who stops responding, cannot keep up or whose changes keep failing is **dropped**, not kicked: their game
  offers to join again. Names are unique in a session and never the host's; the host's chat lines carry a badge and
  the session's own lines an info mark.
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
  loader, which was not designed for hostile files. Only join hosts you trust. (The render prefabs a host bundles with
  the save are added only as prefab and texture assets with plain file names inside the join folder.)
- **Many addresses.** The password attempt limit is per Steam ID and per address (IPv6 per /64 network); an attacker
  with many addresses gets three free guesses on each. Use a password that is not easy to guess when direct IP
  connections are on.
- **Relays between two direct connections.** When both the joining player and the host use direct IP without Steam
  authentication, nothing ties the password proof to the connection (Steam offers nothing to bind it to): someone the
  player connects to instead of the host could pass the host's challenge on. Join by Steam ID where you can.
- **Address bans** are as good as the address: a player without a Steam identity who changes their IP address is not
  recognised again.
- **Passwords are stored in plain text** in the game's settings file on each PC (the host password and the last join
  password).
- **Visible identifiers.** Players in a session see each other's Steam IDs. With direct IP connections, joining players
  know the host's IP address. A hosted session creates a friends-only Steam lobby whose data includes the session name
  and whether a password is set.
- **Logs and diagnostics** contain player names, Steam IDs, session names and, for direct-IP bans, IP addresses (not
  passwords or chat). The game's `Player.log` contains paths with your Windows user name; `Collect-Logs.cmd` replaces
  it in the files it collects. Crash dumps (`.dmp`) can contain anything that was in the game's memory, including
  passwords: share them only privately (`Collect-Logs.cmd` includes them only with `-IncludeDumps`). The mod's own log
  is rate-limited and capped at 64 MiB per game start. Think before attaching files to a public issue; see
  [Reporting a bug](docs/guide/troubleshooting.md#reporting-a-bug).
