import Foundation
#if canImport(Darwin)
import Darwin
let streamType = SOCK_STREAM
let datagramType = SOCK_DGRAM
#else
import Glibc
let streamType = Int32(SOCK_STREAM.rawValue)
let datagramType = Int32(SOCK_DGRAM.rawValue)
#endif

public struct SocketError: Error, CustomStringConvertible {
    public let operation: String
    public let code: Int32
    public var description: String { "\(operation): \(String(cString: strerror(code)))" }
}

@discardableResult
func check(_ result: Int32, _ operation: String) throws -> Int32 {
    if result < 0 { throw SocketError(operation: operation, code: errno) }
    return result
}

/// Opens a connection into the guest on a vsock port; nil when the guest is not reachable.
public typealias GuestConnector = (UInt32, @escaping (Int32?) -> Void) -> Void

public enum Sockets {
    public static func listenUnix(path: String) throws -> Int32 {
        unlink(path)
        let fd = try check(socket(AF_UNIX, streamType, 0), "socket")
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        let bytes = Array(path.utf8)
        guard bytes.count < MemoryLayout.size(ofValue: address.sun_path) else {
            close(fd)
            throw SocketError(operation: "socket path too long", code: ENAMETOOLONG)
        }
        withUnsafeMutableBytes(of: &address.sun_path) { buffer in
            buffer.copyBytes(from: bytes)
            buffer[bytes.count] = 0
        }
        try withUnsafePointer(to: &address) {
            try $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                _ = try check(bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)), "bind \(path)")
            }
        }
        chmod(path, 0o600)
        try check(listen(fd, 64), "listen")
        return fd
    }

    public static func connectUnix(path: String) throws -> Int32 {
        let fd = try check(socket(AF_UNIX, streamType, 0), "socket")
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        withUnsafeMutableBytes(of: &address.sun_path) { buffer in
            let bytes = Array(path.utf8)
            buffer.copyBytes(from: bytes)
            buffer[bytes.count] = 0
        }
        do {
            try withUnsafePointer(to: &address) {
                try $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    _ = try check(connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)), "connect \(path)")
                }
            }
        } catch {
            close(fd)
            throw error
        }
        return fd
    }

    /// An IPv6 host listens on IPv6 only, so it can sit next to the IPv4 listener for the same port.
    public static func listenInet(host: String, port: Int, datagram: Bool) throws -> Int32 {
        if host.contains(":") { return try listenInet6(host: host, port: port, datagram: datagram) }
        let fd = try check(socket(AF_INET, datagram ? datagramType : streamType, 0), "socket")
        var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = UInt16(port).bigEndian
        guard inet_pton(AF_INET, host, &address.sin_addr) == 1 else {
            close(fd)
            throw SocketError(operation: "invalid address \(host)", code: EINVAL)
        }
        do {
            try withUnsafePointer(to: &address) {
                try $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    _ = try check(bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)), "bind \(host):\(port)")
                }
            }
            if !datagram { try check(listen(fd, 128), "listen") }
        } catch {
            close(fd)
            throw error
        }
        return fd
    }

    static func listenInet6(host: String, port: Int, datagram: Bool) throws -> Int32 {
        let fd = try check(socket(AF_INET6, datagram ? datagramType : streamType, 0), "socket")
        var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, Int32(IPPROTO_IPV6), IPV6_V6ONLY, &yes, socklen_t(MemoryLayout<Int32>.size))
        var address = sockaddr_in6()
        address.sin6_family = sa_family_t(AF_INET6)
        address.sin6_port = UInt16(port).bigEndian
        guard inet_pton(AF_INET6, host, &address.sin6_addr) == 1 else {
            close(fd)
            throw SocketError(operation: "invalid address \(host)", code: EINVAL)
        }
        do {
            try withUnsafePointer(to: &address) {
                try $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    _ = try check(bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in6>.size)), "bind [\(host)]:\(port)")
                }
            }
            if !datagram { try check(listen(fd, 128), "listen") }
        } catch {
            close(fd)
            throw error
        }
        return fd
    }

    public static func connectLoopback(port: Int) -> Int32? {
        connectV4Loopback(port: port) ?? connectV6Loopback(port: port)
    }

    static func connectV4Loopback(port: Int) -> Int32? {
        let fd = socket(AF_INET, streamType, 0)
        guard fd >= 0 else { return nil }
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = UInt16(port).bigEndian
        inet_pton(AF_INET, "127.0.0.1", &address.sin_addr)
        let result = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        if result == 0 { return fd }
        close(fd)
        return nil
    }

    static func connectV6Loopback(port: Int) -> Int32? {
        let fd = socket(AF_INET6, streamType, 0)
        guard fd >= 0 else { return nil }
        var address = sockaddr_in6()
        address.sin6_family = sa_family_t(AF_INET6)
        address.sin6_port = UInt16(port).bigEndian
        inet_pton(AF_INET6, "::1", &address.sin6_addr)
        let result = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in6>.size)) }
        }
        if result == 0 { return fd }
        close(fd)
        return nil
    }

    /// Accepts on a background thread until the listener is shut down.
    public static func acceptLoop(_ listener: Int32, _ handle: @escaping (Int32) -> Void) {
        Thread.detachNewThread {
            while true {
                let client = accept(listener, nil, nil)
                if client >= 0 {
                    handle(client)
                } else if errno != EINTR {
                    return
                }
            }
        }
    }

    public static func stop(_ listener: Int32) {
        shutdown(listener, Int32(SHUT_RDWR))
        close(listener)
    }

    public static func writeAll(_ fd: Int32, _ data: Data) -> Bool {
        data.withUnsafeBytes { buffer in
            var offset = 0
            while offset < buffer.count {
                let written = write(fd, buffer.baseAddress! + offset, buffer.count - offset)
                if written <= 0 { return false }
                offset += written
            }
            return true
        }
    }

    /// Copies both ways; an end-of-stream on one side is passed on as a half-close, both close when both directions end.
    public static func pump(_ a: Int32, _ b: Int32, done: (() -> Void)? = nil) {
        let group = DispatchGroup()
        for (from, to) in [(a, b), (b, a)] {
            group.enter()
            Thread.detachNewThread {
                copy(from: from, to: to)
                group.leave()
            }
        }
        group.notify(queue: .global()) {
            close(a)
            close(b)
            done?()
        }
    }

    static func copy(from: Int32, to: Int32) {
        var buffer = [UInt8](repeating: 0, count: 64 * 1024)
        loop: while true {
            let count = buffer.withUnsafeMutableBytes { read(from, $0.baseAddress, $0.count) }
            if count <= 0 { break }
            var offset = 0
            while offset < count {
                let written = buffer.withUnsafeBytes { write(to, $0.baseAddress! + offset, count - offset) }
                if written <= 0 { break loop }
                offset += written
            }
        }
        shutdown(to, Int32(SHUT_WR))
    }
}

