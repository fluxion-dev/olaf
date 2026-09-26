// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "OlafFixture",
    platforms: [.iOS(.v16), .macOS(.v13)],
    dependencies: [
        .package(url: "https://github.com/Alamofire/Alamofire.git", from: "5.8.0"),
        .package(url: "https://github.com/SnapKit/SnapKit.git", exact: "5.6.0"),
        .package(url: "https://github.com/apple/swift-argument-parser.git", from: "1.2.0"),
    ]
)
