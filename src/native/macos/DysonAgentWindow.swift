import AppKit
import WebKit

private let consoleHook = """
(function() {
  function post(level, text) {
    try {
      window.webkit.messageHandlers.dysonConsole.postMessage({
        level: level,
        message: String(text),
        source: String(location.href || '')
      });
    } catch (e) {}
  }
  ['log','info','warn','error','debug'].forEach(function(level) {
    var orig = console[level] ? console[level].bind(console) : function() {};
    console[level] = function() {
      var parts = Array.prototype.slice.call(arguments).map(function(a) {
        try { return typeof a === 'string' ? a : JSON.stringify(a); } catch (e) { return String(a); }
      });
      post(level, parts.join(' '));
      return orig.apply(console, arguments);
    };
  });
  window.addEventListener('error', function(e) {
    post('error', e && e.message ? e.message : 'error');
  });
})();
"""

private func newBrowserId() -> String {
    UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
}

private final class ConsoleProxy: NSObject, WKScriptMessageHandler {
    weak var tab: DysonAgentTab?

    func userContentController(
        _ userContentController: WKUserContentController,
        didReceive message: WKScriptMessage)
    {
        guard let body = message.body as? [String: Any] else { return }
        let level = body["level"] as? String ?? "log"
        let text = body["message"] as? String ?? ""
        let source = body["source"] as? String
        tab?.appendConsole(level: level, message: text, source: source)
    }
}

final class DysonAgentTab: NSObject, WKNavigationDelegate, WKUIDelegate {
    let id: String
    let webView: WKWebView
    weak var owner: DysonAgentWindow?
    var console: [[String: Any]] = []
    var network: [[String: Any]] = []
    private var navGeneration = 0
    private var waiters: [(DispatchSemaphore, Int)] = []

