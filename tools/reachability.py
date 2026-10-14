#!/usr/bin/env python3
"""Member-level reachability of SkylineTogether.Core from the game layer (static; no compiler, no download).

Answers "does the shipped mod ever reach this Core type or member?" more strictly than tools/test_inventory.py:

  * Sources are tokenized with comments, string literals and preprocessor lines removed (identifiers inside
    interpolation holes of $"..." strings are kept).
  * Every Core file is split into declarations: types (namespace, enclosing types, partial parts merged) and their
    members (methods, constructors, properties, fields, events, operators, indexers). Each member keeps its own
    tokens, so a type referenced only from the body of a member nobody calls is NOT reached.
  * A type name is resolved like the C# compiler would for the cases this code base uses: qualified names
    (Ns.Type, Outer.Inner), nested types of the enclosing types, the namespace of the file and its parents (which
    shadow using directives, e.g. the game layer's own Sync.DerivedKey), using directives, using aliases, using
    static, and the "Attribute" suffix. A name after "x." that is not a qualified type name is a member access.
  * Extension methods are matched by name alone (a call does not name their static class).
  * Members are matched by name: a member of a reached type is reached when its name occurs in reached code (an
    over-approximation: any "Encode" call keeps every reached type's Encode). Constructors, finalizers, operators,
    indexers, enum members, object overrides (ToString, Equals, ...) and pattern members the compiler calls by
    itself (Dispose, GetEnumerator, Current, MoveNext, Deconstruct, CompareTo, ...) come with their type.

The roots are every token of the game layer (src/SkylineTogether; its own dead code is not analysed) or, with
--roots tests, of the Core test project. The result is conservative in one direction: what it reports as
unreached has no static reference path from the roots (reflection is not used on Core types; check `--explain`
before deleting). Name-based member matching means "reached" can be wrong, never "unreached".

Usage:  python tools/reachability.py [--roots game|tests|both] [--types] [--members] [--files] [--area <Folder>]
                                      [--explain <Type>] [--json]
  (no option)   summary per Core folder: types, reached types, unreached lines
  --types       list unreached types (game roots), marking those only tests reach
  --members     list unreached members of reached types (dead methods/fields in live types)
  --files       list Core files none of whose types is reached
  --area        restrict lists to one Core folder (e.g. Integrity)
  --explain T   print why type T (simple or qualified name) is reached: the chain of references from a root
  --json        machine-readable output
Exit code 0 always (a report, not a gate). Pure standard library.
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
GAME = os.path.join(REPO, "src", "SkylineTogether")
TESTS = os.path.join(REPO, "tests", "SkylineTogether.Core.Tests")

KEYWORDS = set("""abstract as base bool break byte case catch char checked class const continue decimal default delegate
do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface
internal is lock long namespace new null object operator out override params private protected public readonly ref
return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong
unchecked unsafe ushort using virtual void volatile while var dynamic record partial get set init add remove value
where yield async await nameof when file global""".split())
MODIFIERS = set("""public private protected internal static sealed abstract partial readonly ref unsafe new file
extern virtual override volatile async const""".split())
TYPE_KEYWORDS = {"class", "struct", "interface", "enum", "record"}
# members the compiler or the runtime call without naming them in source
IMPLICIT_MEMBERS = {"ToString", "Equals", "GetHashCode", "Finalize", "Dispose", "GetEnumerator", "Current", "MoveNext",
                    "Reset", "Deconstruct", "CompareTo", "GetAwaiter", "GetResult", "IsCompleted", "OnCompleted",
                    "Main"}

TOKEN = re.compile(r"@?[A-Za-z_]\w*|0[xX][0-9A-Fa-f_]+|\d[\w.]*|=>|::|\?\.|\S")


# ----------------------------------------------------------------------------------------------------------------
# lexing
# ----------------------------------------------------------------------------------------------------------------
def strip_code(text):
    """Comments, string/char literals and preprocessor lines become blanks (newlines kept); interpolation holes of
    $"..." strings are kept as code."""
    out = []
    i, n = 0, len(text)
    at_line_start = True
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if at_line_start and c == "#":
            j = text.find("\n", i)
            i = n if j < 0 else j
            continue
        if c == "\n":
            out.append(c)
            at_line_start = True
            i += 1
            continue
        if not c.isspace():
            at_line_start = False
        if c == "/" and nxt == "/":
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif c == "/" and nxt == "*":
            j = text.find("*/", i + 2)
            seg = text[i:n if j < 0 else j + 2]
            out.append("\n" * seg.count("\n") + " ")
            i = n if j < 0 else j + 2
        elif c in "$@" and (nxt == '"' or (nxt in "$@" and nxt != c and i + 2 < n and text[i + 2] == '"')):
            interp = c == "$" or nxt == "$"
            verbatim = c == "@" or nxt == "@"
            i += 2 if nxt == '"' else 3
            i = _skip_string(text, i, out, interp, verbatim)
        elif c == '"':
            i = _skip_string(text, i + 1, out, False, False)
        elif c == "'":
            i += 1
            while i < n and text[i] != "'":
                if text[i] == "\\":
                    i += 1
                elif text[i] == "\n":
                    break
                i += 1
            i += 1
            out.append(" 0 ")
        else:
            out.append(c)
            i += 1
    return "".join(out)


def _skip_string(text, i, out, interp, verbatim):
    n = len(text)
    out.append(" \"\" ")
    while i < n:
        c = text[i]
        if verbatim and c == '"' and i + 1 < n and text[i + 1] == '"':
            i += 2
        elif c == '"':
            return i + 1
        elif not verbatim and c == "\\":
            i += 2
        elif c == "\n":
            out.append("\n")
            if not verbatim:
                return i + 1
            i += 1
        elif interp and c == "{" and i + 1 < n and text[i + 1] == "{":
            i += 2
        elif interp and c == "{":
            # copy the hole as code up to the matching '}' (nested strings inside holes are rare: skipped simply)
            depth = 1
            i += 1
            out.append(" ( ")
            while i < n and depth:
                d = text[i]
                if d == "{":
                    depth += 1
                elif d == "}":
                    depth -= 1
                    if depth == 0:
                        break
                elif d == '"':
                    i = _skip_string(text, i + 1, [], False, False) - 1
                elif d == ":" and depth == 1:
                    # format specifier: skip to the closing brace
                    while i < n and text[i] != "}":
                        i += 1
                    break
                out.append(d)
                i += 1
            out.append(" ) ")
            i += 1
        else:
            i += 1
    return i


class Tok(object):
    __slots__ = ("s", "line")

    def __init__(self, s, line):
        self.s = s
        self.line = line


def tokenize(path):
    with open(path, encoding="utf-8-sig") as fh:
        code = strip_code(fh.read())
    toks = []
    line = 1
    pos = 0
    for m in TOKEN.finditer(code):
        line += code.count("\n", pos, m.start())
        pos = m.start()
        s = m.group(0)
        if s.startswith("@") and len(s) > 1:
            s = s[1:]
        toks.append(Tok(s, line))
    return toks


def is_ident(s):
    return (s[0].isalpha() or s[0] == "_") and s not in KEYWORDS


# ----------------------------------------------------------------------------------------------------------------
# declarations
# ----------------------------------------------------------------------------------------------------------------
class FileCtx(object):
    def __init__(self, path, project):
        self.path = path
        self.project = project          # "core" | "game" | "tests"
        self.usings = []                # namespaces
        self.aliases = {}               # alias -> qualified name
        self.static_usings = []         # qualified type names


class TypeDecl(object):
    def __init__(self, name, kind, namespace, outer, ctx, line):
        self.name = name
        self.kind = kind
        self.namespace = namespace
        self.outer = outer
        self.ctxs = [ctx]
        self.parts = []                 # (path, first line, last line) per partial part
        self.headers = []               # (tokens, scope)
        self.members = []               # MemberDecl
        self.nested = {}                # name -> [TypeDecl]
        self.line = line

    @property
    def qual(self):
        if self.outer is not None:
            return self.outer.qual + "." + self.name
        return (self.namespace + "." if self.namespace else "") + self.name

    @property
    def local(self):
        return (self.outer.local + "." if self.outer is not None else "") + self.name

    @property
    def project(self):
        return self.ctxs[0].project

    @property
    def path(self):
        return self.parts[0][0] if self.parts else self.ctxs[0].path

    @property
    def area(self):
        rel = os.path.relpath(self.path, CORE).replace("\\", "/").split("/")
        return rel[0] if len(rel) > 1 else "(root)"

    def lines(self):
        return sum(b - a + 1 for _, a, b in self.parts)


class MemberDecl(object):
    def __init__(self, owner, name, kind, tokens, scope, implicit, line, last):
        self.owner = owner
        self.name = name
        self.kind = kind
        self.tokens = tokens
        self.scope = scope
        self.implicit = implicit
        self.line = line
        self.last = last
        self.extension = False


class Scope(object):
    """Where a token list sits: file context, namespace, enclosing type (innermost)."""
    __slots__ = ("ctx", "namespace", "type")

    def __init__(self, ctx, namespace, type_):
        self.ctx = ctx
        self.namespace = namespace
        self.type = type_


OPEN = {"(": ")", "[": "]", "{": "}"}


def match_close(toks, i):
    """Index of the token closing the bracket at toks[i]."""
    stack = [OPEN[toks[i].s]]
    j = i + 1
    while j < len(toks):
        s = toks[j].s
        if s in OPEN:
            stack.append(OPEN[s])
        elif stack and s == stack[-1]:
            stack.pop()
            if not stack:
                return j
        j += 1
    return len(toks) - 1


class Model(object):
    def __init__(self):
        self.types = OrderedDict()      # qual -> TypeDecl
        self.by_ns_name = defaultdict(list)   # (namespace, name) -> top-level TypeDecl
        self.namespaces = set([""])
        self.root_chunks = defaultdict(list)  # project -> [(tokens, scope, label)]

    # -------------------------------------------------------------------------------------------- parsing
    def parse_file(self, path, project):
        toks = tokenize(path)
        ctx = FileCtx(path, project)
        self._scope(toks, 0, len(toks), ctx, "", None)

    def _add_ns(self, ns):
        parts = ns.split(".")
        for k in range(1, len(parts) + 1):
            self.namespaces.add(".".join(parts[:k]))

    def _scope(self, toks, i, end, ctx, ns, outer):
        """Parses declarations in toks[i:end] (a namespace body, a file, or a type body)."""
        while i < end:
            s = toks[i].s
            if s == ";":
                i += 1
                continue
            if s == "using" and outer is None and i + 1 < end and toks[i + 1].s != "(":
                j = i
                while j < end and toks[j].s != ";":
                    j += 1
                self._using([t.s for t in toks[i + 1:j]], ctx)
                i = j + 1
                continue
            if s == "namespace" and outer is None:
                j = i + 1
                name = []
                while j < end and toks[j].s not in ("{", ";"):
                    name.append(toks[j].s)
                    j += 1
                full = (ns + "." if ns else "") + "".join(name)
                self._add_ns(full)
                if j < end and toks[j].s == "{":
                    close = match_close(toks, j)
                    self._scope(toks, j + 1, close, ctx, full, None)
                    i = close + 1
                else:
                    self._scope(toks, j + 1, end, ctx, full, None)
                    return
                continue
            i = self._decl(toks, i, end, ctx, ns, outer)

    def _using(self, words, ctx):
        if not words:
            return
        if words[0] == "global":
            words = words[1:]
        if words and words[0] == "static":
            ctx.static_usings.append("".join(words[1:]).replace("global::", ""))
        elif "=" in words:
            k = words.index("=")
            ctx.aliases["".join(words[:k])] = "".join(words[k + 1:]).replace("global::", "")
        else:
            ctx.usings.append("".join(words).replace("global::", ""))

    def _chunk_end(self, toks, i, end):
        """End (inclusive) of the declaration starting at i: a ';' at depth 0, or the '}' closing a body block."""
        saw_assign = False
        j = i
        while j < end:
            s = toks[j].s
            if s in ("(", "["):
                j = match_close(toks, j) + 1
                continue
            if s in ("=", "=>"):
                saw_assign = True
            elif s == ";":
                return j
            elif s == "{":
                close = match_close(toks, j)
                if saw_assign:
                    j = close + 1
                    continue
                if close + 1 < end and toks[close + 1].s == "=":
                    j = close + 1
                    continue
                return close
            j += 1
        return end - 1

    def _decl(self, toks, i, end, ctx, ns, outer):
        last = self._chunk_end(toks, i, end)
        chunk = toks[i:last + 1]
        # skip attributes and modifiers to classify
        k = 0
        while k < len(chunk):
            s = chunk[k].s
            if s == "[":
                k = match_close(chunk, k) + 1
            elif s in MODIFIERS:
                k += 1
            else:
                break
        head = chunk[k].s if k < len(chunk) else ""
        if head in TYPE_KEYWORDS or head == "delegate":
            kind = head
            k += 1
            if head == "record" and k < len(chunk) and chunk[k].s in ("struct", "class"):
                k += 1
            if head == "delegate":
                # delegate <ret> Name<...>(...);
                name = None
                for q in range(k, len(chunk)):
                    if chunk[q].s == "(":
                        p = q - 1
                        if chunk[p].s == ">":
                            p = self._open_angle(chunk, p) - 1
                        name = chunk[p].s
                        break
            else:
                name = chunk[k].s if k < len(chunk) else None
            if not name or not is_ident(name):
                return last + 1
            t = self._type(name, kind, ns, outer, ctx, chunk[0].line, chunk[-1].line)
            body = None
            for q in range(k, len(chunk)):
                if chunk[q].s == "{":
                    body = q
                    break
                if chunk[q].s in ("(", "["):
                    pass
            if kind == "delegate" or body is None:
                t.headers.append((chunk, Scope(ctx, ns, t)))
                return last + 1
            # primary constructor parameters / base list / constraints
            t.headers.append((chunk[:body], Scope(ctx, ns, t)))
            body_close = match_close(chunk, body)
            if kind == "enum":
                # enum members come with the enum (wire values stay reserved); their initializers are header code
                t.headers.append((chunk[body:body_close + 1], Scope(ctx, ns, t)))
            else:
                self._scope(chunk, body + 1, body_close, ctx, ns, t)
            return last + 1
        if outer is None:
            # top-level statements or junk outside types: treat as root code of this file
            self.root_chunks[ctx.project].append((chunk, Scope(ctx, ns, None), os.path.relpath(ctx.path, REPO)))
            return last + 1
        self._member(chunk, k, outer, Scope(ctx, ns, outer))
        return last + 1

    def _open_angle(self, chunk, p):
        depth = 0
        while p >= 0:
            if chunk[p].s == ">":
                depth += 1
            elif chunk[p].s == "<":
                depth -= 1
                if depth == 0:
                    return p
            p -= 1
        return 0

    def _type(self, name, kind, ns, outer, ctx, line, last):
        if outer is not None:
            qual = outer.qual + "." + name
        else:
            qual = (ns + "." if ns else "") + name
        t = self.types.get(qual)
        if t is None:
            t = TypeDecl(name, kind, ns, outer, ctx, line)
            self.types[qual] = t
            if outer is None:
                self.by_ns_name[(ns, name)].append(t)
            else:
                outer.nested.setdefault(name, []).append(t)
        else:
            t.ctxs.append(ctx)
        t.parts.append((ctx.path, line, last))
        return t

    def _member(self, chunk, k, owner, scope):
        """Classifies a member declaration (k = index after attributes and modifiers)."""
        names = []
        kind = "member"
        implicit = False
        extension = False
        mods = set(t.s for t in chunk[:k] if t.s in MODIFIERS)
        j = k
        angle = 0
        while j < len(chunk):
            s = chunk[j].s
            if s == "<":
                angle += 1
            elif s == ">" and angle:
                angle -= 1
            elif s == "," and angle:
                j += 1
                continue
            if s == "[":
                if j > k and chunk[j - 1].s == "this":
                    kind, implicit = "indexer", True
                    names = ["this[]"]
                    break
                j = match_close(chunk, j) + 1
                continue
            if s == "operator":
                kind, implicit = "operator", True
                names = ["operator"]
                break
            if s == "~":
                kind, implicit = "finalizer", True
                names = ["~"]
                break
            if s == "(":
                p = j - 1
                if p >= k and chunk[p].s == ">":
                    p = self._open_angle(chunk, p) - 1
                if p >= k and is_ident(chunk[p].s):
                    name = chunk[p].s
                    if name == owner.name and (p == k or chunk[p - 1].s not in (".",)):
                        kind, implicit = "ctor", True
                    else:
                        kind = "method"
                        extension = j + 1 < len(chunk) and chunk[j + 1].s == "this"
                    names = [name]
                    break
                j = match_close(chunk, j) + 1
                continue
            if s in ("{", "=>", "=", ";", ","):
                # property / field / event: the identifier before this token, plus further declarators
                p = j - 1
                if p >= k and is_ident(chunk[p].s):
                    names.append(chunk[p].s)
                kind = "property" if s in ("{", "=>") else "field"
                if s in ("=", ","):
                    q = j
                    while q < len(chunk):
                        r = chunk[q].s
                        if r in ("(", "[", "{"):
                            q = match_close(chunk, q) + 1
                            continue
                        if r == "," and q + 1 < len(chunk) and is_ident(chunk[q + 1].s):
                            names.append(chunk[q + 1].s)
                        q += 1
                break
            j += 1
        if not names:
            names = ["?"]
        if "event" in [t.s for t in chunk[:j]]:
            kind = "event"
        for name in names:
            imp = implicit or name in IMPLICIT_MEMBERS or (kind == "ctor") or ("static" in mods and kind == "ctor")
            mem = MemberDecl(owner, name, kind, chunk, scope, imp, chunk[0].line, chunk[-1].line)
            mem.extension = extension
            owner.members.append(mem)

    # -------------------------------------------------------------------------------------------- resolution
    def lookup_qualified(self, parts):
        """Types whose qualified name is parts (namespace + types) or that an alias/partial chain can't express."""
        full = ".".join(parts)
        t = self.types.get(full)
        return [t] if t is not None else []

    def resolve(self, toks, idx, scope):
        """TypeDecls named by toks[idx] in scope ([] when it is not a type name)."""
        name = toks[idx].s
        # qualified: walk back over "a.b.c."
        if idx >= 2 and toks[idx - 1].s in (".", "::"):
            chain = [name]
            p = idx - 1
            while p >= 1 and toks[p].s in (".", "::"):
                prev = toks[p - 1].s
                if prev == "global" or not is_ident(prev):
                    break
                chain.insert(0, prev)
                p -= 2
            if len(chain) > 1:
                return self._resolve_chain(chain, scope)
            return []
        return self._resolve_simple(name, scope)

    def _resolve_chain(self, chain, scope):
        head = chain[0]
        # alias
        if head in scope.ctx.aliases:
            base = scope.ctx.aliases[head].split(".")
            return self.lookup_qualified(base + chain[1:])
        # fully qualified namespace
        for k in range(len(chain) - 1, 0, -1):
            ns = ".".join(chain[:k])
            if ns in self.namespaces:
                t = self.lookup_qualified(chain)
                if t:
                    return t
        # namespace-relative to the current namespace chain or a using (Ns.Sub.Type with "using Ns")
        prefixes = []
        ns = scope.namespace
        while True:
            prefixes.append(ns)
            if not ns:
                break
            ns = ns.rpartition(".")[0]
        prefixes += scope.ctx.usings
        for pre in prefixes:
            t = self.lookup_qualified(([pre] if pre else []) + chain)
            if t:
                return t
        # Outer.Inner where Outer resolves as a simple type name
        outers = self._resolve_simple(head, scope)
        found = []
        for o in outers:
            cur = [o]
            for part in chain[1:]:
                nxt = []
                for c in cur:
                    nxt += c.nested.get(part, [])
                cur = nxt
            found += cur
        return found

    def _resolve_simple(self, name, scope, suffix=True):
        # nested types of the enclosing types (and their partial parts)
        t = scope.type
        while t is not None:
            if name in t.nested:
                return t.nested[name]
            if t.name == name:
                return [t]
            t = t.outer
        # namespace chain (shadows using directives)
        ns = scope.namespace
        while True:
            hit = self.by_ns_name.get((ns, name))
            if hit:
                return hit
            if not ns:
                break
            ns = ns.rpartition(".")[0]
        if name in scope.ctx.aliases:
            return self.lookup_qualified(scope.ctx.aliases[name].split("."))
        found = []
        for u in scope.ctx.usings:
            found += self.by_ns_name.get((u, name), [])
        for st in scope.ctx.static_usings:
            for o in self.lookup_qualified(st.split(".")):
                found += o.nested.get(name, [])
        if not found and suffix and not name.endswith("Attribute"):
            return self._resolve_simple(name + "Attribute", scope, False)
        return found


