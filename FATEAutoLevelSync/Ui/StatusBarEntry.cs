using System;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FATEAutoLevelSync.Core;
using FATEAutoLevelSync.Game;

namespace FATEAutoLevelSync.Ui;

/// <summary>
/// サーバー情報バー（DTR）の「レベシンク: ON / OFF」。OFF でも隠さない。
/// <para>
/// Umbra を使っていて、その「サーバー情報バー」ウィジェットが本来のバーを隠している場合は、Umbra のツールバーに同じ項目が出る。
/// Umbra 3.1.18 は項目を最初に見つけた時点で「クリックできるか」（OnClick の有無）を決めるので、
/// 項目を作ったのと同じ呼び出しの中で OnClick を付ける（フレームの更新の中で作るので、Umbra の更新と入れ違わない）。
/// </para>
/// </summary>
internal sealed class StatusBarEntry : IDisposable
{
    /// <summary>Dalamud の設定画面（サーバー情報バーの並び・非表示）に出る名前。</summary>
    private const string Title = "FATE自動レベルシンク";

    // 色の目安（0xRRGGBB）。実際の色は UIColor シートから一番近いものを選ぶ
    private const uint OnColorTarget = 0x5CD65C;
    private const uint OffColorTarget = 0x9A9A9A;

    private readonly IDtrBar dtrBar;
    private readonly Action onLeftClick;
    private readonly Action onRightClick;
    private readonly SeString onText;
    private readonly SeString offText;

    private IDtrBarEntry? entry;
    private bool? shownEnabled;
    private string? shownTooltip;
    private bool? shownVisible;

    public StatusBarEntry(IDtrBar dtrBar, GameDataLookup lookup, Action onLeftClick, Action onRightClick)
    {
        this.dtrBar = dtrBar;
        this.onLeftClick = onLeftClick;
        this.onRightClick = onRightClick;
        this.onText = Colored(StatusText.LabelOn, lookup.FindNearestUiColor(OnColorTarget));
        this.offText = Colored(StatusText.LabelOff, lookup.FindNearestUiColor(OffColorTarget));
    }

    /// <summary>
    /// 表示を合わせる。フレームの更新から呼ぶ。変わったときだけ書き換える。
    /// </summary>
    /// <param name="enabled">自動化が ON か（文字を決める）。</param>
    /// <param name="tooltip">ツールチップの文。</param>
    /// <param name="visible">バーに出すか（設定「サーバー情報バーに表示する」）。隠すときも項目は消さず、Shown を false にする（Umbra もこれを見て隠す）。</param>
    public void Update(bool enabled, string tooltip, bool visible)
    {
        if (this.entry == null)
        {
            var created = this.dtrBar.Get(Title);
            created.OnClick = this.OnClick;
            created.Shown = visible;
            this.entry = created;
            this.shownVisible = visible;
        }

        if (this.shownVisible != visible)
        {
            this.entry.Shown = visible;
            this.shownVisible = visible;
        }

        if (this.shownEnabled != enabled)
        {
            this.entry.Text = enabled ? this.onText : this.offText;
            this.shownEnabled = enabled;
        }

        if (this.shownTooltip != tooltip)
        {
            this.entry.Tooltip = new SeStringBuilder().AddText(tooltip).Build();
            this.shownTooltip = tooltip;
        }
    }

    public void Dispose()
    {
        if (this.entry == null)
            return;

        this.entry.OnClick = null;
        this.entry.Remove();
        this.entry = null;
    }

    private void OnClick(DtrInteractionEvent ev)
    {
        var isScroll = ev.ScrollDirection != MouseScrollDirection.None;
        if (InputPolicy.ShouldToggleOnClick(ev.ClickType == MouseClickType.Left, isScroll))
            this.onLeftClick();
        else if (ev.ClickType == MouseClickType.Right && !isScroll)
            this.onRightClick();
    }

    private static SeString Colored(string text, ushort? colorKey)
    {
        if (colorKey is not { } key)
            return new SeStringBuilder().AddText(text).Build();

        return new SeStringBuilder().AddUiForeground(key).AddText(text).AddUiForegroundOff().Build();
    }
}
