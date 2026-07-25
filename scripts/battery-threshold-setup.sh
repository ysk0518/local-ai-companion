#!/bin/bash
# Battery charge threshold setup for ThinkPad X1 Carbon Gen6
# Stop charging at 50%, resume at 45% — preserves Li-ion health

set -e

THRESHOLD_STOP=50
THRESHOLD_START=45

echo "=== Setting charge thresholds ==="

# Apply immediately
echo "$THRESHOLD_STOP" > /sys/class/power_supply/BAT0/charge_control_end_threshold
echo "$THRESHOLD_START" > /sys/class/power_supply/BAT0/charge_control_start_threshold

echo "Stop charging:  $(cat /sys/class/power_supply/BAT0/charge_control_end_threshold)%"
echo "Start charging: $(cat /sys/class/power_supply/BAT0/charge_control_start_threshold)%"

# Install systemd service for persistence across reboots
SERVICE_FILE="/etc/systemd/system/battery-thresholds.service"

cat > "$SERVICE_FILE" << 'EOF'
[Unit]
Description=Set ThinkPad battery charge thresholds
After=multi-user.target

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/bin/bash -c 'echo 50 > /sys/class/power_supply/BAT0/charge_control_end_threshold && echo 45 > /sys/class/power_supply/BAT0/charge_control_start_threshold'

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable battery-thresholds.service
systemctl start battery-thresholds.service

echo ""
echo "=== Done ==="
echo "Thresholds active and persistent across reboots."
echo "  Stop charging at  : 50%"
echo "  Resume charging at: 45%"
systemctl status battery-thresholds.service --no-pager
