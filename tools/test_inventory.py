#!/usr/bin/env python3
"""Static test inventory of SkylineTogether.Core (no coverage tool, no download).

Lists every type of src/SkylineTogether.Core and every public method/property of it, and whether the Core test
project (tests/SkylineTogether.Core.Tests) references it. "Referenced" is decided on identifier tokens with comments
and string literals stripped:

  * a type is DIRECT when a test file names it;
  * a type is INDIRECT when no test names it, but it is reachable through Core sources from a DIRECT type (the tests
    probably exercise it through another type; worth a look, not necessarily a gap);
  * a type is UNTESTED when neither holds.
  * a public member is referenced when its name occurs in a test file that also names its declaring type (or, for a
    nested type, the enclosing type), or when the name is distinctive (no other Core type declares a member with that
    name) and occurs in any test file.

This is an approximation (it cannot tell which branches run), but it shows which areas have no test at all.

The "game" column counts the types the game layer (src/SkylineTogether) reaches, computed member by member with
namespace-aware name resolution by tools/reachability.py (use that tool directly for dead-code questions): an
UNTESTED type the game reaches is a gap that matters.

Usage:  python tools/test_inventory.py [--area <Folder>] [--untested] [--members] [--unused] [--json]
  --area      restrict the detail lists to one Core folder (e.g. Session, Protocol)
  --untested  list the UNTESTED (and INDIRECT) types, marking those the game layer reaches
  --members   list unreferenced public members of tested types
  --unused    list the types the game layer does not reach (tools/reachability.py --types has the details)
  --json      machine-readable output of everything
Exit code 0 always (it is a report, not a gate). Pure standard library.
"""
import argparse
import json
import os
import re
import sys
from collections import defaultdict, OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
CORE = os.path.join(REPO, "src", "SkylineTogether.Core")
TESTS = os.path.join(REPO, "tests", "SkylineTogether.Core.Tests")

TYPE_DECL = re.compile(
    r"\b(?P<mods>(?:(?:public|internal|private|protected|sealed|abstract|static|partial|readonly|unsafe|new|ref)\s+)*)"
    r"(?P<kind>class|struct|interface|enum|record(?:\s+struct|\s+class)?)\s+(?P<name>[A-Za-z_]\w*)")
# a public method: modifiers, a return type, a name, optional generic parameters, '('
METHOD_DECL = re.compile(
    r"\bpublic\s+(?:(?:static|virtual|override|abstract|sealed|new|unsafe|extern|readonly|async)\s+)*"
    r"(?P<ret>[A-Za-z_][\w.]*(?:<[^;{}()]*?>)?(?:\[\s*,*\s*\])*\??)\s+(?P<name>[A-Za-z_]\w*)\s*(?:<[^;{}()]*?>)?\s*\(")
PROPERTY_DECL = re.compile(
    r"\bpublic\s+(?:(?:static|virtual|override|abstract|sealed|new|readonly)\s+)*"
    r"(?P<ret>[A-Za-z_][\w.]*(?:<[^;{}()]*?>)?(?:\[\s*,*\s*\])*\??)\s+(?P<name>[A-Za-z_]\w*)\s*(?:\{|=>)")
IDENT = re.compile(r"[A-Za-z_]\w*")
KEYWORDS = {"operator", "implicit", "explicit", "class", "struct", "interface", "enum", "delegate", "event", "const"}
OBJECT_MEMBERS = {"ToString", "Equals", "GetHashCode", "Dispose", "CompareTo", "GetEnumerator", "MoveNext", "Reset"}


def strip_code(text):
    """Removes comments, string and char literals (keeps newlines and braces of code)."""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif c == "/" and nxt == "*":
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
            out.append(" ")
        elif c == "@" and nxt == '"':
            i += 2
            while i < n:
                if text[i] == '"' and i + 1 < n and text[i + 1] == '"':
                    i += 2
                elif text[i] == '"':
                    i += 1
                    break
                else:
                    i += 1
            out.append('""')
        elif c == '"' or c == "'":
            q = c
            i += 1
            while i < n and text[i] != q:
                if text[i] == "\\":
                    i += 1
                elif text[i] == "\n":
                    break
                i += 1
            i += 1
            out.append(q + q)
        elif c == "$" and nxt == '"':
            # interpolated string: drop it, but keep identifiers inside {...} is not needed for this report
            out.append("$")
            i += 1
        else:
            out.append(c)
            i += 1
    return "".join(out)


