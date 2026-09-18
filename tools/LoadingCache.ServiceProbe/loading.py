#!/usr/bin/env python3
"""Frozen opt-in HTTP loading profile runner and independent raw validator."""

import argparse
import copy
import hashlib
import json
import math
import os
import re
import subprocess
import sys
from pathlib import Path

import provenance

ROOT = Path(__file__).resolve().parents[2]
OUTCOMES = ("completed", "rejected", "timeout", "failed")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def percentile(values, frequency):
    ordered = sorted(values)
    return ordered[math.ceil(len(ordered) * 0.99) - 1] * 1000 / frequency if ordered else None


def validate_execution_bound(active, limit):
    require(
        type(active) is int and active >= 0 and (limit is None or active <= limit),
        "Execution bound",
    )


def validate_resource_observation(row, limit):
    stats = row["statistics"]
    require(isinstance(row["activeFlights"], bool), "Missing active-flight registry observation")
    validate_execution_bound(row["active"], limit)
    validate_execution_bound(stats["InFlightLoads"], limit)
    require(
        stats["MaintenanceFaults"] == 0 and row["notifications"]["HandlerFailures"] == 0,
        "Maintenance/listener failure",
    )
    if row["label"].endswith("-drained"):
        require(row["active"] == row["pending"] == 0, "Incomplete client/backend drain")
        require(row["activeFlights"] is False, "Cache-owned flight reservations did not drain")
        require(
            all(
                stats[key] == 0
                for key in ("InFlightLoads", "MaintenanceBacklog", "WriteBufferBacklog")
            ),
            "Cache did not drain",
        )
        require(
            row["notifications"]["Queued"] == 0 and not row["notifications"]["HandlerRunning"],
            "Listener did not drain",
        )
        require(0 <= row["residents"] <= 256, "Capacity did not converge")


def validate_observations(case):
    required_drains = {
        "fan-in": {"fan-in-drained", "cleanup-drained"},
        "expiry-refresh": {"fan-in-drained", "cleanup-drained"},
        "burst": {"burst-drained", "recovery-drained", "cleanup-drained"},
        "normal": {"warmup-drained", "normal-drained", "cleanup-drained"},
    }[case["name"]]
    require(
        all(
            sum(row["label"] == label for row in case["observations"]) == 1
            for label in required_drains
        ),
        "Missing/duplicate required drain observation",
    )
    for row in case["observations"]:
        if row["label"] not in {label + "-duration" for label in required_drains}:
            validate_resource_observation(row, case["maxConcurrentLoads"])
    return {row["label"]: row for row in case["observations"]}


def validate_timeout_origins(case, frequency):
    requests = [row for row in case["requests"] if row["Phase"] in ("burst-admitted", "recovery")]
    origins = case["timeoutOrigins"]
    require(
        len(origins) == len({row["RequestId"] for row in origins}) == 72,
        "Missing/duplicate cache timeout origins",
    )
    origins = {row["RequestId"]: row for row in origins}
    require(
        set(origins) == {row["Id"] for row in requests}, "Cache timeout request association differs"
    )
    by_key = {row["Key"]: row for row in case["backend"]}
    returned = {row["Key"]: row["Value"] for row in case["returned"]}
    for request in requests:
        origin = origins[request["Id"]]
        require(
            origin["Key"] == request["Key"]
            and (origin["DueTimeTicks"], origin["PeriodTicks"]) == (20000000, -10000),
            "Timeout origin identity/timer configuration mismatch",
        )
        require(
            request["Sent"]
            <= origin["TimeoutStartTimestamp"]
            <= origin["TimerCreatedTimestamp"]
            <= by_key[request["Key"]]["Started"],
            "Invalid cache timeout origin sequence",
        )
        if request["Phase"] == "burst-admitted":
            require(
                returned[request["Id"]] - origin["TimeoutStartTimestamp"] >= 2 * frequency,
                "Cache deadline fired before the two-second load timeout",
            )


