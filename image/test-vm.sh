#!/bin/sh
# Boots the x86_64 VM assets with QEMU/KVM the way the macOS helper boots them (agent in TCP test mode)
# and checks the docker proxy, port forwarding, the control channel, clean poweroff and data persistence.
# Run in a container with /dev/kvm: test-vm.sh <dockyard-vm-*-x86_64.tar.gz>
set -eu
apk add -q qemu-system-x86_64 qemu-img docker-cli curl >/dev/null

work=/tmp/vm; mkdir -p $work; cd $work
tar -xzf "$1"
truncate -s 8G data.img

boot() {
  qemu-system-x86_64 -enable-kvm -cpu host -m 2048 -smp 2 -nographic -no-reboot \
    -kernel kernel -initrd initramfs \
    -append "console=ttyS0 root=/dev/vda rootfstype=ext4 modules=ext4,virtio_blk rw quiet dockyard.agent=tcp" \
    -drive file=root.img,if=virtio,format=raw -drive file=data.img,if=virtio,format=raw \
    -netdev user,id=n0,hostfwd=tcp:127.0.0.1:12375-:2375,hostfwd=tcp:127.0.0.1:12376-:2376,hostfwd=tcp:127.0.0.1:12377-:2377 \
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

control="$( (sleep 6) | nc -w 7 127.0.0.1 12377 | head -3)"
echo "$control" | grep -q '"type":"ports".*"port":8080' && echo "control reports port 8080" || { echo "FAIL: control: $control"; exit 1; }
echo "$control" | grep -q '"type":"memory"' && echo "control reports memory" || { echo "FAIL: no memory report"; exit 1; }

stop_vm

boot
docker image inspect nginx:alpine >/dev/null && echo "images persisted on the data disk"
stop_vm
echo PASS