/// A connection the guest opened to reach a service on the Mac: host.docker.internal.
public enum HostDial {
    /// Reads "tcp <port>\n" from the guest and connects it to that port on this Mac's loopback, over IPv4 or IPv6
    /// because dev servers often bind only one of them. Nothing but loopback is reachable this way.
    public static func serve(_ guest: Int32, connect: @escaping (Int) -> Int32? = Sockets.connectLoopback) {
        Thread.detachNewThread {
            guard let port = readPort(guest), !refused.contains(port), let host = connect(port) else {
                close(guest)
                return
            }
            Sockets.pump(guest, host)
        }
    }

    /// Docker's own TCP ports and Dockyard's, as the agent refuses them on Linux and Windows: they are for this Mac, not
    /// for any image a container runs. 15353 answers container domains to macOS.
    static let refused: Set<Int> = [2353, 2354, 2375, 2376, 2377, 2378, 2379, 2380, 2381, 2382, 2383, 15353]

    static func readPort(_ fd: Int32) -> Int? {
        var line = [UInt8]()
        var byte: UInt8 = 0
        while line.count < 16 {
            guard Glue.read(fd, &byte, 1) == 1 else { return nil }
            if byte == 0x0A {
                let parts = String(decoding: line, as: UTF8.self).split(separator: " ")
                guard parts.count == 2, parts[0] == "tcp", let port = Int(parts[1]), (1...65535).contains(port) else { return nil }
                return port
            }
            line.append(byte)
        }
        return nil
    }
}

/// Exposes the guest's docker socket on the Mac.
public final class DockerSocketServer {
    let listener: Int32

    public init(path: String, guestPort: UInt32 = VsockPort.docker, connector: @escaping GuestConnector) throws {
        listener = try Sockets.listenUnix(path: path)
        Sockets.acceptLoop(listener) { client in
            connector(guestPort) { guest in
                guard let guest else { close(client); return }
                Sockets.pump(client, guest)
            }
        }
    }

