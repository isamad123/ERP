#!/usr/bin/env python3
"""Decides whether a change needs the build and tests (CI 'changes' job, product owner 2026-11-16).

Reads changed file paths (one per line) on standard input and prints 'true' when CI must build and test,
'false' when every file is a prose document. Run with --self-test to check the rule itself.

Prose documents: top-level *.md files and anything under docs/, EXCEPT documents the build or tests read:
  docs/data-design/**            generator input and the generated data dictionary (drift checks)
  docs/handoff/**                GeneratorTests treats a handoff note as "slice finished"
  docs/registers/todo-stubs.md   written by the generator
When in doubt the answer is 'true': an empty list, or any other path, runs everything.
"""
import sys

BUILD_READS = ("docs/data-design/", "docs/handoff/", "docs/registers/todo-stubs.md")


def is_prose(path: str) -> bool:
    if path.startswith(BUILD_READS):
        return False
    if path.startswith("docs/"):
        return True
    return "/" not in path and path.lower().endswith(".md")


def needs_build(paths: list[str]) -> bool:
    paths = [p.strip() for p in paths if p.strip()]
    return not paths or not all(is_prose(p) for p in paths)


def self_test() -> None:
    cases = [
        ([], True),
        (["docs/slices/S1.07-audit.md"], False),
        (["docs/slices/S1.07-audit.md", "docs/registers/risks-and-changes.md", "CLAUDE.md"], False),
        (["docs/data-design/kernel.yaml"], True),
        (["docs/handoff/2026-11-16-S1.07.md"], True),
        (["docs/registers/todo-stubs.md"], True),
        (["docs/slices/S1.07-audit.md", "src/Kernel/Audit.cs"], True),
        (["src/README.md"], True),
        ([".github/workflows/ci.yml"], True),
        (["build/ci-changes.py"], True),
    ]
    for paths, expected in cases:
        got = needs_build(paths)
        assert got == expected, f"{paths}: expected {expected}, got {got}"
    print(f"ci-changes self-test: {len(cases)} cases passed")


if __name__ == "__main__":
    if "--self-test" in sys.argv:
        self_test()
    else:
        print("true" if needs_build(sys.stdin.read().splitlines()) else "false")
