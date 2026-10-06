using OICQStickerManager.Services;
using System.Windows;

internal static class FocusSettlementTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Closing a panel cannot reuse an earlier quiet focus interval", () => Program.RunAsync(EarlierQuietDoesNotSettle())),
        ("A late focus change extends settlement without waiting forever", () => Program.RunAsync(LateFocusDelaysSettlement())),
        ("Slow focus probes share one overall deadline", SlowProbesShareDeadline),
        ("Legacy close resolves the same button from all three tabs", LegacyTabsShareButton),
        ("Legacy tab offsets scale without accepting unknown labels", LegacyTabDpiAndUnknown),
        ("Legacy tab anchoring leaves the native panel unobstructed", LegacyTabAnchorAvoidsOverlap),
        ("Legacy toolbar anchoring uses the native panel extent", LegacyToolbarAnchor),
        ("Legacy toolbar anchoring scales and rejects unusable geometry", LegacyToolbarAnchorValidation),
        ("A cold legacy editor gets one guarded focus recovery retry", () => Program.RunAsync(ColdEditorRetry())),
        ("New user activity during focus settlement cancels the retry", () => Program.RunAsync(NewActivityCancelsRetry())),
    ];

    private static async Task EarlierQuietDoesNotSettle()
    {
        long now = 10000;
        await WindowService.WaitForFocusSettlementAsync(() => now, () => 0,
            ms => { now += ms; return Task.CompletedTask; });
        Program.Require(now >= 10720 && now <= 10900, "focus settled immediately using an old quiet interval");
    }
    private static async Task LateFocusDelaysSettlement()
    {
        long now = 10000, focus = 0;
        await WindowService.WaitForFocusSettlementAsync(() => now, () => focus, ms =>
            {
                now += ms;
                if (now >= 10500 && now <= 11400) focus = now;
                return Task.CompletedTask;
            });
        Program.Require(now >= 11620 && now <= 11680, "late focus changes were ignored or exceeded the bounded deadline");
    }

    private static void SlowProbesShareDeadline()
    {
        long now = 10000;
        int probes = 0;
        bool verified = WindowService.PollEditorFocus(remaining =>
            { probes++; now += Math.Min(700, remaining); return false; }, () => now, ms => now += ms, 1000);
        Program.Require(!verified && now == 11000 && probes == 2,
            "each slow probe got a fresh timeout instead of the remaining overall deadline");
    }

    private static Rect NormalizeTab(string name, Rect rect, double scale)
    {
        return QqPanelWatcher.NormalizeLegacyTabRect(name, rect, scale);
    }

    private static void LegacyTabsShareButton()
    {
        var expected = new Rect(339, 678, 24, 24);
        Program.Require(NormalizeTab("切换默认表情按钮", expected, 1) == expected, "default tab moved");
        Program.Require(NormalizeTab("切换我的收藏按钮", new(393, 678, 24, 24), 1) == expected, "favorite tab targets another toolbar button");
        Program.Require(NormalizeTab("切换GIF热图按钮", new(447, 678, 24, 24), 1) == expected, "GIF tab targets another toolbar button");
    }

    private static void LegacyTabDpiAndUnknown()
    {
        Program.Require(NormalizeTab("切换GIF热图按钮", new(-106, 400, 36, 36), 1.5) == new Rect(-268, 400, 36, 36),
            "150-percent tab normalization lost scaling or negative coordinates");
        Program.Require(NormalizeTab("unknown", new(447, 678, 24, 24), 1).IsEmpty,
            "an unknown label can synthesize an unverified close target");
    }

    private static void LegacyTabAnchorAvoidsOverlap()
    {
        var native = QqPanelWatcher.LegacyPanelRectFromTab(new Rect(1571, 808, 24, 24), 1.0);
        Program.Require(native == new Rect(1547, 508, 450, 336), "native panel estimate differs from the observed old QQ geometry");
        var placement = PanelPlacement.Place(new Rect(0, 0, 3840, 2160), new Size(680, 540),
            native.TopLeft, 1, true, native.Width);
        Program.Require(placement.Right <= native.Left, "coexist panel covers the native default/favorite tabs");
    }

    private static Rect PanelFromToolbar(Rect button, double scale)
    {
        return QqPanelWatcher.LegacyPanelRectFromButton(button, scale);
    }

    private static void LegacyToolbarAnchor()
    {
        var native = PanelFromToolbar(new Rect(1019, 1007, 24, 24), 1);
        Program.Require(native == new Rect(1004, 663, 450, 336),
            "toolbar anchor differs from the observed 9.9.19 popup");
        var placement = PanelPlacement.Place(new Rect(0, 0, 3440, 1400), new Size(680, 540),
            native.TopLeft, 1, true, native.Width);
        Program.Require(placement.Top == 663 && !placement.IntersectsWith(native),
            "coexist panel is anchored below or over the native popup");
    }

    private static void LegacyToolbarAnchorValidation()
    {
        Program.Require(PanelFromToolbar(new Rect(1011, 999, 40, 40), 1) == new Rect(1004, 663, 450, 336),
            "pixel template padding moves the native panel anchor");
        Program.Require(PanelFromToolbar(new Rect(-1000, 900, 36, 36), 1.5) == new Rect(-1022.5, 384, 675, 504),
            "toolbar anchor lost DPI scaling or negative coordinates");
        Program.Require(PanelFromToolbar(Rect.Empty, 1).IsEmpty && PanelFromToolbar(new Rect(0, 0, 0, 24), 1).IsEmpty
            && PanelFromToolbar(new Rect(0, 0, 24, 24), double.NaN).IsEmpty,
            "unusable toolbar geometry creates a synthetic popup");
    }

    private static Task<bool> Restore(Func<Task<bool>> click, Func<bool> allowed, Func<Task> settle)
    {
        return WindowService.RestoreEditorFocusAsync(click, allowed, settle);
    }

    private static async Task ColdEditorRetry()
    {
        int clicks = 0, settlements = 0;
        bool result = await Restore(() => Task.FromResult(++clicks == 2), () => true,
            () => { settlements++; return Task.CompletedTask; });
        Program.Require(result && clicks == 2 && settlements == 1, "a cold editor was not retried after focus settlement");
        clicks = 0;
        result = await Restore(() => { clicks++; return Task.FromResult(false); }, () => true, () => Task.CompletedTask);
        Program.Require(!result && clicks == 2, "focus recovery retries without a bounded attempt count");
    }

    private static async Task NewActivityCancelsRetry()
    {
        int clicks = 0;
        bool allowed = true;
        bool result = await Restore(() => { clicks++; return Task.FromResult(false); }, () => allowed,
            () => { allowed = false; return Task.CompletedTask; });
        Program.Require(!result && clicks == 1, "focus recovery overrode new user activity during settlement");
        clicks = 0;
        result = await Restore(() => { clicks++; return Task.FromResult(true); }, () => false, () => Task.CompletedTask);
        Program.Require(!result && clicks == 0, "an already superseded send still clicked the editor");
    }
}