def cs_files(root, exclude_dirs=("obj", "bin")):
    for base, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in exclude_dirs]
        for f in files:
            if f.endswith(".cs"):
                yield os.path.join(base, f)


class TypeInfo(object):
    def __init__(self, name, kind, access, path, outer):
        self.name = name
        self.kind = kind
        self.access = access
        self.path = path
        self.outer = outer          # enclosing TypeInfo or None
        self.members = OrderedDict()  # name -> "method" | "property"
        self.body_idents = set()

    @property
    def area(self):
        rel = os.path.relpath(self.path, CORE)
        parts = rel.replace("\\", "/").split("/")
        return parts[0] if len(parts) > 1 else "(root)"

    @property
    def full(self):
        return (self.outer.full + "." if self.outer else "") + self.name


def parse_core():
    types = []
    for path in sorted(cs_files(CORE)):
        with open(path, encoding="utf-8-sig") as fh:
            code = strip_code(fh.read())
        # walk the file; a stack of (TypeInfo or None, brace depth at which its body opened)
        stack = []
        depth = 0
        pending = None   # a type declaration seen, waiting for its '{'
        pos = 0
        events = []
        for m in TYPE_DECL.finditer(code):
            # skip "where T : class", "new() class" constraints and generic "struct" constraints
            before = code[max(0, m.start() - 12):m.start()]
            if re.search(r"[:,]\s*$", before) or "where" in code[max(0, m.start() - 40):m.start()].split("\n")[-1]:
                continue
            events.append((m.start(), "type", m))
        for m in METHOD_DECL.finditer(code):
            events.append((m.start(), "method", m))
        for m in PROPERTY_DECL.finditer(code):
            events.append((m.start(), "property", m))
        for i, ch in enumerate(code):
            if ch in "{};":
                events.append((i, ch, None))
        events.sort(key=lambda e: (e[0], 0 if e[1] in ("type", "method", "property") else 1))
        for at, what, m in events:
            if what == "type":
                mods = m.group("mods").split()
                access = "public" if "public" in mods else ("private" if "private" in mods else
                                                            ("protected" if "protected" in mods else "internal"))
                outer = next((t for t, _ in reversed(stack) if t is not None), None)
                info = TypeInfo(m.group("name"), m.group("kind").split()[0], access, path, outer)
                types.append(info)
                pending = info
            elif what in ("method", "property"):
                name = m.group("name")
                if name in KEYWORDS or m.group("ret") in KEYWORDS:
                    continue
                owner = next((t for t, _ in reversed(stack) if t is not None), None)
                if owner is None or name == owner.name:
                    continue
                if owner.kind == "interface":
                    continue
                owner.members.setdefault(name, what)
            elif what == "{":
                depth += 1
                stack.append((pending, depth))
                pending = None
            elif what == "}":
                if stack:
                    stack.pop()
                depth -= 1
            elif what == ";":
                # "record Foo(...);" or a forward-style declaration without a body
                if pending is not None and pending.kind == "record":
                    pending = None
        # identifiers per type body (for the indirect reach): approximate with the whole file
        idents = set(IDENT.findall(code))
        for t in types:
            if t.path == path:
                t.body_idents = idents
    return types


def parse_tests():
    files = {}
    for path in cs_files(TESTS):
        with open(path, encoding="utf-8-sig") as fh:
            files[path] = set(IDENT.findall(strip_code(fh.read())))
    return files


def game_reach():
    """Local names (Outer.Inner) of the Core types the game layer reaches, from tools/reachability.py."""
    sys.path.insert(0, HERE)
    import reachability
    model = reachability.build_model()
    reached, _ = reachability.reach(model, ("game",))
    return {model.types[q].local for q in reached}


