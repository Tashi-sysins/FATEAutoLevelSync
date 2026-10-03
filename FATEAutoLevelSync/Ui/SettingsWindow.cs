using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FATEAutoLevelSync.Core;

namespace FATEAutoLevelSync.Ui;

/// <summary>
/// 設定と状態の画面。/fatelevels（引数なし）・バーの右クリック・プラグイン一覧の設定ボタンで開く。
/// 「詳細ログを書く」は隠してあり、題名の文字「FATE自動レベルシンク」を続けて5回クリックすると出る（もう一度5回で隠れる）。
/// </summary>
internal sealed class SettingsWindow : Window
{
    /// <summary>題名に出る文字（### の後ろは ImGui の識別用で表示されない）。</summary>
    private const string TitleText = "FATE自動レベルシンク";

    private static readonly Vector4 OnColor = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 OffColor = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 FaultColor = new(1f, 0.45f, 0.4f, 1f);

    private readonly Plugin plugin;
    private readonly TapSequence titleTaps = new();
    private bool showDebugOption;

    public SettingsWindow(Plugin plugin)
        : base($"{TitleText}###FATEAutoLevelSyncSettings", ImGuiWindowFlags.NoCollapse)
    {
        // 折りたたみを無くしているのは、題名を続けてクリックしたときにダブルクリックで窓が畳まれないようにするため
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 160),
            MaximumSize = new Vector2(900, 900),
        };
    }

    public override void OnClose()
    {
        // 閉じたら隠し項目も隠す（次に開いたときは隠れた状態から）
        this.showDebugOption = false;
        this.titleTaps.Reset();
    }

    public override void Draw()
    {
        this.CountTitleClicks();

        var status = this.plugin.Controller.GetStatus();
        var config = this.plugin.Configuration;

        ImGui.TextColored(status.Enabled ? OnColor : OffColor, StatusText.SettingsLabel(status.Enabled));
        ImGui.SameLine();
        if (ImGui.Button(status.Enabled ? "OFF にする" : "ON にする"))
            this.plugin.SetEnabledByUser(!status.Enabled, ChangeSource.SettingsWindow);

        foreach (var line in StatusText.SettingsLines(status))
        {
            if (status.State == ControllerState.Faulted && line.StartsWith("停止の理由", StringComparison.Ordinal))
                ImGui.TextColored(FaultColor, line);
            else
                ImGui.TextWrapped(line);
        }

        ImGui.Separator();

        var showInBar = config.ShowInServerInfoBar;
        if (ImGui.Checkbox("サーバー情報バーに表示する", ref showInBar))
        {
            config.ShowInServerInfoBar = showInBar;
            this.plugin.RequestSave();
        }

        ImGui.TextDisabled("  オフにすると、バーの「レベシンク：ON/OFF」を隠します（自動レベルシンクの ON/OFF はそのまま）。");

        var restore = config.RestoreEnabledOnLogin;
        if (ImGui.Checkbox("ログイン時に前回の ON/OFF を戻す", ref restore))
        {
            config.RestoreEnabledOnLogin = restore;
            this.plugin.RequestSave();
        }

        ImGui.TextDisabled("  オフ（既定）なら、ログインやプラグインの読み込み直しのたびに OFF で始まります。");

        var mounted = config.SyncWhileMounted;
        if (ImGui.Checkbox("騎乗・飛行中でもシンクする", ref mounted))
        {
            config.SyncWhileMounted = mounted;
            this.plugin.Controller.SyncWhileMounted = mounted;
            this.plugin.RequestSave();
        }

        ImGui.TextDisabled("  オフ（既定）なら、FATE の範囲で降りたときにシンクします（通りすがりの空中ではシンクしない）。");

        if (this.showDebugOption)
        {
            var debug = config.DebugLogging;
            if (ImGui.Checkbox("詳細ログを書く（/xllog で見られる）", ref debug))
            {
                config.DebugLogging = debug;
                this.plugin.RequestSave();
            }
        }
    }

    /// <summary>
    /// 題名の文字の上でのクリックを数える。5回続いたら「詳細ログを書く」を出す・隠す。
    /// 文字の位置は ImGui のタイトルバーの描き方（左の余白から書き始め、題名の寄せ方に従う）から求める。
    /// </summary>
    private void CountTitleClicks()
    {
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left) || !ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            return;

        var style = ImGui.GetStyle();
        var windowPos = ImGui.GetWindowPos();
        var windowWidth = ImGui.GetWindowSize().X;
        var titleBarHeight = ImGui.GetFrameHeight(); // 文字の高さ＋上下の余白＝タイトルバーの高さ
        var textWidth = ImGui.CalcTextSize(TitleText).X;

        // 折りたたみの三角は無い（NoCollapse）ので、左の余白は FramePadding だけ
        var left = windowPos.X + style.FramePadding.X;
        var room = windowWidth - (style.FramePadding.X * 2) - textWidth;
        var textStart = left + (Math.Max(0f, room) * style.WindowTitleAlign.X);

        var mouse = ImGui.GetMousePos();
        var onTitleText = mouse.Y >= windowPos.Y && mouse.Y < windowPos.Y + titleBarHeight
                          && mouse.X >= textStart - 2 && mouse.X <= textStart + textWidth + 2;

        if (!onTitleText)
        {
            // 題名の文字以外を押したら数え直す
            this.titleTaps.Reset();
            return;
        }

        if (this.titleTaps.Tap(DateTime.UtcNow))
            this.showDebugOption = !this.showDebugOption;
    }
}
