"""Check runtime evidence and shipping isolation; no game state is synthesized."""
from pathlib import Path
import csv
import hashlib
import json
import re

repo = Path(__file__).resolve().parents[1]
tests = repo / 'build/drop-chip-tests'
results = {'phases': {}, 'hover_samples': {}, 'builds': {}}
for phase, marker in {
    'acceptance-final': '[drop-test]',
    'reload-final': '[drop-test]',
    'reload-legacy-final': '[drop-test]',
    'crystals-original-config': '[crystal-test]',
    'ending-carrier-final': '[route-test]',
    'sandbox-final': '[sandbox-test]',
}.items():
    folder = tests / phase
    logs = {side: (folder / (side + '.log')).read_text(encoding='utf-8-sig', errors='replace') for side in ('host', 'client')}
    assert all(marker + ' FAIL' not in value for value in logs.values()), phase
    if phase == 'sandbox-final':
        assert all('[sandbox-test] complete side=' + side in logs[side] for side in logs), phase
    else:
        assert ((folder / 'host.done').exists() or (folder / 'server.done').exists()) and (folder / 'client.done').exists(), phase
    results['phases'][phase] = {side + '_assertions': value.count(marker + ' PASS') for side, value in logs.items()}

for path in (tests / 'acceptance-final').glob('*.csv'):
    rows = list(csv.DictReader(path.open(encoding='utf-8')))
    per_item = {}
    for item in sorted({r['id'] for r in rows}):
        samples = [r for r in rows if r['id'] == item]
        components = {r['component'] for r in samples if r['component'] != '0'}
        assert len(components) == 1, (path, item, 'component rebuilt')
        expiry = min(float(r['elapsed']) for r in samples if r['component'] == '0')
        assert 9.5 < expiry < 10.5, (path, item, expiry)
        stable = [r for r in samples if 2 < float(r['elapsed']) < 6]
        assert stable and all(float(r['gravity']) == 0 and r['component'] != '0' for r in stable)
        per_item[item] = {'component_instances': len(components), 'observed_expiry_seconds': expiry}
    results['hover_samples'][path.name] = per_item

for label, folder in [('ship', 'drop-chip-ship'), ('playtest', 'ending-playtest')]:
    data = (repo / 'build' / folder / 'CasualtiesMP.dll').read_bytes()
    log = (repo / 'build' / ('drop-chip-' + label + '-build.log')).read_text(encoding='utf-8-sig')
    assert re.search(r'\b0 (?:个错误|Error\(s\))', log), label
    for fixture in ('DropChipRegression', 'DropSourceRegression', 'DropFrameProbe', '[drop-debug]'):
        present = fixture.encode() in data or fixture.encode('utf-16le') in data
        assert present == (label == 'playtest'), (label, fixture)
    assert b'FreshDropRelevance' in data and b'ChipState' in data
    results['builds'][label] = {'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data), 'errors': 0}

source = repo / 'ScavPrototypeMP'
assert re.search(r'EnableEscape\s*=\s*false', (source / 'Compatibility/EndingFlow.cs').read_text(encoding='utf-8-sig'))
for locale in ('EN', 'zh-CN'):
    data = json.loads((source / 'assets/locale' / (locale + '.json')).read_text(encoding='utf-8-sig'))
    assert all(data.get('casualtiestogether_chip_' + key) for key in ('chipped', 'unchipped', 'hint'))
results['escape_disabled'] = True
results['network'] = 'existing reserved ___5 payload extended; Netmsg IDs unchanged; BasePlayer SyncVar appended'
results['environment'] = 'repository launchers copied from A/B; shared original data; loopback 7791; original A/B left running'
(repo / 'build/drop-chip-results.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(results, ensure_ascii=False, indent=2))