# ----------------------------------------------------------------------------------------------------------------
# reachability
# ----------------------------------------------------------------------------------------------------------------
def build_model():
    m = Model()
    for project, root in (("core", CORE), ("game", GAME), ("tests", TESTS)):
        if not os.path.isdir(root):
            continue
        for base, dirs, files in os.walk(root):
            dirs[:] = sorted(d for d in dirs if d not in ("obj", "bin"))
            for f in sorted(files):
                if f.endswith(".cs"):
                    m.parse_file(os.path.join(base, f), project)
    return m


def reach(m, root_projects):
    """Returns (reached types {qual: (label, from_qual)}, reached members {id(member): label}).

    label names the declaration whose tokens made the type reachable; from_qual is the Core type that declaration
    belongs to (None for a root)."""
    reached_types = {}
    reached_members = {}
    names = set()
    work = []   # (tokens, scope, label, from_qual)

    for qual, t in m.types.items():
        if t.project in root_projects:
            for toks, scope in t.headers:
                work.append((toks, scope, "%s (%s)" % (t.local, t.project), None))
            for mem in t.members:
                work.append((mem.tokens, mem.scope, "%s.%s (%s)" % (t.local, mem.name, t.project), None))
    for p in root_projects:
        work += [(toks, scope, label, None) for toks, scope, label in m.root_chunks.get(p, [])]

    core_members_by_name = defaultdict(list)
    for t in m.types.values():
        if t.project == "core":
            for mem in t.members:
                core_members_by_name[mem.name].append(mem)

    def mark_type(t, why, src):
        if t.project != "core" or t.qual in reached_types:
            return
        reached_types[t.qual] = (why, src)
        for toks, scope in t.headers:
            work.append((toks, scope, t.local, t.qual))
        for mem in t.members:
            if mem.implicit or mem.name in names:
                mark_member(mem, t.local)
        # a nested type keeps its enclosing type alive
        if t.outer is not None:
            mark_type(t.outer, t.local, t.qual)

    def mark_member(mem, why):
        if id(mem) in reached_members:
            return
        reached_members[id(mem)] = why
        work.append((mem.tokens, mem.scope, "%s.%s" % (mem.owner.local, mem.name), mem.owner.qual))

    while work:
        toks, scope, label, src = work.pop()
        for idx, tok in enumerate(toks):
            s = tok.s
            if not (s[0].isalpha() or s[0] == "_") or s in KEYWORDS:
                continue
            for t in m.resolve(toks, idx, scope):
                mark_type(t, label, src)
            if s not in names:
                names.add(s)
                for mem in core_members_by_name.get(s, ()):
                    if mem.extension:
                        # an extension method is called without naming its class
                        mark_type(mem.owner, label, src)
                        mark_member(mem, label)
                    elif mem.owner.qual in reached_types:
                        mark_member(mem, label)
    return reached_types, reached_members


