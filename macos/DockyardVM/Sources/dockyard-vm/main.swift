import DockyardVMCore
import Foundation

#if os(macOS)
import AppKit
import Virtualization

// dockyard-vm prepare --assets <dir> [--config <path>]   settings, root image clone, data disk
// dockyard-vm run [--config <path>]                       boots the VM in the foreground until SIGTERM or "stop"

// $HOME first, like the .NET app that starts this helper: the two must agree on where ~/.dockyard is.
let home = ProcessInfo.processInfo.environment["HOME"].flatMap { $0.isEmpty ? nil : $0 } ?? FileManager.default.homeDirectoryForCurrentUser.path
let arguments = CommandLine.arguments
func option(_ name: String) -> String? {
    guard let index = arguments.firstIndex(of: name), index + 1 < arguments.count else { return nil }
    return arguments[index + 1]
}
let configPath = option("--config") ?? home + "/.dockyard/vm.json"

func log(_ line: String) {
    FileHandle.standardError.write(Data("dockyard-vm: \(line)\n".utf8))
}

func fail(_ message: String) -> Never {
    log(message)
    exit(1)
}

func prepare(assets: String) throws {
    let fm = FileManager.default
    let dir = home + "/.dockyard"
    try fm.createDirectory(atPath: dir + "/logs", withIntermediateDirectories: true)
    try fm.createDirectory(atPath: dir + "/run", withIntermediateDirectories: true)

    var settings = (try? VMSettings.load(path: configPath))
        ?? VMSettings.defaults(home: home, assets: assets, cores: ProcessInfo.processInfo.processorCount,
                               physicalMemory: ProcessInfo.processInfo.physicalMemory)
    settings.kernel = assets + "/kernel"
    settings.initramfs = assets + "/initramfs"
    try settings.save(path: configPath)

    // The root image is cloned (APFS copy-on-write) whenever the bundled release changes; the data disk is never replaced.
    let bundledRelease = try String(contentsOfFile: assets + "/dockyard-release", encoding: .utf8)
    let installedRelease = try? String(contentsOfFile: dir + "/root-release", encoding: .utf8)
    if bundledRelease != installedRelease || !fm.fileExists(atPath: settings.rootImage) {
        try? fm.removeItem(atPath: settings.rootImage)
        try fm.copyItem(atPath: assets + "/root.img", toPath: settings.rootImage)
        try bundledRelease.write(toFile: dir + "/root-release", atomically: true, encoding: .utf8)
        log("root image updated: \(bundledRelease.replacingOccurrences(of: "\n", with: " "))")
    }

    if !fm.fileExists(atPath: settings.dataImage) {
        fm.createFile(atPath: settings.dataImage, contents: nil)
        let handle = try FileHandle(forWritingTo: URL(fileURLWithPath: settings.dataImage))
        try handle.truncate(atOffset: settings.dataImageGiB * VMSettings.gib)
        try handle.close()
        log("created \(settings.dataImageGiB) GiB sparse data disk")
    }
}

final class HostDialListener: NSObject, VZVirtioSocketListenerDelegate {
    func listener(_ listener: VZVirtioSocketListener, shouldAcceptNewConnection connection: VZVirtioSocketConnection,
                  from socketDevice: VZVirtioSocketDevice) -> Bool {
        let fd = dup(connection.fileDescriptor)
        connection.close()
        if fd >= 0 { HostDial.serve(fd) }
        return fd >= 0
    }
}

final class Machine: NSObject, VZVirtualMachineDelegate {
    let settings: VMSettings
    let queue = DispatchQueue(label: "dockyard.vm")
    let vm: VZVirtualMachine
    let balloon: BalloonPolicy
    var currentMiB: UInt64
    var dockerServer: DockerSocketServer?
    var controlServer: ControlServer?
    var forwarder: PortForwarder!
    var control: ControlClient!
    var stopping = false
    let hostDial = HostDialListener()
    let hostDialListener = VZVirtioSocketListener()

