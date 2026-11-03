import AppKit
import Darwin
import Foundation
import WebKit

// ponytail: WKWebView has no public WebUSB, WebSerial, WebBluetooth, or WebMIDI.
// Network log is main-frame didFinish/didFail only (same ceiling as Cef FrameLoadEnd).
// Camera, mic, and location still show the macOS TCC dialog once; Info.plist strings do not skip it.
// Device-orientation permission is unavailable on macOS. Display capture is not a WKMediaCaptureType.
// Geolocation grant exists only on macOS 27+; older systems keep WebKit's default prompt.
// Do not add private WebKit SPI.

private let agentStoreId = UUID(uuidString: "8F3C2A1E-6B47-4D19-9C55-2E7A4B0D6F31")!

private final class Box<T> {
    var value: T
    init(_ value: T) { self.value = value }
}

private func jsonText(_ object: Any) -> String {
    guard JSONSerialization.isValidJSONObject(object),
          let data = try? JSONSerialization.data(withJSONObject: object),
          let text = String(data: data, encoding: .utf8) else {
        return "{\"ok\":false,\"error\":\"json encode failed\"}"
    }
    return text
}

func dysonOk(_ fields: [String: Any] = [:]) -> String {
    var object: [String: Any] = ["ok": true]
    for (key, value) in fields {
        object[key] = value
    }
    return jsonText(object)
}

func dysonErr(_ message: String) -> String {
    jsonText(["ok": false, "error": message])
}

func dysonOnMain<T>(_ body: @escaping () -> T) -> T {
    if Thread.isMainThread {
        return body()
    }

    let box = Box<T?>(nil)
    let sem = DispatchSemaphore(value: 0)
    DispatchQueue.main.async {
        box.value = body()
        sem.signal()
    }
    sem.wait()
    return box.value!
}

private func number(_ value: Any?) -> Int? {
    (value as? NSNumber)?.intValue
}

private func text(_ value: Any?) -> String? {
    if let s = value as? String { return s }
    if value is NSNull { return nil }
    return nil
}

private func flag(_ value: Any?) -> Bool {
    (value as? NSNumber)?.boolValue ?? false
}

private let isoStamp: ISO8601DateFormatter = {
    let formatter = ISO8601DateFormatter()
    formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    return formatter
}()

func dysonStamp(_ date: Date = Date()) -> String {
    isoStamp.string(from: date)
}

func dysonStringify(_ value: Any?) -> String {
    if value == nil || value is NSNull { return "" }
    if let s = value as? String { return s }
    if let n = value as? NSNumber { return n.stringValue }
    if let value, JSONSerialization.isValidJSONObject(value),
       let data = try? JSONSerialization.data(withJSONObject: value),
       let text = String(data: data, encoding: .utf8) {
        return text
    }
    return String(describing: value!)
}

public typealias SnipFn = @convention(c) (UnsafePointer<CChar>?) -> Void

final class DysonWebKitHost: NSObject, NSApplicationDelegate {
    static let shared = DysonWebKitHost()

    var shell: DysonShellWindow?
    var agents: [String: DysonAgentWindow] = [:]
    private var snipFn: SnipFn?
    private var agentStore: WKWebsiteDataStore?

    func store() -> WKWebsiteDataStore {
        if let agentStore { return agentStore }
        let created = WKWebsiteDataStore(forIdentifier: agentStoreId)
        agentStore = created
        return created
    }

    func prepare(_ callback: SnipFn?) {
        snipFn = callback
        let app = NSApplication.shared
        app.setActivationPolicy(.regular)
        if app.delegate == nil {
            app.delegate = self
        }
        applyAppIcon()
    }

    private func applyAppIcon() {
        if let url = Bundle.main.url(forResource: "DysonHarness", withExtension: "icns"),
           let image = NSImage(contentsOf: url) {
            NSApp.applicationIconImage = image
            return
        }
        guard let exe = Bundle.main.executableURL else { return }
        let png = exe.deletingLastPathComponent().appendingPathComponent("DysonHarnessIcon.png")
        if let image = NSImage(contentsOf: png) {
            NSApp.applicationIconImage = image
        }
    }

    func emitSnip(_ json: String) {
        guard let snipFn else { return }
        json.withCString { snipFn($0) }
    }