    init(configuration: WKWebViewConfiguration) {
        id = newBrowserId()
        let hook = ConsoleProxy()
        configuration.userContentController.add(hook, name: "dysonConsole")
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.allowsBackForwardNavigationGestures = true
        if #available(macOS 13.3, *) {
            view.isInspectable = true
        }
        webView = view
        super.init()
        hook.tab = self
        view.navigationDelegate = self
        view.uiDelegate = self
    }

    func load(_ urlString: String) {
        guard let url = URL(string: urlString) else { return }
        webView.load(URLRequest(url: url))
    }

    func reload(ignoreCache: Bool) {
        if ignoreCache {
            webView.reloadFromOrigin()
        } else {
            webView.reload()
        }
    }

    func appendConsole(level: String, message: String, source: String?) {
        var row: [String: Any] = [
            "level": level,
            "message": message,
            "timestamp": dysonStamp(),
        ]
        if let source { row["source"] = source }
        console.append(row)
        // ponytail: cap 500 so a noisy page cannot grow the JSON pull forever. Drop oldest.
        if console.count > 500 {
            console.removeFirst(console.count - 500)
        }
    }

    func appendNetwork(url: String, status: Int?) {
        var row: [String: Any] = [
            "url": url,
            "method": "GET",
            "timestamp": dysonStamp(),
        ]
        if let status { row["status"] = status }
        network.append(row)
        if network.count > 500 {
            network.removeFirst(network.count - 500)
        }
    }

    func armWait(_ sem: DispatchSemaphore) {
        waiters.append((sem, navGeneration))
    }

    func cancelWait(_ sem: DispatchSemaphore) {
        waiters.removeAll { $0.0 === sem }
    }

    private func bumpNavigation() {
        navGeneration += 1
        let ready = waiters.filter { navGeneration > $0.1 }
        waiters.removeAll { navGeneration > $0.1 }
        for (sem, _) in ready {
            sem.signal()
        }
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        appendNetwork(url: webView.url?.absoluteString ?? "", status: 200)
        bumpNavigation()
        owner?.syncChrome()
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        appendNetwork(url: webView.url?.absoluteString ?? "", status: nil)
        bumpNavigation()
        owner?.syncChrome()
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        appendNetwork(url: webView.url?.absoluteString ?? "", status: nil)
        bumpNavigation()
        owner?.syncChrome()
    }

    func webView(_ webView: WKWebView, didCommit navigation: WKNavigation!) {
        owner?.syncChrome()
    }

    func webView(
        _ webView: WKWebView,
        requestMediaCapturePermissionFor origin: WKSecurityOrigin,
        initiatedByFrame frame: WKFrameInfo,
        type: WKMediaCaptureType,
        decisionHandler: @escaping (WKPermissionDecision) -> Void)
    {
        decisionHandler(.grant)
    }

    @available(macOS 27.0, *)
    func webView(
        _ webView: WKWebView,
        requestGeolocationPermissionFor origin: WKSecurityOrigin,
        initiatedByFrame frame: WKFrameInfo,
        decisionHandler: @escaping (WKPermissionDecision) -> Void)
    {
        decisionHandler(.grant)
    }

    func webView(
        _ webView: WKWebView,
        runOpenPanelWith parameters: WKOpenPanelParameters,
        initiatedByFrame frame: WKFrameInfo,
        completionHandler: @escaping ([URL]?) -> Void)
    {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = parameters.allowsDirectories
        panel.allowsMultipleSelection = parameters.allowsMultipleSelection
        panel.begin { response in
            completionHandler(response == .OK ? panel.urls : nil)
        }
    }

    func webView(
        _ webView: WKWebView,
        createWebViewWith configuration: WKWebViewConfiguration,
        for navigationAction: WKNavigationAction,
        windowFeatures: WKWindowFeatures) -> WKWebView?
    {
        owner?.addTab(configuration: configuration).webView
    }

    func webView(
        _ webView: WKWebView,
        runJavaScriptAlertPanelWithMessage message: String,
        initiatedByFrame frame: WKFrameInfo,
        completionHandler: @escaping () -> Void)
    {
        completionHandler()
    }

    func webView(
        _ webView: WKWebView,
        runJavaScriptConfirmPanelWithMessage message: String,
        initiatedByFrame frame: WKFrameInfo,
        completionHandler: @escaping (Bool) -> Void)
    {
        completionHandler(true)
    }

    func webView(
        _ webView: WKWebView,
        runJavaScriptTextInputPanelWithPrompt prompt: String,
        defaultText: String?,
        initiatedByFrame frame: WKFrameInfo,
        completionHandler: @escaping (String?) -> Void)
    {
        completionHandler(defaultText)
    }
}

final class DysonAgentWindow: NSObject, NSWindowDelegate {
    let id = newBrowserId()
    let window: NSWindow
    private(set) var tabs: [DysonAgentTab] = []
    private(set) var activeId: String?

    private let urlField = NSTextField()
    private let tabBar = NSStackView()
    private let webHost = NSView()
    private var snipOverlay: SnipOverlay?

    init(width: Int, height: Int) {
        let frame = NSRect(x: 0, y: 0, width: CGFloat(max(width, 200)), height: CGFloat(max(height, 200)))
        window = NSWindow(
            contentRect: frame,
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false)
        super.init()
        window.title = "Dyson Browser"
        window.delegate = self
        window.isReleasedWhenClosed = false
        window.minSize = NSSize(width: 200, height: 200)
        window.contentView = buildChrome()
        window.center()
    }

    func show() {
        window.makeKeyAndOrderFront(nil)
        NSApp.activate()
    }

    func close() {
        window.close()
    }

    func resize(width: Int, height: Int) {
        var frame = window.frame
        frame.size = NSSize(width: CGFloat(width), height: CGFloat(height))
        window.setFrame(frame, display: true)
    }

    func bringToFront() {
        if window.isMiniaturized { window.deminiaturize(nil) }
        window.makeKeyAndOrderFront(nil)
        NSApp.activate()
    }

    @discardableResult
    func addTab(url: String?) -> DysonAgentTab {
        let tab = addTab(configuration: Self.makeConfiguration())
        let target = (url?.isEmpty == false) ? url! : "about:blank"
        tab.load(target)
        return tab
    }