    init(settings: VMSettings) throws {
        self.settings = settings
        currentMiB = settings.memoryMiB
        balloon = BalloonPolicy(maximumMiB: settings.memoryMiB)

        let config = VZVirtualMachineConfiguration()
        config.platform = VZGenericPlatformConfiguration()
        config.cpuCount = max(VZVirtualMachineConfiguration.minimumAllowedCPUCount,
                              min(settings.cpus, VZVirtualMachineConfiguration.maximumAllowedCPUCount))
        config.memorySize = settings.memoryMiB * 1024 * 1024

        let boot = VZLinuxBootLoader(kernelURL: URL(fileURLWithPath: settings.kernel))
        boot.initialRamdiskURL = URL(fileURLWithPath: settings.initramfs)
        boot.commandLine = settings.commandLine
        config.bootLoader = boot

        config.storageDevices = [
            VZVirtioBlockDeviceConfiguration(attachment: try VZDiskImageStorageDeviceAttachment(
                url: URL(fileURLWithPath: settings.rootImage), readOnly: false)),
            VZVirtioBlockDeviceConfiguration(attachment: try VZDiskImageStorageDeviceAttachment(
                url: URL(fileURLWithPath: settings.dataImage), readOnly: false,
                cachingMode: .automatic, synchronizationMode: .fsync)),
        ]

        let network = VZVirtioNetworkDeviceConfiguration()
        network.attachment = VZNATNetworkDeviceAttachment()
        config.networkDevices = [network]

        var directories: [String: VZSharedDirectory] = [:]
        for (tag, path) in settings.shares where FileManager.default.fileExists(atPath: path) {
            directories[tag] = VZSharedDirectory(url: URL(fileURLWithPath: path), readOnly: false)
        }
        let share = VZVirtioFileSystemDeviceConfiguration(tag: "dockyard")
        share.share = VZMultipleDirectoryShare(directories: directories)
        var sharing: [VZDirectorySharingDeviceConfiguration] = [share]
        if settings.rosetta, VZLinuxRosettaDirectoryShare.availability == .installed {
            let rosetta = VZVirtioFileSystemDeviceConfiguration(tag: "rosetta")
            rosetta.share = try VZLinuxRosettaDirectoryShare()
            sharing.append(rosetta)
        }
        config.directorySharingDevices = sharing

        config.socketDevices = [VZVirtioSocketDeviceConfiguration()]
        config.memoryBalloonDevices = [VZVirtioTraditionalMemoryBalloonDeviceConfiguration()]
        config.entropyDevices = [VZVirtioEntropyDeviceConfiguration()]

        FileManager.default.createFile(atPath: settings.consoleLog, contents: nil)
        let console = VZVirtioConsoleDeviceSerialPortConfiguration()
        console.attachment = VZFileHandleSerialPortAttachment(
            fileHandleForReading: nil,
            fileHandleForWriting: try FileHandle(forWritingTo: URL(fileURLWithPath: settings.consoleLog)))
        config.serialPorts = [console]

        try config.validate()
        vm = VZVirtualMachine(configuration: config, queue: queue)
        super.init()
        vm.delegate = self
    }

    /// Connects to the agent; the descriptor handed out is a dup the caller owns.
    lazy var connector: GuestConnector = { [unowned self] port, completion in
        self.queue.async {
            guard let device = self.vm.socketDevices.first as? VZVirtioSocketDevice else { completion(nil); return }
            device.connect(toPort: port) { result in
                switch result {
                case .success(let connection):
                    let fd = dup(connection.fileDescriptor)
                    connection.close()
                    completion(fd >= 0 ? fd : nil)
                case .failure:
                    completion(nil)
                }
            }
        }
    }

    func start() {
        queue.async {
            self.vm.start { result in
                if case .failure(let error) = result { fail("start failed: \(error.localizedDescription)") }
                log("VM started (\(self.settings.cpus) CPUs, \(self.settings.memoryMiB) MiB)")
                self.hostDialListener.delegate = self.hostDial
                (self.vm.socketDevices.first as? VZVirtioSocketDevice)?.setSocketListener(self.hostDialListener, forPort: VsockPort.hostDial)
                DispatchQueue.main.async { self.attach() }
            }
        }
    }

