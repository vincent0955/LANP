#!/usr/bin/env bash
#
# Builds the GameDashboard WSL2 distro tarball (docs/docker-migration.md →
# Part 2): Alpine minirootfs + static Docker Engine binaries + a wsl.conf boot
# command that starts dockerd on loopback TCP 2375.
#
# Run on Linux (CI or WSL) as root (or under fakeroot) so file ownership in
# the tarball comes out as root:root:
#
#   sudo scripts/runtime/build-wsl-distro.sh [output.tar.gz]
#
# The resulting tarball is what `wsl --import gamedashboard <dir> <tarball>`
# consumes; the app downloads it on first run (keeps the installer slim).
set -euo pipefail

# Pinned versions — bump deliberately, and bump RUNTIME_VERSION whenever the
# produced image changes so the app can offer a re-import.
ALPINE_VERSION="${ALPINE_VERSION:-3.21.3}"
DOCKER_VERSION="${DOCKER_VERSION:-27.5.1}"
RUNTIME_VERSION="${RUNTIME_VERSION:-4}"

ALPINE_MAJOR_MINOR="${ALPINE_VERSION%.*}"
ALPINE_URL="https://dl-cdn.alpinelinux.org/alpine/v${ALPINE_MAJOR_MINOR}/releases/x86_64/alpine-minirootfs-${ALPINE_VERSION}-x86_64.tar.gz"
DOCKER_URL="https://download.docker.com/linux/static/stable/x86_64/docker-${DOCKER_VERSION}.tgz"
APK_TOOLS_URL="https://dl-cdn.alpinelinux.org/alpine/v${ALPINE_MAJOR_MINOR}/main/x86_64"

OUTPUT="${1:-gamedashboard-wsl-rootfs.tar.gz}"
WORKDIR="$(mktemp -d)"
ROOTFS="$WORKDIR/rootfs"
trap 'rm -rf "$WORKDIR"' EXIT

echo "==> Downloading Alpine minirootfs $ALPINE_VERSION"
mkdir -p "$ROOTFS"
curl -fsSL "$ALPINE_URL" | tar -xz -C "$ROOTFS"

echo "==> Downloading static Docker Engine $DOCKER_VERSION"
curl -fsSL "$DOCKER_URL" | tar -xz -C "$WORKDIR"
install -d "$ROOTFS/usr/local/bin"
install -m 0755 "$WORKDIR"/docker/* "$ROOTFS/usr/local/bin/"

echo "==> Installing iptables + ca-certificates into the rootfs"
# dockerd needs iptables inside the VM for container NAT/port publishing.
# apk can install into a foreign root without chroot, using the rootfs's own
# statically-capable apk configuration.
cp /etc/resolv.conf "$ROOTFS/etc/resolv.conf" 2>/dev/null || true
apk_install() {
    apk --root "$ROOTFS" --arch x86_64 \
        --repository "https://dl-cdn.alpinelinux.org/alpine/v${ALPINE_MAJOR_MINOR}/main" \
        --repository "https://dl-cdn.alpinelinux.org/alpine/v${ALPINE_MAJOR_MINOR}/community" \
        --allow-untrusted --no-cache add "$@"
}
if command -v apk >/dev/null 2>&1; then
    apk_install iptables ip6tables ca-certificates
else
    # No host apk (e.g. Ubuntu CI): use the rootfs's apk via proot-less chroot.
    chroot "$ROOTFS" /sbin/apk --no-cache add iptables ip6tables ca-certificates
fi

echo "==> Writing engine configuration"
install -d "$ROOTFS/etc/docker"
cat > "$ROOTFS/etc/docker/daemon.json" <<'EOF'
{
  "hosts": ["tcp://127.0.0.1:2375"],
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "3" },
  "userland-proxy-path": "/usr/local/bin/docker-proxy"
}
EOF

cat > "$ROOTFS/usr/local/bin/start-dockerd.sh" <<'EOF'
#!/bin/sh
# Started by WSL on VM boot (wsl.conf [boot] command). Idempotent: WSL runs
# the boot command on every `wsl -d gamedashboard` entry after a shutdown.
#
# PATH is set explicitly: however this script is invoked (boot command vs.
# direct `wsl -d gamedashboard <path>`), dockerd's own env must include
# /usr/local/bin so its managed-containerd supervisor can find `containerd`
# via exec.LookPath — leaving this to an inherited PATH is not reliable.
export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"

if ! pidof dockerd >/dev/null 2>&1; then
    mkdir -p /var/log
    nohup /usr/local/bin/dockerd --config-file /etc/docker/daemon.json \
        >> /var/log/dockerd.log 2>&1 &
fi

# WSL mirrored networking delivers Windows-loopback connections (the
# dashboard's readiness probe, RCON, local players on localhost:hostPort)
# into the VM on the loopback0 interface. Docker's PREROUTING DNAT would
# rewrite them to the container IP while their source stays 127.0.0.1, and
# the kernel drops such forwarded packets (route_localnet=0) — every
# connection would hang. Skipping DNAT for loopback0 traffic hands it to
# docker-proxy instead, which serves loopback clients exactly like
# WSL-internal ones. LAN/internet players arrive on eth0 and keep using the
# DNAT path. The rule must sit above the DOCKER jump dockerd installs, so
# wait until dockerd has built its NAT chains before inserting.
nohup sh -c '
    i=0
    while [ "$i" -lt 120 ]; do
        if iptables -t nat -C PREROUTING -i loopback0 -j ACCEPT 2>/dev/null; then
            exit 0
        fi
        if iptables -t nat -S PREROUTING 2>/dev/null | grep -q -e "-j DOCKER"; then
            iptables -t nat -I PREROUTING 1 -i loopback0 -j ACCEPT
            exit 0
        fi
        sleep 1
        i=$((i + 1))
    done
' >/dev/null 2>&1 &
EOF
chmod 0755 "$ROOTFS/usr/local/bin/start-dockerd.sh"

cat > "$ROOTFS/etc/wsl.conf" <<'EOF'
[boot]
command = "/usr/local/bin/start-dockerd.sh"

[automount]
enabled = true

[interop]
enabled = false
appendWindowsPath = false
EOF

# Version marker the app reads to decide whether to offer a re-import
# (docs/docker-migration.md → Lifecycle).
echo "$RUNTIME_VERSION" > "$ROOTFS/etc/game-dashboard.io-runtime-version"

echo "==> Packing $OUTPUT"
tar -czf "$OUTPUT" -C "$ROOTFS" .
echo "==> Done: $OUTPUT (runtime version $RUNTIME_VERSION, docker $DOCKER_VERSION, alpine $ALPINE_VERSION)"
