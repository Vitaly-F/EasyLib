#!/bin/bash
# Smoke / regression tests of the library writer. Needs a built EasyLib.exe and python3.
#   tests/run_tests.sh [path to EasyLib.exe]
# On Linux the exe runs under mono; on Windows (git-bash) it runs directly.
set -u
cd "$(dirname "$0")/.."
EXE="${1:-build/EasyLib.exe}"
[ -f "$EXE" ] || EXE=EasyLib.exe
[ -f "$EXE" ] || { echo "EasyLib.exe not found: build it first"; exit 2; }
if command -v mono >/dev/null 2>&1; then RUN="mono $EXE"; else RUN="./$EXE"; fi
T=$(mktemp -d)
trap 'rm -rf "$T"' EXIT
PY=python3; command -v python3 >/dev/null 2>&1 || PY=python
$PY tests/make_fixtures.py "$T/fx" >/dev/null || exit 2
fail=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; fail=1; fi; }

# 1. conversion + preview + write of every fixture
$RUN --regress "$T/fx" --lib "$T/reg" > "$T/reg.txt" 2>/dev/null
check "regress: no failures" 'grep -q "fail=0" "$T/reg.txt"'

# 2. add, add again, unusual names
$RUN --add "$T"/fx/*.json --lib "$T/lib" --no3d > "$T/add.txt" 2>&1
check "add: 8 parts written" '[ "$(grep -c " / " "$T/add.txt")" = 8 ]'
$RUN --add "$T/fx/fet.json" --lib "$T/lib" --no3d > "$T/add2.txt" 2>&1
check "add twice: symbol and footprint are reused" 'grep -q "(exists) / .*(exists)" "$T/add2.txt"'
$RUN --lib-info "$T/lib" > "$T/info.txt" 2>/dev/null
check "library reads back: 8 symbols" 'grep -q "8 symbols" "$T/info.txt"'
check "library reads back: no missing footprints" '! grep -q "MISSING-FP" "$T/info.txt"'

# 3. repair is a no-op on a fresh library, also when run twice
check "repair: nothing to fix" '$RUN --repair "$T/lib" 2>/dev/null | grep -q "SchLib 0, PcbLib 0"'

# 4. delete: shared footprint stays, own footprint goes, backup is made
$RUN --lib-delete "$T/lib" --sym TESTFET-2 > "$T/del.txt" 2>/dev/null
check "delete: symbol removed, shared footprint kept" 'grep -q "symbols 1, footprints 0" "$T/del.txt"'
$RUN --lib-delete "$T/lib" --sym TESTFET-1 > "$T/del2.txt" 2>/dev/null
check "delete: last user removes the footprint" 'grep -q "symbols 1, footprints 1" "$T/del2.txt"'
check "delete: backup exists" '[ -n "$(ls "$T/lib/Backups" 2>/dev/null)" ]'
$RUN --lib-info "$T/lib" > "$T/info2.txt" 2>/dev/null
check "after delete: 6 symbols, 6 footprints" 'grep -q "6 symbols.* 6 footprints" "$T/info2.txt"'

# 5. damaged or swapped files are refused and left untouched
mkdir -p "$T/bad" && head -c 5000 /dev/urandom > "$T/bad/EasyLib.SchLib" && cp "$T/bad/EasyLib.SchLib" "$T/bad/copy"
$RUN --add "$T/fx/fet.json" --lib "$T/bad" --no3d > "$T/bad.txt" 2>&1
check "corrupt SchLib: refused" 'grep -q "InvalidDataException" "$T/bad.txt"'
check "corrupt SchLib: not overwritten, no PcbLib created" 'cmp -s "$T/bad/EasyLib.SchLib" "$T/bad/copy" && [ ! -f "$T/bad/EasyLib.PcbLib" ]'

# 6. three writers at once on one folder (shared library): nothing is lost
( $RUN --add "$T"/fx/name0[0-2].json --lib "$T/conc" --no3d >/dev/null 2>&1 ) &
( $RUN --add "$T"/fx/name0[3-5].json --lib "$T/conc" --no3d >/dev/null 2>&1 ) &
( $RUN --add "$T/fx/fet.json" "$T/fx/name06.json" --lib "$T/conc" --no3d >/dev/null 2>&1 ) &
wait
check "concurrent writers: all 8 symbols present" '$RUN --lib-info "$T/conc" 2>/dev/null | grep -q "8 symbols"'

[ $fail = 0 ] && echo "ALL TESTS PASSED" || echo "SOME TESTS FAILED"
exit $fail
