"""Run the local correctness matrix and retain exact commands and source hashes.

Usage: uv run --no-project python tools/validate.py --output artifacts/validation/<name>
The .NET 8 runtime must already exist at artifacts/runtime8/dotnet. This script
does not install runtimes, change global configuration, or publish packages.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import time
import xml.etree.ElementTree as ET


def inspect_test_results(path: Path) -> dict[str, int]:
    """Require executed passing tests, rather than trusting a test process exit alone."""
    report = ET.parse(path).getroot()
    counters = report.find(".//{*}ResultSummary/{*}Counters")
    if counters is None:
        raise ValueError(f"Missing test counters: {path}")
    counts = {name: int(counters.get(name, "0")) for name in ("total", "executed", "passed", "failed", "notExecuted")}
    results = report.findall(".//{*}Results/{*}UnitTestResult")
    if (
        counts["total"] <= 0
        or counts["executed"] != counts["total"]
        or counts["passed"] != counts["total"]
        or counts["failed"] != 0
        or counts["notExecuted"] != 0
        or len(results) != counts["total"]
        or any(result.get("outcome") != "Passed" for result in results)
    ):
        raise ValueError(f"Test gate requires nonempty, fully executed, passing tests: {counts}")
    return counts


def source_manifest(root: Path) -> dict[str, str]:
    names = subprocess.check_output(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        cwd=root,
    ).decode().split("\0")
    suffixes = {".cs", ".csproj", ".props", ".targets", ".slnx", ".json", ".py", ".yml"}
    return {
        name: hashlib.sha256((root / name).read_bytes()).hexdigest()
        for name in sorted(set(names))
        if name
        and (root / name).is_file()
        and Path(name).suffix in suffixes
        and not name.startswith("docs/")
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--host-smokes", action="store_true", help="Run the three local host samples after correctness tests.")
    parser.add_argument("--polling-watcher", action="store_true", help="Use an explicit process-only polling watcher for host smokes; never changes sample or global settings.")
    parser.add_argument("--runtime10-host", help="Use an existing local .NET 10 host for tests and the source consumer; host samples still use the system ASP.NET runtime.")
    arguments = parser.parse_args()
    if arguments.polling_watcher and not arguments.host_smokes:
        parser.error("--polling-watcher requires --host-smokes")
    root = Path(__file__).resolve().parent.parent
    output = (root / arguments.output).resolve()
    if output.exists():
        parser.error(f"Use a fresh output directory to preserve prior evidence: {output}")
    runtime8 = root / "artifacts/runtime8/dotnet"
    if not runtime8.is_file():
        parser.error(f"Expected an existing .NET 8 host at {runtime8}")
    runtime10 = (root / arguments.runtime10_host).resolve() if arguments.runtime10_host else None
    if runtime10 is not None and not runtime10.is_file():
        parser.error(f"Expected an existing .NET 10 host at {runtime10}")
    output.mkdir(parents=True)

    test_projects = ("LoadingCache.Tests", "LoadingCache.DependencyInjection.Tests", "LoadingCache.StressTests")
    consumer = "tests/LoadingCache.ConsumerSmoke/LoadingCache.ConsumerSmoke.csproj"
    commands = [
        ("environment", ["dotnet", "--info"]),
        ("restore", ["dotnet", "restore", "LoadingCache.slnx"]),
        ("tool-restore", ["dotnet", "tool", "restore"]),
        ("format-check", ["dotnet", "csharpier", "check", "."]),
        ("build", ["dotnet", "build", "LoadingCache.slnx", "-c", "Release", "--no-restore"]),
    ]
    if runtime10 is not None:
        commands.insert(1, ("environment-net10", [str(runtime10), "--info"]))
    for project in test_projects:
        for framework in ("net8.0", "net10.0"):
            command = [
                "dotnet", "test", f"tests/{project}/{project}.csproj", "-c", "Release", "--no-build", "--no-restore",
                "-f", framework, "--logger", f"trx;LogFileName={project}-{framework}.trx",
                "--results-directory", str(output),
            ]
            if framework == "net8.0":
                command += ["--", f"RunConfiguration.DotNetHostPath={runtime8}"]
            elif runtime10 is not None:
                command += ["--", f"RunConfiguration.DotNetHostPath={runtime10}"]
            commands.append((f"{project}-{framework}", command))
    commands.extend([
        ("consumer-net8", [str(runtime8), str(root / Path(consumer).parent / "bin/Release/net8.0/LoadingCache.ConsumerSmoke.dll")]),
        ("consumer-net10", [str(runtime10) if runtime10 else "dotnet", str(root / Path(consumer).parent / "bin/Release/net10.0/LoadingCache.ConsumerSmoke.dll")]),
    ])
    if arguments.host_smokes:
        for sample in ("AspNetCore", "Grpc", "Worker"):
            commands.append((f"host-{sample}", ["dotnet", str(root / f"samples/LoadingCache.{sample}/bin/Release/net10.0/LoadingCache.{sample}.dll"), "--smoke"]))

    before = source_manifest(root)
    (output / "source-manifest.json").write_text(json.dumps(before, indent=2) + "\n")
    results = {
        "platform": platform.platform(),
        "architecture": platform.machine(),
        "logical_processors": os.cpu_count(),
        "commands": [],
        "source_unchanged": None,
        "host_smokes_requested": arguments.host_smokes,
        "polling_watcher_requested": arguments.polling_watcher,
        "runtime10_test_host": str(runtime10) if runtime10 else None,
    }
    exit_code = 0
    for name, command in commands:
        print(f"Running {name}", flush=True)
        started = time.monotonic()
        environment_overrides = {"DOTNET_USE_POLLING_FILE_WATCHER": "1"} if name.startswith("host-") and arguments.polling_watcher else {}
        environment = os.environ.copy()
        environment.update(environment_overrides)
        with (output / f"{name}.log").open("w") as log:
            try:
                completed = subprocess.run(command, cwd=root, env=environment, stdout=log, stderr=subprocess.STDOUT, timeout=30 if name.startswith("host-") else 600)
                status = completed.returncode
            except subprocess.TimeoutExpired:
                status = 124
                log.write("\nValidation watchdog expired; this is not a passing result.\n")
        test_counts = None
        if status == 0 and any(name.startswith(project + "-") for project in test_projects):
            try:
                test_counts = inspect_test_results(output / f"{name}.trx")
            except (OSError, ET.ParseError, ValueError) as error:
                status = 3
                with (output / f"{name}.log").open("a") as log:
                    log.write(f"\nTRX acceptance failed: {error}\n")
        results["commands"].append({
            "name": name,
            "command": command,
            "exit_code": status,
            "elapsed_seconds": time.monotonic() - started,
            "log": f"{name}.log",
            "environment_overrides": environment_overrides,
            "test_counts": test_counts,
        })
        print(f"{name}: exit {status}", flush=True)
        if status:
            print((output / f"{name}.log").read_text()[-12_000:], flush=True)
            exit_code = status
            break

    after = source_manifest(root)
    results["source_unchanged"] = before == after
    if before != after:
        (output / "source-manifest-after.json").write_text(json.dumps(after, indent=2) + "\n")
        print("Source changed during validation; do not treat this as a fixed-revision result.")
        exit_code = exit_code or 2
    results["unrun_commands"] = [name for name, _ in commands[len(results["commands"]):]]
    (output / "results.json").write_text(json.dumps(results, indent=2) + "\n")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
