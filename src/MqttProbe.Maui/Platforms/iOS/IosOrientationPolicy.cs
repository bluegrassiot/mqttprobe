#if IOS
using System.Runtime.CompilerServices;
using UIKit;

namespace MqttProbe;

public static class IosOrientationPolicy
{
    private static readonly ConditionalWeakTable<UIWindow, StrongBox<UIInterfaceOrientationMask>> _notifiedMasks = new();

    public static UIInterfaceOrientationMask ForWindow(UIWindow? window, UIUserInterfaceIdiom fallbackIdiom = UIUserInterfaceIdiom.Unspecified)
    {
        var idiom = window?.TraitCollection.UserInterfaceIdiom ?? UIUserInterfaceIdiom.Unspecified;
        if (idiom == UIUserInterfaceIdiom.Unspecified)
        {
            idiom = fallbackIdiom;
        }

        if (idiom == UIUserInterfaceIdiom.Unspecified)
        {
            idiom = UIDevice.CurrentDevice.UserInterfaceIdiom;
        }

        if (idiom == UIUserInterfaceIdiom.Pad)
        {
            return UIInterfaceOrientationMask.All;
        }

        if (idiom != UIUserInterfaceIdiom.Phone)
        {
            return UIInterfaceOrientationMask.Portrait;
        }

        return HasDivision(window)
            ? UIInterfaceOrientationMask.AllButUpsideDown
            : UIInterfaceOrientationMask.Portrait;
    }

    public static void Refresh(UIWindow? window)
    {
        if (window is null || window.RootViewController is not { } root)
        {
            return;
        }

        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            return;
        }

        var mask = ForWindow(window);
        if (_notifiedMasks.TryGetValue(window, out var notified) && notified.Value == mask)
        {
            return;
        }

        _notifiedMasks.AddOrUpdate(window, new StrongBox<UIInterfaceOrientationMask>(mask));

        root.SetNeedsUpdateOfSupportedInterfaceOrientations();
        for (var presented = root.PresentedViewController; presented is not null; presented = presented.PresentedViewController)
        {
            presented.SetNeedsUpdateOfSupportedInterfaceOrientations();
        }
    }

    private static bool HasDivision(UIWindow? window)
    {
        // Division regions are the supported fold check. They are unavailable before iOS 27.1, and
        // a folded outer display may not report them at all, so a negative answer is never cached.
        if (window is null)
        {
            return false;
        }

        if (OperatingSystem.IsMacCatalyst() || !OperatingSystem.IsIOSVersionAtLeast(27, 1))
        {
            return false;
        }

#pragma warning disable CA1416 // CA1416 does not accept this platform guard for the UIKit fold API
        using var kind = UIViewReservedRegionKind.CreateDivision();
        var regions = window.GetReservedRegions(kind, UIViewReservedRegionQueryOptions.IncludeInactive);
#pragma warning restore CA1416
        try
        {
            return regions.Length > 0;
        }
        finally
        {
            foreach (var region in regions)
            {
                region.Dispose();
            }
        }
    }
}
#endif
