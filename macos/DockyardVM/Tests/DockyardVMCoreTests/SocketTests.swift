import Foundation
import XCTest
@testable import DockyardVMCore
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// A guest stand-in: every connector call gets one end of a socketpair; `guest` handles the other end.
final class FakeGuest {
    let guest: (UInt32, Int32) -> Void
    init(_ guest: @escaping (UInt32, Int32) -> Void) { self.guest = guest }

    var connector: GuestConnector {
        { port, completion in
            var fds: [Int32] = [0, 0]
            #if canImport(Darwin)
            let type = SOCK_STREAM
            #else
            let type = Int32(SOCK_STREAM.rawValue)
            #endif
            guard socketpair(AF_UNIX, type, 0, &fds) == 0 else { completion(nil); return }
            let other = fds[1]
            Thread.detachNewThread { self.guest(port, other) }
            completion(fds[0])
        }
    }
}

func readAll(_ fd: Int32) -> Data {
    var data = Data()
    var buffer = [UInt8](repeating: 0, count: 4096)
    while true {
        let n = buffer.withUnsafeMutableBytes { Glue.read(fd, $0.baseAddress, $0.count) }
        if n <= 0 { break }
        data.append(contentsOf: buffer.prefix(n))
    }
    return data
}

func readLine(_ fd: Int32) -> String {
    var bytes: [UInt8] = []
    var one: UInt8 = 0
    while Glue.read(fd, &one, 1) == 1, one != 0x0A { bytes.append(one) }
    return String(decoding: bytes, as: UTF8.self)
}

/// Echo: reads until the client half-closes, then answers with everything it got.
func echo(_ fd: Int32) {
    let data = readAll(fd)
    _ = Sockets.writeAll(fd, data)
    close(fd)
}

final class SocketTests: XCTestCase {
    func testDockerSocketForwardsAndKeepsReplyAfterHalfClose() throws {
        let path = NSTemporaryDirectory() + "dy-\(UUID().uuidString.prefix(8)).sock"
        let server = try DockerSocketServer(path: path, connector: FakeGuest { port, fd in
            XCTAssertEqual(port, VsockPort.docker)
            echo(fd)
        }.connector)
        defer { server.stop(); unlink(path) }

        let client = try Sockets.connectUnix(path: path)
        XCTAssertTrue(Sockets.writeAll(client, Data("GET /_ping HTTP/1.0\r\n\r\n".utf8)))
        shutdown(client, Int32(SHUT_WR))
        XCTAssertEqual(String(decoding: readAll(client), as: UTF8.self), "GET /_ping HTTP/1.0\r\n\r\n")
        close(client)
    }

    func testTcpPortIsForwardedWithHeader() throws {
        let headers = Headers()
        let forwarder = PortForwarder(connector: FakeGuest { port, fd in
            XCTAssertEqual(port, VsockPort.forward)
            headers.append(readLine(fd))
            echo(fd)
        }.connector, log: { _ in })
        let port = 40000 + Int.random(in: 0..<20000)
        forwarder.apply([PublishedPort(protocol: "tcp", hostIp: "127.0.0.1", port: port)])
        defer { forwarder.stopAll() }
        XCTAssertEqual(forwarder.open.count, 1)

        let client = socket(AF_INET, Int32(streamType), 0)
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = UInt16(port).bigEndian
        inet_pton(AF_INET, "127.0.0.1", &address.sin_addr)
        let connected = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(client, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        XCTAssertEqual(connected, 0)
        XCTAssertTrue(Sockets.writeAll(client, Data("hello".utf8)))
        shutdown(client, Int32(SHUT_WR))
        XCTAssertEqual(String(decoding: readAll(client), as: UTF8.self), "hello")
        close(client)
        XCTAssertEqual(headers.values, ["tcp \(port)"])

        forwarder.apply([])
        XCTAssertTrue(forwarder.open.isEmpty)
    }

    func testControlClientReceivesPortsAndSendsStop() throws {
        let received = expectation(description: "ports")
        let stopped = expectation(description: "stop")
        let client = ControlClient(connector: FakeGuest { port, fd in
            XCTAssertEqual(port, VsockPort.control)
            _ = Sockets.writeAll(fd, Data("{\"type\":\"ports\",\"ports\":[{\"protocol\":\"tcp\",\"hostIp\":\"0.0.0.0\",\"port\":8080}]}\n".utf8))
            if readLine(fd) == "{\"type\":\"stop\"}" { stopped.fulfill() }
            close(fd)
        }.connector) { message in
            if message.type == "ports", message.ports?.first?.port == 8080 { received.fulfill() }
        }
        client.start()
        wait(for: [received], timeout: 5)
        client.send(.stop)
        wait(for: [stopped], timeout: 5)
        client.stop()
    }
}

final class Headers {
    let lock = NSLock()
    var values: [String] = []
    func append(_ value: String) { lock.lock(); values.append(value); lock.unlock() }
}

final class HostDialTests: XCTestCase {
    func testHostDialConnectsThePortTheGuestAsksFor() throws {
        var guest: [Int32] = [0, 0]
        var service: [Int32] = [0, 0]
        XCTAssertEqual(socketpair(AF_UNIX, streamType, 0, &guest), 0)
        XCTAssertEqual(socketpair(AF_UNIX, streamType, 0, &service), 0)
        let asked = expectation(description: "dialed")
        HostDial.serve(guest[1]) { port in
            XCTAssertEqual(port, 5173)
            asked.fulfill()
            return service[1]
        }
        XCTAssertTrue(Sockets.writeAll(guest[0], Data("tcp 5173\nhello".utf8)))
        wait(for: [asked], timeout: 5)
        var bytes = [UInt8](repeating: 0, count: 5)
        var total = 0
        while total < 5 {
            let n = bytes.withUnsafeMutableBytes { read(service[0], $0.baseAddress! + total, 5 - total) }
            if n <= 0 { break }
            total += n
        }
        XCTAssertEqual(String(decoding: bytes, as: UTF8.self), "hello")
        close(guest[0])
        close(service[0])
    }

    func testHostDialClosesOnANonsenseHeaderWithoutDialing() throws {
        for header in ["udp 53\n", "tcp 0\n", "tcp 99999\n", "tcp 80 extra\n", "/etc/passwd\n"] {
            var guest: [Int32] = [0, 0]
            XCTAssertEqual(socketpair(AF_UNIX, streamType, 0, &guest), 0)
            HostDial.serve(guest[1]) { _ in
                XCTFail("dialed for \(header)")
                return nil
            }
            XCTAssertTrue(Sockets.writeAll(guest[0], Data(header.utf8)))
            var byte: UInt8 = 0
            XCTAssertEqual(read(guest[0], &byte, 1), 0)
            close(guest[0])
        }
    }
}
