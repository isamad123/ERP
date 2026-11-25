#!/usr/bin/env python3
"""The integration test shards cover every test namespace exactly once (R-318).

Shards 1 to n-1 in .github/workflows/ci.yml (job `dotnet-integration`) name test namespaces with --filter-namespace; the last shard runs
everything else with --filter-not-namespace over the same names. Two things can go wrong by typing: a named namespace that does not exist
(its tests would silently land in the last shard, and the shard would be empty), or the last shard's exclusions not matching the others'.
Exit code 1 lists the problems.
"""
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent.parent
workflow = (root / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
block = workflow.split("  dotnet-integration:", 1)[1].split("    services:", 1)[0]
filters = re.findall(r'filter: "([^"]*)"', block)
named, excluded = [], []
for f in filters:
    named += re.findall(r"--filter-namespace (\S+)", f)
    excluded += re.findall(r"--filter-not-namespace (\S+)", f)

declared = set()
for path in (root / "tests" / "Erp.IntegrationTests").rglob("*.cs"):
    if "obj" in path.parts or "bin" in path.parts:
        continue
    declared.update(re.findall(r"^namespace (Erp\.IntegrationTests[\w.]*)\s*[;{]?", path.read_text(encoding="utf-8"), re.M))

problems = []
problems += [f"{n}: named in a shard but no test file declares this namespace" for n in named if n not in declared]
problems += [f"{n}: named in more than one shard" for n in sorted(set(named)) if named.count(n) > 1]
if sorted(named) != sorted(excluded):
    problems.append("the last shard's --filter-not-namespace list differs from the namespaces named by the other shards")
if problems:
    print("\n".join(problems))
    sys.exit(1)
print(f"integration shards: {len(named)} namespaces named, {len(declared)} declared; the last shard runs the rest")