def analyse():
    types = parse_core()
    tests = parse_tests()
    all_test_idents = set()
    for idents in tests.values():
        all_test_idents |= idents
    by_name = defaultdict(list)
    for t in types:
        by_name[t.name].append(t)
    member_owners = defaultdict(set)
    for t in types:
        for name in t.members:
            member_owners[name].add(t.full)

    status = {}
    for t in types:
        status[t.full] = "DIRECT" if t.name in all_test_idents else None
    # indirect reach: breadth-first from DIRECT types through the identifiers of their source files
    frontier = [t for t in types if status[t.full] == "DIRECT"]
    seen_files = set()
    while frontier:
        nxt = []
        for t in frontier:
            if t.path in seen_files:
                continue
            seen_files.add(t.path)
            for ident in t.body_idents:
                for target in by_name.get(ident, ()):
                    if status[target.full] is None:
                        status[target.full] = "INDIRECT"
                        nxt.append(target)
        frontier = nxt
    for t in types:
        if status[t.full] is None:
            status[t.full] = "UNTESTED"

    members = {}
    for t in types:
        names_for_type = {t.name}
        o = t.outer
        while o is not None:
            names_for_type.add(o.name)
            o = o.outer
        files_naming = [idents for idents in tests.values() if names_for_type & idents]
        for name, what in t.members.items():
            if name in OBJECT_MEMBERS:
                continue
            hit = any(name in idents for idents in files_naming)
            if not hit and len(member_owners[name]) == 1 and name in all_test_idents:
                hit = True
            members[(t.full, name)] = (what, hit)
    game = game_reach()
    return types, status, members, game


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--area")
    ap.add_argument("--untested", action="store_true")
    ap.add_argument("--members", action="store_true")
    ap.add_argument("--unused", action="store_true")
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    types, status, members, game = analyse()
    visible = [t for t in types if t.access in ("public", "internal")]

    if args.json:
        out = []
        for t in visible:
            out.append({
                "type": t.full, "kind": t.kind, "access": t.access, "area": t.area,
                "file": os.path.relpath(t.path, REPO).replace("\\", "/"), "status": status[t.full], "game": t.full in game,
                "members": [{"name": n, "kind": w, "referenced": h}
                            for (tf, n), (w, h) in members.items() if tf == t.full],
            })
        json.dump(out, sys.stdout, indent=1)
        print()
        return 0

    areas = OrderedDict()
    for t in sorted(visible, key=lambda t: (t.area, t.full)):
        a = areas.setdefault(t.area, {"types": 0, "DIRECT": 0, "INDIRECT": 0, "UNTESTED": 0, "members": 0, "memhit": 0, "game": 0})
        a["types"] += 1
        a["game"] += 1 if t.full in game else 0
        a[status[t.full]] += 1
        for (tf, n), (w, h) in members.items():
            if tf == t.full:
                a["members"] += 1
                a["memhit"] += 1 if h else 0

    def pct(x, y):
        return "  -  " if y == 0 else "%4.0f%%" % (100.0 * x / y)

    print("SkylineTogether.Core test inventory (static; tokens in tests/SkylineTogether.Core.Tests)")
    print()
    print("%-14s %6s %7s %9s %9s %8s %8s %6s" % ("area", "types", "direct", "indirect", "untested", "members", "refd", "game"))
    tot = defaultdict(int)
    for name, a in areas.items():
        print("%-14s %6d %7d %9d %9d %8d %8s %6d" % (name, a["types"], a["DIRECT"], a["INDIRECT"], a["UNTESTED"],
                                                      a["members"], pct(a["memhit"], a["members"]), a["game"]))
        for k, v in a.items():
            tot[k] += v
    print("%-14s %6d %7d %9d %9d %8d %8s %6d" % ("TOTAL", tot["types"], tot["DIRECT"], tot["INDIRECT"], tot["UNTESTED"],
                                                  tot["members"], pct(tot["memhit"], tot["members"]), tot["game"]))

    selected = [t for t in visible if args.area is None or t.area.lower() == args.area.lower()]
    if args.untested:
        print()
        print("Types without a direct test reference (INDIRECT = reachable from a tested type):")
        for t in sorted(selected, key=lambda t: (status[t.full], t.area, t.full)):
            if status[t.full] != "DIRECT":
                print("  %-9s %-4s %-10s %-9s %s  (%s)" % (status[t.full], "GAME" if t.full in game else "", t.area, t.kind, t.full,
                                                          os.path.relpath(t.path, REPO).replace("\\", "/")))
    if args.unused:
        print()
        print("Types the game layer does not reach (dead-code candidates; tests do not count):")
        for t in sorted(selected, key=lambda t: (t.area, t.full)):
            if t.full not in game:
                print("  %-10s %-9s %s  [%s]" % (t.area, t.kind, t.full, status[t.full].lower()))
    if args.members:
        print()
        print("Public members never referenced by a test (of types that tests do reference):")
        for t in sorted(selected, key=lambda t: (t.area, t.full)):
            if status[t.full] != "DIRECT":
                continue
            missing = [n for (tf, n), (w, h) in members.items() if tf == t.full and not h]
            if missing:
                print("  %-10s %s: %s" % (t.area, t.full, ", ".join(missing)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
