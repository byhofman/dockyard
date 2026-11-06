# syntax=docker/dockerfile:1
# Build context: the repository root (image/build.ps1, image/build-vm.ps1).
ARG ALPINE=3.24
FROM alpine:${ALPINE} AS rootfs
ARG DOCKER
ARG BUILDX
ARG COMPOSE
ARG TARGETARCH
ARG IMAGE_VERSION=0.0.0
RUN apk add --no-cache iptables ip6tables ca-certificates e2fsprogs \
 && addgroup -S docker
RUN set -eu; \
    case "$TARGETARCH" in amd64) a=x86_64; b=amd64; c=x86_64 ;; arm64) a=aarch64; b=arm64; c=aarch64 ;; *) exit 1 ;; esac; \
    wget -qO- "https://download.docker.com/linux/static/stable/$a/docker-$DOCKER.tgz" | tar -xz -C /usr/local/bin --strip-components=1; \
    mkdir -p /usr/local/lib/docker/cli-plugins; \
    wget -qO /usr/local/lib/docker/cli-plugins/docker-buildx "https://github.com/docker/buildx/releases/download/v$BUILDX/buildx-v$BUILDX.linux-$b"; \
    wget -qO /usr/local/lib/docker/cli-plugins/docker-compose "https://github.com/docker/compose/releases/download/v$COMPOSE/docker-compose-linux-$c"; \
    chmod +x /usr/local/lib/docker/cli-plugins/*; \
    printf 'VERSION=%s\nDOCKER_VERSION=%s\n' "$IMAGE_VERSION" "$DOCKER" > /etc/dockyard-release
COPY image/rootfs/ /
RUN chmod 0755 /usr/local/sbin/dockyard-init /usr/local/sbin/dockyard-update

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS agent
RUN apk add --no-cache clang build-base zlib-dev zlib-static openssl-dev openssl-libs-static cmake linux-headers
WORKDIR /src
COPY src/Dockyard.Engine/ src/Dockyard.Engine/
COPY src/Dockyard.Agent/ src/Dockyard.Agent/
RUN case "$(uname -m)" in x86_64) rid=linux-musl-x64 ;; aarch64) rid=linux-musl-arm64 ;; esac; \
    dotnet publish src/Dockyard.Agent/Dockyard.Agent.csproj -c Release -r "$rid" -o /agent

# What the app itself lays over an engine that already exists: the image is only imported once, on the first install.
FROM scratch AS guest-export
COPY image/rootfs/usr/local/sbin/ /usr/local/sbin/
COPY image/wsl/ /
COPY --from=agent /agent/dockyard-agent /usr/local/sbin/dockyard-agent

FROM scratch AS export
COPY --from=rootfs / /
COPY image/wsl/ /
COPY --from=agent /agent/dockyard-agent /usr/local/sbin/dockyard-agent

FROM rootfs AS vm
RUN apk add --no-cache linux-virt mkinitfs \
 && echo 'features="base ext4 virtio"' > /etc/mkinitfs/mkinitfs.conf \
 && mkinitfs -o /boot/initramfs-virt "$(ls /lib/modules)"
COPY image/vm/ /
COPY --from=agent /agent/dockyard-agent /usr/local/sbin/dockyard-agent
RUN chmod 0755 /usr/local/sbin/dockyard-vm-init /usr/local/sbin/dockyard-agent

FROM alpine:${ALPINE} AS vm-assets
RUN apk add --no-cache e2fsprogs gzip zstd
COPY --from=vm / /rootfs
COPY image/extract-kernel.sh /extract-kernel.sh
RUN set -eu; mkdir /assets; \
    sh /extract-kernel.sh /rootfs/boot/vmlinuz-virt /assets/kernel; \
    cp /rootfs/boot/initramfs-virt /assets/initramfs; \
    rm -f /rootfs/boot/vmlinuz-virt /rootfs/boot/initramfs-virt; \
    cp /rootfs/etc/dockyard-release /assets/dockyard-release; \
    mke2fs -q -t ext4 -L dockyard-root -d /rootfs /assets/root.img 1536M

FROM scratch AS vm-export
COPY --from=vm-assets /assets/ /
