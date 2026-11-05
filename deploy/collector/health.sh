#!/usr/bin/env bash
# Shows whether collection is healthy. Read-only; run as root (it reads the service's open files):
#   sudo /opt/transitpulse/health.sh
# One line per check: OK | WARN | FAIL | INFO. Exits 1 if any check FAILs, so a monitor can call it too.
# Gaps in the history ("no gap > 5 min over 48 h") are checked on the archive itself, not here.
set -euo pipefail

archive=${ARCHIVE_DIR:-/var/lib/transitpulse/archive}
app=${APP_DIR:-/opt/transitpulse/ingestor}
unit=transitpulse-ingestor.service

failures=0
report() {
  local level=$1 check=$2
  shift 2
  printf '%-4s  %-8s  %s\n' "$level" "$check" "$*"
  if [[ $level == FAIL ]]; then failures=$((failures + 1)); fi
}

show() { systemctl show "$1" -p "$2" --value; }
# GNU date reads an empty string as today's midnight, not an error: an empty time must mean "never" (0).
epoch() { if [[ -n $1 ]]; then date -d "$1" +%s 2>/dev/null || echo 0; else echo 0; fi; }
# Milliseconds since each of the collector's HSL broker sockets last received bytes, one line per connection; nothing
# without a connection. ss -i prints the socket's counters on the line after the one naming the process.
hsl_lastrcvs() {
  ss -tnpi state established "( dport = :${HSL_PORT:-8883} )" 2>/dev/null |
    grep -A1 "pid=$pid," | grep -o 'lastrcv:[0-9]*' | cut -d: -f2 || true
}
# One broker connection per MQTT subscription; only entries of Feeds:Mqtt have a "Topics" key.
hsl_expected=$(grep -c '"Topics"' "$app/appsettings.json" 2>/dev/null || true)
[[ $hsl_expected =~ ^[1-9][0-9]*$ ]] || hsl_expected=1
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
# The commit publish.sh built from: shows at a glance whether the server runs what main has.
report INFO version "$(cat "$app/VERSION" 2>/dev/null || echo unknown)"

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
# A .tmp is written only when a row group fills or the hour closes: its mtime means nothing, even for HSL, whose
# 16 MiB row group takes about a minute in the evening but most of an hour at night. HSL's liveness is read from its
# broker socket instead: the time since bytes last arrived (data or ping answers; data silence the source handles).
hour=$(date -u +%Y%m%dT%H)
day=$(date -u +%F)
minute=$((10#$(date -u +%M)))
now=$(date +%s)
for dir in "$archive"/*/; do
  [[ -d $dir ]] || continue
  feed=$(basename "$dir")
  # || true: with no folder for today (a feed dead since yesterday) find fails, and set -e would end the script silently.
  current=$(find "$dir$day" -maxdepth 1 -name "$feed-${hour}Z-*" 2>/dev/null | sort | tail -n1 || true)
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
    # Covers every broker connection (hsl, hsl-stop): pings are answered every 30 s, so a live one is never quiet long.
    mapfile -t rcvs < <(hsl_lastrcvs)
    # A reconnect leaves about a second without a connection: look once more before calling it down.
    if (( ${#rcvs[@]} < hsl_expected )); then sleep 3; mapfile -t rcvs < <(hsl_lastrcvs); fi
    quietest=0
    for rcv in "${rcvs[@]}"; do if (( rcv > quietest )); then quietest=$rcv; fi; done
    if (( ${#rcvs[@]} == 0 )); then
      report FAIL feed "hsl: no connection to the broker"
    elif (( ${#rcvs[@]} < hsl_expected )); then
      report FAIL feed "hsl: ${#rcvs[@]} of $hsl_expected broker connections"
    elif (( quietest > 120000 )); then
      report FAIL feed "hsl: a broker connection received nothing for $(( quietest / 1000 ))s"
    else
      report OK feed "hsl: data this hour, ${#rcvs[@]} broker connections, quietest heard $(( quietest / 1000 ))s ago"
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
last_epoch=$(epoch "$last")
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

# --- copy to the PC: the copying script writes the marker once every finished file is copied and verified.
# Without the cloud upload the server holds the only copy, so an old marker is a warning.
if [[ $uploading != enabled ]]; then
  marker=/var/lib/transitpulse/last-pull
  pulled=$(cat "$marker" 2>/dev/null || true)
  pulled_epoch=$(epoch "$pulled")
  if (( pulled_epoch == 0 )); then
    report WARN pull "never copied to the PC: the server holds the only copy"
  elif (( now - pulled_epoch > 7 * 86400 )); then
    report WARN pull "the PC has files up to $pulled, $(( (now - pulled_epoch) / 86400 )) days ago: copy soon"
  else
    report OK pull "the PC has files up to $pulled"
  fi
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

# --- static schedule snapshots (twice a day): an unchanged export is not stored, so judge by the last run.
gtfs_result=$(show transitpulse-gtfs.service Result)
# The service's own exit time, not the timer's: a manual run counts too.
gtfs_last=$(show transitpulse-gtfs.service ExecMainExitTimestamp)
gtfs_epoch=$(epoch "$gtfs_last")
latest=$(for d in /var/lib/transitpulse/gtfs/*/; do
           [[ -d $d ]] || continue
           f=$(ls "$d" | grep -E '\.zip$' | sort | tail -1)
           printf '%s %s, ' "$(basename "$d")" "${f%.zip}"
         done)
if [[ $gtfs_result != success ]]; then
  report FAIL gtfs "last run: $gtfs_result (journalctl -u transitpulse-gtfs)"
elif (( now - gtfs_epoch > 13 * 3600 )); then
  report WARN gtfs "last run at '${gtfs_last:-never}', more than 13 h ago"
else
  report OK gtfs "last run at $gtfs_last; latest: ${latest%, }"
fi

recent_log=$(journalctl -u "$unit" --since -1h -o cat --no-pager 2>/dev/null || true)

# --- HSL broker drops in the last hour: normally 0-2 a day. The service, its files and its connections all look fine
# meanwhile, so nothing above notices. The cause can be on either side; what is lost during a drop is lost either way.
drops=$(grep -c 'MQTT feed .*connection ended' <<<"$recent_log" || true)
if (( drops > 3 )); then
  report FAIL drops "HSL broker connections ended $drops times in the last hour: data is being lost"
else
  report OK drops "$drops HSL broker connection drops in the last hour"
fi

# --- size of the last finished HSL hour against the same hour a week ago: the same weekday and hour differ by under 2 %
# (2026-10-29 and 2026-11-04), so this sees a partial loss that "data this hour" cannot. An hour's file closes a little
# after the hour ends, and files of quiet night hours are too small to compare.
hour_bytes() {
  { find "$archive/hsl/$(date -u -d "$1 hours ago" +%F)" -maxdepth 1 -name "hsl-$(date -u -d "$1 hours ago" +%Y%m%dT%H)Z-*.parquet" -printf '%s\n' 2>/dev/null || true; } |
    awk '{ total += $1 } END { print total + 0 }'
}
hours_back=1
if (( minute < 5 )); then hours_back=2; fi
hour_now=$(hour_bytes "$hours_back")
hour_before=$(hour_bytes $((hours_back + 7 * 24)))
hour_name=$(date -u -d "$hours_back hours ago" +%H:00)
if (( hour_before < 20 * 1048576 )); then
  report INFO hour "hsl $hour_name: $(mib "$hour_now"); a week ago too small to compare"
elif (( hour_now * 100 < hour_before * 70 )); then
  report FAIL hour "hsl $hour_name: $(mib "$hour_now"), $(( hour_now * 100 / hour_before ))% of $(mib "$hour_before") a week ago (a public holiday lowers it too)"
else
  report OK hour "hsl $hour_name: $(mib "$hour_now"), $(( hour_now * 100 / hour_before ))% of the same hour a week ago"
fi

# --- errors logged in the last hour (Entur's HTTP 429 is a warning and is not counted).
errors=$(grep -cE '^(fail|crit):' <<<"$recent_log" || true)
if (( errors > 0 )); then
  report WARN errors "$errors error entries in the last hour (journalctl -u $unit --since -1h)"
else
  report OK errors "none in the last hour"
fi

exit $(( failures > 0 ? 1 : 0 ))
