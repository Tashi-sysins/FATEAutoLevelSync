using System;

namespace FATEAutoLevelSync.Core;

/// <summary>
/// 判断側（SyncController）の処理状態。
/// 「自動化が ON か」（Enabled）とは別物で、キャラクターが実際にシンクしているかとも別物。
/// </summary>
public enum ControllerState
{
    /// <summary>自動化が OFF。</summary>
    Disabled,

    /// <summary>ON。対象の FATE が無い。</summary>
    Waiting,

    /// <summary>ON。対象の FATE があり、まだ要求を出していない（条件待ちを含む）。</summary>
    Ready,

    /// <summary>ON。今回の開始要求を送った。成立を見ている。</summary>
    Requested,

    /// <summary>ON。この FATE（この侵入）では処理を終えた。</summary>
    Handled,

    /// <summary>ON。一時的に安全な判断ができない（ロード中など）。処理済みの記録は保つ。</summary>
    Suspended,

    /// <summary>異常で停止した。自動化は OFF になっている。自動では戻らない。</summary>
    Faulted,
}

/// <summary>
/// この FATE（この侵入）での処理を終えた理由。
/// </summary>
public enum HandledReason
{
    /// <summary>要求を送り、シンクの成立を確かめた。</summary>
    Confirmed,

    /// <summary>侵入した時点ですでにこの FATE にシンクしていた（手動のシンクを含む）。</summary>
    AlreadySynced,

    /// <summary>レベルが上限以下なのでシンクは要らない。</summary>
    NotNeeded,

    /// <summary>いまのクラス・ジョブはこの FATE の対象外。</summary>
    JobNotAllowed,

    /// <summary>確かめていない種類の FATE・エリアなので動かない。</summary>
    Unsupported,

    /// <summary>要求を送ったが、上限時間内に成立を確かめられなかった。再送はしない。</summary>
    TimedOut,
}

/// <summary>
/// ON/OFF を変えたきっかけ（記録用）。
/// </summary>
public enum ChangeSource
{
    BarClick,
    Command,
    SettingsWindow,
    LoginRestore,
    Logout,
    Unload,
}

/// <summary>
/// ゲームへ「レベルシンク開始」を頼む口。解除の手段はわざと持たせていない（このプラグインは解除をしない）。
/// </summary>
public interface ILevelSyncSender
{
    /// <summary>開始の要求を1回送る。送れなかったら例外を投げる。</summary>
    void SendLevelSyncOn();
}

/// <summary>
/// 記録の口。Debug は詳細ログが有効なときだけ書かれる想定。
/// </summary>
public interface IControllerLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message);

    void Debug(string message);
}

/// <summary>
/// 判定から送信までの間に持ち回る札。送信の直前に、この札がまだ有効かを確かめる。
/// </summary>
public sealed record SyncTicket(int SessionNumber, FateOccurrence Occurrence, ZoneKey Zone, long Generation);

/// <summary>
/// 画面に出すための判断側の状態の写し。
/// </summary>
public sealed record ControllerStatus
{
    public bool Enabled { get; init; }

    public ControllerState State { get; init; }

    /// <summary>状態の説明（待っている理由・処理を終えた理由・止まった理由など）。</summary>
    public string Detail { get; init; } = string.Empty;

    public HandledReason? Handled { get; init; }

    public int? SessionNumber { get; init; }

    public string? FateName { get; init; }

    public ushort? FateId { get; init; }

    public string? FaultReason { get; init; }

    /// <summary>異常で止まった回数。同じ理由で続けて止まっても、そのたびに知らせるために使う。</summary>
    public int FaultCount { get; init; }

    /// <summary>このプラグインを読み込んでから送った開始要求の回数。</summary>
    public int RequestCount { get; init; }

    public DateTime? LastRequestAt { get; init; }

    public long Generation { get; init; }
}

/// <summary>
/// 時間の決まり。時間は「諦めるまでの上限」と「境界の揺れを無視する幅」にだけ使う。
/// </summary>
public sealed class ControllerTimings
{
    /// <summary>範囲外がこの長さ続いたら、FATE から出たと確定する。</summary>
    public TimeSpan ExitDebounce { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>要求を送ってからこの長さのうちに成立を確かめられなければ、その侵入では諦める（再送しない）。</summary>
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>本来読めるはずの内部の参照が、この長さ続けて読めなければ異常として止める。</summary>
    public TimeSpan IncompatibilityLimit { get; init; } = TimeSpan.FromSeconds(10);
}