def early_timeout_with_dispatch_delay(data):
    burst = data["cases"][2]
    origins = {row["RequestId"]: row for row in burst["timeoutOrigins"]}
    ids = {row["Id"] for row in burst["requests"] if row["Phase"] == "burst-admitted"}
    delay = data["stopwatchFrequency"] // 2
    for row in burst["requests"]:
        if row["Id"] in ids:
            row["Scheduled"] -= delay
            row["Sent"] -= delay
    for row in burst["returned"]:
        if row["Key"] in ids:
            row["Value"] = origins[row["Key"]]["TimeoutStartTimestamp"] + 3 * delay


def validate(raw):
    require(
        raw["profile"] == "loading-admission-v2" and raw["schemaVersion"] == 4,
        "Wrong profile or missing optional-admission schema",
    )
    require(raw["error"] is None and raw["fault"] == "none", "Failed or injected-fault run")
    require(
        isinstance(raw["argv"], list) and "--loading-profile" in raw["argv"],
        "Missing executed arguments",
    )
    frequency = raw["stopwatchFrequency"]
    require(isinstance(frequency, int) and frequency > 0, "Invalid frequency")
    require(
        [case["name"] for case in raw["cases"]] == ["fan-in", "expiry-refresh", "burst", "normal"],
        "Missing cases",
    )
    report = {}
    for case in raw["cases"]:
        name = case["name"]
        require(case["primaryError"] is None and not case["cleanupErrors"], "Case/cleanup failed")
        limit = None if name == "normal" else 8
        require(
            case["capacity"] == 256 and case["backendServiceMilliseconds"] == 20,
            "Resource profile drift",
        )
        for field in ("maxConcurrentLoads", "maxPendingLoadKeys"):
            require(
                case[field] is None
                if limit is None
                else type(case[field]) is int and case[field] == limit,
                "Admission configuration drift",
            )
        requests = case["requests"]
        require(
            [row["Id"] for row in requests] == list(range(1, len(requests) + 1)),
            "Missing/duplicate request identity",
        )
        phases = {}
        for row in requests:
            require(row["Outcome"] in OUTCOMES, "Unrecorded outcome")
            require(0 < row["Scheduled"] <= row["Finished"], "Invalid scheduled/finish time")
            require(row["PendingAtAdmission"] > 0, "Missing admission sample")
            if row["Sent"]:
                require(row["Scheduled"] <= row["Sent"] <= row["Finished"], "Invalid dispatch time")
            if row["HeadersReceived"]:
                require(
                    row["Sent"] <= row["HeadersReceived"] <= row["Finished"], "Invalid headers time"
                )
            if row["Outcome"] == "completed":
                require(
                    row["StatusCode"] == 200
                    and row["Value"] == (-1 if row["Phase"] == "stale" else row["Key"]),
                    "Invalid successful value",
                )
            phases.setdefault(row["Phase"], []).append(row)
        observations = validate_observations(case)
        require("cleanup-drained" in observations, "Missing cleanup evidence")
        backend = case["backend"]
        require(
            [row["Id"] for row in backend] == list(range(1, len(backend) + 1)),
            "Backend identity accounting",
        )
        events = []
        for row in backend:
            require(0 < row["Started"] <= row["Finished"], "Unfinished backend")
            events.extend(((row["Started"], 1), (row["Finished"], -1)))
            if row["ServiceStart"]:
                require(
                    row["Started"] <= row["ServiceStart"] <= row["Finished"],
                    "Invalid backend service span",
                )
                require(
                    (row["Finished"] - row["ServiceStart"]) * 1000 / frequency >= 19,
                    "Backend delay omitted",
                )
        active = peak = 0
        for _, delta in sorted(events):
            active += delta
            peak = max(peak, active)
            validate_execution_bound(active, limit)
        require(active == 0, "Backend records did not balance")

        def expect(phase, count, outcome="completed", status=200):
            rows = phases[phase]
            require(len(rows) == count, f"{name}/{phase}: wrong offered count")
            require(
                all(row["Outcome"] == outcome and row["StatusCode"] == status for row in rows),
                f"{name}/{phase}: unexpected outcomes",
            )
            return rows

        if name in ("fan-in", "expiry-refresh"):
            require(
                set(phases) == ({"fan-in"} if name == "fan-in" else {"fan-in", "stale"}),
                "Unexpected fan-in phases",
            )
            rows = expect("fan-in", 64)
            require(all(row["Key"] == 42 for row in rows), "Fan-in keys changed")
            require(
                len(backend) == 1 and backend[0]["Key"] == 42, "Exact-flight backend count violated"
            )
            require(
                backend[0]["Started"] <= case["releaseTimestamp"] <= backend[0]["Finished"],
                "Backend was not gated through release",
            )
            before = observations["before-release"]
            require(before["backendCalls"] == before["active"] == 1, "Backend was not held")
            require(before["activeFlights"] is True, "Held backend lost its flight reservation")
            require(
                before["returned"] == (0 if name == "fan-in" else 1), "Premature/expired response"
            )
            require(
                before["invoked"] == (64 if name == "fan-in" else 65),
                "Callers did not join before release",
            )
            invoked = {row["Key"]: row["Value"] for row in case["invoked"]}
            returned = {row["Key"]: row["Value"] for row in case["returned"]}
            require(
                all(
                    invoked[row["Id"]]
                    <= before["timestamp"]
                    <= case["releaseTimestamp"]
                    <= returned[row["Id"]]
                    for row in rows
                ),
                "Invalid gate ordering",
            )
            if name == "expiry-refresh":
                require(expect("stale", 1)[0]["Key"] == 42, "Stale key changed")
        elif name == "burst":
            require(
                set(phases) == {"burst-admitted", "burst-excess", "permit-check", "recovery"},
                "Unexpected burst phases",
            )
            admitted = expect("burst-admitted", 8, "timeout", 504)
            excess = expect("burst-excess", 24, "rejected", 429)
            checks = expect("permit-check", 8, "rejected", 429)
            require([row["Key"] for row in admitted] == list(range(8)), "Admitted keys changed")
            require([row["Key"] for row in excess] == list(range(8, 32)), "Excess keys changed")
            require(
                [row["Key"] for row in checks] == list(range(100, 108)), "Permit check keys changed"
            )
            returned = {row["Key"]: row["Value"] for row in case["returned"]}
            require(
                all(
                    row["Detail"] is None and row["Sent"] <= returned[row["Id"]] <= row["Finished"]
                    for row in admitted
                ),
                "Timeout was not a server response",
            )
            validate_timeout_origins(case, frequency)
            recovery = expect("recovery", 64)
            require(len(backend) == 72 and peak == 8, "Wrong burst/recovery backend calls")
            require(
                sorted(row["Key"] for row in backend) == list(range(8)) + list(range(1000, 1064)),
                "Burst/recovery backend key accounting",
            )
            for label in ("after-timeout", "permits-retained"):
                row = observations[label]
                require(
                    row["active"] == row["backendCalls"] == row["statistics"]["InFlightLoads"] == 8,
                    "Permits released before backend termination",
                )
                require(
                    row["activeFlights"] is True, "Non-cooperative flight reservations disappeared"
                )
            require(case["timedOutKeysAbsent"] is True, "Timed-out backend published late")
            held = [row for row in backend if row["Key"] < 8]
            require(
                len(held) == 8
                and all(
                    row["Started"]
                    < observations["after-timeout"]["timestamp"]
                    <= observations["permits-retained"]["timestamp"]
                    <= case["releaseTimestamp"]
                    <= row["Finished"]
                    for row in held
                ),
                "Non-cooperative backend not held through rejection checks",
            )
            duration = (
                observations["recovery-drained"]["timestamp"] - case["releaseTimestamp"]
            ) / frequency
            require(0 <= duration <= 5, "Recovery budget exceeded")
            require(
                [row["Key"] for row in recovery] == list(range(1000, 1064)), "Recovery keys changed"
            )
        else:
            require(set(phases) == {"warmup", "normal"}, "Unexpected normal phases")
            warmup = expect("warmup", 400)
            measured = expect("normal", 400 if raw["smoke"] else 6000)
            require(
                [row["Key"] for row in warmup] == list(range(1000, 1400)), "Warmup keys changed"
            )
            require(
                [row["Key"] for row in measured] == list(range(10000, 10000 + len(measured))),
                "Normal keys changed",
            )
            require(
                len(backend) == len(warmup) + len(measured),
                "Missing/duplicate normal backend invocation",
            )
            require(
                sorted(row["Key"] for row in backend) == sorted(row["Key"] for row in requests),
                "Backend key accounting",
            )
            require(
                all(row["ServiceStart"] > 0 for row in backend), "Normal backend service omitted"
            )
            ticks = [row["Finished"] - row["Scheduled"] for row in measured]
            require(
                percentile(ticks, frequency) <= 250 and max(ticks) * 1000 / frequency <= 1000,
                "Normal queue/tail budget exceeded",
            )
            duration = observations["normal-drained-duration"]
            require(
                (duration["timestamp"] - duration["start"]) / frequency <= 5,
                "Normal drain budget exceeded",
            )
        for phase, rows in phases.items():
            if phase in ("normal", "warmup", "recovery"):
                rate = 100 if phase == "recovery" else 200
                origin = rows[0]["Scheduled"]
                require(
                    all(
                        abs(row["Scheduled"] - origin - int(index * frequency / rate)) <= 1
                        for index, row in enumerate(rows)
                    ),
                    "Offered schedule changed",
                )
        report[name] = {
            "backendCalls": len(backend),
            "peakBackendExecutions": peak,
            "phases": {
                phase: {
                    "offered": len(rows),
                    **{
                        outcome: sum(row["Outcome"] == outcome for row in rows)
                        for outcome in OUTCOMES
                    },
                    "allOutcomeP99Milliseconds": percentile(
                        [row["Finished"] - row["Scheduled"] for row in rows], frequency
                    ),
                    "successOnlyP99Milliseconds": percentile(
                        [
                            row["Finished"] - row["Scheduled"]
                            for row in rows
                            if row["Outcome"] == "completed"
                        ],
                        frequency,
                    ),
                    "dispatchP99Milliseconds": percentile(
                        [row["Sent"] - row["Scheduled"] for row in rows if row["Sent"]], frequency
                    ),
                    "pendingHighWater": max(row["PendingAtAdmission"] for row in rows),
                }
                for phase, rows in phases.items()
            },
        }
    return report


