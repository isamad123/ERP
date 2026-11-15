#!/usr/bin/env python3
"""Licence gate (D-09). Lists every NuGet package (direct and transitive) with its licence and fails the build
on any licence that is not allowed. Standard library only, so the gate itself adds no dependency.

Usage:  python3 build/licence-gate.py [--solution Erp.sln] [--self-test]
Run `dotnet restore` first. Exit code 0 = all allowed, 1 = a licence is not allowed or unknown, 2 = gate error.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def load_config():
    with open(os.path.join(HERE, "licence-allowlist.json"), encoding="utf-8") as f:
        return json.load(f)


def is_allowed(expression, config):
    """SPDX expression check: any OR-alternative may be chosen; every AND-part must be allowed; WITH is refused."""
    if not expression:
        return False, "no licence declared"
    allowed = set(config["allowed"])
    approved = {k for k, v in config.get("decisionRequired", {}).items() if v}
    if " WITH " in f" {expression} ":
        return False, "licence exceptions (WITH) need a decision"
    flat = expression.replace("(", " ").replace(")", " ")
    for alternative in re.split(r"\s+OR\s+", flat.strip()):
        parts = [p.strip() for p in re.split(r"\s+AND\s+", alternative) if p.strip()]
        if parts and all(p in allowed or p in approved for p in parts):
            return True, ""
    pending = [p for p in config.get("decisionRequired", {}) if p in flat.split()]
    if pending:
        return False, f"{', '.join(pending)} is allowed only with a recorded decision"
    return False, "not in the allow list"


def packages_root():
    return os.environ.get("NUGET_PACKAGES") or os.path.join(os.path.expanduser("~"), ".nuget", "packages")


def list_packages(solution):
    out = subprocess.run(
        ["dotnet", "list", solution, "package", "--include-transitive", "--format", "json"],
        cwd=ROOT, capture_output=True, text=True, check=False)
    if out.returncode != 0:
        print(out.stdout + out.stderr, file=sys.stderr)
        sys.exit(2)
    data = json.loads(out.stdout)
    found = {}
    for project in data.get("projects", []):
        name = os.path.splitext(os.path.basename(project["path"]))[0]
        for framework in project.get("frameworks", []):
            for key in ("topLevelPackages", "transitivePackages"):
                for p in framework.get(key, []):
                    found.setdefault((p["id"], p["resolvedVersion"]), set()).add(name)
    return found


def read_licence(package_id, version, config):
    override = config.get("overrides", {}).get(f"{package_id}@{version}")
    if override:
        return override["licence"], "override: " + override["evidence"]
    nuspec = os.path.join(packages_root(), package_id.lower(), version.lower(), package_id.lower() + ".nuspec")
    if not os.path.exists(nuspec):
        return None, f"nuspec not found at {nuspec} (run dotnet restore)"
    meta = next((e for e in ET.parse(nuspec).getroot() if e.tag.endswith("metadata")), None)
    licence = url = None
    for element in meta if meta is not None else []:
        tag = element.tag.split("}")[-1]
        if tag == "license" and element.get("type") == "expression":
            licence = (element.text or "").strip()
        elif tag == "license":
            return None, "licence is a file inside the package; review it and add an override"
        elif tag == "licenseUrl":
            url = (element.text or "").strip()
    if licence:
        return licence, "nuspec expression"
    if url:
        mapped = config.get("licenceUrls", {}).get(url)
        return (mapped, f"licenceUrl {url}") if mapped else (None, f"unrecognised licenceUrl {url}")
    return None, "no licence metadata"


def self_test(config):
    cases = [("MIT", True), ("Apache-2.0", True), ("MIT OR GPL-3.0-only", True), ("GPL-3.0-only", False),
             ("MIT AND GPL-2.0-only", False), ("MS-PL", False), ("Apache-2.0 WITH LLVM-exception", False),
             ("", False), (None, False)]
    failures = [(e, want) for e, want in cases if is_allowed(e, config)[0] != want]
    for e, want in failures:
        print(f"self-test FAILED: {e!r} expected allowed={want}")
    print(f"licence gate self-test: {len(cases) - len(failures)}/{len(cases)} passed")
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--solution", default="Erp.sln")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    config = load_config()
    if args.self_test:
        return self_test(config)

    packages = list_packages(args.solution)
    rows, refused = [], []
    for (package_id, version), users in sorted(packages.items(), key=lambda kv: kv[0][0].lower()):
        licence, source = read_licence(package_id, version, config)
        ok, reason = is_allowed(licence, config)
        rows.append((package_id, version, licence or "UNKNOWN", "ok" if ok else "REFUSED", source))
        if not ok:
            refused.append(f"{package_id} {version}: {licence or 'UNKNOWN'} ({reason or source}); used by {', '.join(sorted(users))}")

    widths = [max(len(r[i]) for r in rows + [("Package", "Version", "Licence", "Result", "")]) for i in range(4)]
    header = ("Package", "Version", "Licence", "Result")
    print("  ".join(h.ljust(w) for h, w in zip(header, widths)))
    for r in rows:
        print("  ".join(c.ljust(w) for c, w in zip(r[:4], widths)))
    print(f"\n{len(rows)} packages checked; {len(refused)} refused.")
    if refused:
        print("\nLicence gate FAILED (D-09):")
        for line in refused:
            print("  - " + line)
        return 1
    print("Licence gate passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
