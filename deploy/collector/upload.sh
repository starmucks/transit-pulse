#!/usr/bin/env bash
# Moves completed archive files from the collector to the cloud buffer (Yandex Disk, app-folder access only).
# Run hourly by transitpulse-upload.timer. For each closed *.parquet file (never *.tmp):
#   upload as <name>.partial -> compare the SHA-256 the cloud reports with the local one -> rename to <name>
#   -> delete the local file.
# A reader on the other side sees only finished names, and "upload.lock" in the buffer while a run is in progress.
# A failed file stays on the server and is retried next hour.
# During peak hours the run is skipped: the collector needs the CPU (see the settings below).
set -euo pipefail

archive=${ARCHIVE_DIR:-/var/lib/transitpulse/archive}
token_file=${TOKEN_FILE:-/etc/transitpulse/yandex.token}
remote_root=app:/archive
api=https://cloud-api.yandex.net/v1/disk

# UTC hour windows "start-end" (end exclusive, may wrap past midnight) with no upload. Measured on the 1-vCPU server:
# 50-85% CPU from the collector alone there, and an upload burst on top saturated it and stalled the HSL feed.
pause_hours=${UPLOAD_PAUSE_HOURS_UTC:-04-08 12-16}
# Above this disk use the upload runs even in a pause: a full disk loses more data than a slow hour.
force_disk_percent=${UPLOAD_FORCE_DISK_PERCENT:-75}
# Spreads an hourly file (up to ~200 MB at HSL peaks) over minutes instead of a burst that takes the whole CPU.
rate_limit=${UPLOAD_RATE_LIMIT:-300k}

# Calls the Disk API; prints the body, then the HTTP status on the last line. The token reaches curl as a header
# on stdin, so it never shows up in the process list or the journal.
call() {
  local method=$1 url=$2
  printf 'Authorization: OAuth %s\n' "$(tr -d '\r\n' < "$token_file")" |
    curl -sS --retry 3 --max-time 120 -X "$method" -H @- -w '\n%{http_code}' "$api/$url"
}

status() { tail -n1 <<< "$1"; }
json_field() { sed '$d' <<< "$1" | grep -o "\"$2\":\"[^\"]*\"" | head -1 | cut -d'"' -f4; }

mkdir_remote() {
  local code
  code=$(status "$(call PUT "resources?path=$1")")
  [[ $code == 201 || $code == 409 ]] || { echo "create folder $1: HTTP $code" >&2; return 1; }
}

# Uploads a local file (or stdin with "-") to a remote path, overwriting it.
put_file() {
  local source=$1 remote=$2 response code href
  response=$(call GET "resources/upload?path=$remote&overwrite=true")
  code=$(status "$response")
  [[ $code == 200 ]] || { echo "upload link for $remote: HTTP $code" >&2; return 1; }
  # The upload link is pre-signed: no token is sent with the data.
  href=$(json_field "$response" href)
  code=$(curl -sS --retry 3 --max-time 1800 --limit-rate "$rate_limit" -T "$source" -o /dev/null -w '%{http_code}' "$href")
  [[ $code == 201 || $code == 202 ]] || { echo "upload $remote: HTTP $code" >&2; return 1; }
}

# The cloud may compute the hash shortly after the upload completes.
remote_sha256() {
  local hash attempt
  for attempt in 1 2 3 4 5; do
    hash=$(json_field "$(call GET "resources?path=$1&fields=sha256")" sha256)
    [[ -n $hash ]] && { echo "$hash"; return 0; }
    sleep 2
  done
  return 1
}

upload_one() {
  local file=$1 relative remote local_hash cloud_hash code
  relative=${file#"$archive"/}
  remote=$remote_root/$relative

  local_hash=$(sha256sum "$file" | cut -d' ' -f1)
  # A run killed between the rename and the local delete leaves the file in both places: finish that run's work.
  if [[ $(json_field "$(call GET "resources?path=$remote&fields=sha256")" sha256) == "$local_hash" ]]; then
    echo "already uploaded $relative"
    rm -f "$file"
    return 0
  fi

  mkdir_remote "$remote_root/${relative%%/*}" || return 1
  mkdir_remote "$(dirname "$remote")" || return 1
  put_file "$file" "$remote.partial" || return 1

  cloud_hash=$(remote_sha256 "$remote.partial") || { echo "no checksum from the cloud for $relative" >&2; return 1; }
  [[ $local_hash == "$cloud_hash" ]] || { echo "checksum mismatch for $relative" >&2; return 1; }

  # Never overwrite: the same file was ruled out above, so a file of this name in the buffer is an earlier hour whose
  # number the collector reused after it was deleted here. Replacing it would lose that hour; keeping this one loses nothing.
  code=$(status "$(call POST "resources/move?from=$remote.partial&path=$remote&overwrite=false")")
  if [[ $code == 409 ]]; then
    echo "rename $relative: a different file with this name is already in the buffer; kept on the server" >&2
    return 1
  fi
  [[ $code == 201 || $code == 202 ]] || { echo "rename $relative: HTTP $code" >&2; return 1; }

  echo "uploaded $relative ($(stat -c %s "$file") bytes)"
  rm -f "$file"
}

# True when the current UTC hour falls into one of the pause windows.
in_pause() {
  local hour window start end
  hour=$((10#$(date -u +%H)))
  for window in $pause_hours; do
    start=$((10#${window%-*}))
    end=$((10#${window#*-}))
    if (( start <= end ? (hour >= start && hour < end) : (hour >= start || hour < end) )); then
      return 0
    fi
  done
  return 1
}

if in_pause; then
  disk_percent=$(df --output=pcent "$archive" | tail -n1 | tr -dc '0-9')
  if (( disk_percent < force_disk_percent )); then
    echo "peak hours ($pause_hours UTC): upload skipped, disk ${disk_percent}%"
    exit 0
  fi
  echo "peak hours, but disk ${disk_percent}% >= ${force_disk_percent}%: uploading anyway"
fi

mapfile -t files < <(find "$archive" -type f -name '*.parquet' | sort)
if (( ${#files[@]} == 0 )); then
  exit 0
fi

mkdir_remote "$remote_root"
date -u +%FT%TZ | put_file - "$remote_root/upload.lock"
trap 'call DELETE "resources?path=$remote_root/upload.lock&permanently=true" > /dev/null || true' EXIT

failed=0
for file in "${files[@]}"; do
  upload_one "$file" || failed=1
done

# Remove empty day folders left after their files were uploaded, except today's: the collector creates it before the
# hour's first file, and deleting it in between would fail that write.
find "$archive" -mindepth 2 -maxdepth 2 -type d -empty ! -name "$(date -u +%F)" -delete
exit $failed
