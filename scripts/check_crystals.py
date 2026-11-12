"""Port coverage and shipping-fixture isolation checks. Run after both builds."""
from collections import Counter
from pathlib import Path
import difflib
import hashlib
import json
import re

repo = Path(__file__).resolve().parents[1]
out = repo / 'build/crystal-tests'
upstream = Path(r'F:\cu-port\upstream\main-9e6a5d8\casualties-together-9e6a5d87328cbc8c1796bab17249ac4ba1b87db2\ScavPrototypeMP\Fullgame\Crystals.cs')
port = repo / 'ScavPrototypeMP/Compatibility/Crystals.cs'
pattern = r'\[HarmonyPatch\(typeof\((\w+)\),\s*nameof\(\w+\.(\w+)\)'
original = upstream.read_text(encoding='utf-8-sig')
current = port.read_text(encoding='utf-8-sig')
assert Counter(re.findall(pattern, original)) == Counter(re.findall(pattern, current))
assert len(re.findall(pattern, current)) == 14
assert '#if FULLGAME' in current and 'namespace Together;' in current
ids_before = (out / 'originals/MSG_ID.cs').read_text(encoding='utf-8-sig')
ids_after = (repo / 'ScavPrototypeMP/Net/MSG_ID.cs').read_text(encoding='utf-8-sig')
addition = '\n    SERVER_CrystalContact = ___11,\n    SERVER_CrystalState = ___12,\n    CLIENT_CrystalState = ___13,\n    CLIENT_CrystalEffect = ___14,'
assert ids_before.replace('    CLIENT_EndingGuards = ___10,', '    CLIENT_EndingGuards = ___10,' + addition) == ids_after
ending = (repo / 'ScavPrototypeMP/Compatibility/EndingFlow.cs').read_text(encoding='utf-8-sig')
assert re.search(r'EnableEscape\s*=\s*false', ending)
ship = repo / 'build/crystal-ship/CasualtiesMP.dll'
play = repo / 'build/crystal-playtest/CasualtiesMP.dll'
ship_data = ship.read_bytes()
for marker in ('CrystalTests', '--mp-crystal-test', '[crystal-test]', '[crystal] activated', '[crystal] feedback'):
    assert marker.encode() not in ship_data and marker.encode('utf-16le') not in ship_data, marker
assert b'--mp-crystal-test'.decode().encode('utf-16le') in play.read_bytes()
results = {
    'original_patch_targets_retained': 14,
    'message_aliases_only': True,
    'escape_disabled': True,
    'shipping_fixture_and_info_logs_absent': True,
    'ship_sha256': hashlib.sha256(ship_data).hexdigest(),
    'playtest_sha256': hashlib.sha256(play.read_bytes()).hexdigest(),
    'targets': re.findall(pattern, current),
}
(out / 'selfcheck.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
changes = []
for path, before in [('ScavPrototypeMP/Compatibility/Crystals.cs', original), ('ScavPrototypeMP/Net/MSG_ID.cs', ids_before)]:
    after = (repo / path).read_text(encoding='utf-8-sig')
    changes.extend(difflib.unified_diff(before.splitlines(True), after.splitlines(True), fromfile='upstream-or-before/' + path, tofile=path))
(out / 'port.diff').write_text(''.join(changes), encoding='utf-8')
print(json.dumps(results, indent=2))
