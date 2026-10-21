#!/usr/bin/env bash
# Shows whether collection is healthy. Read-only; run as root (it reads the service's open files):
#   sudo /opt/transitpulse/health.sh
# One line per check: OK | WARN | FAIL | INFO. Exits 1 if any check FAILs, so a monitor can call it too.
# Gaps in the history ("no gap > 5 min over 48 h") are checked on the archive itself, not here.
set -euo pipefail

archive=${ARCHIVE_DIR:-/var/lib/transitpulse/archive}
unit=transitpulse-ingestor.service

failures=0
report() {
  local level=$1 check=$2
  shift 2
  printf '%-4s  %-8s  %s\n' "$level" "$check" "$*"
  if [[ $level == FAIL ]]; then failures=$((failures + 1)); fi
}

show() { systemctl show "$1" -p "$2" --value; }
mib() { awk -v b="$1" 'BEGIN { printf "%.0f MiB", b / 1048576 }'; }

# --- service: a crash restart is silent in systemd; NRestarts is reset by a manual restart or deploy.
state=$(show "$unit" ActiveState)
restarts=$(show "$unit" NRestarts)
since=$(show "$unit" ActiveEnterTimestamp)
pid=$(show "$unit" MainPID)
if [[ $state != active ]]; then
  report FAIL service "$unit is $state"
elif (( restarts > 0 )); then
  report FAIL service "$restarts crash restarts since the last deploy; running since $since (journalctl -u $unit)"
else
  report OK service "active since $since, no crash restarts"
fi

# --- memory: anon is the process's own memory; MemoryCurrent would also count reclaimable page cache.
# .NET caps its heap at 75% of MemoryMax, so 60% of MemoryMax is 80% of the heap limit.
cgroup=/sys/fs/cgroup$(show "$unit" ControlGroup)
max=$(show "$unit" MemoryMax)
if [[ -r $cgroup/memory.stat && $max =~ ^[0-9]+$ ]]; then
  anon=$(awk '$1 == "anon" { print $2 }' "$cgroup/memory.stat")
  percent=$(( anon * 100 / max ))
  if (( percent > 60 )); then
    report WARN memory "$(mib "$anon") = $percent% of MemoryMax $(mib "$max"); the GC heap limit is 75%"
  else
    report OK memory "$(mib "$anon") = $percent% of MemoryMax $(mib "$max")"
  fi
else
  report WARN memory "no cgroup memory data (service not running or MemoryMax not set)"
fi

# --- open files: .tmp files the running process holds; any other .tmp is an orphan from a crash.
declare -A open=()
if (( pid > 0 )); then
  for fd in /proc/"$pid"/fd/*; do
    target=$(readlink "$fd" || true)
    if [[ $target == "$archive"/*.tmp ]]; then open[$target]=1; fi
  done
fi

# --- feeds: a file is created on the first record of the hour, so its existence proves data this hour.
# A quiet feed's .tmp is written only when a row group fills or the hour closes: its mtime means nothing.
# HSL fills a row group every few seconds, so its mtime does show a stalled stream.
hour=$(date -u +%Y%m%dT%H)
day=$(date -u +%F)
minute=$((10#$(date -u +%M)))
now=$(date +%s)
for dir in "$archive"/*/; do
  [[ -d $dir ]] || continue
  feed=$(basename "$dir")
  current=$(find "$dir$day" -maxdepth 1 -name "$feed-${hour}Z-*" 2>/dev/null | sort | tail -n1)
  case $feed in
    *-alerts | *-sx) grace=-1 ;; # written only when the content changes
    weather) grace=20 ;;         # polled every 15 minutes
    *) grace=10 ;;
  esac

  if [[ -z $current ]]; then
    if (( grace < 0 )); then
      report INFO feed "$feed: no change this hour (expected for announcements)"
    elif (( minute > grace )); then
      report FAIL feed "$feed: no data since $(date -u +%H):00 UTC"
    else
      report OK feed "$feed: hour just started"
    fi
  elif [[ $feed == hsl ]]; then
    age=$(( now - $(stat -c %Y "$current") ))
    if (( age > 120 )); then
      report FAIL feed "hsl: nothing written for ${age}s"
    else
      report OK feed "hsl: written ${age}s ago"
    fi
  else
    report OK feed "$feed: data this hour"
  fi
