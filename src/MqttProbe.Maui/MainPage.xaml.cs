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
    private const string KeyboardStateAttributeName = "data-app-native-keyboard-state";
    private const string KeyboardStateChangeEventName = "app-native-keyboard-change";
    private const string KeyboardReadyMessageName = "appKeyboardReady";

    // An accessory/QuickType bar without a software keyboard is at most ~55 pt tall; every real
    // software keyboard is taller, so the keyboard's own height tells the two apart.
    private const double SoftwareKeyboardMinimumHeight = 100;

    // A frame this tall relative to its window cannot be a keyboard occlusion; on a fold it is a
    // foreign coordinate-space artifact.
    private const double KeyboardFullHeightRatio = 0.9;

    private static readonly string _keyboardReadyScript =
        "var handlers = window.webkit && window.webkit.messageHandlers; " +
        $"if (handlers && handlers.{KeyboardReadyMessageName}) {{ handlers.{KeyboardReadyMessageName}.postMessage(1); }}";

    private WKWebView? _webView;
    private WKWebView? _keyboardBridgeWebView;
    private NSObject? _keyboardFrameObserver;
    private NSObject? _keyboardHideObserver;
    private NSObject? _foregroundObserver;
    private IosKeyboardState _keyboardState = IosKeyboardState.Closed;
    private IosKeyboardState? _appliedKeyboardState;
    private bool _applyingKeyboardState;
    private bool _keyboardStateQueued;
    private double _lastLayoutWidth = double.NaN;
    private double _lastLayoutHeight = double.NaN;
#endif

    public MainPage()
    {
        InitializeComponent();
#if IOS
        // The web app owns the keyboard band. MAUI's SoftInput safe area shrinks this view to
        // zero height after a fold; Container keeps the hardware insets only.
        SafeAreaEdges = new Microsoft.Maui.SafeAreaEdges(Microsoft.Maui.SafeAreaRegions.Container);
        Loaded += OnPageLoaded;
#endif
    }

#if IOS
    protected override void OnAppearing()
    {
        base.OnAppearing();
        AttachKeyboardObservers();
        AttachKeyboardBridge();
        ReplayKeyboardState();
        RefreshOrientationPolicy();
    }

    protected override void OnDisappearing()
    {
        DetachKeyboardObservers();
        base.OnDisappearing();
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (Math.Abs(width - _lastLayoutWidth) < 0.5 && Math.Abs(height - _lastLayoutHeight) < 0.5)
        {
            return;
        }

        _lastLayoutWidth = width;
        _lastLayoutHeight = height;
        Dispatcher.Dispatch(RefreshOrientationPolicy);
    }

    private void OnPageLoaded(object? sender, EventArgs e)
    {
        Dispatcher.Dispatch(RefreshOrientationPolicy);
    }

    private void RefreshOrientationPolicy()
    {
        var window = _webView?.Window ?? Window?.Handler?.PlatformView as UIWindow;
        IosOrientationPolicy.Refresh(window);
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
        RefreshOrientationPolicy();
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
        _keyboardHideObserver = UIKeyboard.Notifications.ObserveWillHide((_, _) => SetKeyboardState(IosKeyboardState.Closed));
        _foregroundObserver = UIApplication.Notifications.ObserveWillEnterForeground((_, _) =>
        {
            ReplayKeyboardState();
            RefreshOrientationPolicy();
        });
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
        SetKeyboardState(ClassifyKeyboardFrame(keyboardFrame));
    }

    private IosKeyboardState ClassifyKeyboardFrame(CGRect keyboardFrame)
    {
        var window = _webView?.Window;
        var screen = window?.Screen;
        if (window is null || screen is null)
        {
            return IosKeyboardState.Unknown;
        }

        var bounds = window.Bounds;
        if (!IsUsableRectangle(bounds) || !IsUsableRectangle(keyboardFrame))
        {
            return IosKeyboardState.Unknown;
        }

        var frameInWindow = window.ConvertRectFromCoordinateSpace(keyboardFrame, screen.CoordinateSpace);
        if (!IsUsableRectangle(frameInWindow))
        {
            return IosKeyboardState.Unknown;
        }

        var visible = CGRect.Intersect(frameInWindow, bounds);
        if (visible.Width <= 0 || visible.Height <= 0)
        {
            return IosKeyboardState.Unknown;
        }

        if (visible.Height >= bounds.Height * (nfloat)KeyboardFullHeightRatio)
        {
            return IosKeyboardState.Unknown;
        }

        return visible.Height < (nfloat)SoftwareKeyboardMinimumHeight
            ? IosKeyboardState.Unknown
            : IosKeyboardState.Open;
    }

    private static bool IsUsableRectangle(CGRect rect)
        => IsFinite(rect.X)
            && IsFinite(rect.Y)
            && IsFinite(rect.Width)
            && IsFinite(rect.Height)
            && rect.Width > 0
            && rect.Height > 0;

    private static bool IsFinite(nfloat value)
    {
        var number = (double)value;
        return !double.IsNaN(number) && !double.IsInfinity(number);
    }

    private void SetKeyboardState(IosKeyboardState state)
    {
        if (_keyboardState == state && _appliedKeyboardState == state)
        {
            return;
        }

        _keyboardState = state;
        PushKeyboardState();
    }

    private void ReplayKeyboardState()
    {
        _appliedKeyboardState = null;
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
                var state = _keyboardState;
                if (await EvaluateKeyboardStateAsync(state))
                {
                    _appliedKeyboardState = state;
                }
            }
            while (_keyboardStateQueued);
        }
        finally
        {
            _applyingKeyboardState = false;
        }
    }

    private async Task<bool> EvaluateKeyboardStateAsync(IosKeyboardState state)
    {
        var webView = _webView;
        if (webView is null)
        {
            return false;
        }

        var script = BuildKeyboardStateScript(state);
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

    private static string BuildKeyboardStateScript(IosKeyboardState state)
    {
        var token = state switch
        {
            IosKeyboardState.Open => "open",
            IosKeyboardState.Closed => "closed",
            _ => "unknown",
        };

        return $"document.documentElement.setAttribute('{KeyboardStateAttributeName}', '{token}');"
            + $"window.dispatchEvent(new Event('{KeyboardStateChangeEventName}'));";
    }

    private enum IosKeyboardState
    {
        Unknown,
        Open,
        Closed,
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
