#!/usr/bin/env bash
# Installs or updates the collector on the server. Run as root from the folder that publish.sh produced:
#   sudo ./install.sh
# Safe to re-run: an update stops the service (open hourly files are closed), swaps the binary and starts it again.
set -euo pipefail

src="$(cd "$(dirname "$0")" && pwd)"
app=/opt/transitpulse/ingestor
unit=transitpulse-ingestor.service

id transitpulse >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin transitpulse

# Checked before touching the running service: a binary that cannot write and read an archive is never installed.
install -m 755 "$src/TransitPulse.Ingestor" /tmp/TransitPulse.Ingestor.new
runuser -u transitpulse -- /tmp/TransitPulse.Ingestor.new --self-test

systemctl stop "$unit" 2>/dev/null || true
install -d -m 755 "$app"
mv /tmp/TransitPulse.Ingestor.new "$app/TransitPulse.Ingestor"
install -m 644 "$src/appsettings.json" "$src/VERSION" "$app/"
install -m 644 "$src/$unit" "/etc/systemd/system/$unit"

systemctl daemon-reload
systemctl enable --now "$unit"
sleep 5
systemctl --no-pager --lines=10 status "$unit"
