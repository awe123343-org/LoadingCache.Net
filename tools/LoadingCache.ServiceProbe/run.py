#!/usr/bin/env python3
"""Run frozen HTTP hosts serially; retain raw data and conservative paired gates."""

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
METRICS = ("cpuSecondsPerRequest", "p99Microseconds")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def source_paths():
    names = subprocess.check_output(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--",
         "src", "tools/LoadingCache.ServiceProbe", "Directory.Build.props",
         "Directory.Build.targets", "Directory.Packages.props", "global.json",
         "NuGet.Config", "docs/v1-service-profile.md"], cwd=ROOT,
    ).decode().split("\0")
    return sorted({name for name in names if name and (ROOT / name).is_file()})


def file_manifest(folder):
    return {str(path.relative_to(folder)): sha(path) for path in sorted(folder.rglob("*")) if path.is_file()}


def metrics(result, profile, backend):
    for key, expected in profile.items():
        require(result[key] == expected, f"Client profile changed: {key}")
    frequency = result["stopwatchFrequency"]
    require(isinstance(frequency, int) and frequency > 0, "Invalid Stopwatch frequency")
    for name, seconds in (("warmup", profile["warmupSeconds"]), ("measured", profile["seconds"])):
        phase = result[name]
        offered = profile["rate"] * seconds
        require(phase["Offered"] == offered, f"Wrong {name} offered count")
        outcomes = phase["Outcomes"]
        ticks = phase["LatencyTicks"]
        require(len(outcomes) == len(ticks) == offered, f"Incomplete {name} raw outcomes")
        require(all(value in ("completed", "rejected", "timeout", "failed") for value in outcomes), "Unknown request outcome")
        require(all(isinstance(value, int) and value >= 0 for value in ticks), "Invalid request latency")
        for outcome, count in (("completed", "Completed"), ("rejected", "Rejected"), ("timeout", "Timeout"), ("failed", "Failed")):
            require(phase[count] == outcomes.count(outcome), f"Wrong {name} outcome accounting")
        successful = sorted(value for value, outcome in zip(ticks, outcomes, strict=True) if outcome == "completed")
        expected_p99 = successful[max(0, math.ceil(len(successful) * 0.99) - 1)] * (1_000_000 / frequency) if successful else None
        require(phase["P99Microseconds"] == expected_p99, f"Wrong {name} p99")
        require(math.isfinite(phase["WallSecondsIncludingDrain"]) and phase["WallSecondsIncludingDrain"] > 0, "Invalid phase duration")
    measured = result["measured"]
    server = result["server"]
    require(math.isfinite(server["cpuSeconds"]) and server["cpuSeconds"] > 0, "Invalid server CPU time")
    require(server["capacity"] == 1024 and server["payloadCharacters"] == 1024 and server["lookupsPerRequest"] == 1, "Server workload changed")
    require(server["loaderServiceTimeMilliseconds"] == server["expectedLoads"] == 0, "Server loader profile changed")
    if backend == "cache":
        drain = server["drain"]
        require(drain is not None and drain["attempts"] > 0, "Missing pre-disposal drain evidence")
        require(all(drain["final"][key] == 0 for key in ("inFlightLoads", "maintenanceBacklog", "writeBufferBacklog", "maintenanceFaults")), "Final cache state was not quiescent")
    else:
        require(server["drain"] is None, "Control unexpectedly contains cache drain work")
    valid = (
        measured["Completed"] == measured["Offered"]
        and measured["Rejected"] == measured["Timeout"] == measured["Failed"] == 0
        and result["warmup"]["Completed"] == result["warmup"]["Offered"]
        and server["actualLoads"] == 0
        and server["measuredRequests"] == measured["Offered"]
    )
    return {
        "valid": valid,
        "cpuSecondsPerRequest": server["cpuSeconds"] / measured["Completed"] if measured["Completed"] else None,
        "p99Microseconds": measured["P99Microseconds"],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", required=True, type=Path)
    parser.add_argument("--framework", required=True, choices=("net8.0", "net10.0"))
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--smoke", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    summary = {"schemaVersion": 1, "status": "running", "smoke": args.smoke, "framework": args.framework, "runs": []}
    write(output / "summary.json", summary)
    records = summary["runs"]
    manifest = {}
    try:
        dotnet = args.dotnet.resolve()
        built = ROOT / "tools/LoadingCache.ServiceProbe/bin/Release" / args.framework
        require((built / "LoadingCache.ServiceProbe.dll").is_file(), "Build the requested framework in Release before running")
        inputs = output / "inputs"
        shutil.copytree(built, inputs / "bin")
        sources = {name: sha(ROOT / name) for name in source_paths()}
        for name in sources:
            target = inputs / "source" / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / name, target)
        dll = inputs / "bin/LoadingCache.ServiceProbe.dll"
        runtime_result = subprocess.run([str(dotnet), "--list-runtimes"], capture_output=True, text=True, timeout=30)
        (output / "runtime-list.log").write_text(runtime_result.stdout + runtime_result.stderr, encoding="utf-8")
        require(runtime_result.returncode == 0, "Could not inspect selected runtime")
        family = args.framework.removeprefix("net") + "."
        installations = {}
        for framework in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
            matches = re.findall(r"^" + re.escape(framework) + r" (\S+) \[(.+)\]$", runtime_result.stdout, re.MULTILINE)
            matches = [(version, Path(base) / version) for version, base in matches if version.startswith(family)]
            require(len(matches) == 1, f"Select an installation with exactly one {framework} patch for {family}")
            installations[framework] = matches[0]
        core_version, core_path = installations["Microsoft.NETCore.App"]
        _, aspnet_path = installations["Microsoft.AspNetCore.App"]
        runtime_files = {str(dotnet): sha(dotnet)}
        for directory in (core_path, aspnet_path, dotnet.parent / "host/fxr"):
            runtime_files.update({str(path): sha(path) for path in sorted(directory.rglob("*")) if path.is_file()})
        runtime_environment = {key: value for key, value in os.environ.items() if key.startswith(("DOTNET_", "COMPlus_", "CORECLR_", "COR_"))}
        harmless = {"DOTNET_ROOT", "DOTNET_ROOT_ARM64", "DOTNET_ROOT_X64", "DOTNET_CLI_HOME", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "DOTNET_BUNDLE_EXTRACT_BASE_DIR", "DOTNET_USE_POLLING_FILE_WATCHER", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"}
        require(all(key in harmless for key in runtime_environment), f"Runtime overrides are incompatible with the default-runtime profile: {sorted(set(runtime_environment) - harmless)}")
        manifest = {
            "schemaVersion": 1, "argv": sys.argv, "smoke": args.smoke,
            "gitHead": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
            "gitStatus": subprocess.check_output(["git", "status", "--porcelain=v1"], cwd=ROOT, text=True),
            "sources": sources, "inputs": file_manifest(inputs), "runtimeFiles": runtime_files,
            "expectedRuntime": ".NET " + core_version,
            "expectedCacheSha256": sha(inputs / "bin/LoadingCache.dll"),
            "expectedProbeSha256": sha(dll),
            "expectedCoreLibrarySha256": sha(core_path / "System.Private.CoreLib.dll"),
            "expectedAspNetCoreAssemblySha256": sha(aspnet_path / "Microsoft.AspNetCore.dll"),
            "environment": runtime_environment,
            "serverEnvironmentOverrides": {"DOTNET_USE_POLLING_FILE_WATCHER": "1"},
            "profile": {"rate": 2000, "seconds": 2 if args.smoke else 30, "warmupSeconds": 1 if args.smoke else 10, "maxPending": 256, "timeoutMilliseconds": 5000},
        }
        write(output / "manifest.json", manifest)
        profile = manifest["profile"]

        def verify_inputs():
            require({name: sha(ROOT / name) for name in source_paths()} == manifest["sources"], "Candidate source paths or bytes changed")
            require(file_manifest(inputs) == manifest["inputs"], "Frozen source/binary bytes changed")
            require(all(sha(Path(path)) == digest for path, digest in runtime_files.items()), "Runtime installation bytes changed")

        def run(label, backend, expiry="ttl", statistics=False):
            verify_inputs()
            directory = output / label
            directory.mkdir()
            ready = directory / "ready.txt"
            raw = directory / "raw.json"
            server_command = [str(dotnet), str(dll), "--mode", "server", "--backend", backend, "--expiry", expiry, "--ready-file", str(ready)]
            if statistics:
                server_command.append("--statistics")
            environment = dict(os.environ, DOTNET_USE_POLLING_FILE_WATCHER="1")
            commands = {"server": server_command}
            write(directory / "commands.json", commands)
            with (directory / "server.log").open("w") as log:
                server = subprocess.Popen(server_command, stdout=log, stderr=subprocess.STDOUT, cwd=ROOT, env=environment)
                commands["serverPid"] = server.pid
                write(directory / "commands.json", commands)
                try:
                    deadline = time.monotonic() + 30
                    while not ready.exists() or not ready.read_text().startswith("http://127.0.0.1:"):
                        if server.poll() is not None or time.monotonic() >= deadline:
                            raise RuntimeError(f"Host failed to become ready: {directory}")
                        time.sleep(0.05)
                    command = [str(dotnet), str(dll), "--mode", "client", "--url", ready.read_text(), "--output", str(raw), "--rate", str(profile["rate"]), "--seconds", str(profile["seconds"]), "--warmup-seconds", str(profile["warmupSeconds"]), "--max-pending", str(profile["maxPending"]), "--timeout-ms", str(profile["timeoutMilliseconds"])]
                    commands["client"] = command
                    write(directory / "commands.json", commands)
                    started = time.monotonic()
                    with (directory / "client.log").open("w") as client_log:
                        result_process = subprocess.run(command, stdout=client_log, stderr=subprocess.STDOUT, cwd=ROOT, timeout=profile["seconds"] + profile["warmupSeconds"] + 90)
                    commands.update(clientExitCode=result_process.returncode, clientElapsedSeconds=time.monotonic() - started)
                    write(directory / "commands.json", commands)
                    require(result_process.returncode == 0, f"Client failed: {directory}")
                    result = json.loads(raw.read_text())
                    server_result = result["server"]
                    require(server_result["runtime"] == manifest["expectedRuntime"], "Actual runtime did not match selected framework/patch")
                    for field, expected in (("cacheAssemblySha256", "expectedCacheSha256"), ("probeAssemblySha256", "expectedProbeSha256"), ("coreLibrarySha256", "expectedCoreLibrarySha256"), ("aspNetCoreAssemblySha256", "expectedAspNetCoreAssemblySha256")):
                        require(server_result[field].lower() == manifest[expected], f"Executing {field} differs from frozen inputs")
                    require((server_result["backend"], server_result["expiry"], server_result["statistics"]) == (backend, expiry, statistics), "Server executed a different profile arm")
                    record = {"label": label, "backend": backend, "expiry": expiry, "statistics": statistics, "rawSha256": sha(raw), **metrics(result, profile, backend)}
                    records.append(record)
                    write(output / "progress.json", records)
                    write(output / "summary.json", summary)
                    print(json.dumps(record), flush=True)
                    return record
                finally:
                    if server.poll() is None:
                        server.terminate()
                    try:
                        server.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        server.kill()
                        server.wait()
                    commands["serverExitCodeAfterRequestedShutdown"] = server.returncode
                    write(directory / "commands.json", commands)

        def compare(first, second, precision):
            valid = first["valid"] and second["valid"]
            return {"first": first["label"], "second": second["label"], "valid": valid,
                    **{metric: (abs(second[metric] / first[metric] - 1) if precision else second[metric] / first[metric] - 1) if valid else None for metric in METRICS}}

        rounds = 1 if args.smoke else 3
        aa = []
        for index in range(rounds):
            aa.append(compare(run(f"aa-{index}-a", "control"), run(f"aa-{index}-b", "control"), True))
        pairs = []
        for expiry in ("ttl", "tti"):
            for statistics in (False, True):
                for index in range(rounds):
                    paired = {}
                    for backend in (("control", "cache") if index % 2 == 0 else ("cache", "control")):
                        paired[backend] = run(f"{expiry}-stats{int(statistics)}-{index}-{backend}", backend, expiry, statistics)
                    pairs.append(compare(paired["control"], paired["cache"], False))
        precise = all(row["valid"] and row[metric] <= 0.05 for row in aa for metric in METRICS)
        passed = all(row["valid"] and row[metric] <= 0.05 for row in pairs for metric in METRICS)
        verify_inputs()
        status = "smoke-only" if args.smoke else "inconclusive" if not precise else "passed" if passed else "budget-not-met"
        summary.update(status=status, sourcesUnchanged=True, binariesUnchanged=True, runtimeUnchanged=True, aaPrecisionWithinFivePercent=precise, allPairsWithinFivePercent=passed, aa=aa, pairs=pairs)
        print(status, flush=True)
    except BaseException as error:
        summary.update(status="failed", error=repr(error))
        raise
    finally:
        write(output / "summary.json", summary)
        write(output / "raw-hashes.json", {str(path.relative_to(output)): sha(path) for path in sorted(output.rglob("*.json")) if path.name != "raw-hashes.json"})


if __name__ == "__main__":
    main()
