import Foundation
import XCTest
@testable import DockyardVMCore

final class CoreTests: XCTestCase {
    func testDefaultsUseHalfTheMemoryCappedAt8GiB() {
        let small = VMSettings.defaults(home: "/Users/me", assets: "/app", cores: 8, physicalMemory: 8 * VMSettings.gib)
        XCTAssertEqual(small.memoryMiB, 4096)
        XCTAssertEqual(small.cpus, 8)
        let large = VMSettings.defaults(home: "/Users/me", assets: "/app", cores: 12, physicalMemory: 64 * VMSettings.gib)
        XCTAssertEqual(large.memoryMiB, 8192)
        XCTAssertEqual(large.dockerSocket, "/Users/me/.dockyard/docker.sock")
        XCTAssertEqual(large.shares["private_var_folders"], "/private/var/folders")
        XCTAssertTrue(large.commandLine.contains("console=hvc0"))
    }

    func testSettingsRoundTrip() throws {
        let path = NSTemporaryDirectory() + "dockyard-vm-\(UUID().uuidString).json"
        let settings = VMSettings.defaults(home: "/Users/me", assets: "/app", cores: 4, physicalMemory: 16 * VMSettings.gib)
        try settings.save(path: path)
        XCTAssertEqual(try VMSettings.load(path: path), settings)
        try? FileManager.default.removeItem(atPath: path)
    }

    func testBalloonShrinksToUsagePlusHeadroom() {
        let policy = BalloonPolicy(maximumMiB: 8192)
        XCTAssertEqual(policy.target(currentMiB: 8192, availableKb: 7000 * 1024), 1960)
    }

    func testBalloonNeverGoesBelowMinimum() {
        let policy = BalloonPolicy(maximumMiB: 8192)
        XCTAssertEqual(policy.target(currentMiB: 8192, availableKb: 8000 * 1024), 1024)
    }

    func testBalloonGrowsFastWhenGuestRunsLow() {
        let policy = BalloonPolicy(maximumMiB: 8192)
        XCTAssertEqual(policy.target(currentMiB: 2048, availableKb: 200 * 1024), 2048 + 312 + 768)
    }

    func testBalloonGrowthIsCappedAtMaximum() {
        let policy = BalloonPolicy(maximumMiB: 4096)
        XCTAssertEqual(policy.target(currentMiB: 4000, availableKb: 100 * 1024), 4096)
    }

    func testBalloonIgnoresSmallChanges() {
        let policy = BalloonPolicy(maximumMiB: 8192)
        XCTAssertNil(policy.target(currentMiB: 2048, availableKb: 700 * 1024))
    }

    func testPortTableReconciles() {
        let a = PublishedPort(protocol: "tcp", hostIp: "0.0.0.0", port: 8080)
        let b = PublishedPort(protocol: "udp", hostIp: "0.0.0.0", port: 53)
        let c = PublishedPort(protocol: "tcp", hostIp: "127.0.0.1", port: 5432)
        let result = PortTable.reconcile(open: [a, b], desired: [a, c])
        XCTAssertEqual(result.open, [c])
        XCTAssertEqual(result.close, [b])
        XCTAssertEqual(c.forwardHeader, "tcp 5432\n")
    }

    func testControlMessagesMatchTheAgent() throws {
        let json = #"{"type":"ports","ports":[{"protocol":"tcp","hostIp":"0.0.0.0","port":80}]}"#
        let message = try JSONDecoder().decode(ControlMessage.self, from: Data(json.utf8))
        XCTAssertEqual(message.ports, [PublishedPort(protocol: "tcp", hostIp: "0.0.0.0", port: 80)])

        let memory = try JSONDecoder().decode(ControlMessage.self, from: Data(#"{"type":"memory","totalKb":10,"availableKb":5}"#.utf8))
        XCTAssertEqual(memory.availableKb, 5)

        XCTAssertEqual(String(decoding: ControlMessage.stop.line(), as: UTF8.self), "{\"type\":\"stop\"}\n")
        let clock = ControlMessage.clock(now: Date(timeIntervalSince1970: 2))
        XCTAssertEqual(clock.unixNs, 2_000_000_000)
    }

    func testLineBufferJoinsChunks() {
        var buffer = LineBuffer()
        XCTAssertEqual(buffer.append(Data("{\"a\"".utf8)), [])
        XCTAssertEqual(buffer.append(Data(":1}\n{\"b\":2}\n{".utf8)), ["{\"a\":1}", "{\"b\":2}"])
    }

    func testUdpFramesRoundTrip() {
        var stream = UdpFrame.encode(Data([1, 2, 3]))
        stream.append(UdpFrame.encode(Data()))
        stream.append(Data([0, 5, 9]))
        XCTAssertEqual(UdpFrame.decode(&stream), Data([1, 2, 3]))
        XCTAssertEqual(UdpFrame.decode(&stream), Data())
        XCTAssertNil(UdpFrame.decode(&stream))
        XCTAssertEqual(stream.count, 3)
    }
}