    func stopApp() {
        NSApp.stop(nil)
        let event = NSEvent.otherEvent(
            with: .applicationDefined,
            location: .zero,
            modifierFlags: [],
            timestamp: 0,
            windowNumber: 0,
            context: nil,
            subtype: 0,
            data1: 0,
            data2: 0)
        if let event {
            NSApp.postEvent(event, atStart: true)
        }
    }

    func activatePeer() {
        let bundleId = Bundle.main.bundleIdentifier ?? "com.entitysystems.dysonharness"
        for app in NSRunningApplication.runningApplications(withBundleIdentifier: bundleId)
        where app.processIdentifier != getpid() {
            app.activate(options: [.activateAllWindows])
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    func invoke(_ json: String) -> String {
        guard let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let op = obj["op"] as? String else {
            return dysonErr("invalid request")
        }

        switch op {
        case "eval":
            return eval(obj)
        case "screenshot":
            return screenshot(obj)
        case "clearCache":
            return clearCache()
        case "waitNav":
            return waitNav(obj)
        default:
            return dysonOnMain { self.invokeOnMain(op: op, obj: obj) }
        }
    }

    private func invokeOnMain(op: String, obj: [String: Any]) -> String {
        switch op {
        case "showShell":
            let url = text(obj["url"]) ?? ""
            if url.isEmpty { return dysonErr("url is required") }
            if shell == nil { shell = DysonShellWindow() }
            shell?.load(url)
            return dysonOk()
        case "stop":
            stopApp()
            return dysonOk()
        case "openWindow":
            let window = DysonAgentWindow(
                width: number(obj["width"]) ?? 1280,
                height: number(obj["height"]) ?? 800)
            agents[window.id] = window
            let tab = window.addTab(url: text(obj["url"]))
            window.show()
            return dysonOk(["windowId": window.id, "tabId": tab.id])
        case "listWindows":
            let rows: [[String: Any]] = agents.values.map { window in
                [
                    "id": window.id,
                    "activeTabId": window.activeId ?? "",
                    "tabIds": window.tabs.map(\.id),
                ]
            }
            return dysonOk(["windows": rows])
        case "getWindow":
            guard let id = text(obj["windowId"]), agents[id] != nil else {
                return dysonErr("Window not found: \(text(obj["windowId"]) ?? "")")
            }
            return dysonOk(["windowId": id])
        case "closeWindow":
            guard let window = agent(obj) else { return missingWindow(obj) }
            window.close()
            agents.removeValue(forKey: window.id)
            return dysonOk()
        case "resizeWindow":
            guard let window = agent(obj) else { return missingWindow(obj) }
            let width = number(obj["width"]) ?? 0
            let height = number(obj["height"]) ?? 0
            window.resize(width: width, height: height)
            return dysonOk()
        case "bringToFront":
            guard let window = agent(obj) else { return missingWindow(obj) }
            window.bringToFront()
            return dysonOk()
        case "listTabs":
            guard let window = agent(obj) else { return missingWindow(obj) }
            return dysonOk(["tabs": window.tabRows()])
        case "newTab":
            guard let window = agent(obj) else { return missingWindow(obj) }
            let tab = window.addTab(url: text(obj["url"]))
            return dysonOk(["windowId": window.id, "tabId": tab.id])
        case "closeTab":
            guard let window = agent(obj) else { return missingWindow(obj) }
            guard let tabId = text(obj["tabId"]) else { return dysonErr("tabId is required") }
            guard window.closeTab(tabId) else { return dysonErr("Tab not found: \(tabId)") }
            if window.tabs.isEmpty {
                window.close()
                agents.removeValue(forKey: window.id)
            }
            return dysonOk()
        case "activateTab":
            guard let window = agent(obj) else { return missingWindow(obj) }
            guard let tabId = text(obj["tabId"]) else { return dysonErr("tabId is required") }
            guard window.activate(tabId) else { return dysonErr("Tab not found: \(tabId)") }
            return dysonOk()
        case "navigate":
            guard let tab = tab(obj) else { return missingTab(obj) }
            let url = text(obj["url"]) ?? ""
            if url.isEmpty { return dysonErr("url is required") }
            tab.load(url)
            return dysonOk()
        case "reload":
            guard let tab = tab(obj) else { return missingTab(obj) }
            tab.reload(ignoreCache: flag(obj["ignoreCache"]))
            return dysonOk()
        case "goBack":
            guard let tab = tab(obj) else { return missingTab(obj) }
            if tab.webView.canGoBack { tab.webView.goBack() }
            return dysonOk()
        case "goForward":
            guard let tab = tab(obj) else { return missingTab(obj) }
            if tab.webView.canGoForward { tab.webView.goForward() }
            return dysonOk()
        case "getUrl":
            guard let tab = tab(obj) else { return missingTab(obj) }
            return dysonOk(["value": tab.webView.url?.absoluteString ?? ""])
        case "getTitle":
            guard let tab = tab(obj) else { return missingTab(obj) }
            return dysonOk(["value": tab.webView.title ?? ""])
        case "readConsole":
            guard let tab = tab(obj) else { return missingTab(obj) }
            return dysonOk(["console": tab.console])
        case "readNetwork":
            guard let tab = tab(obj) else { return missingTab(obj) }
            return dysonOk(["network": tab.network])
        case "clearConsole":
            guard let tab = tab(obj) else { return missingTab(obj) }
            tab.console.removeAll()
            return dysonOk()
        case "clearNetwork":
            guard let tab = tab(obj) else { return missingTab(obj) }
            tab.network.removeAll()
            return dysonOk()
        default:
            return dysonErr("unknown op \(op)")
        }
    }

    private func agent(_ obj: [String: Any]) -> DysonAgentWindow? {
        guard let id = text(obj["windowId"]) else { return nil }
        return agents[id]
    }

    private func tab(_ obj: [String: Any]) -> DysonAgentTab? {
        guard let window = agent(obj), let tabId = text(obj["tabId"]) else { return nil }
        return window.tabs.first { $0.id == tabId }
    }

    private func missingWindow(_ obj: [String: Any]) -> String {
        dysonErr("Window not found: \(text(obj["windowId"]) ?? "")")
    }

    private func missingTab(_ obj: [String: Any]) -> String {
        if agent(obj) == nil { return missingWindow(obj) }
        return dysonErr("Tab not found: \(text(obj["tabId"]) ?? "")")
    }

    private func eval(_ obj: [String: Any]) -> String {
        if Thread.isMainThread { return dysonErr("eval must not run on the main thread") }
        let code = text(obj["code"]) ?? ""
        if code.isEmpty { return dysonErr("code is required") }
        let timeout = number(obj["timeoutMs"]) ?? 60_000
        let box = Box(dysonErr("JavaScript evaluation failed."))
        let sem = DispatchSemaphore(value: 0)
        var finished = false
        let lock = NSLock()
        DispatchQueue.main.async {
            guard let tab = self.tab(obj) else {
                box.value = self.missingTab(obj)
                sem.signal()
                return
            }
            tab.webView.evaluateJavaScript(code) { result, error in
                lock.lock()
                let already = finished
                finished = true
                lock.unlock()
                if already { return }
                if let error {
                    box.value = dysonErr(error.localizedDescription)
                } else {
                    box.value = dysonOk(["value": dysonStringify(result)])
                }
                sem.signal()
            }
        }
        if sem.wait(timeout: .now() + .milliseconds(timeout)) == .timedOut {
            lock.lock()
            finished = true
            lock.unlock()
            return dysonErr("JavaScript evaluation timed out.")
        }
        return box.value
    }

    private func screenshot(_ obj: [String: Any]) -> String {
        if Thread.isMainThread { return dysonErr("screenshot must not run on the main thread") }
        let timeout = number(obj["timeoutMs"]) ?? 60_000
        let box = Box(dysonErr("Screenshot capture returned empty."))
        let sem = DispatchSemaphore(value: 0)
        DispatchQueue.main.async {
            guard let tab = self.tab(obj) else {
                box.value = self.missingTab(obj)
                sem.signal()
                return
            }
            tab.webView.takeSnapshot(with: nil) { image, error in
                if let error {
                    box.value = dysonErr("TakeScreenshot not available or failed: " + error.localizedDescription)
                } else if let image, let png = dysonPng(image), !png.isEmpty {
                    box.value = dysonOk(["pngBase64": png.base64EncodedString()])
                } else {
                    box.value = dysonErr("Screenshot capture returned empty.")
                }
                sem.signal()
            }
        }
        if sem.wait(timeout: .now() + .milliseconds(timeout)) == .timedOut {
            return dysonErr("Screenshot timed out after \(timeout)ms.")
        }
        return box.value
    }

    private func clearCache() -> String {
        if Thread.isMainThread { return dysonErr("clearCache must not run on the main thread") }
        let types: Set<String> = [
            WKWebsiteDataTypeDiskCache,
            WKWebsiteDataTypeMemoryCache,
            WKWebsiteDataTypeFetchCache,
            WKWebsiteDataTypeOfflineWebApplicationCache,
        ]
        let box = Box("")
        let sem = DispatchSemaphore(value: 0)
        DispatchQueue.main.async {
            self.store().removeData(ofTypes: types, modifiedSince: .distantPast) {
                DispatchQueue.main.async {
                    var windows = 0
                    var tabs = 0
                    for window in self.agents.values {
                        windows += 1
                        for tab in window.tabs {
                            tab.webView.reloadFromOrigin()
                            tabs += 1
                        }
                    }
                    box.value = dysonOk(["windows": windows, "tabsReloaded": tabs])
                    sem.signal()
                }
            }
        }
        sem.wait()
        return box.value
    }

    private func waitNav(_ obj: [String: Any]) -> String {
        if Thread.isMainThread { return dysonErr("waitNav must not run on the main thread") }
        let timeout = number(obj["timeoutMs"]) ?? 60_000
        let sem = DispatchSemaphore(value: 0)
        let failure = Box<String?>(nil)
        DispatchQueue.main.async {
            guard let tab = self.tab(obj) else {
                failure.value = self.missingTab(obj)
                sem.signal()
                return
            }
            tab.armWait(sem)
        }
        if sem.wait(timeout: .now() + .milliseconds(timeout)) == .timedOut {
            DispatchQueue.main.async {
                self.tab(obj)?.cancelWait(sem)
            }
            return dysonErr("Timeout waiting for navigation.")
        }
        if let message = failure.value, !message.isEmpty {
            return message
        }
        return dysonOk()
    }
}

func dysonPng(_ image: NSImage) -> Data? {
    var proposed = NSRect(origin: .zero, size: image.size)
    guard let cg = image.cgImage(forProposedRect: &proposed, context: nil, hints: nil) else { return nil }
    let rep = NSBitmapImageRep(cgImage: cg)
    return rep.representation(using: .png, properties: [:])
}

// public: Swift otherwise emits these as local symbols and NativeLibrary cannot find them.
@_cdecl("dyson_webkit_init")
public func dyson_webkit_init(_ callback: SnipFn?) -> Int32 {
    if Thread.isMainThread {
        DysonWebKitHost.shared.prepare(callback)
    } else {
        dysonOnMain { DysonWebKitHost.shared.prepare(callback) }
    }
    return 0
}

@_cdecl("dyson_webkit_run")
public func dyson_webkit_run() {
    DysonWebKitHost.shared.prepare(nil)
    NSApp.run()
}

@_cdecl("dyson_webkit_invoke")
public func dyson_webkit_invoke(_ request: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>? {
    let json = request.map { String(cString: $0) } ?? ""
    return strdup(DysonWebKitHost.shared.invoke(json))
}

@_cdecl("dyson_webkit_free")
public func dyson_webkit_free(_ pointer: UnsafeMutablePointer<CChar>?) {
    free(pointer)
}

@_cdecl("dyson_webkit_activate_peer")
public func dyson_webkit_activate_peer() {
    DysonWebKitHost.shared.activatePeer()
}

@_cdecl("dyson_webkit_alert")
public func dyson_webkit_alert(_ message: UnsafePointer<CChar>?) {
    let text = message.map { String(cString: $0) } ?? "Failed to start."
    let present = {
        let alert = NSAlert()
        alert.messageText = "Dyson Harness"
        alert.informativeText = text
        alert.runModal()
    }
    if Thread.isMainThread {
        present()
    } else {
        dysonOnMain(present)
    }
}