def find_type(m, name):
    hits = [t for t in m.types.values() if t.project == "core" and (t.qual == name or t.local == name or t.name == name)]
    return hits


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--roots", choices=("game", "tests", "both"), default="game")
    ap.add_argument("--types", action="store_true")
    ap.add_argument("--members", action="store_true")
    ap.add_argument("--files", action="store_true")
    ap.add_argument("--area")
    ap.add_argument("--explain")
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    m = build_model()
    roots = {"game": ("game",), "tests": ("tests",), "both": ("game", "tests")}[args.roots]
    rtypes, rmembers = reach(m, roots)
    ttypes, _ = reach(m, ("tests",)) if args.roots == "game" else ({}, {})
    core = [t for t in m.types.values() if t.project == "core"]
    sel = [t for t in core if args.area is None or t.area.lower() == args.area.lower()]

    def rel(p):
        return os.path.relpath(p, REPO).replace("\\", "/")

    if args.explain:
        hits = find_type(m, args.explain)
        if not hits:
            print("no Core type named %s" % args.explain)
            return 0
        for t in hits:
            print("%s  (%s)" % (t.qual, rel(t.path)))
            if t.qual not in rtypes:
                print("  not reached from %s%s" % (args.roots, " (tests reach it)" if t.qual in ttypes else ""))
                continue
            cur, seen = t.qual, set()
            while cur is not None and cur not in seen:
                seen.add(cur)
                why, src = rtypes[cur]
                print("  <- %s" % why)
                cur = src
        return 0

    # dead files: every type declared in the file is unreached (and the file declares at least one type)
    file_types = defaultdict(list)
    for t in core:
        for path, _, _ in t.parts:
            file_types[path].append(t)
    dead_files = sorted(p for p, ts in file_types.items() if not any(t.qual in rtypes for t in ts))

    def file_lines(p):
        with open(p, encoding="utf-8-sig") as fh:
            return sum(1 for _ in fh)

    if args.json:
        out = {"roots": args.roots, "types": [], "dead_files": [rel(p) for p in dead_files]}
        for t in core:
            out["types"].append({
                "type": t.qual, "kind": t.kind, "file": rel(t.path), "reached": t.qual in rtypes,
                "tests_reach": t.qual in ttypes,
                "unreached_members": [{"name": mm.name, "kind": mm.kind, "line": mm.line}
                                      for mm in t.members if id(mm) not in rmembers] if t.qual in rtypes else [],
            })
        json.dump(out, sys.stdout, indent=1)
        print()
        return 0

    areas = OrderedDict()
    for t in sorted(core, key=lambda t: (t.area, t.qual)):
        a = areas.setdefault(t.area, [0, 0, 0])
        a[0] += 1
        a[1] += 1 if t.qual in rtypes else 0
    for p in dead_files:
        rp = os.path.relpath(p, CORE).replace("\\", "/").split("/")
        areas.setdefault(rp[0] if len(rp) > 1 else "(root)", [0, 0, 0])[2] += file_lines(p)
    print("SkylineTogether.Core reachability from %s (member-level, static)" % args.roots)
    print()
    print("%-14s %6s %8s %10s" % ("area", "types", "reached", "dead lines"))
    tot = [0, 0, 0]
    for name, a in areas.items():
        print("%-14s %6d %8d %10d" % (name, a[0], a[1], a[2]))
        tot = [x + y for x, y in zip(tot, a)]
    print("%-14s %6d %8d %10d" % ("TOTAL", tot[0], tot[1], tot[2]))

    if args.types:
        print()
        print("Unreached types (test = a test reaches it):")
        for t in sorted(sel, key=lambda t: (t.area, t.qual)):
            if t.qual not in rtypes:
                print("  %-4s %-10s %-9s %s  (%s)" % ("test" if t.qual in ttypes else "", t.area, t.kind, t.local, rel(t.path)))
    if args.files:
        print()
        print("Files with no reached type:")
        for p in dead_files:
            if args.area is None or rel(p).split("/")[2].lower() == args.area.lower():
                print("  %5d  %s" % (file_lines(p), rel(p)))
    if args.members:
        print()
        print("Unreached members of reached types:")
        for t in sorted(sel, key=lambda t: (t.area, t.qual)):
            if t.qual not in rtypes or t.kind == "interface":
                continue
            dead = [mm for mm in t.members if id(mm) not in rmembers]
            if dead:
                print("  %s  (%s)" % (t.local, rel(t.path)))
                for mm in dead:
                    print("      %-9s %-28s line %d" % (mm.kind, mm.name, mm.line))
    return 0


if __name__ == "__main__":
    sys.exit(main())
