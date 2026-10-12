#!/usr/bin/env python3
"""Sync coverage extractor (docs/design/SYNC-COVERAGE.md).

Enumerates every persistent state of Cities: Skylines II from the decompiled Game assembly, exactly as the game's own
serializer finds it:
  * components and buffers: every struct implementing ISerializable or IEmptySerializable together with
    IComponentData / IBufferElementData / ISharedComponentData (Colossal.Core ComponentSerializerLibrary.Initialize),
  * systems: every concrete class implementing IDefaultSerializable or IJobSerializable, directly or through a base
    class (SystemSerializerLibrary.Initialize), with its update phases from Game.Common/SystemOrder.cs.
Each item is matched against tools/sync_coverage_rules.txt (first matching rule wins: exact name, then the longest
prefix) and the generated part of SYNC-COVERAGE.md is rewritten between the GENERATED markers.

Usage:  python tools/sync_coverage.py [--decomp <decomp/Game>] [--write docs/design/SYNC-COVERAGE.md] [--check]
  --check   exit 1 when an item has no rule or a rule says "gap" (run it after a game patch to find new state)
  --list    print the raw inventory without rules
Pure standard library; reads only the decompiled sources (never the game folder).
"""
import argparse
import os
import re
import sys
from collections import OrderedDict, defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
DEFAULT_DECOMP = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Temp", "claude", "mod_audit", "decomp", "Game")
RULES = os.path.join(HERE, "sync_coverage_rules.txt")
BEGIN = "<!-- GENERATED:BEGIN (tools/sync_coverage.py; do not edit by hand) -->"
END = "<!-- GENERATED:END -->"

STATUS_TEXT = OrderedDict([
    ("structure", "structure codec (S): host captures tagged changes, clients apply and re-derive"),
    ("presence", "structure codec + presence watch: components the simulation adds or removes without an Updated tag"),
    ("scan", "structure value scan: entity values hashed every 0.25-1 s, changes re-sent"),
    ("values", "simulation values (attention scan, SimValues stream)"),
    ("keyed", "keyed state on derived entities (zone cells, sub-element deletions, line rows)"),
    ("global", "global state channel (GlobalStateSync: a system's own ISerializable blob or a typed channel)"),
    ("actions", "UI intent / unlock channel (ActionSync)"),
    ("statistics", "statistics history channel (StatisticsSync)"),
    ("cellmap", "cell map channel (CellMapSync)"),
    ("terrain", "terrain channel (TerrainSync)"),
    ("water", "water channel (WaterSync); water sources travel as structure"),
    ("movers", "mover channel (MoverHost/MoverClient: existence, discrete state, motion)"),
    ("icons", "notification icon channel (IconSync)"),
    ("clock", "follower clock (frame index, speed) and the local pure functions of it"),
    ("derived", "derived identically on every peer from synced inputs (the vanilla system keeps running)"),
    ("snapshot", "arrives with the join snapshot and is invariant during a session (or changes only with synced inputs)"),
    ("local", "per-player by design (camera, UI, tool, preferences)"),
    ("transient", "one-frame command, tool-time or render-time data; never outlives a frame or a tool action"),
    ("other-work", "agent replication (citizens, households, companies, their requests): implemented by another branch"),
    ("excluded", "irrelevant in a session (achievements, tutorials, editor-only, telemetry)"),
    ("gap", "NOT synced (must be fixed or justified)"),
])

DECL = re.compile(r"^(?P<indent>\s*)(?:\[[^\]]*\]\s*)*(?P<mods>(?:(?:public|internal|private|protected|sealed|abstract|static|partial|readonly|unsafe|new)\s+)*)"
                  r"(?P<kind>struct|class)\s+(?P<name>\w+)(?:<[^>]*>)?\s*:\s*(?P<bases>[^{\n]+)")
NS = re.compile(r"^\s*namespace\s+([\w.]+)")
ORDER = re.compile(r"Update(?:At|Before|After)<\s*([\w.]+)\s*>\s*\(\s*SystemUpdatePhase\.(\w+)")


def split_bases(text):
    out, depth, cur = [], 0, ""
    for ch in text:
        if ch == "<":
            depth += 1
        elif ch == ">":
            depth -= 1
        if ch == "," and depth == 0:
            out.append(cur.strip())
            cur = ""
        else:
            cur += ch
    if cur.strip():
        out.append(cur.strip())
    return [re.sub(r"<.*>", "", b).split(".")[-1].strip() for b in out]


