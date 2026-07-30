#!/usr/bin/env python3
"""Compare the method surface of two CanMessageFormats.h files.

The layout probe proves that every struct size, member offset and bitfield position matches CANlib's,
but it says nothing about the methods: a method that the schema forgets, or a qualifier the emitter
drops, changes no layout and so passes every existing check. That is not hypothetical — a missing
ClearReservedFields, a set of missing constructors and a dropped noexcept all reached the checked-in
output before this script existed.

For every struct declared in both headers this compares, at struct scope only:

  * the set of method names;
  * for each name, the arity and qualifiers (static / constexpr / explicit / const / noexcept) of each
    overload.

Parameter and return types are left to the compiler: verify-cpp-layout.sh builds CANlib's own
translation units against the generated header, which will not compile if those drift.

Usage: compare-method-surface.py <reference header> <generated header>
"""

import re
import sys

# Keywords and macros that can appear where a method name would, and control flow inside a body
NOT_A_METHOD = {
    "if", "for", "while", "switch", "return", "sizeof", "static_assert", "alignof",
    "__attribute__", "decltype", "noexcept", "operator", "and", "or", "not",
}

QUALIFIERS = ("static", "constexpr", "explicit", "const", "noexcept", "virtual")

STRUCT_HEADER = re.compile(
    r"(?:template\s*<[^>]*>\s*)?(?:struct|union|class)\s+"
    r"(?:__attribute__\s*\(\([^)]*\)\)\s*)?"
    r"(\w+)\s*(?:final\s*)?(?::[^{;]*)?\{"
)


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.S)
    return re.sub(r"//[^\n]*", " ", text)


def struct_scope_text(body):
    """The parts of a struct body that are at struct scope, i.e. outside any nested braces."""
    depth, out, start = 0, [], 0
    for i, c in enumerate(body):
        if c == "{":
            if depth == 0:
                out.append(body[start:i])
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                start = i + 1
                # A nested definition ends in ';', which must not glue onto the next declaration
                out.append(";")
    if depth == 0:
        out.append(body[start:])
    return " ".join(out)


def split_top_level(params):
    """Split a parameter list on commas that are not nested inside brackets."""
    depth, current, out = 0, "", []
    for c in params:
        if c in "([<":
            depth += 1
        elif c in ")]>":
            depth -= 1
        if c == "," and depth == 0:
            out.append(current)
            current = ""
        else:
            current += c
    if current.strip():
        out.append(current)
    return out


def parse_structs(path):
    text = strip_comments(open(path, encoding="utf-8").read())
    structs = {}
    for match in STRUCT_HEADER.finditer(text):
        name, i, depth = match.group(1), match.end() - 1, 0
        while i < len(text):
            if text[i] == "{":
                depth += 1
            elif text[i] == "}":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        # A constructor's member initialiser list sits at struct scope and its entries look exactly like
        # declarations ('idlePercent(0)'), so drop it before anything else. Requiring a ')' before the
        # colon keeps bitfield declarations ('mode : 2') intact, and keeping the trailing brace leaves
        # the nesting that struct_scope_text relies on unchanged.
        body = re.sub(r"\)\s*(?:noexcept\s*)?:\s*[^{;]*\{", ") {", text[match.end():i])
        scope = struct_scope_text(body)

        methods = {}
        for m in re.finditer(r"(\w+)\s*\(", scope):
            method = m.group(1)
            if method in NOT_A_METHOD:
                continue
            # Find the matching close parenthesis
                                                            # noqa: E114 - alignment of the comment below
            depth, j = 0, m.end() - 1
            while j < len(scope):
                if scope[j] == "(":
                    depth += 1
                elif scope[j] == ")":
                    depth -= 1
                    if depth == 0:
                        break
                j += 1
            params = scope[m.end():j]
            trailer = scope[j + 1:j + 60]
            if not re.match(r"[\w\s:*&()]*[{;]", trailer):
                continue                                    # not a declaration, e.g. a macro invocation
            leader = scope[max(0, m.start() - 60):m.start()]
            if "=" in leader.split(";")[-1]:
                continue                                    # an initialiser, not a declaration

            quals = frozenset(
                q for q in QUALIFIERS
                if re.search(rf"\b{q}\b", leader.split(";")[-1])
                or (q in ("const", "noexcept") and re.search(rf"\b{q}\b", trailer.split("{")[0].split(";")[0]))
            )
            methods.setdefault(method, set()).add((len(split_top_level(params)), quals))
        structs[name] = methods
    return structs


def describe(quals):
    return f"[{' '.join(sorted(quals))}]" if quals else "[no qualifiers]"


# 'const' and 'constexpr' only ever widen what a caller may do, so the generated header is allowed to
# carry them where CANlib does not — CANlib omits const on two GetActualDataLength overloads and
# constexpr on four static helpers, and propagating those oversights would make the C# side worse
# (it drops 'readonly') for no benefit. Everything else must match: losing noexcept or const, or
# gaining explicit or static, changes what compiles against the header.
WIDENING = {"const", "constexpr"}


def compare_overloads(struct, method, want, have):
    """Report on one method name. Overloads are matched up by arity."""
    problems = []
    for arity in sorted({a for a, _ in want} | {a for a, _ in have}):
        wanted = {q for a, q in want if a == arity}
        found = {q for a, q in have if a == arity}
        if not wanted:
            problems.append(f"FAIL {struct}::{method} has a generated {arity}-arg overload that the reference header does not declare")
            continue
        if not found:
            problems.append(f"FAIL {struct}::{method} is declared with {arity} arg(s) in the reference header but not generated")
            continue
        # Every reference overload must have a generated counterpart that keeps all its qualifiers
        # and adds nothing beyond the harmless widenings
        for quals in sorted(wanted, key=sorted):
            if not any(quals <= candidate and (candidate - quals) <= WIDENING for candidate in found):
                problems.append(
                    f"FAIL {struct}::{method} ({arity} arg) qualifiers differ\n"
                    f"  reference: {describe(quals)}\n"
                    f"  generated: {', '.join(sorted(describe(c) for c in found))}"
                )
    return problems


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    reference, generated = (parse_structs(p) for p in sys.argv[1:3])
    shared = sorted(set(reference) & set(generated))
    problems = 0
    checks = 0

    for name in shared:
        want, have = reference[name], generated[name]
        for method in sorted(set(want) | set(have)):
            checks += 1
            if method not in have:
                print(f"FAIL {name}::{method} is declared in the reference header but not generated")
                problems += 1
            elif method not in want:
                print(f"FAIL {name}::{method} is generated but not declared in the reference header")
                problems += 1
            else:
                for problem in compare_overloads(name, method, want[method], have[method]):
                    print(problem)
                    problems += 1

    for name in sorted(set(reference) - set(generated)):
        print(f"FAIL struct {name} is declared in the reference header but not generated")
        problems += 1
        checks += 1

    print(f"{checks} method surface checks over {len(shared)} structs, {problems} failures")
    return 0 if problems == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