    func attach() {
        forwarder = PortForwarder(connector: connector, log: log)
        control = ControlClient(connector: connector) { [weak self] message in self?.handle(message) }
        control.start()
        do {
            dockerServer = try DockerSocketServer(path: settings.dockerSocket, connector: connector)
            controlServer = try ControlServer(path: settings.controlSocket, status: { [weak self] in self?.status() ?? "stopped" },
                                              stop: { [weak self] in DispatchQueue.main.async { self?.stop() } })
        } catch {
            fail("cannot listen: \(error)")
        }
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            self?.control.send(.clock(now: Date()))
        }
        log("docker socket at \(settings.dockerSocket)")
    }

    func status() -> String {
        let state: String
        switch vm.state {
        case .running: state = "running"
        case .starting: state = "starting"
        case .stopping: state = "stopping"
        case .stopped: state = "stopped"
        default: state = "\(vm.state.rawValue)"
        }
        return "{\"state\":\"\(state)\",\"memoryMiB\":\(currentMiB),\"ports\":\(forwarder?.open.count ?? 0)}"
    }

    func handle(_ message: ControlMessage) {
        switch message.type {
        case "ports":
            forwarder.apply(message.ports ?? [])
        case "memory":
            guard let available = message.availableKb, let target = balloon.target(currentMiB: currentMiB, availableKb: available) else { return }
            currentMiB = target
            queue.async {
                (self.vm.memoryBalloonDevices.first as? VZVirtioTraditionalMemoryBalloonDevice)?.targetVirtualMachineMemorySize = target * 1024 * 1024
            }
        default:
            break
        }
        if message.type == "ports" { control.send(.clock(now: Date())) }
    }

    func stop() {
        guard !stopping else { return }
        stopping = true
        log("stopping")
        control?.send(.stop)
        DispatchQueue.main.asyncAfter(deadline: .now() + 20) {
            log("guest did not stop in time; forcing")
            self.queue.async { self.vm.stop { _ in DispatchQueue.main.async { self.finish(code: 0) } } }
        }
    }

    func finish(code: Int32) {
        forwarder?.stopAll()
        control?.stop()
        dockerServer?.stop()
        controlServer?.stop()
        unlink(settings.dockerSocket)
        unlink(settings.controlSocket)
        exit(code)
    }

    func guestDidStop(_ virtualMachine: VZVirtualMachine) {
        log("guest stopped")
        DispatchQueue.main.async { self.finish(code: 0) }
    }

    func virtualMachine(_ virtualMachine: VZVirtualMachine, didStopWithError error: Error) {
        log("VM stopped with error: \(error.localizedDescription)")
        DispatchQueue.main.async { self.finish(code: 1) }
    }
}

switch arguments.dropFirst().first {
case "prepare":
    guard let assets = option("--assets") else { fail("prepare needs --assets <dir>") }
    do { try prepare(assets: assets) } catch { fail("prepare failed: \(error)") }
case "run", nil:
    let settings: VMSettings
    do { settings = try VMSettings.load(path: configPath) } catch { fail("cannot read \(configPath): \(error); run prepare first") }
    let machine: Machine
    do { machine = try Machine(settings: settings) } catch { fail("invalid VM configuration: \(error)") }

    // Writing to a client that already hung up must fail with EPIPE, not kill the VM with every container in it.
    signal(SIGPIPE, SIG_IGN)
    signal(SIGTERM, SIG_IGN)
    signal(SIGINT, SIG_IGN)
    var signalSources: [DispatchSourceSignal] = []
    for sig in [SIGTERM, SIGINT] {
        let source = DispatchSource.makeSignalSource(signal: sig, queue: .main)
        source.setEventHandler { machine.stop() }
        source.resume()
        signalSources.append(source)
    }
    machine.start()
    withExtendedLifetime((machine, signalSources)) { RunLoop.main.run() }
default:
    fail("usage: dockyard-vm prepare --assets <dir> | run [--config <path>]")
}

#else
FileHandle.standardError.write(Data("dockyard-vm runs on macOS only\n".utf8))
exit(1)
#endif
