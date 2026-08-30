#!/usr/bin/env bash
set -euo pipefail

# Local long-running work must pass this gate before restore, build, test, or
# multi-size image rendering. Pass a command after `--` to hold a host-wide
# lock from the resource check through command completion. CI has its own
# isolated runner resource policy.

profile="${1:-standard}"
if (( $# > 0 )); then
  shift
fi
run_command=false
if (( $# > 0 )); then
  if [[ "$1" != "--" || $# -lt 2 ]]; then
    printf 'usage: %s [light|standard|heavy] [-- command [argument ...]]\n' "$0" >&2
    exit 2
  fi
  shift
  run_command=true
fi
case "$profile" in
  light)
    minimum_available_mib=4096
    minimum_swap_free_mib=1024
    maximum_memory_full_psi=5.0
    maximum_load_per_cpu=1.0
    minimum_tmp_free_mib=2048
    minimum_workspace_free_mib=4096
    minimum_combined_headroom_mib=6144
    ;;
  standard)
    minimum_available_mib=8192
    minimum_swap_free_mib=2048
    maximum_memory_full_psi=2.0
    maximum_load_per_cpu=0.75
    minimum_tmp_free_mib=4096
    minimum_workspace_free_mib=8192
    minimum_combined_headroom_mib=12288
    ;;
  heavy)
    minimum_available_mib=12288
    minimum_swap_free_mib=4096
    maximum_memory_full_psi=1.0
    maximum_load_per_cpu=0.5
    minimum_tmp_free_mib=6144
    minimum_workspace_free_mib=12288
    minimum_combined_headroom_mib=18432
    ;;
  *)
    printf 'usage: %s [light|standard|heavy] [-- command [argument ...]]\n' "$0" >&2
    exit 2
    ;;
esac

minimum_available_mib="${CLASHSHARP_MIN_AVAILABLE_MIB:-$minimum_available_mib}"
minimum_swap_free_mib="${CLASHSHARP_MIN_SWAP_FREE_MIB:-$minimum_swap_free_mib}"
maximum_memory_full_psi="${CLASHSHARP_MAX_MEMORY_FULL_PSI:-$maximum_memory_full_psi}"
maximum_load_per_cpu="${CLASHSHARP_MAX_LOAD_PER_CPU:-$maximum_load_per_cpu}"
oom_cooldown_minutes="${CLASHSHARP_OOM_COOLDOWN_MINUTES:-15}"

failures=0

fail()
{
  printf 'FAIL  %s\n' "$1"
  failures=$((failures + 1))
}

pass()
{
  printf 'PASS  %s\n' "$1"
}

warn()
{
  printf 'WARN  %s\n' "$1"
}

if [[ "$run_command" == true ]]; then
  if ! command -v flock >/dev/null 2>&1; then
    fail "flock is unavailable; an atomic resource-gated command cannot be started."
  else
    resource_lock_path="/tmp/clashsharp-resource-budget-$(id -u).lock"
    exec 9>"$resource_lock_path"
    if flock --exclusive --nonblock 9; then
      pass "Acquired the host-wide ClashSharp build/test lock."
    else
      fail "Another resource-gated ClashSharp command owns the host-wide lock."
    fi
  fi
fi

read_meminfo_mib()
{
  local field="$1"
  awk -v key="$field" '$1 == key ":" { printf "%d", $2 / 1024; found=1 } END { if (!found) exit 1 }' \
    /proc/meminfo
}

read_free_mib()
{
  local path="$1"
  df -Pk "$path" | awk 'NR == 2 { printf "%d", $4 / 1024 }'
}

if [[ ! -r /proc/meminfo || ! -r /proc/loadavg ]]; then
  fail "Linux resource telemetry is unavailable."
else
  available_mib="$(read_meminfo_mib MemAvailable)"
  if (( available_mib < minimum_available_mib )); then
    fail "MemAvailable ${available_mib} MiB is below ${minimum_available_mib} MiB (${profile})."
  else
    pass "MemAvailable ${available_mib} MiB >= ${minimum_available_mib} MiB."
  fi

  swap_total_mib="$(read_meminfo_mib SwapTotal)"
  swap_free_mib="$(read_meminfo_mib SwapFree)"
  combined_headroom_mib=$((available_mib + swap_free_mib))
  if (( swap_total_mib > 0 && swap_free_mib < minimum_swap_free_mib )); then
    if (( combined_headroom_mib < minimum_combined_headroom_mib )); then
      fail "SwapFree ${swap_free_mib} MiB is low and combined memory headroom ${combined_headroom_mib} MiB is below ${minimum_combined_headroom_mib} MiB (${profile})."
    else
      warn "SwapFree ${swap_free_mib} MiB is low, but combined memory headroom ${combined_headroom_mib} MiB remains sufficient."
    fi
  else
    pass "SwapFree ${swap_free_mib} MiB is acceptable."
  fi

  cpu_count="$(nproc)"
  load_one="$(awk '{ print $1 }' /proc/loadavg)"
  maximum_load="$(awk -v cpus="$cpu_count" -v factor="$maximum_load_per_cpu" \
    'BEGIN { printf "%.2f", cpus * factor }')"
  if awk -v actual="$load_one" -v maximum="$maximum_load" 'BEGIN { exit !(actual > maximum) }'; then
    fail "Load average ${load_one} exceeds ${maximum_load} for ${cpu_count} CPUs (${profile})."
  else
    pass "Load average ${load_one} <= ${maximum_load} for ${cpu_count} CPUs."
  fi
fi

if [[ -r /proc/pressure/memory ]]; then
  memory_full_psi="$(awk '$1 == "full" { for (field = 1; field <= NF; field++) if ($field ~ /^avg10=/) { split($field, value, "="); print value[2] } }' \
    /proc/pressure/memory)"
  if [[ -z "$memory_full_psi" ]]; then
    fail "Memory PSI full avg10 could not be parsed."
  elif awk -v actual="$memory_full_psi" -v maximum="$maximum_memory_full_psi" \
    'BEGIN { exit !(actual > maximum) }'; then
    fail "Memory PSI full avg10 ${memory_full_psi} exceeds ${maximum_memory_full_psi} (${profile})."
  else
    pass "Memory PSI full avg10 ${memory_full_psi} <= ${maximum_memory_full_psi}."
  fi
else
  pass "Memory PSI is unavailable; MemAvailable and SwapFree remain enforced."
fi

tmp_free_mib="$(read_free_mib /tmp)"
workspace_free_mib="$(read_free_mib .)"
if (( tmp_free_mib < minimum_tmp_free_mib )); then
  fail "/tmp free space ${tmp_free_mib} MiB is below ${minimum_tmp_free_mib} MiB."
else
  pass "/tmp free space ${tmp_free_mib} MiB is sufficient."
fi
if (( workspace_free_mib < minimum_workspace_free_mib )); then
  fail "Workspace free space ${workspace_free_mib} MiB is below ${minimum_workspace_free_mib} MiB."
else
  pass "Workspace free space ${workspace_free_mib} MiB is sufficient."
fi

worker_command_pattern='^(dotnet|MSBuild|VBCSCompiler|testhost|vstest|vstest.console|cargo|rustc|cc1|magick|convert|inkscape|rsvg-convert)$'
excluded_worker_pids=" $$ "
ancestor_pid="$PPID"
while [[ "$ancestor_pid" =~ ^[0-9]+$ ]] && (( ancestor_pid > 1 )); do
  excluded_worker_pids+="$ancestor_pid "
  ancestor_pid="$(ps -o ppid= -p "$ancestor_pid" | tr -d '[:space:]')"
done
active_workers="$(ps -u "$(id -u)" -o pid=,etimes=,comm=,args= \
  | awk -v excluded="$excluded_worker_pids" -v workers="$worker_command_pattern" \
    'index(excluded, " " $1 " ") == 0 && $3 ~ workers' \
  | grep -Ev 'dotnet[^ ]* .*languageServer|VBCSCompiler.*-pipename' \
  || true)"
if [[ -n "$active_workers" ]]; then
  fail $'Another build, test, compiler, or renderer worker is active:\n'"$active_workers"
else
  pass "No competing build, test, compiler, or renderer worker is active."
fi

recent_oom=""
if command -v journalctl >/dev/null 2>&1; then
  recent_oom="$(journalctl -k --since "-${oom_cooldown_minutes} minutes" --no-pager 2>/dev/null \
    | grep -Ei 'out of memory: killed process|oom-kill:' \
    | tail -n 1 \
    || true)"
fi
if [[ -n "$recent_oom" ]]; then
  fail "Kernel OOM activity occurred within the ${oom_cooldown_minutes}-minute cooldown: ${recent_oom}"
else
  pass "No readable kernel OOM event exists in the last ${oom_cooldown_minutes} minutes."
fi

if (( failures > 0 )); then
  printf '\nRESOURCE GATE: BLOCKED (%d failed check(s), profile=%s)\n' "$failures" "$profile"
  exit 1
fi

printf '\nRESOURCE GATE: READY (profile=%s)\n' "$profile"

if [[ "$run_command" == true ]]; then
  exec "$@"
fi
