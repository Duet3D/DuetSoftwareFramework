#!/usr/bin/env python3
"""Compare the schema's enums against CANlib's.

These carry values that travel on the wire — a message type in the CAN id, a result code in a reply, a sensor
error in a report. A value that disagrees with CANlib does not produce a malformed message; it produces a
well-formed one that means something else, which no layout or table check would notice.

Every enum is compared in both directions, so a value CANlib adds is reported as well as one the schema gets
wrong. Retired ids are compared too, because the whole point of keeping them is that the number stays spent;
if CANlib ever reuses one, the comment recording it here has become a lie.

An enum marked checkOnly is generated somewhere outside this tool — DuetAPI's object model owns
TemperatureError — so its C# file is parsed and compared as well, which is the only thing tying that copy to
CANlib.

Usage: compare-enums.py <can-messages.json> <CANlib src directory>
"""

import json
import os
import re
import sys


def parse_canlib(path, name):
    """CANlib declares these either as an enum class or through the NamedEnum macro."""
    text = re.sub(r"/\*.*?\*/", "", open(path, encoding="utf-8").read(), flags=re.S)
    body = re.search(r"enum class " + name + r"\s*:\s*[\w ]+\s*\{(.*?)\n\};", text, re.S)
    implicit = False
    if body is None:
        body = re.search(r"NamedEnum\(" + name + r",\s*\w+,(.*?)\n\);", text, re.S)
        implicit = True
    if body is None:
        raise SystemExit(f"error: no {name} enum found in {path}")

    live, retired, pending, auto = {}, {}, {}, 0
    for raw in body.group(1).splitlines():
        line = raw.strip()
        if not line:
            continue
        if line.startswith("//"):
            comment = line[2:].strip()
            # A commented-out enumerator is a retired id; anything else is a section heading
            entry = re.match(r"^(\w+)\s*=\s*(\d+)\s*,?$", comment)
            if entry:
                retired[entry.group(1)] = int(entry.group(2))
            continue
        entry = re.match(r"^(\w+)\s*(?:=\s*([^,/]+?))?\s*,?\s*(?://.*)?$", line)
        if not entry:
            raise SystemExit(f"error: cannot parse {line!r}")
        member, value = entry.group(1), (entry.group(2) or "").strip()
        if not value:
            live[member] = auto                             # implicit, one past the previous value
        elif re.match(r"^(0x[0-9A-Fa-f]+|\d+)$", value):
            live[member] = int(value, 0)
        else:
            pending[member] = value                         # an alias of another enumerator
        auto = live.get(member, auto) + 1
    for member, target in pending.items():
        live[member] = live[target]
    return live, retired


def parse_schema(values):
    live, retired, pending, cpp_only = {}, {}, {}, set()
    for entry in values:
        if "section" in entry:
            continue
        name, value = entry["name"], entry["value"]
        # An entry the schema keeps for C# alone has no counterpart in CanId.h to compare against
        if "csharp" in entry.get("emit", ["cpp", "csharp"]) and "cpp" not in entry.get("emit", ["cpp", "csharp"]):
            cpp_only.add(name)
        target = retired if entry.get("retired") else live
        if isinstance(value, int):
            target[name] = value
        elif re.match(r"^(0x[0-9A-Fa-f]+|\d+)$", value):
            target[name] = int(value, 0)                    # the schema may spell an id in hex
        else:
            pending[name] = value
    for name, alias in pending.items():
        live[name] = live[alias]
    return live, retired, cpp_only


def compare(kind, reference, other, skip, problems, reference_name="CANlib", other_name="the schema"):
    for name in sorted(set(reference) | set(other)):
        if name in skip:
            continue
        if name not in other:
            problems.append(f"FAIL {kind} {name} = {reference[name]} is in {reference_name} but not in {other_name}")
        elif name not in reference:
            problems.append(f"FAIL {kind} {name} = {other[name]} is in {other_name} but not in {reference_name}")
        elif reference[name] != other[name]:
            problems.append(f"FAIL {kind} {name}: {reference_name} says {reference[name]}, {other_name} says {other[name]}")


def parse_csharp(path):
    """The members of a C# enum, so that a copy this tool does not generate can still be checked."""
    text = open(path, encoding="utf-8").read()
    body = re.search(r"enum \w+[^{]*\{(.*)\n\}", text, re.S)
    if body is None:
        raise SystemExit(f"error: no enum found in {path}")
    values, auto = {}, 0
    for raw in body.group(1).splitlines():
        line = raw.strip()
        if not line or line.startswith("//") or line.startswith("["):
            continue
        entry = re.match(r"^(\w+)\s*(?:=\s*(\w+))?\s*,?$", line)
        if entry:
            auto = int(entry.group(2), 0) if entry.group(2) else auto
            values[entry.group(1)] = auto
            auto += 1
    return values


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    schema = json.load(open(sys.argv[1], encoding="utf-8"))
    canlib_dir = sys.argv[2]

    problems, checks, enums = [], 0, 0
    for definition in schema["enums"]:
        name = definition["name"]
        live, retired = parse_canlib(os.path.join(canlib_dir, definition["cppHeader"]), name)
        schema_live, schema_retired, csharp_only = parse_schema(definition["values"])

        before = len(problems)
        compare(f"{name}", live, schema_live, csharp_only, problems)
        compare(f"{name} retired", retired, schema_retired, csharp_only, problems)
        checks += len(set(live) | set(schema_live)) + len(set(retired) | set(schema_retired))

        # An enum generated elsewhere is only tied to CANlib if its own file is checked too
        if definition.get("csharpSource"):
            csharp = parse_csharp(definition["csharpSource"])
            expected = {n[0].upper() + n[1:]: v for n, v in schema_live.items()}
            compare(f"{name} (C#)", expected, csharp, set(), problems,
                    reference_name="the schema", other_name=definition["csharpSource"])
            checks += len(set(expected) | set(csharp))

        enums += 1
        if len(problems) == before:
            continue

    for problem in problems:
        print(problem)
    print(f"{checks} enum value checks over {enums} enums, {len(problems)} failures")
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main())