    public func stop() { Sockets.stop(listener) }
}

/// Listens on the Mac for every port the guest publishes and pipes connections through the agent.
public final class PortForwarder {
    let connector: GuestConnector
    let log: (String) -> Void
    let lock = NSLock()
    var listeners: [PublishedPort: [Int32]] = [:]

    public init(connector: @escaping GuestConnector, log: @escaping (String) -> Void) {
        self.connector = connector
        self.log = log
    }

    public var open: Set<PublishedPort> {
        lock.lock(); defer { lock.unlock() }
        return Set(listeners.keys)
    }

    public func apply(_ desired: [PublishedPort]) {
        lock.lock(); defer { lock.unlock() }
        let change = PortTable.reconcile(open: Set(listeners.keys), desired: desired)
        for port in change.close {
            listeners.removeValue(forKey: port)?.forEach(Sockets.stop)
            log("stopped forwarding \(port.protocol)/\(port.port)")
        }
        for port in change.open {
            let wildcard = port.hostIp == "0.0.0.0"
            let datagram = port.protocol == "udp"
            let fd: Int32
            do {
                fd = try Sockets.listenInet(host: wildcard ? "0.0.0.0" : "127.0.0.1", port: port.port, datagram: datagram)
            } catch {
                log("cannot forward \(port.protocol)/\(port.port): \(error)")
                continue
            }
            var fds = [fd]
            // [::1] too: a URL with it, or a client that tries IPv6 first and does not fall back, finds the port. What
            // already holds the IPv6 side keeps it, and IPv4 still forwards.
            let v6 = wildcard ? "::" : "::1"
            do {
                fds.append(try Sockets.listenInet(host: v6, port: port.port, datagram: datagram))
            } catch {
                log("\(port.protocol)/\(port.port) on [\(v6)] stays with what holds it: \(error)")
            }
            listeners[port] = fds
            for listener in fds {
                if datagram { serveUdp(listener, port) } else { serveTcp(listener, port) }
            }
            log("forwarding \(port.protocol)/\(port.port) on \(port.hostIp)")
        }
    }

    public func stopAll() { apply([]) }

    /// Answers this Mac's lookups for container domains: the agent's DNS on 53 in the guest, on a high port on loopback
    /// that needs no privileges, and that /etc/resolver names so that no other name comes here.
    public func serveDns(hostPort: Int) throws {
        let fd = try Sockets.listenInet(host: "127.0.0.1", port: hostPort, datagram: true)
        serveUdp(fd, PublishedPort(protocol: "udp", hostIp: "127.0.0.1", port: 53))
    }

    func serveTcp(_ listener: Int32, _ port: PublishedPort) {
        Sockets.acceptLoop(listener) { [connector] client in
            connector(VsockPort.forward) { guest in
                guard let guest, Sockets.writeAll(guest, Data(port.forwardHeader.utf8)) else {
                    close(client)
                    if let guest { close(guest) }
                    return
                }
                Sockets.pump(client, guest)
            }
        }
    }

    func serveUdp(_ listener: Int32, _ port: PublishedPort) {
        let sessions = UdpSessions(listener: listener, port: port, connector: connector)
        Thread.detachNewThread { sessions.run() }
    }
}

final class UdpSessions {
    let listener: Int32
    let port: PublishedPort
    let connector: GuestConnector
    let lock = NSLock()
    var streams: [Data: Int32] = [:]

    init(listener: Int32, port: PublishedPort, connector: @escaping GuestConnector) {
        self.listener = listener
        self.port = port
        self.connector = connector
    }

