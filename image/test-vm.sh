#!/bin/sh
# Boots the x86_64 VM assets with QEMU/KVM the way the macOS helper boots them (agent in TCP test mode)
# and checks the docker proxy, port forwarding, the control channel, clean poweroff and data persistence.
# Run in a container with /dev/kvm: test-vm.sh <dockyard-vm-*-x86_64.tar.gz>
set -eu
apk add -q qemu-system-x86_64 qemu-img docker-cli curl openssl >/dev/null

work=/tmp/vm; mkdir -p $work; cd $work
tar -xzf "$1"
truncate -s 8G data.img

boot() {
  qemu-system-x86_64 -enable-kvm -cpu host -m 2048 -smp 2 -nographic -no-reboot \
    -kernel kernel -initrd initramfs \
    -append "console=ttyS0 root=/dev/vda rootfstype=ext4 modules=ext4,virtio_blk rw quiet dockyard.agent=tcp" \
    -drive file=root.img,if=virtio,format=raw -drive file=data.img,if=virtio,format=raw \
    -netdev user,id=n0,hostfwd=tcp:127.0.0.1:12375-:2375,hostfwd=tcp:127.0.0.1:12376-:2376,hostfwd=tcp:127.0.0.1:12377-:2377,hostfwd=tcp:127.0.0.1:12379-:2379,hostfwd=tcp:127.0.0.1:12380-:80,hostfwd=tcp:127.0.0.1:12443-:443 \
    -device virtio-net-pci,netdev=n0 -device virtio-balloon-pci \
    -serial file:$work/console.log -monitor none > /dev/null 2>&1 &
  qemu=$!
  start=$(date +%s)
  until curl -sf http://127.0.0.1:12375/_ping >/dev/null 2>&1; do
    if [ $(( $(date +%s) - start )) -gt 180 ]; then echo "FAIL: docker API not ready"; tail -40 $work/console.log; exit 1; fi
    sleep 1
  done
  echo "docker API ready through the agent after $(( $(date +%s) - start ))s"
}

stop_vm() {
  printf '{"type":"stop"}\n' | nc -w 2 127.0.0.1 12377 >/dev/null 2>&1 || true
  i=0; while kill -0 $qemu 2>/dev/null && [ $i -lt 60 ]; do sleep 1; i=$((i + 1)); done
  if kill -0 $qemu 2>/dev/null; then echo "FAIL: VM did not power off after stop"; kill $qemu; exit 1; fi
  echo "VM powered off after stop (${i}s)"
}

export DOCKER_HOST=tcp://127.0.0.1:12375
boot
grep -m1 "dockyard-vm-init: ready" console.log || true
docker version --format 'engine {{.Server.Version}} ({{.Server.Os}}/{{.Server.Arch}})'
docker run --rm --privileged --pid=host alpine:3.24 nsenter -t 1 -m -- grep -q '^/dev/vdb /var/lib/docker ' /proc/mounts \
  && echo "docker stores its data on the data disk" || { echo "FAIL: /var/lib/docker is not on the data disk"; exit 1; }
[ "$(docker run --rm alpine:3.24 echo ok)" = ok ] && echo "container runs"

docker run -d --name web -p 8080:80 nginx:alpine >/dev/null
sleep 2
reply="$(printf 'tcp 8080\nGET / HTTP/1.0\r\nHost: x\r\n\r\n' | nc -w 5 127.0.0.1 12376 | head -1)"
case "$reply" in HTTP/1.1\ 200*) echo "port 8080 forwarded through the agent: $reply" ;; *) echo "FAIL: forward reply '$reply'"; exit 1 ;; esac

# A service on the machine that started the VM, on its loopback, as host.docker.internal should reach it.
(while :; do printf 'HTTP/1.0 200 OK\r\n\r\nfrom-the-host' | nc -l -s 127.0.0.1 -p 18080 >/dev/null; done) &
listener=$!
for args in "" "--network dockyard-e2e" "--network host"; do
  [ "$args" = "--network dockyard-e2e" ] && docker network create dockyard-e2e >/dev/null
  reply="$(docker run --rm $args alpine:3.24 wget -qO- -T 10 http://host.docker.internal:18080/ || true)"
  [ "$reply" = from-the-host ] && echo "host.docker.internal reaches the host (${args:-default network})" || { echo "FAIL: host.docker.internal with '$args': '$reply'"; exit 1; }
done
docker network rm dockyard-e2e >/dev/null
kill $listener 2>/dev/null || true
docker run --rm alpine:3.24 nslookup dl-cdn.alpinelinux.org >/dev/null && echo "other names resolve through the host's DNS" || { echo "FAIL: DNS"; exit 1; }

# What only the VM on a Mac otherwise uses goes through libc in a static binary; vsock itself cannot be tried here, and this can.
printf '{"type":"clock","unixNs":%s000000000}\n' "$(date +%s)" | nc -w 3 127.0.0.1 12377 >/dev/null || true
sleep 1
grep -aq 'clock set from host' console.log && echo "the agent reaches libc" || { echo "FAIL: the agent cannot call libc"; grep -a dockyard-agent console.log | tail -20; exit 1; }
docker run -d --name hostnet --network host alpine:3.24 sh -c 'while :; do printf "HTTP/1.0 200 OK\r\n\r\nfrom-host-network" | nc -l -p 8091 >/dev/null; done' >/dev/null
sleep 4
reply="$(printf 'tcp 8091\nGET / HTTP/1.0\r\n\r\n' | nc -w 5 127.0.0.1 12376 | tail -1)"
[ "$reply" = from-host-network ] && echo "a server in a --network host container is reachable through the agent" || { echo "FAIL: --network host: '$reply'"; exit 1; }
ports="$( (sleep 4) | nc -w 5 127.0.0.1 12377 | head -1)"
echo "$ports" | grep -q '"port":8091' && echo "control reports the --network host port" || { echo "FAIL: control: $ports"; exit 1; }
docker rm -f hostnet >/dev/null

