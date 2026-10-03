using System;

namespace FATEAutoLevelSync.Core;

/// <summary>
/// /fatelevels コマンドの引数の意味。
/// </summary>
public enum CommandAction
{
    /// <summary>引数なし：設定画面を開く。</summary>
    OpenSettings,
    On,
    Off,
    Toggle,

    /// <summary>知らない引数：使い方を出す（ON/OFF は変えない）。</summary>
    ShowUsage,
}

/// <summary>
/// 入力の決まり。ゲームに触らないので試験できる。
/// </summary>
public static class InputPolicy
{
    /// <summary>
    /// バーの項目が押されたとき、ON/OFF を切り替えるか。左クリックだけを切り替えにする（右クリック・スクロールでは切り替えない）。
    /// </summary>
    public static bool ShouldToggleOnClick(bool isLeftClick, bool isScroll) => isLeftClick && !isScroll;

    /// <summary>
    /// ログイン時（読み込み時にすでにログインしている場合を含む）に ON へ戻すか。
    /// 「前回の状態を復元する」設定が有効で、前回の利用者の選択が ON だったときだけ戻す。既定では戻さない。
    /// </summary>
    public static bool ShouldEnableOnLogin(bool restoreEnabledOnLogin, bool lastEnabled) => restoreEnabledOnLogin && lastEnabled;

    public static CommandAction ParseCommand(string? arguments)
    {
        var a = (arguments ?? string.Empty).Trim();
        if (a.Length == 0)
            return CommandAction.OpenSettings;

        return a.ToLowerInvariant() switch
        {
            "on" => CommandAction.On,
            "off" => CommandAction.Off,
            "toggle" => CommandAction.Toggle,
            "config" => CommandAction.OpenSettings,
            _ => CommandAction.ShowUsage,
        };
    }
}

/// <summary>
/// 設定の読み込み結果。
/// </summary>
/// <param name="Config">使う設定（読めなければ既定値）。</param>
/// <param name="Problem">読めなかった理由。問題が無ければ null。</param>
/// <param name="BackupPath">壊れていたファイルを退避した先。退避していなければ null。</param>
public sealed record ConfigLoadResult<T>(T Config, string? Problem, string? BackupPath);

/// <summary>
/// 設定を安全に読む。読めなかったら既定値（自動化 OFF）で始め、元のファイルは別名で残してから上書きされるようにする。
/// </summary>
public static class SafeConfigLoader
{
    /// <param name="load">読み込み。ファイルが無ければ null を返す。壊れていれば例外を投げてよい。</param>
    /// <param name="fileExists">設定ファイルがあるか。</param>
    /// <param name="backup">壊れたファイルを別名で残す。残した先を返す。</param>
    /// <param name="createDefault">既定値を作る。</param>
    public static ConfigLoadResult<T> Load<T>(Func<T?> load, Func<bool> fileExists, Func<string?> backup, Func<T> createDefault)
        where T : class
    {
        string problem;
        try
        {
            var loaded = load();
            if (loaded != null)
                return new ConfigLoadResult<T>(loaded, null, null);
            if (!fileExists())
                return new ConfigLoadResult<T>(createDefault(), null, null);
            problem = "設定ファイルの中身を読めなかった（空か、形式が違う）";
        }
        catch (Exception ex)
        {
            problem = $"設定ファイルを読めなかった：{Unwrap(ex).GetType().Name}: {Unwrap(ex).Message}";
        }

        string? backupPath = null;
        try
        {
            backupPath = backup();
        }
        catch (Exception ex)
        {
            problem += $"（壊れたファイルの退避にも失敗：{ex.Message}）";
        }

        return new ConfigLoadResult<T>(createDefault(), problem, backupPath);
    }

    private static Exception Unwrap(Exception ex)
        => ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
}