    @discardableResult
    func addTab(configuration: WKWebViewConfiguration) -> DysonAgentTab {
        Self.installConsoleHook(configuration)
        let tab = DysonAgentTab(configuration: configuration)
        tab.owner = self
        tabs.append(tab)
        activate(tab.id)
        return tab
    }

    func closeTab(_ tabId: String) -> Bool {
        guard let index = tabs.firstIndex(where: { $0.id == tabId }) else { return false }
        let tab = tabs.remove(at: index)
        tab.webView.removeFromSuperview()
        if activeId == tabId {
            activeId = nil
            if let next = tabs.first {
                activate(next.id)
            }
        }
        rebuildTabs()
        return true
    }

    @discardableResult
    func activate(_ tabId: String) -> Bool {
        guard let tab = tabs.first(where: { $0.id == tabId }) else { return false }
        activeId = tabId
        for child in webHost.subviews where child !== snipOverlay {
            child.removeFromSuperview()
        }
        tab.webView.frame = webHost.bounds
        tab.webView.autoresizingMask = [.width, .height]
        webHost.addSubview(tab.webView, positioned: .below, relativeTo: snipOverlay)
        syncChrome()
        rebuildTabs()
        return true
    }

    func tabRows() -> [[String: Any]] {
        tabs.map { tab in
            [
                "id": tab.id,
                "url": tab.webView.url?.absoluteString ?? "",
                "title": tab.webView.title ?? "",
            ]
        }
    }

    func syncChrome() {
        guard let tab = tabs.first(where: { $0.id == activeId }) else { return }
        let url = tab.webView.url?.absoluteString ?? ""
        if urlField.stringValue != url && urlField.currentEditor() == nil {
            urlField.stringValue = url
        }
        let title = tab.webView.title
        window.title = (title?.isEmpty == false) ? title! : "Dyson Browser"
    }

    func windowWillClose(_ notification: Notification) {
        DysonWebKitHost.shared.agents.removeValue(forKey: id)
    }