def scan(decomp):
    types = {}
    for root, _, files in os.walk(decomp):
        for f in sorted(files):
            if not f.endswith(".cs"):
                continue
            path = os.path.join(root, f)
            try:
                with open(path, encoding="utf-8", errors="replace") as fh:
                    lines = fh.readlines()
            except OSError:
                continue
            ns = ""
            for line in lines:
                m = NS.match(line)
                if m and not ns:
                    ns = m.group(1)
                    continue
                d = DECL.match(line)
                if not d:
                    continue
                name = d.group("name")
                full = (ns + "." if ns else "") + name
                types.setdefault(full, {
                    "full": full, "ns": ns, "name": name, "kind": d.group("kind"),
                    "bases": split_bases(d.group("bases")), "abstract": "abstract" in d.group("mods").split(),
                    "nested": bool(d.group("indent")), "file": os.path.relpath(path, decomp).replace("\\", "/"),
                })
    return types


def resolve_interfaces(types):
    by_short = defaultdict(list)
    for t in types.values():
        by_short[t["name"]].append(t)
    cache = {}

    def ifaces(t, seen):
        if t["full"] in cache:
            return cache[t["full"]]
        out = set(t["bases"])
        for b in t["bases"]:
            for base in by_short.get(b, []):
                if base["kind"] == "class" and base["full"] not in seen:
                    out |= ifaces(base, seen | {base["full"]})
        cache[t["full"]] = out
        return out

    for t in types.values():
        t["ifaces"] = ifaces(t, {t["full"]})


