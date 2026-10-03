using System;
using System.IO;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FATEAutoLevelSync.Core;
using FATEAutoLevelSync.Game;
using FATEAutoLevelSync.Ui;

namespace FATEAutoLevelSync;

/// <summary>
/// FATE自動レベルシンク。画面のバーの「レベシンク: ON / OFF」で自動化を切り替え、ON のときだけ FATE に入ったらレベルシンクする。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/fatelevels";
    private const string ChatTag = "FATE自動レベルシンク";

    /// <summary>判断の間隔。表示の切り替えはこれを待たずに次のフレームで反映する。</summary>
    private static readonly TimeSpan EvaluateInterval = TimeSpan.FromMilliseconds(200);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private readonly GameStateReader reader;
    private readonly StatusBarEntry bar;
    private readonly WindowSystem windowSystem = new("FATEAutoLevelSync");
    private readonly SettingsWindow settingsWindow;

    private DateTime nextEvaluateAt = DateTime.MinValue;
    private bool loginRestoreApplied;
    private volatile bool saveRequested;
    private int announcedFaultCount;

    // 表示を作り直すかの判定用（毎フレーム文言を作らない）
    private long shownRevision = -1;
    private string? actualSyncText;
    private string? shownActualSyncText;
    private bool? shownBarVisible;

    // 同じ例外を毎フレーム記録しないための控え
    private string? lastErrorText;
    private DateTime lastErrorLoggedAt;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        IPlayerState playerState,
        ICondition condition,
        IDataManager dataManager,
        IDtrBar dtrBar,
        ICommandManager commandManager,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.clientState = clientState;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.log = log;

        this.Configuration = this.LoadConfiguration();

        var lookup = new GameDataLookup(dataManager, log);

        // ON にする前の確認（ゲームに /levelsync があるか）を読み込み時に1回だけ済ませておく（結果は覚えておく）
        if (lookup.CheckLevelSyncCommand() is { } problem)
            log.Warning($"{problem}。このままでは ON にできない");

        this.Controller = new SyncController(
            new LevelSyncCommandSender(framework),
            new ControllerLog(log, () => this.Configuration.DebugLogging),
            prerequisiteCheck: lookup.CheckLevelSyncCommand)
        {
            SyncWhileMounted = this.Configuration.SyncWhileMounted,
        };

        this.reader = new GameStateReader(clientState, objectTable, playerState, condition, lookup);
        this.bar = new StatusBarEntry(dtrBar, lookup, this.OnBarLeftClick, this.OpenSettings);

        this.settingsWindow = new SettingsWindow(this);
        this.windowSystem.AddWindow(this.settingsWindow);
        pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += this.OpenSettings;
        pluginInterface.UiBuilder.OpenMainUi += this.OpenSettings;

        commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            // プラグイン一覧に「/fatelevels → この文」の形で出る。使い方ごとに1行にする。
            // 1行目は「/fatelevels → 設定画面を開く」になり、2行目からは改行の後ろに自分で「コマンド → 意味」を書く
            HelpMessage = string.Join(
                "\n",
                "設定画面を開く",
                $"{CommandName} on → 自動レベルシンクを ON",
                $"{CommandName} off → 自動レベルシンクを OFF",
                $"{CommandName} toggle → ON/OFF を切り替え"),
        });

        clientState.Login += this.OnLogin;
        clientState.Logout += this.OnLogout;
        framework.Update += this.OnFrameworkUpdate;

        log.Information("読み込んだ。自動レベルシンクは OFF で始める");
    }

    /// <summary>判断の正本。</summary>
    internal SyncController Controller { get; }

    internal Configuration Configuration { get; }

    /// <summary>最後に読んだゲームの状態（表示用）。</summary>
    internal GameSnapshot? LastSnapshot { get; private set; }

    public void Dispose()
    {
        // 先に更新を止める（片づけの途中で判断や送信が走らないように）
        this.framework.Update -= this.OnFrameworkUpdate;
        this.clientState.Login -= this.OnLogin;
        this.clientState.Logout -= this.OnLogout;
        this.commandManager.RemoveHandler(CommandName);

        this.pluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenSettings;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenSettings;
        this.windowSystem.RemoveAllWindows();

        this.bar.Dispose();

        // 送らなくなるだけ。いまのレベルシンクは解除しない
        this.Controller.Shutdown();

        if (this.saveRequested)
            this.SaveConfiguration();
    }

    /// <summary>
    /// 利用者の操作で ON/OFF を変える（設定画面・コマンド）。変わったら「前回の選択」として保存する。
    /// </summary>
    internal bool SetEnabledByUser(bool on, ChangeSource source)
    {
        var changed = this.Controller.SetEnabled(on, source, DateTime.UtcNow);
        if (changed)
            this.RememberChoice();
        return changed;
    }

    /// <summary>次のフレームで保存する（毎フレームは書かない。保存は変わったときの1回だけ）。</summary>
    internal void RequestSave() => this.saveRequested = true;

    private void OnBarLeftClick()
    {
        // ほかのプラグイン（Umbra）の描画の中から呼ばれることもあるので、例外を外へ出さない
        try
        {
            if (this.Controller.Toggle(ChangeSource.BarClick, DateTime.UtcNow))
                this.RememberChoice();
        }
        catch (Exception ex)
        {
            this.LogErrorThrottled(ex, "バーのクリックの処理で例外");
        }
    }

    private void OpenSettings() => this.settingsWindow.IsOpen = true;

    private void RememberChoice()
    {
        this.Configuration.LastEnabled = this.Controller.Enabled;
        this.RequestSave();
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        try
        {
            if (!this.loginRestoreApplied && this.clientState.IsLoggedIn)
            {
                this.loginRestoreApplied = true;
                this.ApplyLoginRestore();
            }

            var now = DateTime.UtcNow;
            if (now >= this.nextEvaluateAt)
            {
                this.nextEvaluateAt = now + EvaluateInterval;
                var snapshot = this.reader.Read();
                this.LastSnapshot = snapshot;
                this.actualSyncText = StatusText.ActualSync(snapshot);
                this.Controller.Tick(snapshot, now);
            }

            // 切り替え・表示の設定の変更は次のフレームで表示に出る（判断の間隔を待たない）
            var revision = this.Controller.Revision;
            var barVisible = this.Configuration.ShowInServerInfoBar;
            if (revision != this.shownRevision || this.actualSyncText != this.shownActualSyncText || barVisible != this.shownBarVisible)
            {
                this.shownRevision = revision;
                this.shownActualSyncText = this.actualSyncText;
                this.shownBarVisible = barVisible;
                var status = this.Controller.GetStatus();
                this.AnnounceFaultOnce(status);
                this.bar.Update(status.Enabled, StatusText.Tooltip(status, this.LastSnapshot), barVisible);
            }

            if (this.saveRequested)
            {
                this.saveRequested = false;
                this.SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            this.LogErrorThrottled(ex, "フレームの更新で例外");

            // 内部の異常で止まったら、表示も OFF にする（ON の表示のまま処理が壊れている状態を作らない）
            if (this.Controller.Enabled)
                this.Controller.Fault($"内部の例外：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>同じ例外は 60 秒に1回だけ記録する。</summary>
    private void LogErrorThrottled(Exception ex, string where)
    {
        var text = $"{ex.GetType().FullName}: {ex.Message}";
        var now = DateTime.UtcNow;
        if (text == this.lastErrorText && now - this.lastErrorLoggedAt < TimeSpan.FromSeconds(60))
            return;

        this.lastErrorText = text;
        this.lastErrorLoggedAt = now;
        this.log.Error(ex, where);
    }

    private void OnLogin()
    {
        this.loginRestoreApplied = true;
        this.ApplyLoginRestore();
    }

    private void OnLogout(int type, int code)
    {
        // 再ログインのときは既定で OFF から始める。「前回の選択」は書き換えない（復元の設定のため）
        this.Controller.SetEnabled(false, ChangeSource.Logout, DateTime.UtcNow);
        this.loginRestoreApplied = false;
    }

    private void ApplyLoginRestore()
    {
        if (!InputPolicy.ShouldEnableOnLogin(this.Configuration.RestoreEnabledOnLogin, this.Configuration.LastEnabled))
            return;

        if (this.Controller.SetEnabled(true, ChangeSource.LoginRestore, DateTime.UtcNow))
            this.log.Information("設定により、前回の ON を戻した");
    }

    /// <summary>異常で止まるたびに1回だけチャットへ知らせる（同じ理由で続けて止まっても、そのたびに知らせる）。</summary>
    private void AnnounceFaultOnce(ControllerStatus status)
    {
        if (status.FaultCount == this.announcedFaultCount)
            return;

        this.announcedFaultCount = status.FaultCount;
        if (status.FaultReason != null)
            this.chatGui.PrintError($"異常のため OFF にしました：{status.FaultReason}", ChatTag);
    }

    private void OnCommand(string command, string arguments)
    {
        switch (InputPolicy.ParseCommand(arguments))
        {
            case CommandAction.OpenSettings:
                this.OpenSettings();
                break;
            case CommandAction.On:
                this.ReportCommandResult(this.SetEnabledByUser(true, ChangeSource.Command));
                break;
            case CommandAction.Off:
                this.ReportCommandResult(this.SetEnabledByUser(false, ChangeSource.Command));
                break;
            case CommandAction.Toggle:
                this.ReportCommandResult(this.SetEnabledByUser(!this.Controller.Enabled, ChangeSource.Command));
                break;
            default:
                this.chatGui.Print($"使い方：{CommandName}（設定画面）／{CommandName} on｜off｜toggle", ChatTag);
                break;
        }
    }

    private void ReportCommandResult(bool changed)
    {
        var status = this.Controller.GetStatus();
        if (status.State == ControllerState.Faulted)
            return; // 理由は次のフレームでチャットに出る

        var label = StatusText.BarLabel(status.Enabled);
        this.chatGui.Print(changed ? label : $"{label}（すでにこの状態）", ChatTag);
    }

    private Configuration LoadConfiguration()
    {
        var file = this.pluginInterface.ConfigFile;
        var result = SafeConfigLoader.Load(
            () => this.pluginInterface.GetPluginConfig() as Configuration,
            () => File.Exists(file.FullName),
            () =>
            {
                var backup = $"{file.FullName}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(file.FullName, backup, overwrite: false);
                return backup;
            },
            () => new Configuration());

        if (result.Problem != null)
        {
            this.log.Warning($"{result.Problem}。OFF・既定の設定で始める。元のファイルは {result.BackupPath ?? "（退避できなかった）"} に残した");
        }

        var config = result.Config;
        if (config.Version < Configuration.CurrentVersion)
            config.Version = Configuration.CurrentVersion;
        return config;
    }

    private void SaveConfiguration()
    {
        try
        {
            this.pluginInterface.SavePluginConfig(this.Configuration);
        }
        catch (Exception ex)
        {
            // 保存に失敗しても、いまの ON/OFF はそのまま（OFF の操作を ON へ戻したりしない）
            this.log.Warning($"設定を保存できなかった：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>判断側の記録を Dalamud の記録へ渡す。詳細は設定で有効にしたときだけ書く。</summary>
    private sealed class ControllerLog(IPluginLog log, Func<bool> debugEnabled) : IControllerLog
    {
        public void Info(string message) => log.Information(message);

        public void Warn(string message) => log.Warning(message);

        public void Error(string message) => log.Error(message);

        public void Debug(string message)
        {
            if (debugEnabled())
                log.Information($"[詳細] {message}");
        }
    }
}
