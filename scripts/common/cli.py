# pyright: strict
"""The scripts' command lines: building the parser, and the options it parses as a dataclass."""

from __future__ import annotations

import argparse
import dataclasses
from collections.abc import Callable, Sequence
from typing import Any, TypeVar

T = TypeVar('T')


def parser(doc: str | None, **kwargs: object) -> argparse.ArgumentParser:
    """A parser whose description is the script's docstring, laid out as written."""
    return argparse.ArgumentParser(description=doc, formatter_class=argparse.RawDescriptionHelpFormatter,
                                   **kwargs)  # pyright: ignore[reportArgumentType]


def complete(p: argparse.ArgumentParser) -> None:
    """Answer a shell's completion request if argcomplete is installed (see scripts/completions/), which
    exits; otherwise do nothing."""
    try:
        import argcomplete  # pyright: ignore[reportMissingTypeStubs]
    except ImportError:
        return
    argcomplete.autocomplete(p)


def complete_with(action: argparse.Action, values: Callable[[], Sequence[str]]) -> argparse.Action:
    """Have argcomplete offer these values for an argument, worked out only when asked for."""
    def completer(**_: object) -> Sequence[str]:
        return values()
    setattr(action, 'completer', completer)
    return action


def complete_with_prefix(action: argparse.Action,
                         values: Callable[[str, argparse.Namespace], Sequence[str]]) -> argparse.Action:
    """Have argcomplete offer values that depend on the word being completed and the arguments before it."""
    def completer(prefix: str, parsed_args: argparse.Namespace, **_: object) -> Sequence[str]:
        return values(prefix, parsed_args)
    setattr(action, 'completer', completer)
    return action


def parse(p: argparse.ArgumentParser, argv: Sequence[str] | None = None) -> argparse.Namespace:
    """Parse the command line, or the arguments given, after answering any completion request."""
    complete(p)
    return p.parse_args(argv)


def typed(cls: type[T], namespace: argparse.Namespace, **values: Any) -> T:
    """A command line's options as the dataclass cls, from the parsed arguments that are its fields and
    any values given."""
    fields = {f.name for f in dataclasses.fields(cls)}  # pyright: ignore[reportArgumentType]
    return cls(**{k: v for k, v in vars(namespace).items() if k in fields}, **values)
