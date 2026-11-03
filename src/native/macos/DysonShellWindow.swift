import AppKit
import WebKit

/// Blazor shell. Stays on the Kestrel loopback origin. Other http(s) opens in the OS browser.
final class DysonShellWindow: NSObject, NSWindowDelegate, WKNavigationDelegate, WKUIDelegate {
    let window: NSWindow
    let webView: WKWebView
    private var origin: URL?

    override init() {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .default()
        config.defaultWebpagePreferences.allowsContentJavaScript = true
        config.preferences.javaScriptCanOpenWindowsAutomatically = false
        let view = WKWebView(frame: .zero, configuration: config)
        view.allowsBackForwardNavigationGestures = true
        if #available(macOS 13.3, *) {
            view.isInspectable = true
        }

        let frame = NSRect(x: 0, y: 0, width: 1280, height: 840)
        let window = NSWindow(
            contentRect: frame,
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false)
        window.title = "Dyson Harness"
        window.contentView = view
        window.isReleasedWhenClosed = false
        window.center()
        self.window = window
        webView = view
        super.init()
        window.delegate = self
        view.navigationDelegate = self
        view.uiDelegate = self
    }

    func load(_ urlString: String) {
        guard let url = URL(string: urlString) else { return }
        origin = url
        webView.load(URLRequest(url: url))
        window.makeKeyAndOrderFront(nil)
        NSApp.activate()
    }

    func windowWillClose(_ notification: Notification) {
        DysonWebKitHost.shared.shell = nil
        DysonWebKitHost.shared.stopApp()
    }

    func webView(
        _ webView: WKWebView,
        decidePolicyFor navigationAction: WKNavigationAction,
        decisionHandler: @escaping (WKNavigationActionPolicy) -> Void)
    {
        guard let url = navigationAction.request.url else {
            decisionHandler(.allow)
            return
        }
        if allows(url) {
            decisionHandler(.allow)
            return
        }
        if url.scheme == "http" || url.scheme == "https" {
            NSWorkspace.shared.open(url)
        }
        decisionHandler(.cancel)
    }

    func webView(
        _ webView: WKWebView,
        createWebViewWith configuration: WKWebViewConfiguration,
        for navigationAction: WKNavigationAction,
        windowFeatures: WKWindowFeatures) -> WKWebView?
    {
        if let url = navigationAction.request.url {
            if allows(url) {
                webView.load(navigationAction.request)
            } else if url.scheme == "http" || url.scheme == "https" {
                NSWorkspace.shared.open(url)
            }
        }
        return nil
    }

    private func allows(_ url: URL) -> Bool {
        let scheme = url.scheme?.lowercased() ?? ""
        if scheme == "about" || scheme == "webkit" {
            return true
        }
        guard let origin, let host = url.host?.lowercased(), let originHost = origin.host?.lowercased() else {
            return false
        }
        return scheme == (origin.scheme?.lowercased() ?? "") && host == originHost && url.port == origin.port
    }
}
