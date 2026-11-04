import Foundation

public enum VsockPort {
    public static let docker: UInt32 = 2375
    public static let forward: UInt32 = 2376
    public static let control: UInt32 = 2377
}

/// Everything the helper needs to boot the VM; stored as ~/.dockyard/vm.json.
public struct VMSettings: Codable, Equatable {
    public var cpus: Int
    public var memoryMiB: UInt64
    public var kernel: String
    public var initramfs: String
    public var rootImage: String
    public var dataImage: String
    public var dataImageGiB: UInt64
    public var consoleLog: String
    public var dockerSocket: String
    public var controlSocket: String
    public var shares: [String: String]
    public var rosetta: Bool
    public var commandLine: String

    public static let gib: UInt64 = 1024 * 1024 * 1024

    /// Defaults: all cores, half the RAM capped at 8 GiB, the macOS folders bind mounts usually need.
    public static func defaults(home: String, assets: String, cores: Int, physicalMemory: UInt64) -> VMSettings {
        let dir = home + "/.dockyard"
        return VMSettings(
            cpus: max(1, cores),
            memoryMiB: min(physicalMemory / 2, 8 * gib) / (1024 * 1024),
            kernel: assets + "/kernel",
            initramfs: assets + "/initramfs",
            rootImage: dir + "/root.img",
            dataImage: dir + "/data.img",
            dataImageGiB: 64,
            consoleLog: dir + "/logs/vm-console.log",
            dockerSocket: dir + "/docker.sock",
            controlSocket: dir + "/run/vm.sock",
            shares: [
                "Users": "/Users",
                "Volumes": "/Volumes",
                "private_tmp": "/private/tmp",
                "private_var_folders": "/private/var/folders",
            ],
            rosetta: true,
            commandLine: "console=hvc0 root=/dev/vda rootfstype=ext4 modules=ext4,virtio_blk rw quiet")
    }

    public static func load(path: String) throws -> VMSettings {
        try JSONDecoder().decode(VMSettings.self, from: Data(contentsOf: URL(fileURLWithPath: path)))
    }

    public func save(path: String) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(self).write(to: URL(fileURLWithPath: path), options: .atomic)
    }
}

/// Decides how much memory the guest keeps, so memory it does not use goes back to macOS.
public struct BalloonPolicy {
    public let minimumMiB: UInt64
    public let maximumMiB: UInt64
    public let headroomMiB: UInt64
    public let lowWaterMiB: UInt64
    public let stepMiB: UInt64

    public init(maximumMiB: UInt64, minimumMiB: UInt64 = 1024, headroomMiB: UInt64 = 768, lowWaterMiB: UInt64 = 512, stepMiB: UInt64 = 256) {
        self.maximumMiB = maximumMiB
        self.minimumMiB = min(minimumMiB, maximumMiB)
        self.headroomMiB = headroomMiB
        self.lowWaterMiB = lowWaterMiB
        self.stepMiB = stepMiB
    }

    /// The guest reports MemAvailable inside its current allowance; returns a new allowance, or nil to keep it.
    public func target(currentMiB: UInt64, availableKb: UInt64) -> UInt64? {
        let availableMiB = availableKb / 1024
        let desired: UInt64
        if availableMiB < lowWaterMiB {
            desired = currentMiB + max(1024, lowWaterMiB - availableMiB + headroomMiB)
        } else {
            let used = currentMiB > availableMiB ? currentMiB - availableMiB : 0
            desired = used + headroomMiB
        }
        let clamped = min(max(desired, minimumMiB), maximumMiB)
        let delta = clamped > currentMiB ? clamped - currentMiB : currentMiB - clamped
        return delta >= stepMiB || (clamped > currentMiB && availableMiB < lowWaterMiB) ? clamped : nil
    }
}

public struct PublishedPort: Codable, Hashable {
    public var `protocol`: String
    public var hostIp: String
    public var port: Int

    public init(protocol: String, hostIp: String, port: Int) {
        self.protocol = `protocol`
        self.hostIp = hostIp
        self.port = port
    }

    public var forwardHeader: String { "\(`protocol`) \(port)\n" }
}

public enum PortTable {
    /// Listeners to open and close so the Mac exposes exactly the ports the guest publishes.
    public static func reconcile(open current: Set<PublishedPort>, desired: [PublishedPort]) -> (open: [PublishedPort], close: [PublishedPort]) {
        let wanted = Set(desired)
        let toOpen = wanted.subtracting(current).sorted { ($0.port, $0.protocol) < ($1.port, $1.protocol) }
        let toClose = current.subtracting(wanted).sorted { ($0.port, $0.protocol) < ($1.port, $1.protocol) }
        return (toOpen, toClose)
    }
}

/// Newline-delimited JSON shared with dockyard-agent.
public struct ControlMessage: Codable, Equatable {
    public var type: String
    public var ports: [PublishedPort]?
    public var totalKb: UInt64?
    public var availableKb: UInt64?
    public var unixNs: Int64?

    public init(type: String, ports: [PublishedPort]? = nil, totalKb: UInt64? = nil, availableKb: UInt64? = nil, unixNs: Int64? = nil) {
        self.type = type
        self.ports = ports
        self.totalKb = totalKb
        self.availableKb = availableKb
        self.unixNs = unixNs
    }

    public static func clock(now: Date) -> ControlMessage {
        ControlMessage(type: "clock", unixNs: Int64(now.timeIntervalSince1970 * 1_000_000_000))
    }

    public static let stop = ControlMessage(type: "stop")

    public func line() -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        var data = (try? encoder.encode(self)) ?? Data()
        data.append(0x0A)
        return data
    }
}

/// Splits a byte stream into lines.
public struct LineBuffer {
    var pending = Data()

    public init() {}

    public mutating func append(_ data: Data) -> [String] {
        pending.append(data)
        var lines: [String] = []
        while let index = pending.firstIndex(of: 0x0A) {
            let line = pending[pending.startIndex..<index]
            lines.append(String(decoding: line, as: UTF8.self))
            pending.removeSubrange(pending.startIndex...index)
        }
        return lines
    }
}

/// UDP datagrams over a stream: [length:2 big-endian][payload], as dockyard-agent expects.
public enum UdpFrame {
    public static func encode(_ payload: Data) -> Data {
        precondition(payload.count <= Int(UInt16.max))
        var frame = Data([UInt8(payload.count >> 8), UInt8(payload.count & 0xFF)])
        frame.append(payload)
        return frame
    }

    public static func decode(_ buffer: inout Data) -> Data? {
        guard buffer.count >= 2 else { return nil }
        let start = buffer.startIndex
        let length = Int(buffer[start]) << 8 | Int(buffer[start + 1])
        guard buffer.count >= 2 + length else { return nil }
        let payload = buffer.subdata(in: (start + 2)..<(start + 2 + length))
        buffer.removeSubrange(start..<(start + 2 + length))
        return payload
    }
}
