# pyright: strict
"""The scripts' command lines: building the parser, and the options it parses as a dataclass."""

from __future__ import annotations

import argparse
import dataclasses
from collections.abc import Sequence
from typing import Any, TypeVar

T = TypeVar('T')


def parser(doc: str | None, **kwargs: object) -> argparse.ArgumentParser:
    """A parser whose description is the script's docstring, laid out as written."""
    return argparse.ArgumentParser(description=doc, formatter_class=argparse.RawDescriptionHelpFormatter,
                                   **kwargs)  # pyright: ignore[reportArgumentType]


def parse(p: argparse.ArgumentParser, argv: Sequence[str] | None = None) -> argparse.Namespace:
    """Parse the command line, or the arguments given."""
    return p.parse_args(argv)


def typed(cls: type[T], namespace: argparse.Namespace, **values: Any) -> T:
    """A command line's options as the dataclass cls, from the parsed arguments that are its fields and
    any values given."""
    fields = {f.name for f in dataclasses.fields(cls)}  # pyright: ignore[reportArgumentType]
    return cls(**{k: v for k, v in vars(namespace).items() if k in fields}, **values)
