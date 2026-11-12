"""Audit release12 runtime evidence, build outputs and fixture isolation."""
from pathlib import Path
from collections import Counter
import csv
import hashlib
import json
import re

repo = Path(__file__).resolve().parents[1]
tests = repo / "build/drop-chip-tests"
phases = ("release12-acceptance-final2", "release12-reload", "release12-legacy", "release12-host-unchipped")
results = {"phases": {}, "hover": {}, "builds": {}}
for phase in phases:
    folder = tests / phase
    result = json.loads((folder / "result.json").read_text(encoding="utf-8-sig"))
    assert result["passed"], phase
    assert (folder / "host.done").exists() and (folder / "client.done").exists(), phase
    logs = {side: (folder / (side + ".log")).read_text(encoding="utf-8-sig", errors="replace") for side in ("host", "client")}
    for side, log in logs.items():
        assert "[drop-test] FAIL" not in log and "IOException: Sharing violation" not in log, (phase, side)
        assert "gameplay and NetBody chip states match choices" in log, (phase, side)
        assert "visible depth meter matches choice" in log, (phase, side)
    restores = re.findall(r"client restored fresh drop sync=(\d+) id=.*? remaining=([\d.]+)s", logs["client"])
    tenths = Counter(sync for sync, remaining in restores if remaining == "0.1")
    assert all(count == 1 for count in tenths.values()), (phase, "repeated terminal restores", tenths)
    results["phases"][phase] = {
        "assertions": {side: log.count("[drop-test] PASS") for side, log in logs.items()},
        "restores_at_0.1s": dict(tenths),
    }

folder = tests / phases[0]
client_log = (folder / "client.log").read_text(encoding="utf-8-sig")
host_log = (folder / "host.log").read_text(encoding="utf-8-sig")
assert "stale true/0.1s expires locally and never rebuilds for two seconds" in client_log
assert "server publishes expiry false/0 with snapshot tracker disabled" in host_log
assert "client receives false/0 and stays expired with host snapshot tracker disabled" in client_log
for path in folder.glob("*.csv"):
    rows = list(csv.DictReader(path.open(encoding="utf-8-sig")))
    evidence = {}
    for sync in sorted({row["id"] for row in rows}):
        samples = [row for row in rows if row["id"] == sync]
        instances = {row["component"] for row in samples if row["component"] != "0"}
        assert len(instances) == 1, (path.name, sync, instances)
        expiry = min(float(row["elapsed"]) for row in samples if row["component"] == "0")
        assert 9.4 < expiry < 10.5, (path.name, sync, expiry)
        stable = [row for row in samples if 2 < float(row["elapsed"]) < 6]
        assert stable and all(float(row["gravity"]) == 0 for row in stable), (path.name, sync)
        evidence[sync] = {"instances": 1, "expiry_seconds": expiry}
    results["hover"][path.name] = evidence

for config, label in (("ShipFullgame", "ship"), ("PlaytestFullgame", "playtest")):
    data = (repo / "build" / ("release12-" + label) / "CasualtiesMP.dll").read_bytes()
    log = (repo / "build" / ("release12-" + label + "-build.log")).read_text(encoding="utf-8-sig")
    assert re.search(r"\b0 (?:个错误|Error\(s\))", log), config
    for fixture in ("DropSourceRegression", "DropSourcePinBeforeValidation", "DropFrameProbe"):
        assert (fixture.encode() in data) == (label == "playtest"), (config, fixture)
    results["builds"][config] = {"errors": 0, "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}
source = repo / "ScavPrototypeMP"
assert re.search(r"EnableEscape\s*=\s*false", (source / "Compatibility/EndingFlow.cs").read_text(encoding="utf-8-sig"))
chip = (source / "Compatibility/ChipState.cs").read_text(encoding="utf-8-sig")
assert "bool IsUnchipped(ScavPlayer player)" in chip and "bool HostIsUnchipped" in chip
results["escape_disabled"] = True
results["runtime_configuration"] = "PlaytestFullgame, isolated repository A/B, loopback 127.0.0.1:7791"
(repo / "build/release12-results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(results, ensure_ascii=False, indent=2))