def validate_directory(directory):
    manifest = json.loads((directory / "manifest.json").read_text())
    build = provenance.verify(directory / "inputs/build", built=directory / "inputs/bin")
    require(
        sha(directory / "inputs/build/manifest.json") == manifest["buildManifestSha256"],
        "Build manifest attribution changed",
    )
    require(
        build["framework"] == manifest["framework"]
        and build["sourceBefore"] == manifest["sources"],
        "Run/build source or framework attribution differs",
    )
    require(
        manifest["assemblies"]["cacheAssemblySha256"]
        == sha(directory / "inputs/bin/LoadingCache.dll"),
        "Frozen cache binary differs from executing attribution",
    )
    require(
        manifest["assemblies"]["probeAssemblySha256"]
        == sha(directory / "inputs/bin/LoadingCache.ServiceProbe.dll"),
        "Frozen probe binary differs from executing attribution",
    )
    require(
        all(
            manifest["assemblies"][field] in manifest["runtimeFiles"].values()
            for field in ("coreLibrarySha256", "aspNetCoreAssemblySha256")
        ),
        "Runtime assembly attribution missing from frozen runtime hashes",
    )
    reports = []
    for record in manifest["runs"]:
        require(record["exitCode"] == 0, "Probe exited unsuccessfully")
        path = directory / record["raw"]
        require(sha(path) == record["sha256"], "Raw hash changed")
        raw = json.loads(path.read_text())
        require(
            raw["statistics"] == record["statistics"] and raw["smoke"] == manifest["smoke"],
            "Wrong arm",
        )
        for field, digest in manifest["assemblies"].items():
            require(raw[field].lower() == digest, f"Wrong executing {field}")
        require(raw["runtime"] == manifest["expectedRuntime"], "Wrong runtime patch")
        reports.append({"raw": record["raw"], "report": validate(raw)})
    require(len(reports) == (2 if manifest["smoke"] else 6), "Incomplete rounds")
    require(
        all(sha(directory / path) == digest for path, digest in manifest["frozen"].items()),
        "Frozen evidence changed",
    )
    require(
        set(manifest["sourceCopies"]) == set(manifest["sources"]), "Source copy manifest incomplete"
    )
    require(
        all(
            sha(directory / manifest["sourceCopies"][source]) == digest
            for source, digest in manifest["sources"].items()
        ),
        "Frozen source attribution changed",
    )
    require(
        [record["statistics"] for record in manifest["runs"]]
        == ([False, True] if manifest["smoke"] else [False, True, True, False, False, True]),
        "Round ordering changed",
    )
    return {"status": "smoke-only" if manifest["smoke"] else "passed", "reports": reports}


