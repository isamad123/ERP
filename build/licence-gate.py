#!/usr/bin/env python3
"""Licence gate (D-09). Lists every NuGet package (direct and transitive) with its licence and fails the build
on any licence that is not allowed. Standard library only, so the gate itself adds no dependency.
With --npm <folder> (slice S1.12a, ruling Q20, CR-064) it checks every npm package of the pnpm workspace instead:
packages shipped to browsers (production dependencies) against the D-09 list, development-only packages against the
D-09 list, each with the exceptions named per package in licence-allowlist.json ("npm").

Usage:  python3 build/licence-gate.py [--solution Erp.sln] [--self-test]
        python3 build/licence-gate.py --npm web
Run `dotnet restore` (or `pnpm install`) first. Exit code 0 = all allowed, 1 = a licence is not allowed or unknown,
2 = gate error.
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
    # npm (S1.12a): an exception counts only for the package it names, and only in its own group.
    npm_cases = [
        ("tslib", "0BSD", "shipped", True), ("left-pad", "0BSD", "shipped", False),
        ("@fontsource/noto-sans", "OFL-1.1", "shipped", True), ("some-font", "OFL-1.1", "shipped", False),
        ("axe-core", "MPL-2.0", "development", True), ("axe-core", "MPL-2.0", "shipped", False),
        ("react", "MIT", "shipped", True), ("x", "GPL-3.0-only", "development", False), ("x", None, "development", False),
    ]
    for name, licence, group, want in npm_cases:
        if npm_allowed(name, licence, group, config)[0] != want:
            failures.append((f"npm {group} {name} {licence}", want))
    for e, want in failures:
        print(f"self-test FAILED: {e!r} expected allowed={want}")
    total = len(cases) + len(npm_cases)
    print(f"licence gate self-test: {total - len(failures)}/{total} passed")
    return 1 if failures else 0


def npm_allowed(name, licence, group, config):
    """An npm package's licence: the D-09 list, or an exception that names this package for its group."""
    named = {lic for lic, packages in config.get("npm", {}).get(group, {}).items() if not lic.startswith("_") and name in packages}
    widened = dict(config, allowed=list(config["allowed"]) + sorted(named))
    return is_allowed(licence, widened)


def list_npm(folder, prod):
    command = ["pnpm", "licenses", "list", "--json"] + (["--prod"] if prod else [])
    out = subprocess.run(command, cwd=os.path.join(ROOT, folder), capture_output=True, text=True, check=False)
    if out.returncode != 0:
        print(out.stdout + out.stderr, file=sys.stderr)
        sys.exit(2)
    found = {}
    for licence, packages in json.loads(out.stdout or "{}").items():
        for p in packages:
            for version in p.get("versions") or [p.get("version", "?")]:
                found.setdefault((p["name"], version), set()).add(None if licence in ("Unknown", "UNKNOWN", "") else licence)
    return found


def npm_gate(folder, config):
    shipped = list_npm(folder, prod=True)
    everything = list_npm(folder, prod=False)
    rows, refused = [], []
    for (name, version), licences in sorted(everything.items(), key=lambda kv: kv[0][0].lower()):
        group = "shipped" if (name, version) in shipped else "development"
        for licence in sorted(licences, key=lambda x: x or ""):
            ok, reason = npm_allowed(name, licence, group, config)
            rows.append((name, version, licence or "UNKNOWN", group, "ok" if ok else "REFUSED"))
            if not ok:
                refused.append(f"{name} {version}: {licence or 'UNKNOWN'} ({group}; {reason})")
    widths = [max(len(r[i]) for r in rows + [("Package", "Version", "Licence", "Group", "Result")]) for i in range(5)]
    print("  ".join(h.ljust(w) for h, w in zip(("Package", "Version", "Licence", "Group", "Result"), widths)))
    for r in rows:
        print("  ".join(c.ljust(w) for c, w in zip(r, widths)))
    print(f"\n{len(everything)} npm packages checked ({len(shipped)} shipped to browsers); {len(refused)} refused.")
    if refused:
        print("\nnpm licence gate FAILED (D-09, S1.12 ruling Q20, CR-064):")
        for line in refused:
            print("  - " + line)
        return 1
    print("npm licence gate passed.")
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--solution", default="Erp.sln")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--npm", help="check the npm packages of this pnpm workspace folder instead (e.g. web)")
    args = parser.parse_args()
    config = load_config()
    if args.self_test:
        return self_test(config)
    if args.npm:
        return npm_gate(args.npm, config)

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
