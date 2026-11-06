#if IOS
using CoreGraphics;
using Foundation;
using UIKit;
using WebKit;
#endif

namespace MqttProbe;

public partial class MainPage
{
#if IOS
    private const string KeyboardOpenClassName = "app-keyboard-open";
    private const string KeyboardReadyMessageName = "appKeyboardReady";

    // An accessory/QuickType bar without a software keyboard is at most ~55 pt tall; every real
    // software keyboard is taller, so the keyboard's own height tells the two apart.
    private const double SoftwareKeyboardMinimumHeight = 100;

    private static readonly string _keyboardReadyScript =
        "var handlers = window.webkit && window.webkit.messageHandlers; " +
        $"if (handlers && handlers.{KeyboardReadyMessageName}) {{ handlers.{KeyboardReadyMessageName}.postMessage(1); }}";

    private WKWebView? _webView;
    private WKWebView? _keyboardBridgeWebView;
    private NSObject? _keyboardFrameObserver;
    private NSObject? _keyboardHideObserver;
    private NSObject? _foregroundObserver;
    private bool _keyboardOpen;
    private bool? _appliedKeyboardOpen;
    private bool _applyingKeyboardState;
    private bool _keyboardStateQueued;
#endif

    public MainPage()
    {
        InitializeComponent();
    }

#if IOS
    protected override void OnAppearing()
    {
        base.OnAppearing();
        AttachKeyboardObservers();
        AttachKeyboardBridge();
        ReplayKeyboardState();
    }

    protected override void OnDisappearing()
    {
        DetachKeyboardObservers();
        base.OnDisappearing();
    }
#endif

#if IOS
    private void OnBlazorWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
    {
        var backgroundColor = UIKit.UIColor.FromRGB(30, 41, 59);
        e.WebView.Opaque = false;
        e.WebView.BackgroundColor = backgroundColor;
        e.WebView.ScrollView.BackgroundColor = backgroundColor;
        _webView = e.WebView;
        AttachKeyboardBridge();
        ReplayKeyboardState();
    }
#else
    // The keyboard bridge keeps WebView state only on iOS; S2325 is a ship gate on the other
    // targets, so this handler is static there.
    private static void OnBlazorWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
    {
#if WINDOWS
        e.WebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 30, 41, 59);
#endif
    }
#endif

#if IOS
    private void AttachKeyboardObservers()
    {
        if (_keyboardFrameObserver is not null)
        {
            return;
        }

        _keyboardFrameObserver = UIKeyboard.Notifications.ObserveWillChangeFrame((_, e) => OnKeyboardFrameChanged(e.FrameEnd));
        _keyboardHideObserver = UIKeyboard.Notifications.ObserveWillHide((_, _) => SetKeyboardOpen(false));
        _foregroundObserver = UIApplication.Notifications.ObserveWillEnterForeground((_, _) => ReplayKeyboardState());
    }

    private void DetachKeyboardObservers()
    {
        _keyboardFrameObserver?.Dispose();
        _keyboardFrameObserver = null;
        _keyboardHideObserver?.Dispose();
        _keyboardHideObserver = null;
        _foregroundObserver?.Dispose();
        _foregroundObserver = null;
    }

    // The document reloads without a keyboard notification, so it reports readiness and the
    // retained state is pushed again. The controller owns the handler for the WebView's lifetime.
    private void AttachKeyboardBridge()
    {
        var webView = _webView;
        if (webView is null || ReferenceEquals(_keyboardBridgeWebView, webView))
        {
            return;
        }

        var controller = webView.Configuration.UserContentController;
        controller.AddUserScript(new WKUserScript(new NSString(_keyboardReadyScript), WKUserScriptInjectionTime.AtDocumentStart, true));
        controller.AddScriptMessageHandler(new KeyboardReadyHandler(this), KeyboardReadyMessageName);
        _keyboardBridgeWebView = webView;
    }

    private void OnKeyboardFrameChanged(CGRect keyboardFrame)
    {
        var window = _webView?.Window;
        var screen = window?.Screen;
        if (window is null || screen is null)
        {
            SetKeyboardOpen(false);
            return;
        }

        var frameInWindow = window.ConvertRectFromCoordinateSpace(keyboardFrame, screen.CoordinateSpace);
        var overlappingWindow = CGRect.Intersect(frameInWindow, window.Bounds).Height > 0;
        SetKeyboardOpen(overlappingWindow && keyboardFrame.Height >= SoftwareKeyboardMinimumHeight);
    }

    private void SetKeyboardOpen(bool open)
    {
        if (_keyboardOpen == open && _appliedKeyboardOpen == open)
        {
            return;
        }

        _keyboardOpen = open;
        PushKeyboardState();
    }

    private void ReplayKeyboardState()
    {
        _appliedKeyboardOpen = null;
        PushKeyboardState();
    }

    private void PushKeyboardState()
    {
        if (_applyingKeyboardState)
        {
            _keyboardStateQueued = true;
            return;
        }

        _ = PushKeyboardStateAsync();
    }

    // Keyboard notifications can arrive faster than a script evaluation round trip, so a running
    // pass repeats until it has pushed the latest state.
    private async Task PushKeyboardStateAsync()
    {
        _applyingKeyboardState = true;
        try
        {
            do
            {
                _keyboardStateQueued = false;
                var open = _keyboardOpen;
                if (await EvaluateKeyboardStateAsync(open))
                {
                    _appliedKeyboardOpen = open;
                }
            }
            while (_keyboardStateQueued);
        }
        finally
        {
            _applyingKeyboardState = false;
        }
    }

    private async Task<bool> EvaluateKeyboardStateAsync(bool open)
    {
        var webView = _webView;
        if (webView is null)
        {
            return false;
        }

        var script = open
            ? $"document.documentElement.classList.add('{KeyboardOpenClassName}')"
            : $"document.documentElement.classList.remove('{KeyboardOpenClassName}')";

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => webView.EvaluateJavaScriptAsync(script));
            return true;
        }
        catch (Exception)
        {
            // The WebView rejects script while it is loading or tearing down. The state stays
            // pending, so the next notification or document-ready message retries it.
            return false;
        }
    }

    private sealed class KeyboardReadyHandler(MainPage page) : WKScriptMessageHandler
    {
        private readonly WeakReference<MainPage> _page = new(page);

        public override void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
        {
            if (_page.TryGetTarget(out var target))
            {
                target.ReplayKeyboardState();
            }
        }
    }
#endif
}
