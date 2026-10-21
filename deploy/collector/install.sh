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

# Hourly upload to the cloud buffer; the token is installed separately and is never part of the package.
install -m 755 "$src/upload.sh" /opt/transitpulse/upload.sh
install -m 755 "$src/health.sh" /opt/transitpulse/health.sh
install -m 644 "$src/transitpulse-upload.service" "$src/transitpulse-upload.timer" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now "$unit"

# The upload is optional: without a cloud token the archive stays on the server and the PC copies it over SSH.
if [[ -s /etc/transitpulse/yandex.token ]]; then
  systemctl enable --now transitpulse-upload.timer
else
  systemctl disable --now transitpulse-upload.timer 2>/dev/null || true
  echo "No /etc/transitpulse/yandex.token: cloud upload disabled, files stay in the archive." >&2
fi
sleep 5
systemctl --no-pager --lines=10 status "$unit"
