#!/usr/bin/env python3
"""Compare the schema's CanMessageType against CANlib's CanId.h.

A message type is the id a message travels under, so a value that disagrees with CANlib does not produce a
malformed message — it produces a well-formed one that the board hands to the wrong handler. Nothing else in
the pipeline would notice: the layouts and the parameter tables are all checked independently of the id.

This compares the two in both directions, so an id CANlib adds is reported as well as one the schema gets
wrong. Retired ids are compared too, because the whole point of keeping them is that the number stays spent;
if CANlib ever reuses one, the comment recording it here has become a lie.

Usage: compare-message-types.py <CanId.h> <can-messages.json>
"""

import json
import re
import sys


def parse_canlib(path):
    text = open(path, encoding="utf-8").read()
    body = re.search(r"enum class CanMessageType\s*:\s*\w+\s*\{(.*?)\n\};", text, re.S)
    if body is None:
        raise SystemExit(f"error: no CanMessageType enum found in {path}")

    live, retired, pending = {}, {}, {}
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
        entry = re.match(r"^(\w+)\s*=\s*(\w+)\s*,?", line)
        if not entry:
            raise SystemExit(f"error: cannot parse {line!r}")
        name, value = entry.groups()
        if re.match(r"^(0x[0-9A-Fa-f]+|\d+)$", value):
            live[name] = int(value, 0)
        else:
            pending[name] = value                           # an alias of another enumerator
    for name, target in pending.items():
        live[name] = live[target]
    return live, retired


def parse_schema(path):
    values = json.load(open(path, encoding="utf-8"))["messageTypes"]["values"]
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


def compare(kind, reference, schema, skip, problems):
    for name in sorted(set(reference) | set(schema)):
        if name in skip:
            continue
        if name not in schema:
            problems.append(f"FAIL {kind} {name} = {reference[name]} is in CanId.h but not in the schema")
        elif name not in reference:
            problems.append(f"FAIL {kind} {name} = {schema[name]} is in the schema but not in CanId.h")
        elif reference[name] != schema[name]:
            problems.append(f"FAIL {kind} {name}: CanId.h says {reference[name]}, the schema says {schema[name]}")


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    live, retired = parse_canlib(sys.argv[1])
    schema_live, schema_retired, csharp_only = parse_schema(sys.argv[2])

    problems = []
    compare("message type", live, schema_live, csharp_only, problems)
    compare("retired id", retired, schema_retired, csharp_only, problems)
    for problem in problems:
        print(problem)

    checks = len(set(live) | set(schema_live)) + len(set(retired) | set(schema_retired))
    extra = f", {len(csharp_only)} C#-only" if csharp_only else ""
    print(f"{checks} message type checks ({len(retired)} retired{extra}), {len(problems)} failures")
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main())
