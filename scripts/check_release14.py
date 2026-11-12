"""Validate archived real-game evidence and shipping fixture isolation."""
import hashlib
import json
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent
run = sys.argv[1] if len(sys.argv) > 1 else 'r14-verified'
rows = json.loads((root / 'build' / (run + '-results.json')).read_text(encoding='utf-8-sig'))
if isinstance(rows, dict):
    rows = [rows]
expected = {'r14-merc', 'r14-escape', 'r14-remote', 'r14-coredeath', 'r14-capture', 'r14-noncoredeath', 'r14-timeout'}
if any(r['scenario'] == 'r14-chipped' for r in rows):
    expected.add('r14-chipped')
assert {r['scenario'] for r in rows} == expected
playtest = root / 'build/ending-playtest/CasualtiesMP.dll'
test_hash = hashlib.sha256(playtest.read_bytes()).hexdigest().upper()
total = 0
for row in rows:
    assert row['passed'], row
    assert row['dllSha256'] == test_hash, row
    folder = Path(row['logs'])
    ending = 3 if row['scenario'] == 'r14-merc' else 1 if row['scenario'] in {'r14-coredeath', 'r14-capture', 'r14-timeout', 'r14-chipped'} else 2
    for side in ('host', 'client'):
        log = (folder / (side + '.log')).read_text(encoding='utf-8-sig', errors='replace')
        assert '[r14-test] FAIL' not in log, (row, side)
        assert not re.search(r'^(?:NullReferenceException|MissingReferenceException|IndexOutOfRangeException|InvalidOperationException|Exception):', log, re.M), (row, side)
        assert (folder / ('server.done' if side == 'host' else 'client.done')).exists()
        if row['scenario'] == 'r14-chipped':
            assert 'local EndSequence(1) started' in log
            assert 'after chipped shot' in log if side == 'client' else 'actual gun RPC rejected' in log
            total += log.count('[r14-test] PASS')
            continue
        stages = ['initial-fade-start', 'initial-fade-complete']
        if ending == 3:
            stages += ['mercnote1', 'image9', 'note2-fade-start', 'note2-fade-complete', 'mercnote2', 'heartsplosion-black', 'mercnote3']
        elif ending == 2:
            stages += ['escapenote1', 'escape-image8', 'escape-note2-fade-start', 'escape-note2-fade-complete', 'escapenote2']
        else:
            stages += ['biopodnotealt2']
        stages += ['await-party']
        cursor = 0
        for stage in stages:
            found = log.index('local stage=' + stage, cursor)
            cursor = found + len('local stage=' + stage)
        assert log.index(f'local EndSequence({ending}) started', cursor) > cursor
        assert 'PASS real GlobalDark alpha changed through intermediate values' in log
        total += log.count('[r14-test] PASS')
    if row['scenario'] == 'r14-timeout':
        log = (folder / 'host.log').read_text(encoding='utf-8-sig', errors='replace')
        assert [int(n) for n in re.findall(r'authoritative deadline guard (\d)/5', log)] == [1, 2, 3, 4, 5]
ship = root / (sys.argv[2] if len(sys.argv) > 2 else 'build/release14-ship/CasualtiesMP.dll')
binary = ship.read_bytes()
for fixture in (b'Release14EndingTests', b'EndingFlowTests', b'DropSourceRegression', b'AutoNarrative'):
    assert fixture not in binary, fixture
print(json.dumps({'passed': True, 'scenarios': len(rows), 'assertions': total,
                  'playtestSha256': test_hash, 'shipSha256': hashlib.sha256(binary).hexdigest().upper()}, indent=2))
