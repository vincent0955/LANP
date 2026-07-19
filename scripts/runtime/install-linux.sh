#!/usr/bin/env bash
#
# Linux runtime setup (docs/docker-migration.md → Part 2): installs native
# Docker Engine if the machine doesn't have one, and puts the current user in
# the docker group so the dashboard can reach /var/run/docker.sock without
# root. No VM involved — this is the easy platform.
#
#   sudo scripts/runtime/install-linux.sh [username]
#
# The dashboard's engine discovery probes unix:///var/run/docker.sock, so no
# app configuration is needed afterwards. Log out/in (or `newgrp docker`) for
# the group membership to take effect.
set -euo pipefail

TARGET_USER="${1:-${SUDO_USER:-$USER}}"

if [ "$(id -u)" -ne 0 ]; then
    echo "error: run with sudo (installing packages and editing group membership)." >&2
    exit 1
fi

if command -v docker >/dev/null 2>&1 && docker version >/dev/null 2>&1; then
    echo "==> Docker Engine already installed and running."
else
    echo "==> Installing Docker Engine via get.docker.com"
    curl -fsSL https://get.docker.com | sh
    systemctl enable --now docker 2>/dev/null || service docker start || true
fi

if id -nG "$TARGET_USER" | tr ' ' '\n' | grep -qx docker; then
    echo "==> $TARGET_USER is already in the docker group."
else
    echo "==> Adding $TARGET_USER to the docker group"
    usermod -aG docker "$TARGET_USER"
    echo "    Log out and back in (or run: newgrp docker) to apply."
fi

echo "==> Done. The dashboard will find the engine at unix:///var/run/docker.sock."
