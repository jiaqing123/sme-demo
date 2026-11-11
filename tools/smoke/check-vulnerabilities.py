#!/usr/bin/env python3
"""Fail when any repository project resolves a package with a known vulnerability.

`dotnet list package --vulnerable` exits 0 even when it prints findings, so the
CI job cannot rely on its exit code. This wrapper runs the command per project
with `--format json` and fails if any vulnerability entry is present. It also
fails when the command itself fails or emits unparseable output, so a broken
scan is never silently green.

Usage: `python3 tools/smoke/check-vulnerabilities.py [project.csproj ...]`.
With no arguments it scans every project under src/, tests/, and tools/.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SCAN_DIRECTORIES = ("src", "tests", "tools")
PACKAGE_DIRECTORIES = {"bin", "obj"}


def discover_projects(extra_projects: list[str]) -> list[Path]:
    if extra_projects:
        return [Path(argument).resolve() for argument in extra_projects]

    projects: list[Path] = []
    for directory in SCAN_DIRECTORIES:
        root = REPOSITORY_ROOT / directory
        if not root.is_dir():
            continue
        for project in root.rglob("*.csproj"):
            if not PACKAGE_DIRECTORIES.intersection(project.parts):
                projects.append(project)
    return sorted(projects)


def scan(project: Path) -> list[str]:
    """Return one human-readable line per vulnerability found in `project`."""
    command = [
        "dotnet",
        "list",
        str(project),
        "package",
        "--vulnerable",
        "--include-transitive",
        "--format",
        "json",
        # CI runs this after the Release build, which restored every project.
        # Reusing the assets file keeps the scan to the vulnerability query.
        "--no-restore",
    ]
    completed = subprocess.run(
        command,
        cwd=REPOSITORY_ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )

    if completed.returncode != 0:
        raise RuntimeError(
            f"`{' '.join(command)}` exited {completed.returncode}\n"
            f"stdout:\n{completed.stdout}\nstderr:\n{completed.stderr}"
        )

    try:
        document = json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        raise RuntimeError(
            f"`{' '.join(command)}` returned non-JSON output: {error}\n"
            f"stdout:\n{completed.stdout}\nstderr:\n{completed.stderr}"
        ) from error

    findings: list[str] = []
    for scanned_project in document.get("projects", []):
        project_path = scanned_project.get("path", str(project))
        for framework in scanned_project.get("frameworks", []):
            framework_name = framework.get("framework", "?")
            for collection in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(collection, []):
                    for vulnerability in package.get("vulnerabilities", []):
                        findings.append(
                            f"{project_path} [{framework_name}] "
                            f"{package.get('id', '?')} "
                            f"{package.get('resolvedVersion', '?')}: "
                            f"{vulnerability.get('severity', '?')} "
                            f"{vulnerability.get('advisoryurl', '?')}"
                        )
    return findings


def main() -> int:
    projects = discover_projects(sys.argv[1:])
    if not projects:
        print("FAIL: no projects found under src/, tests/, or tools/", file=sys.stderr)
        return 1

    all_findings: list[str] = []
    for project in projects:
        relative = project.relative_to(REPOSITORY_ROOT)
        try:
            findings = scan(project)
        except RuntimeError as error:
            print(f"FAIL: vulnerability scan error for {relative}\n{error}", file=sys.stderr)
            return 1

        if findings:
            print(f"FAIL: {relative} has vulnerable packages:", file=sys.stderr)
            for finding in findings:
                print(f"  {finding}", file=sys.stderr)
            all_findings.extend(findings)
        else:
            print(f"PASS: {relative}")

    if all_findings:
        print(
            f"FAIL: {len(all_findings)} vulnerable package entr"
            f"{'y' if len(all_findings) == 1 else 'ies'} found across {len(projects)} projects",
            file=sys.stderr,
        )
        return 1

    print(f"PASS: no known vulnerabilities across {len(projects)} projects")
    return 0


if __name__ == "__main__":
    sys.exit(main())
