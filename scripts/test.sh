#!/bin/sh
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
# Fixed ShopTests storage permits one gate invocation at a time per worktree.
lock=.test-gate.lock
if ! mkdir "$lock" 2>/dev/null; then
    echo 'Another gate is running; only one run at a time is supported.' >&2
    exit 1
fi
SHOP_TEST_RESULTS=$(mktemp -d "${TMPDIR:-/tmp}/mini-shop-tests.XXXXXX")
export SHOP_TEST_RESULTS
cleanup() {
    result=$?
    trap - EXIT HUP INT TERM
    docker compose --profile test rm --stop --force test-db >/dev/null 2>&1 || true
    rm -rf "$SHOP_TEST_RESULTS"
    rmdir "$lock"
    exit "$result"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

docker compose --profile test run --build --rm api-tests
if docker compose --profile test run --rm ui-tests >"$SHOP_TEST_RESULTS/js.tap" 2>&1; then
    js_result=0
else
    js_result=$?
fi
cat "$SHOP_TEST_RESULTS/js.tap"
[ "$js_result" -eq 0 ] || exit "$js_result"

count_suite() {
    report=$(grep -l "className=\"${1}\\." "$SHOP_TEST_RESULTS"/*.trx || true)
    [ -n "$report" ] || { echo "Missing $1 result" >&2; exit 1; }
    count=$(sed -n 's/.*<Counters .*executed="\([0-9]*\)".*/\1/p' "$report")
    [ "${count:-0}" -gt 0 ] || { echo "No $1 assertions executed" >&2; exit 1; }
    printf '%s' "$count"
}
unit=$(count_suite Shop.UnitTests)
integration=$(count_suite Shop.IntegrationTests)
js=$(sed -n 's/^# tests \([0-9]*\)$/\1/p' "$SHOP_TEST_RESULTS/js.tap")
[ "${js:-0}" -gt 0 ] || { echo 'No JS assertions executed' >&2; exit 1; }
printf 'Gate passed: unit=%s; SQL-backed integration=%s; JS=%s; failed=0\n' "$unit" "$integration" "$js"