    func run() {
        var buffer = [UInt8](repeating: 0, count: 65536)
        while true {
            var from = sockaddr_storage()
            var length = socklen_t(MemoryLayout<sockaddr_storage>.size)
            let count = withUnsafeMutablePointer(to: &from) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { address in
                    buffer.withUnsafeMutableBytes { recvfrom(listener, $0.baseAddress, $0.count, 0, address, &length) }
                }
            }
            if count < 0 { return }
            let client = withUnsafeBytes(of: &from) { Data($0.prefix(Int(length))) }
            let frame = UdpFrame.encode(Data(buffer.prefix(count)))
            lock.lock()
            let existing = streams[client]
            lock.unlock()
            if let existing {
                _ = Sockets.writeAll(existing, frame)
                continue
            }
            connector(VsockPort.forward) { [self] guest in
                guard let guest, Sockets.writeAll(guest, Data(port.forwardHeader.utf8)), Sockets.writeAll(guest, frame) else { return }
                lock.lock(); streams[client] = guest; lock.unlock()
                Thread.detachNewThread { self.replies(from: guest, to: client) }
            }
        }
    }

    func replies(from guest: Int32, to client: Data) {
        var pending = Data()
        var buffer = [UInt8](repeating: 0, count: 65536)
        while true {
            let count = buffer.withUnsafeMutableBytes { read(guest, $0.baseAddress, $0.count) }
            if count <= 0 { break }
            pending.append(contentsOf: buffer.prefix(count))
            while let datagram = UdpFrame.decode(&pending) {
                datagram.withUnsafeBytes { payload in
                    client.withUnsafeBytes { address in
                        _ = sendto(listener, payload.baseAddress, payload.count, 0,
                                   address.baseAddress!.assumingMemoryBound(to: sockaddr.self), socklen_t(client.count))
                    }
                }
            }
        }
        lock.lock(); streams[client] = nil; lock.unlock()
        close(guest)
    }
}

/// Talks to dockyard-agent's control port: ports and memory in, clock and stop out. Reconnects while the VM runs.
public final class ControlClient {
    let connector: GuestConnector
    let onMessage: (ControlMessage) -> Void
    let lock = NSLock()
    var fd: Int32 = -1
    var running = true

    public init(connector: @escaping GuestConnector, onMessage: @escaping (ControlMessage) -> Void) {
        self.connector = connector
        self.onMessage = onMessage
    }

    public func start() { connect() }

    public func send(_ message: ControlMessage) {
        lock.lock(); let current = fd; lock.unlock()
        if current >= 0 { _ = Sockets.writeAll(current, message.line()) }
    }

    public func stop() {
        lock.lock(); running = false; let current = fd; fd = -1; lock.unlock()
        if current >= 0 { shutdown(current, Int32(SHUT_RDWR)); close(current) }
    }

    func connect() {
        connector(VsockPort.control) { [self] guest in
            lock.lock(); let active = running; lock.unlock()
            guard active else { if let guest { close(guest) }; return }
            guard let guest else {
                DispatchQueue.global().asyncAfter(deadline: .now() + 0.5) { self.connect() }
                return
            }
            lock.lock(); fd = guest; lock.unlock()
            Thread.detachNewThread { self.read(guest) }
        }
    }

    func read(_ guest: Int32) {
        var lines = LineBuffer()
        var buffer = [UInt8](repeating: 0, count: 16 * 1024)
        while true {
            let count = buffer.withUnsafeMutableBytes { Glue.read(guest, $0.baseAddress, $0.count) }
            if count <= 0 { break }
            for line in lines.append(Data(buffer.prefix(count))) {
                if let message = try? JSONDecoder().decode(ControlMessage.self, from: Data(line.utf8)) { onMessage(message) }
            }
        }
        lock.lock(); let reconnect = running && fd == guest; if fd == guest { fd = -1 }; lock.unlock()
        close(guest)
        if reconnect { DispatchQueue.global().asyncAfter(deadline: .now() + 0.5) { self.connect() } }
    }
}

enum Glue {
    static func read(_ fd: Int32, _ buffer: UnsafeMutableRawPointer?, _ count: Int) -> Int {
        #if canImport(Darwin)
        Darwin.read(fd, buffer, count)
        #else
        Glibc.read(fd, buffer, count)
        #endif
    }
}

/// ~/.dockyard/run/vm.sock: "status" and "stop" for the tray.
public final class ControlServer {
    let listener: Int32

    public init(path: String, status: @escaping () -> String, stop: @escaping () -> Void) throws {
        try? FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
        listener = try Sockets.listenUnix(path: path)
        Sockets.acceptLoop(listener) { client in
            var lines = LineBuffer()
            var buffer = [UInt8](repeating: 0, count: 1024)
            let count = buffer.withUnsafeMutableBytes { Glue.read(client, $0.baseAddress, $0.count) }
            let command = count > 0 ? lines.append(Data(buffer.prefix(count)) + Data([0x0A])).first ?? "" : ""
            switch command.trimmingCharacters(in: .whitespaces) {
            case "status": _ = Sockets.writeAll(client, Data((status() + "\n").utf8))
            case "stop":
                _ = Sockets.writeAll(client, Data("stopping\n".utf8))
                stop()
            default: _ = Sockets.writeAll(client, Data("unknown command\n".utf8))
            }
            close(client)
        }
    }

    public func stop() { Sockets.stop(listener) }
}