# A domain by Host header through the agent's proxy, as the Mac reaches it on the VM's own address; web publishes 8080,
# and the proxy does not go through that.
sleep 2
reply="$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -H 'Host: web.dockyard.test' http://127.0.0.1:12380/)"
[ "$reply" = 200 ] && echo "a container answers by domain" || { echo "FAIL: domain over HTTP: '$reply'"; exit 1; }
reply="$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -H 'Host: gone.dockyard.test' http://127.0.0.1:12380/)"
[ "$reply" = 404 ] && echo "an unknown domain gets 404" || { echo "FAIL: unknown domain: '$reply'"; exit 1; }
[ "$(docker run --rm alpine:3.24 wget -qO- -T 10 http://web.dockyard.test/ | grep -ci nginx)" -gt 0 ] && echo "the domain works inside containers" || { echo "FAIL: domain inside a container"; exit 1; }
reply="$(curl -sk -o /dev/null -w '%{http_code}' --max-time 5 --resolve web.dockyard.test:12443:127.0.0.1 https://web.dockyard.test:12443/ || true)"
[ "$reply" = 000 ] && echo "no HTTPS before a certificate arrives" || { echo "FAIL: HTTPS without a certificate: '$reply'"; exit 1; }
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -keyout /tmp/k -out /tmp/c -days 2 -subj /CN=web.dockyard.test -addext subjectAltName=DNS:web.dockyard.test 2>/dev/null
tls="$(awk 'NF {printf "%s\\n", $0}' /tmp/c)"; key="$(awk 'NF {printf "%s\\n", $0}' /tmp/k)"
printf '{"op":"tls","cert":"%s","key":"%s"}\n' "$tls" "$key" | nc -w 3 127.0.0.1 12379 | grep -qv error && echo "the agent takes a certificate" || { echo "FAIL: certificate"; exit 1; }
reply="$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 --cacert /tmp/c --resolve web.dockyard.test:12443:127.0.0.1 https://web.dockyard.test:12443/)"
[ "$reply" = 200 ] && echo "a container answers over HTTPS, with that certificate" || { echo "FAIL: HTTPS: '$reply'"; exit 1; }
printf '{"op":"domains"}\n' | nc -w 3 127.0.0.1 12379 | grep -q '"https":true' && echo "the agent says HTTPS is on" || { echo "FAIL: https flag"; exit 1; }

control="$( (sleep 6) | nc -w 7 127.0.0.1 12377 | head -3)"
echo "$control" | grep -q '"type":"ports".*"port":8080' && echo "control reports port 8080" || { echo "FAIL: control: $control"; exit 1; }
echo "$control" | grep -q '"type":"memory"' && echo "control reports memory" || { echo "FAIL: no memory report"; exit 1; }

# kind on the VM's kernel, which the Mac's VM shares: KIND_TAG and KIND_SHA256 are the pins in src/Dockyard/Core/Kind.cs.
if [ -n "${KIND_TAG:-}" ]; then
  wget -qO /usr/local/bin/kind "https://github.com/kubernetes-sigs/kind/releases/download/$KIND_TAG/kind-linux-amd64"
  echo "$KIND_SHA256  /usr/local/bin/kind" | sha256sum -c -s || { echo "FAIL: kind does not match its pinned checksum"; exit 1; }
  chmod +x /usr/local/bin/kind
  docker rm -f web >/dev/null
  if ! kind create cluster --name e2e --wait 180s; then echo "FAIL: kind create cluster"; tail -40 console.log; exit 1; fi
  kind get clusters | grep -qx e2e && echo "kind lists the cluster" || { echo "FAIL: kind get clusters"; exit 1; }
  kind get kubeconfig --name e2e | grep -q 'server: https://127.0.0.1:' && echo "its kubeconfig points at the published API server" || { echo "FAIL: kubeconfig"; exit 1; }
  docker exec e2e-control-plane kubectl get nodes --no-headers | grep -q ' Ready ' && echo "the node is Ready on this kernel" || { echo "FAIL: node not Ready"; docker exec e2e-control-plane kubectl get nodes; exit 1; }
  docker exec e2e-control-plane kubectl -n kube-system wait --for=condition=Ready pod -l k8s-app=kube-dns --timeout=120s >/dev/null \
    && echo "cluster DNS runs" || { echo "FAIL: CoreDNS"; docker exec e2e-control-plane kubectl -n kube-system get pods; exit 1; }
  kind delete cluster --name e2e
  [ -z "$(docker ps -aq --filter label=io.x-k8s.kind.cluster=e2e)" ] && echo "deleting leaves no node behind" || { echo "FAIL: nodes left"; exit 1; }
fi

stop_vm

boot
docker image inspect nginx:alpine >/dev/null && echo "images persisted on the data disk"
stop_vm
echo PASS
