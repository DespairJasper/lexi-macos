import Foundation

// Only disposable outputs inside this source tree are accepted. User data,
// source assets and unrelated applications cannot be passed to this helper.
let scriptURL = URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL
let root = scriptURL.deletingLastPathComponent().deletingLastPathComponent()
let manager = FileManager.default
for argument in CommandLine.arguments.dropFirst() {
    let url = URL(fileURLWithPath: argument).standardizedFileURL
    let prefix = root.path + "/"
    guard url.path.hasPrefix(prefix) else {
        fputs("Refusing path outside project: \(url.path)\n", stderr)
        exit(2)
    }
    let relative = String(url.path.dropFirst(prefix.count))
    let generated = relative.hasPrefix("dist/")
        || relative.hasPrefix("verification/")
        || relative == ".tools/avalonia-native-reference.zip.partial"
        || (relative.hasPrefix("lexi_avalonia/")
            && ["bin", "obj"].contains(url.lastPathComponent))
    guard generated else {
        fputs("Refusing non-generated path: \(relative)\n", stderr)
        exit(2)
    }
    if manager.fileExists(atPath: url.path) {
        do {
            var destination: NSURL?
            try manager.trashItem(at: url, resultingItemURL: &destination)
            let receipt: [String: String] = ["original": url.path, "trash": destination?.path ?? "", "time": ISO8601DateFormatter().string(from: Date())]
            let log = root.appendingPathComponent("verification/generated-trash-receipts.jsonl")
            try manager.createDirectory(at: log.deletingLastPathComponent(), withIntermediateDirectories: true)
            var data = try JSONSerialization.data(withJSONObject: receipt, options: [.sortedKeys])
            data.append(10)
            if !manager.fileExists(atPath: log.path) { manager.createFile(atPath: log.path, contents: nil) }
            let handle = try FileHandle(forWritingTo: log)
            try handle.seekToEnd(); try handle.write(contentsOf: data); try handle.close()
            print("Trashed: \(relative)")
        } catch {
            fputs("Trash failed for \(relative): \(error.localizedDescription)\n", stderr)
            exit(1)
        }
    }
}
