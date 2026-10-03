using System.Collections.Generic;
using System.Linq;

namespace FATEAutoLevelSync.Core;

/// <summary>
/// 画面に出す文言。主表示（バーの文字）は「自動化が ON か」だけを表し、実際にシンクしているかは別の行で出す。
/// </summary>
public static class StatusText
{
    public const string LabelOn = "レベシンク: ON";
    public const string LabelOff = "レベシンク: OFF";

    public static string BarLabel(bool enabled) => enabled ? LabelOn : LabelOff;

    /// <summary>設定画面の先頭の表示。</summary>
    public static string SettingsLabel(bool enabled) => enabled ? "レベシンク自動化：ON" : "レベシンク自動化：OFF";

    /// <summary>
    /// 設定画面に出す状態の行。止まった理由・いまの様子・対象の FATE だけを出す
    /// （「自動化：」「実際のシンク：」の行は出さない。実際のシンクはバーのツールチップで見る）。
    /// </summary>
    public static IEnumerable<string> SettingsLines(ControllerStatus status)
    {
        if (status.State == ControllerState.Faulted && status.FaultReason != null)
            yield return $"停止の理由：{status.FaultReason}";
        else if (status.State != ControllerState.Disabled && status.Detail.Length > 0)
            yield return $"いまの様子：{status.Detail}";
        if (status.FateName != null)
            yield return $"対象の FATE：{status.FateName}（ID {status.FateId}）";
    }

    public static string StateName(ControllerState state) => state switch
    {
        ControllerState.Disabled => "OFF",
        ControllerState.Waiting => "FATE を待っている",
        ControllerState.Ready => "シンクの準備中",
        ControllerState.Requested => "要求済み・成立を待っている",
        ControllerState.Handled => "この FATE では処理済み",
        ControllerState.Suspended => "一時停止中",
        ControllerState.Faulted => "異常で停止",
        _ => state.ToString(),
    };

    /// <summary>キャラクターが実際にシンクしているか（自動化の ON/OFF とは別）。</summary>
    public static string ActualSync(GameSnapshot? snapshot)
    {
        if (snapshot == null || snapshot.Kind == SnapshotKind.Unknown)
            return "実際のシンク：不明（状態を読めていない）";
        if (!snapshot.IsLevelSynced)
            return "実際のシンク：していない";
        if (snapshot.SyncedToCurrentFate && snapshot.Fate != null)
            return $"実際のシンク：している（この FATE・上限 Lv{snapshot.Fate.MaxLevel}）";
        return snapshot.SyncedFateId != 0
            ? $"実際のシンク：している（FATE ID {snapshot.SyncedFateId}）"
            : "実際のシンク：している（FATE 以外のシンク）";
    }

    /// <summary>状態の説明の行（理由・異常など）。</summary>
    public static IEnumerable<string> StatusLines(ControllerStatus status, GameSnapshot? snapshot)
    {
        yield return $"自動化：{(status.Enabled ? "ON" : "OFF")}（{StateName(status.State)}）";
        if (status.State == ControllerState.Faulted && status.FaultReason != null)
            yield return $"停止の理由：{status.FaultReason}";
        else if (status.State != ControllerState.Disabled && status.Detail.Length > 0)
            yield return $"いまの様子：{status.Detail}";
        if (status.FateName != null)
            yield return $"対象の FATE：{status.FateName}（ID {status.FateId}）";
        yield return ActualSync(snapshot);
    }

    public static string Tooltip(ControllerStatus status, GameSnapshot? snapshot)
    {
        var head = new[]
        {
            "FATE自動レベルシンク",
            "左クリックで ON/OFF を切り替え（右クリックで設定を開く）",
            "OFF にしても、いまのシンクは解除しません",
            string.Empty,
        };
        return string.Join("\n", head.Concat(StatusLines(status, snapshot)));
    }
}