    private func buildChrome() -> NSView {
        let root = NSView()
        let row = NSStackView()
        row.orientation = .horizontal
        row.spacing = 6
        row.edgeInsets = NSEdgeInsets(top: 6, left: 8, bottom: 4, right: 8)
        row.translatesAutoresizingMaskIntoConstraints = false

        let back = button("Back", #selector(goBack))
        let forward = button("Forward", #selector(goForward))
        let reload = button("Reload", #selector(reloadPage))
        urlField.placeholderString = "https://"
        urlField.target = self
        urlField.action = #selector(submitUrl)
        urlField.translatesAutoresizingMaskIntoConstraints = false
        let snip = button("Snip", #selector(beginSnip))
        row.addArrangedSubview(back)
        row.addArrangedSubview(forward)
        row.addArrangedSubview(reload)
        row.addArrangedSubview(urlField)
        row.addArrangedSubview(snip)

        tabBar.orientation = .horizontal
        tabBar.spacing = 4
        tabBar.alignment = .centerY
        tabBar.translatesAutoresizingMaskIntoConstraints = false

        webHost.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(row)
        root.addSubview(tabBar)
        root.addSubview(webHost)

        NSLayoutConstraint.activate([
            row.topAnchor.constraint(equalTo: root.topAnchor),
            row.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            row.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            urlField.widthAnchor.constraint(greaterThanOrEqualToConstant: 160),
            tabBar.topAnchor.constraint(equalTo: row.bottomAnchor),
            tabBar.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 8),
            tabBar.trailingAnchor.constraint(lessThanOrEqualTo: root.trailingAnchor, constant: -8),
            tabBar.heightAnchor.constraint(equalToConstant: 28),
            webHost.topAnchor.constraint(equalTo: tabBar.bottomAnchor),
            webHost.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            webHost.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            webHost.bottomAnchor.constraint(equalTo: root.bottomAnchor),
        ])
        return root
    }

    private func button(_ title: String, _ action: Selector) -> NSButton {
        let button = NSButton(title: title, target: self, action: action)
        button.bezelStyle = .rounded
        button.setButtonType(.momentaryPushIn)
        return button
    }

    private func rebuildTabs() {
        for view in tabBar.arrangedSubviews {
            tabBar.removeArrangedSubview(view)
            view.removeFromSuperview()
        }
        for tab in tabs {
            let title = tab.webView.title?.isEmpty == false ? tab.webView.title! : "Tab"
            let button = NSButton(title: title, target: self, action: #selector(tabPressed(_:)))
            button.identifier = NSUserInterfaceItemIdentifier(tab.id)
            button.bezelStyle = .recessed
            button.setButtonType(.momentaryPushIn)
            if tab.id == activeId {
                button.state = .on
            }
            tabBar.addArrangedSubview(button)
        }
    }

    @objc private func tabPressed(_ sender: NSButton) {
        if let id = sender.identifier?.rawValue {
            activate(id)
        }
    }

    @objc private func goBack() { active?.webView.goBack() }
    @objc private func goForward() { active?.webView.goForward() }
    @objc private func reloadPage() { active?.reload(ignoreCache: false) }

    @objc private func submitUrl() {
        var text = urlField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.isEmpty { return }
        if !text.contains("://") { text = "https://" + text }
        active?.load(text)
    }

    @objc private func beginSnip() {
        guard snipOverlay == nil, let tab = active else { return }
        let probe = "JSON.stringify({scrollY: window.scrollY || 0, scrollHeight: (document.documentElement && document.documentElement.scrollHeight) || 0, innerHeight: window.innerHeight || 0})"
        tab.webView.evaluateJavaScript(probe) { [weak self] result, _ in
            let metrics = Self.parseMetrics(result)
            tab.webView.takeSnapshot(with: nil) { image, _ in
                guard let self, let image else { return }
                self.presentSnip(image: image, metrics: metrics, url: tab.webView.url?.absoluteString)
            }
        }
    }

    private func presentSnip(image: NSImage, metrics: SnipMetrics?, url: String?) {
        let overlay = SnipOverlay(frame: webHost.bounds, image: image, metrics: metrics, url: url)
        overlay.autoresizingMask = [.width, .height]
        overlay.onCancel = { [weak self] in self?.endSnip() }
        overlay.onCrop = { [weak self] payload in
            DysonWebKitHost.shared.emitSnip(payload)
            self?.endSnip()
        }
        snipOverlay = overlay
        webHost.addSubview(overlay)
        window.makeFirstResponder(overlay)
    }

    private func endSnip() {
        snipOverlay?.removeFromSuperview()
        snipOverlay = nil
    }

    private var active: DysonAgentTab? {
        tabs.first { $0.id == activeId }
    }

    private static func makeConfiguration() -> WKWebViewConfiguration {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = DysonWebKitHost.shared.store()
        config.defaultWebpagePreferences.allowsContentJavaScript = true
        config.preferences.javaScriptCanOpenWindowsAutomatically = true
        config.preferences.isElementFullscreenEnabled = true
        config.mediaTypesRequiringUserActionForPlayback = []
        return config
    }

    private static func installConsoleHook(_ config: WKWebViewConfiguration) {
        let script = WKUserScript(source: consoleHook, injectionTime: .atDocumentStart, forMainFrameOnly: false)
        config.userContentController.addUserScript(script)
    }

    private static func parseMetrics(_ value: Any?) -> SnipMetrics? {
        guard let text = value as? String, let data = text.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return nil
        }
        func num(_ key: String) -> Double {
            (obj[key] as? NSNumber)?.doubleValue ?? 0
        }
        return SnipMetrics(scrollY: num("scrollY"), scrollHeight: num("scrollHeight"), viewportHeight: num("innerHeight"))
    }
}

private struct SnipMetrics {
    var scrollY: Double
    var scrollHeight: Double
    var viewportHeight: Double
}

private final class SnipOverlay: NSView {
    let image: NSImage
    let metrics: SnipMetrics?
    let pageUrl: String?
    var onCancel: (() -> Void)?
    var onCrop: ((String) -> Void)?
    private var anchor: NSPoint?
    private var selection = NSRect.zero

    init(frame: NSRect, image: NSImage, metrics: SnipMetrics?, url: String?) {
        self.image = image
        self.metrics = metrics
        pageUrl = url
        super.init(frame: frame)
    }

    required init?(coder: NSCoder) { nil }

    override var acceptsFirstResponder: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        image.draw(in: bounds)
        guard selection.width > 1, selection.height > 1 else { return }
        NSColor.systemBlue.withAlphaComponent(0.25).setFill()
        selection.fill()
        NSColor.systemBlue.setStroke()
        let path = NSBezierPath(rect: selection)
        path.lineWidth = 1
        path.stroke()
    }

    override func mouseDown(with event: NSEvent) {
        anchor = convert(event.locationInWindow, from: nil)
        selection = .zero
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard let anchor else { return }
        let point = convert(event.locationInWindow, from: nil)
        selection = NSRect(
            x: min(anchor.x, point.x),
            y: min(anchor.y, point.y),
            width: abs(point.x - anchor.x),
            height: abs(point.y - anchor.y))
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        guard selection.width >= 4, selection.height >= 4 else {
            onCancel?()
            return
        }
        guard let payload = cropPayload() else {
            onCancel?()
            return
        }
        onCrop?(payload)
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            onCancel?()
        } else {
            super.keyDown(with: event)
        }
    }

    private func cropPayload() -> String? {
        var proposed = NSRect(origin: .zero, size: image.size)
        guard bounds.width > 0, bounds.height > 0,
              let cg = image.cgImage(forProposedRect: &proposed, context: nil, hints: nil) else {
            return nil
        }

        // View coords are bottom-left. The snip rect and page percent are top-left, matching DysonBrowserSnipCrop.
        let top = NSRect(
            x: selection.minX,
            y: bounds.height - selection.maxY,
            width: selection.width,
            height: selection.height)
        let scaleX = CGFloat(cg.width) / bounds.width
        let scaleY = CGFloat(cg.height) / bounds.height
        var x = Int(floor(top.minX * scaleX))
        var y = Int(floor(top.minY * scaleY))
        var right = Int(ceil((top.minX + top.width) * scaleX))
        var bottom = Int(ceil((top.minY + top.height) * scaleY))
        x = min(max(x, 0), cg.width - 1)
        y = min(max(y, 0), cg.height - 1)
        right = min(max(right, x + 1), cg.width)
        bottom = min(max(bottom, y + 1), cg.height)
        let width = right - x
        let height = bottom - y
        let cgRect = CGRect(x: x, y: cg.height - y - height, width: width, height: height)
        guard let cropped = cg.cropping(to: cgRect) else { return nil }
        let rep = NSBitmapImageRep(cgImage: cropped)
        guard let jpeg = rep.representation(using: .jpeg, properties: [.compressionFactor: 0.85]) else { return nil }

        var payload: [String: Any] = [
            "imageBase64": jpeg.base64EncodedString(),
            "fileName": "browser-snip.jpg",
        ]
        if let pageUrl, !pageUrl.isEmpty { payload["url"] = pageUrl }
        if let metrics {
            payload["scrollY"] = metrics.scrollY
            payload["scrollHeight"] = metrics.scrollHeight
            payload["viewportHeight"] = metrics.viewportHeight
            let documentY = metrics.scrollY + (Double(top.minY) / Double(bounds.height)) * metrics.viewportHeight
            let percent = Int((100.0 * documentY / max(metrics.scrollHeight, 1)).rounded())
            payload["percentDown"] = min(100, max(0, percent))
        }
        return jsonObject(payload)
    }

    private func jsonObject(_ object: [String: Any]) -> String? {
        guard let data = try? JSONSerialization.data(withJSONObject: object) else { return nil }
        return String(data: data, encoding: .utf8)
    }
}
