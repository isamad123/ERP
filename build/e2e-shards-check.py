#!/usr/bin/env python3
"""Every browser spec file runs in exactly one CI shard (R-318).

The shards are listed in .github/workflows/ci.yml (job `web-e2e`, matrix `files:`). A spec file in web/apps/web/e2e that no shard names would
never run in CI; a name that is no spec file, or one named twice, is a typing mistake. Exit code 1 lists the problems.
"""
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent.parent
workflow = (root / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
block = workflow.split("  web-e2e:", 1)[1].split("    services:", 1)[0]
named = [name for files in re.findall(r'files: "([^"]*)"', block) for name in files.split()]
on_disk = sorted(p.name[: -len(".spec.ts")] for p in (root / "web" / "apps" / "web" / "e2e").glob("*.spec.ts"))

problems = []
problems += [f"{n}: no shard runs this spec file" for n in on_disk if n not in named]
problems += [f"{n}: named in a shard but there is no e2e/{n}.spec.ts" for n in named if n not in on_disk]
problems += [f"{n}: named in more than one shard" for n in sorted(set(named)) if named.count(n) > 1]
if problems:
    print("\n".join(problems))
    sys.exit(1)
print(f"e2e shards: {len(on_disk)} spec files, each in exactly one shard")
