#!/usr/bin/env bash
# Keeps a snapshot of every static schedule (GTFS) the realtime feeds refer to. HSL and Entur drop past dates from each
# new export, so the schedule of a day exists only in exports made around that day. Run by transitpulse-gtfs.timer.
# Layout: {root}/{feed}/{yyyy-MM-dd}T{HHmm}Z.zip (+ .headers.txt, .zip.sha256), the time being the download time.
# An export the server reports as unchanged (ETag) is not downloaded again; a corrupt zip is never kept.
set -euo pipefail

root=${GTFS_DIR:-/var/lib/transitpulse/gtfs}
feeds=(
  "hsl https://infopalvelut.storage.hsldev.com/gtfs/hsl.zip"
  "mbta https://cdn.mbta.com/MBTA_GTFS.zip"
  "entur-sky https://storage.googleapis.com/marduk-production/outbound/gtfs/rb_sky-aggregated-gtfs.zip"
)

failed=0
for entry in "${feeds[@]}"; do
  read -r feed url <<< "$entry"
  dir=$root/$feed
  mkdir -p "$dir"
  tmp=$dir/.download
  args=(-sS --fail -L -R --retry 5 --retry-all-errors --retry-delay 5 --connect-timeout 30 --max-time 1800
        -D "$tmp.headers" -o "$tmp" -w '%{http_code}')
  [[ -s $dir/last.etag ]] && args+=(-H "If-None-Match: $(< "$dir/last.etag")")

  if ! code=$(curl "${args[@]}" "$url"); then
    echo "$feed: download failed" >&2
    failed=1
    rm -f "$tmp" "$tmp.headers"
    continue
  fi
  if [[ $code == 304 ]]; then
    echo "$feed: unchanged"
    rm -f "$tmp" "$tmp.headers"
    continue
  fi

  # testzip reads every member and checks its CRC: a truncated or damaged export is rejected, not archived.
  if ! python3 -c 'import sys, zipfile; sys.exit(1 if zipfile.ZipFile(sys.argv[1]).testzip() else 0)' "$tmp"; then
    echo "$feed: downloaded file is not a valid zip" >&2
    failed=1
    rm -f "$tmp" "$tmp.headers"
    continue
  fi

  stamp=$(date -u +%Y-%m-%dT%H%MZ)
  mv "$tmp.headers" "$dir/$stamp.headers.txt"
  mv "$tmp" "$dir/$stamp.zip"
  (cd "$dir" && sha256sum "$stamp.zip" > "$stamp.zip.sha256")
  # Saved last, so a failure above retries the download next time instead of skipping it as unchanged.
  grep -i '^etag:' "$dir/$stamp.headers.txt" | tail -1 | cut -d' ' -f2- | tr -d '\r' > "$dir/last.etag" || true
  echo "$feed: saved $stamp.zip ($(stat -c %s "$dir/$stamp.zip") bytes)"
done

exit $failed