done

# --- orphans: unreadable as a whole, but their intact row groups can be recovered.
orphan_count=0
orphan_bytes=0
while IFS= read -r -d '' file; do
  [[ -n ${open[$file]:-} ]] && continue
  orphan_count=$((orphan_count + 1))
  orphan_bytes=$((orphan_bytes + $(stat -c %s "$file")))
done < <(find "$archive" -name '*.tmp' -print0)
if (( orphan_count > 0 )); then
  report FAIL orphans "$orphan_count .tmp files, $(mib "$orphan_bytes"), not open by the service: recover with --repair, then delete"
else
  report OK orphans "none"
fi

# --- disk
used=$(df --output=pcent "$archive" | tail -n1 | tr -dc '0-9')
if (( used > 90 )); then
  report FAIL disk "$used% used"
elif (( used > 80 )); then
  report WARN disk "$used% used"
else
  report OK disk "$used% used"
fi

# --- upload to the cloud buffer (hourly timer), optional: without it the archive stays on the server.
result=$(show transitpulse-upload.service Result)
last=$(show transitpulse-upload.timer LastTriggerUSec)
last_epoch=$(date -d "$last" +%s 2>/dev/null || echo 0)
uploading=$(systemctl is-enabled transitpulse-upload.timer 2>/dev/null || true)
if [[ $uploading != enabled ]]; then
  report INFO upload "disabled: files stay in $archive until copied to the PC"
elif [[ $result != success ]]; then
  report FAIL upload "last run: $result (journalctl -u transitpulse-upload)"
elif (( now - last_epoch > 70 * 60 )); then
  report WARN upload "last run at '${last:-never}', more than 70 min ago"
else
  report OK upload "last run at $last"
fi

# --- volume of 24 h, per feed: the measured daily size of the archive (from the files when nothing is uploaded).
if [[ $uploading != enabled ]]; then
  find "$archive" -name '*.parquet' -mmin -1440 -printf '%s %P\n' 2>/dev/null |
    awk '{ split($2, p, "/"); bytes[p[1]] += $1; total += $1 }
         END {
           line = ""
           for (f in bytes) line = line sprintf("%s %.1f MiB, ", f, bytes[f] / 1048576)
           printf "INFO  volume    24 h: %.0f MiB archived (%s)\n", total / 1048576, (line == "" ? "nothing" : substr(line, 1, length(line) - 2))
         }'
else
journalctl -u transitpulse-upload --since -24h -o cat --no-pager 2>/dev/null |
  awk '$1 == "uploaded" { split($2, p, "/"); gsub(/[(]/, "", $3); bytes[p[1]] += $3; total += $3 }
       END {
         line = ""
         for (f in bytes) line = line sprintf("%s %.1f MiB, ", f, bytes[f] / 1048576)
         printf "INFO  volume    24 h: %.0f MiB uploaded (%s)\n", total / 1048576, (line == "" ? "nothing" : substr(line, 1, length(line) - 2))
       }'
fi

# --- errors logged in the last hour (Entur's HTTP 429 is a warning and is not counted).
errors=$(journalctl -u "$unit" --since -1h -o cat --no-pager 2>/dev/null | grep -cE '^(fail|crit):' || true)
if (( errors > 0 )); then
  report WARN errors "$errors error entries in the last hour (journalctl -u $unit --since -1h)"
else
  report OK errors "none in the last hour"
fi

exit $(( failures > 0 ? 1 : 0 ))
