# syntax=docker/dockerfile:1
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
COPY rootfs/ /
RUN chmod 0755 /usr/local/sbin/dockyard-init /usr/local/sbin/dockyard-update

FROM scratch AS export
COPY --from=rootfs / /