def run(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    root = args.repository.resolve()
    build_directory = args.build_manifest.resolve().parent
    built = args.built.resolve() if args.built else build_directory / "bin"
    build = provenance.verify(build_directory, root, built)
    require(build["framework"] == args.framework, "Build framework differs from requested run")
    import shutil

    shutil.copytree(built, output / "inputs/bin")
    shutil.copytree(build_directory, output / "inputs/build")
    dll = output / "inputs/bin/LoadingCache.ServiceProbe.dll"
    require(dll.is_file(), "Missing built probe")
    environment = {
        key: value
        for key, value in os.environ.items()
        if key.startswith(("DOTNET_", "COMPlus_", "CORECLR_", "COR_"))
    }
    harmless = {
        "DOTNET_ROOT",
        "DOTNET_ROOT_ARM64",
        "DOTNET_ROOT_X64",
        "DOTNET_CLI_HOME",
        "DOTNET_CLI_TELEMETRY_OPTOUT",
        "DOTNET_NOLOGO",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE",
        "DOTNET_BUNDLE_EXTRACT_BASE_DIR",
        "DOTNET_USE_POLLING_FILE_WATCHER",
        "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE",
    }
    require(set(environment) <= harmless, "Runtime tuning overrides are not part of this profile")
    sources = build["sourceBefore"]
    source_copies = {}
    for index, path in enumerate(sources):
        target = output / "inputs/source" / str(index) / Path(path).name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
        source_copies[path] = str(target.relative_to(output))
    frozen = {
        str(path.relative_to(output)): sha(path)
        for path in (output / "inputs").rglob("*")
        if path.is_file()
    }
    runtime_list = subprocess.check_output(
        [str(args.dotnet.resolve()), "--list-runtimes"], text=True, timeout=30
    )
    (output / "runtime-list.log").write_text(runtime_list, encoding="utf-8")
    installations = {}
    for framework in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
        found = re.findall(
            r"^" + re.escape(framework) + r" (\S+) \[(.+)\]$", runtime_list, re.MULTILINE
        )
        found = [
            (version, Path(base) / version)
            for version, base in found
            if version.startswith(args.framework.removeprefix("net") + ".")
        ]
        require(
            len(found) == 1,
            f"Select a host containing exactly one {framework} patch for this framework",
        )
        installations[framework] = found[0]
    core_version, core_path = installations["Microsoft.NETCore.App"]
    _, aspnet_path = installations["Microsoft.AspNetCore.App"]
    runtime_files = {str(args.dotnet.resolve()): sha(args.dotnet.resolve())}
    for directory in (core_path, aspnet_path, args.dotnet.resolve().parent / "host/fxr"):
        runtime_files.update(
            {str(path): sha(path) for path in directory.rglob("*") if path.is_file()}
        )
    manifest = {
        "schemaVersion": 2,
        "buildManifestSha256": sha(output / "inputs/build/manifest.json"),
        "framework": args.framework,
        "expectedRuntime": ".NET " + core_version,
        "runtimeFiles": runtime_files,
        "smoke": args.smoke,
        "argv": sys.argv,
        "environment": environment,
        "sources": sources,
        "sourceCopies": source_copies,
        "frozen": frozen,
        "assemblies": {
            "cacheAssemblySha256": sha(dll.parent / "LoadingCache.dll"),
            "probeAssemblySha256": sha(dll),
            "coreLibrarySha256": sha(core_path / "System.Private.CoreLib.dll"),
            "aspNetCoreAssemblySha256": sha(aspnet_path / "Microsoft.AspNetCore.dll"),
        },
        "runs": [],
    }
    write(output / "manifest.json", manifest)
    try:
        for index, stats in enumerate(
            [False, True] if args.smoke else [False, True, True, False, False, True]
        ):
            provenance.verify(build_directory, root, built)
            require(
                all(sha(Path(path)) == digest for path, digest in sources.items()),
                "Sources changed",
            )
            require(
                all(sha(output / path) == digest for path, digest in frozen.items()),
                "Frozen inputs changed",
            )
            require(
                all(sha(Path(path)) == digest for path, digest in runtime_files.items()),
                "Runtime changed",
            )
            raw = output / f"run-{index}-stats{int(stats)}.json"
            command = [
                str(args.dotnet.resolve()),
                str(dll),
                "--loading-profile",
                "--output",
                str(raw),
            ]
            if stats:
                command.append("--statistics")
            if args.smoke:
                command.append("--smoke")
            write(output / f"command-{index}.json", command)
            with (output / f"run-{index}.log").open("w") as log:
                result = subprocess.run(
                    command,
                    cwd=root,
                    stdout=log,
                    stderr=subprocess.STDOUT,
                    env=dict(os.environ, DOTNET_USE_POLLING_FILE_WATCHER="1"),
                    timeout=180,
                )
            if raw.exists():
                manifest["runs"].append(
                    {
                        "raw": raw.name,
                        "sha256": sha(raw),
                        "statistics": stats,
                        "exitCode": result.returncode,
                    }
                )
                write(output / "manifest.json", manifest)
            require(result.returncode == 0, f"Probe failed: {raw}")
            validate(json.loads(raw.read_text()))
        require(
            all(sha(Path(path)) == digest for path, digest in sources.items()), "Sources changed"
        )
        provenance.verify(build_directory, root, built)
        require(
            all(sha(Path(path)) == digest for path, digest in runtime_files.items()),
            "Runtime changed",
        )
        require(
            all(sha(output / path) == digest for path, digest in frozen.items()),
            "Frozen inputs changed after final run",
        )
        write(output / "summary.json", validate_directory(output))
    except BaseException as error:
        write(output / "summary.json", {"status": "failed", "error": repr(error)})
        raise


def self_check(directory):
    raw = json.loads(sorted(directory.glob("run-*-stats*.json"))[0].read_text())
    validate(raw)
    mutations = {
        "old-schema": lambda data: data.update(schemaVersion=3),
        "old-profile": lambda data: data.update(profile="loading-admission-v1"),
        "normal-hidden-concurrency-limit": lambda data: data["cases"][3].update(
            maxConcurrentLoads=8
        ),
        "normal-hidden-pending-limit": lambda data: data["cases"][3].update(maxPendingLoadKeys=8),
        "normal-missing-concurrency-limit": lambda data: data["cases"][3].pop("maxConcurrentLoads"),
        "normal-missing-pending-limit": lambda data: data["cases"][3].pop("maxPendingLoadKeys"),
        "normal-nonnul-pending": lambda data: data["cases"][3].update(maxPendingLoadKeys="null"),
        "burst-unconfigured-concurrency": lambda data: data["cases"][2].update(
            maxConcurrentLoads=None
        ),
        "burst-unconfigured-pending": lambda data: data["cases"][2].update(maxPendingLoadKeys=None),
        "burst-noninteger-concurrency": lambda data: data["cases"][2].update(
            maxConcurrentLoads=8.0
        ),
        "configured-execution-above-eight": lambda data: next(
            row for row in data["cases"][2]["observations"] if row["label"] == "permits-retained"
        ).update(active=9),
        "configured-backend-overlap-above-eight": lambda data: data["cases"][2]["backend"].append(
            dict(data["cases"][2]["backend"][0], Id=len(data["cases"][2]["backend"]) + 1)
        ),
        "missing-outcome": lambda data: data["cases"][0]["requests"].pop(),
        "duplicate-backend": lambda data: data["cases"][0]["backend"].append(
            data["cases"][0]["backend"][0]
        ),
        "premature-permit": lambda data: next(
            row for row in data["cases"][2]["observations"] if row["label"] == "permits-retained"
        ).update(active=0),
        "success-only-tail": lambda data: data["cases"][3]["requests"][-1].update(
            Outcome="rejected"
        ),
        "default-joined-value": lambda data: data["cases"][0]["requests"][0].update(Value=0),
        "early-timeout-with-dispatch-delay": early_timeout_with_dispatch_delay,
        "missing-timeout-origin": lambda data: data["cases"][2]["timeoutOrigins"].pop(),
        "running-zero-with-reserved-flight": lambda data: next(
            row for row in data["cases"][2]["observations"] if row["label"] == "burst-drained"
        ).update(activeFlights=True),
        "missing-drain-statistics": lambda data: next(
            row for row in data["cases"][2]["observations"] if row["label"] == "burst-drained"
        ).pop("statistics"),
        "missing-drain-flight-state": lambda data: next(
            row for row in data["cases"][2]["observations"] if row["label"] == "burst-drained"
        ).pop("activeFlights"),
        "missing-required-drain": lambda data: data["cases"][2]["observations"].remove(
            next(row for row in data["cases"][2]["observations"] if row["label"] == "burst-drained")
        ),
    }
    for label, mutation in mutations.items():
        modified = copy.deepcopy(raw)
        mutation(modified)
        try:
            validate(modified)
        except (ValueError, KeyError):
            continue
        raise ValueError(f"Validator accepted {label}")
    observation = copy.deepcopy(
        next(row for row in raw["cases"][2]["observations"] if row["label"] == "permits-retained")
    )
    observation["active"] = observation["statistics"]["InFlightLoads"] = 9
    validate_resource_observation(observation, None)
    validate_execution_bound(9, None)
    for check in (
        lambda: validate_resource_observation(observation, 8),
        lambda: validate_execution_bound(9, 8),
    ):
        try:
            check()
        except ValueError:
            continue
        raise ValueError("Validator accepted configured execution above eight")
    print(
        json.dumps(
            {
                "status": "passed",
                "rejectedMutations": list(mutations),
                "unconfiguredAboveEightControl": "passed",
                "configuredAboveEightControls": "passed",
            }
        )
    )


def controls(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    results = []
    dll = args.built.resolve() / "LoadingCache.ServiceProbe.dll"
    for fault in ("backend-error", "wrong-value", "stuck-backend"):
        path = output / (fault + ".json")
        command = [
            str(args.dotnet.resolve()),
            str(dll),
            "--loading-profile",
            "--smoke",
            "--statistics",
            "--loading-fault",
            fault,
            "--output",
            str(path),
        ]
        write(output / (fault + "-command.json"), command)
        with (output / (fault + ".log")).open("w") as log:
            child = subprocess.run(
                command,
                stdout=log,
                stderr=subprocess.STDOUT,
                env=dict(os.environ, DOTNET_USE_POLLING_FILE_WATCHER="1"),
                timeout=180,
            )
        raw = json.loads(path.read_text())
        require(
            raw["profile"] == "loading-admission-v2" and raw["schemaVersion"] == 4,
            "Failure control lacks optional-admission schema",
        )
        require(
            raw["fault"] == fault and len(raw["cases"]) == 4,
            "Failure control did not execute all cases",
        )
        case = raw["cases"][3]
        require(
            len(case["requests"]) == len(case["backend"]) == 800,
            "Failure control lost requests/backend work",
        )
        require(
            all(row["Outcome"] in OUTCOMES for row in case["requests"]),
            "Failure control lost outcomes",
        )
        request = next(row for row in case["requests"] if row["Key"] == 10000)
        expected = {
            "backend-error": ("failed", 500, None),
            "wrong-value": ("failed", 200, "wrong-value"),
            "stuck-backend": ("timeout", None, "client-deadline"),
        }[fault]
        require(
            (request["Outcome"], request["StatusCode"], request["Detail"]) == expected,
            "Injected fault was not observed at HTTP boundary",
        )
        require(
            all(not item["cleanupErrors"] for item in raw["cases"]),
            "Failure control cleanup failed",
        )
        drain = next(row for row in case["observations"] if row["label"] == "cleanup-drained")
        require(
            case["maxConcurrentLoads"] is None and case["maxPendingLoadKeys"] is None,
            "Failure control configured normal admission",
        )
        validate_resource_observation(drain, case["maxConcurrentLoads"])
        sanitized = copy.deepcopy(raw)
        sanitized["fault"] = "none"
        sanitized["error"] = None
        for item in sanitized["cases"]:
            item["primaryError"] = None
        try:
            validate(sanitized)
        except ValueError:
            rejected = True
        else:
            rejected = False
        require(rejected, "Validator missed real injected outcome failure")
        results.append(
            {
                "fault": fault,
                "exitCode": child.returncode,
                "rawSha256": sha(path),
                "all800OutcomesRetained": True,
                "cleanupDrained": True,
                "validatorRejectedWithoutFaultFlag": rejected,
            }
        )
        write(output / "summary.json", {"status": "controls-only", "results": results})
    print(json.dumps(results))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    runner = commands.add_parser("run")
    runner.add_argument("--dotnet", type=Path, required=True)
    runner.add_argument("--framework", choices=("net8.0", "net10.0"), required=True)
    runner.add_argument("--output", type=Path, required=True)
    runner.add_argument("--repository", type=Path, default=ROOT)
    runner.add_argument("--built", type=Path)
    runner.add_argument("--build-manifest", type=Path, required=True)
    runner.add_argument("--smoke", action="store_true")
    control = commands.add_parser("controls")
    control.add_argument("--dotnet", type=Path, required=True)
    control.add_argument("--built", type=Path, required=True)
    control.add_argument("--output", type=Path, required=True)
    for name in ("validate", "self-check"):
        commands.add_parser(name).add_argument("directory", type=Path)
    args = parser.parse_args()
    if args.command == "run":
        run(args)
    elif args.command == "validate":
        print(json.dumps(validate_directory(args.directory), indent=2))
    elif args.command == "controls":
        controls(args)
    else:
        self_check(args.directory)


if __name__ == "__main__":
    main()
