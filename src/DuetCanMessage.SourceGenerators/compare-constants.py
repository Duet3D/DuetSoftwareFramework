#!/usr/bin/env python3
"""Compare the schema's constants and string tables against CANlib's.

The layout probe proves where every field sits and is blind to what any of them is worth. A constant is
exactly that blind spot: several of these are protocol magic numbers — CanMessageSetAddressAndNormalTiming's
DoSetTimingYes is 0xB6, CanMessageEnterTestMode's Passwd is a specific word — where a wrong value produces a
message of the right shape that the board rejects or, worse, silently misreads.

Compares three things:

  * the struct constants, i.e. every "static constexpr" CANlib declares inside a struct;
  * the schema-level constants that bound the array members;
  * the string tables, such as what each bit of a driver status word means. The layout probe cannot
    see those either — a string array has no layout — and they are rendered by a board as well as by
    DuetControlServer, so a difference means one machine describes a fault two ways.

Values are compared after normalising the spellings the two sides use for the same number: integer suffixes,
digit separators, casts, and CANlib's habit of writing a length as sizeof(field).

Usage: compare-constants.py <can-messages.json> <CANlib src directory>
"""

import glob
import json
import os
import re
import sys


def strip_comments(text):
    return re.sub(r"//[^\n]*", "", re.sub(r"/\*.*?\*/", " ", text, flags=re.S))


def split_declarators(declaration):
    """Split "a = 0, b = 1" into its declarators, ignoring commas inside brackets."""
    depth, current, parts = 0, "", []
    for char in declaration:
        if char in "([<":
            depth += 1
        elif char in ")]>":
            depth -= 1
        if char == "," and depth == 0:
            parts.append(current)
            current = ""
        else:
            current += char
    parts.append(current)
    return parts


def parse_canlib(directory):
    """Every static constexpr CANlib declares, and the size of every array member, by name."""
    constants, arrays = {}, {}
    for path in sorted(glob.glob(os.path.join(directory, "*.h"))):
        text = strip_comments(open(path, encoding="utf-8", errors="replace").read())
        for m in re.finditer(r"(?:static\s+)?constexpr\s+(?:unsigned\s+|signed\s+|const\s+)*[\w:]+\s+([^;]+);", text):
            for part in split_declarators(m.group(1)):
                name, _, value = part.partition("=")
                if value:
                    constants.setdefault(name.strip().lstrip("*&"), value.strip())
        for m in re.finditer(r"^\s+[\w:]+\s+(\w+)\s*\[([^\]]+)\]\s*;", text, re.M):
            arrays.setdefault(m.group(1), m.group(2).strip())
    return constants, arrays


def normalise(value, canlib, arrays):
    """Reduce the two sides' spellings of the same number to a comparable form."""
    value = str(value).strip()
    # CANlib often writes a buffer length as sizeof(thatBuffer)
    def expand_sizeof(m):
        field = m.group(1)
        return arrays.get(field, m.group(0))
    value = re.sub(r"sizeof\(\s*(\w+)\s*\)", expand_sizeof, value)
    value = re.sub(r"\((?:unsigned\s+|signed\s+)?(?:uint\d+_t|int\d+_t|size_t|unsigned|int|char)\)", "", value)
    value = value.replace("'", "")                          # digit separators: 48'000'000
    value = re.sub(r"(?<=[0-9a-fA-F])[uUlL]+\b", "", value)  # integer suffixes
    value = re.sub(r"\s+", "", value)

    # Fold a constant reference to its value so that "MaxLinearDriversPerCanSlave" and "8" compare equal
    for _ in range(4):
        expanded = re.sub(r"\b([A-Za-z_]\w*)\b", lambda m: canlib.get(m.group(1), m.group(1)), value)
        expanded = re.sub(r"\s+|'", "", expanded)
        if expanded == value:
            break
        value = expanded

    try:
        return str(eval(value.replace("0x", "0x"), {"__builtins__": {}}, {}))    # noqa: S307 - fixed inputs
    except Exception:
        return value.lower()


def parse_canlib_string_tables(directory):
    """Every constexpr array of string literals CANlib declares, by name."""
    tables = {}
    for path in glob.glob(os.path.join(directory, "**", "*.h"), recursive=True):
        text = strip_comments(open(path, encoding="utf-8", errors="replace").read())
        for match in re.finditer(r"constexpr\s+const\s+char\s*\*[\w\s_]*\s+(\w+)\s*\[\s*\]\s*=\s*\{(.*?)\}\s*;", text, re.S):
            tables[match.group(1)] = [literal for literal in re.findall(r'"((?:[^"\\]|\\.)*)"', match.group(2))]
    return tables


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    schema = json.load(open(sys.argv[1], encoding="utf-8"))
    canlib, arrays = parse_canlib(sys.argv[2])

    problems, checks = [], 0
    for struct in schema["structs"]:
        for constant in struct.get("constants", []):
            checks += 1
            name, want = constant["name"], constant["value"]
            # CANlib spells some of these differently in case only; compare case-insensitively by name
            match = next((k for k in (name, name[0].lower() + name[1:]) if k in canlib), None)
            if match is None:
                problems.append(f"FAIL {struct['name']}::{name} = {want} is not declared by CANlib")
                continue
            if normalise(want, canlib, arrays) != normalise(canlib[match], canlib, arrays):
                problems.append(
                    f"FAIL {struct['name']}::{name}: schema says {want}, CANlib says {canlib[match]}")

    for name, want in schema.get("constants", {}).items():
        checks += 1
        # a schema-level constant is either the bare value or an object carrying the value and its doc
        if isinstance(want, dict):
            want = want["value"]
        if name not in canlib:
            problems.append(f"FAIL {name} = {want} is not declared by CANlib")
        elif normalise(want, canlib, arrays) != normalise(canlib[name], canlib, arrays):
            problems.append(f"FAIL {name}: schema says {want}, CANlib says {canlib[name]}")

    string_tables = parse_canlib_string_tables(sys.argv[2])
    for group in schema.get("constantGroups", []):
        for table in group.get("stringTables", []):
            checks += 1
            name, want = table["name"], table["values"]
            if name not in string_tables:
                problems.append(f"FAIL {group['name']}.{name} is not declared by CANlib")
            elif string_tables[name] != want:
                got = string_tables[name]
                if len(got) != len(want):
                    problems.append(f"FAIL {group['name']}.{name}: CANlib has {len(got)} entries, the schema has {len(want)}")
                for index, (theirs, ours) in enumerate(zip(got, want)):
                    if theirs != ours:
                        problems.append(f"FAIL {group['name']}.{name}[{index}]: CANlib says \"{theirs}\", the schema says \"{ours}\"")

        for constant in group["values"]:
            checks += 1
            name, want = constant["name"], constant["value"]
            if name not in canlib:
                problems.append(f"FAIL {group['name']}.{name} = {want} is not declared by CANlib")
            elif normalise(want, canlib, arrays) != normalise(canlib[name], canlib, arrays):
                problems.append(f"FAIL {group['name']}.{name}: schema says {want}, CANlib says {canlib[name]}")

    for problem in problems:
        print(problem)
    print(f"{checks} constant checks, {len(problems)} failures")
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main())
