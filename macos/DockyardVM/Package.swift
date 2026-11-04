// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "DockyardVM",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "DockyardVMCore"),
        .executableTarget(name: "dockyard-vm", dependencies: ["DockyardVMCore"]),
        .testTarget(name: "DockyardVMCoreTests", dependencies: ["DockyardVMCore"]),
    ]
)