def phases(decomp):
    order = os.path.join(decomp, "Game.Common", "SystemOrder.cs")
    out = defaultdict(list)
    if not os.path.exists(order):
        return out
    with open(order, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            for m in ORDER.finditer(line):
                short = m.group(1).split(".")[-1]
                if m.group(2) not in out[short]:
                    out[short].append(m.group(2))
    return out


def load_rules(path):
    exact, prefix = {}, []
    with open(path, encoding="utf-8") as fh:
        for n, raw in enumerate(fh, 1):
            line = raw.rstrip("\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            parts = [p.strip() for p in line.split("|")]
            if len(parts) < 3:
                raise SystemExit("%s:%d: expected 'pattern | status | mechanism [| note]'" % (path, n))
            pattern, status, mech = parts[0], parts[1], parts[2]
            note = parts[3] if len(parts) > 3 else ""
            if status not in STATUS_TEXT:
                raise SystemExit("%s:%d: unknown status %r" % (path, n, status))
            rule = (status, mech, note, n)
            if pattern.endswith("*"):
                prefix.append((pattern[:-1], rule))
            else:
                if pattern in exact:
                    raise SystemExit("%s:%d: duplicate rule for %s" % (path, n, pattern))
                exact[pattern] = rule
    prefix.sort(key=lambda p: -len(p[0]))
    return exact, prefix


def match(full, kind, exact, prefix):
    """kind is 'component' or 'system'; a pattern may be qualified as 'system:Game.X' or 'component:Game.X'."""
    for key in ((kind + ":" + full), full):
        if key in exact:
            return exact[key]
    for p, rule in prefix:
        if (kind + ":" + full).startswith(p) or full.startswith(p):
            return rule
    return None


def collect(decomp):
    types = scan(decomp)
    resolve_interfaces(types)
    ph = phases(decomp)
    comps, systems = [], []
    for t in sorted(types.values(), key=lambda x: x["full"]):
        i = t["ifaces"]
        ser = "ISerializable" in i
        empty = "IEmptySerializable" in i
        if t["kind"] == "struct" and (ser or empty):
            if "IBufferElementData" in i:
                k = "buffer"
            elif "ISharedComponentData" in i:
                k = "shared"
            elif "IComponentData" in i:
                k = "component"
            else:
                continue  # a serializable value type inside a component, not state of its own
            flags = []
            if empty and not ser:
                flags.append("empty")
            if "IEnableableComponent" in i:
                flags.append("enableable")
            comps.append(dict(t, ckind=k, flags=flags))
        elif t["kind"] == "class" and not t["abstract"] and ("IDefaultSerializable" in i or "IJobSerializable" in i):
            systems.append(dict(t, phases=ph.get(t["name"], []), job="IJobSerializable" in i))
    return comps, systems


def md(text):
    return text.replace("|", "\\|")


def render(comps, systems, exact, prefix):
    rows_c, rows_s, missing, gaps = [], [], [], []
    counts = defaultdict(lambda: [0, 0])
    for c in comps:
        r = match(c["full"], "component", exact, prefix)
        if r is None:
            missing.append(c["full"])
            r = ("gap", "NO RULE", "", 0)
        counts[r[0]][0] += 1
        if r[0] == "gap":
            gaps.append(c["full"])
        rows_c.append((c, r))
    for s in systems:
        r = match(s["full"], "system", exact, prefix)
        if r is None:
            missing.append(s["full"])
            r = ("gap", "NO RULE", "", 0)
        counts[r[0]][1] += 1
        if r[0] == "gap":
            gaps.append(s["full"])
        rows_s.append((s, r))

    out = [BEGIN, ""]
    out.append("Generated from **%d** serializable components and buffers and **%d** serializable systems of the decompiled "
               "Game assembly. Items without a rule: **%d**; items with status `gap`: **%d**." % (len(comps), len(systems), len(missing), len(gaps)))
    out.append("")
    out.append("### Counts per status")
    out.append("")
    out.append("| Status | Components and buffers | Systems | Meaning |")
    out.append("|---|---:|---:|---|")
    for st, text in STATUS_TEXT.items():
        if st in counts:
            out.append("| `%s` | %d | %d | %s |" % (st, counts[st][0], counts[st][1], text))
    out.append("| **total** | **%d** | **%d** | |" % (len(comps), len(systems)))
    out.append("")
    out.append("### Systems with saved state (global state)")
    out.append("")
    out.append("| System | Phases | Status | Mechanism | Note |")
    out.append("|---|---|---|---|---|")
    for s, r in rows_s:
        out.append("| `%s` | %s | `%s` | %s | %s |" % (s["full"], ", ".join(s["phases"]) or "-", r[0], md(r[1]), md(r[2])))
    out.append("")
    out.append("### Components and buffers by namespace")
    by_ns = OrderedDict()
    for c, r in rows_c:
        by_ns.setdefault(c["ns"], []).append((c, r))
    for ns, rows in by_ns.items():
        out.append("")
        out.append("#### %s (%d)" % (ns, len(rows)))
        out.append("")
        out.append("| Type | Kind | Status | Mechanism | Note |")
        out.append("|---|---|---|---|---|")
        for c, r in rows:
            kind = c["ckind"] + ("" if not c["flags"] else " (" + ", ".join(c["flags"]) + ")")
            out.append("| `%s` | %s | `%s` | %s | %s |" % (c["name"], kind, r[0], md(r[1]), md(r[2])))
    out.append("")
    out.append(END)
    return "\n".join(out), missing, gaps


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--decomp", default=DEFAULT_DECOMP)
    ap.add_argument("--rules", default=RULES)
    ap.add_argument("--write", default=None)
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--list", action="store_true")
    a = ap.parse_args()
    if not os.path.isdir(a.decomp):
        raise SystemExit("decompiled Game folder not found: " + a.decomp)
    comps, systems = collect(a.decomp)
    if a.list:
        for c in comps:
            print("C\t%s\t%s\t%s\t%s" % (c["full"], c["ckind"], ",".join(c["flags"]), c["file"]))
        for s in systems:
            print("S\t%s\t%s\t%s" % (s["full"], ",".join(s["phases"]), s["file"]))
        return 0
    exact, prefix = load_rules(a.rules)
    text, missing, gaps = render(comps, systems, exact, prefix)
    if a.write:
        path = a.write if os.path.isabs(a.write) else os.path.join(REPO, a.write)
        with open(path, encoding="utf-8") as fh:
            doc = fh.read()
        if BEGIN in doc and END in doc:
            doc = doc[:doc.index(BEGIN)] + text + doc[doc.index(END) + len(END):]
        else:
            doc = doc.rstrip("\n") + "\n\n" + text + "\n"
        with open(path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(doc)
    print("%d components/buffers, %d systems; %d without a rule, %d gaps" % (len(comps), len(systems), len(missing), len(gaps)))
    for m in missing:
        print("  no rule: " + m)
    for g in gaps:
        if g not in missing:
            print("  gap: " + g)
    return 1 if a.check and (missing or gaps) else 0


if __name__ == "__main__":
    sys.exit(main())
