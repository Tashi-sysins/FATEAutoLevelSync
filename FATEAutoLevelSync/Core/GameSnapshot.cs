namespace FATEAutoLevelSync.Core;

/// <summary>
/// ゲームの状態を1回読んだ結果の種類。
/// </summary>
public enum SnapshotKind
{
    /// <summary>安全に読めなかった（ロード中・ログアウト中・内部の参照が無い等）。FATE からの退出とはみなさない。</summary>
    Unknown,

    /// <summary>ゲームが「いま参加している」と認識している FATE が無い。</summary>
    Outside,

    /// <summary>ゲームが「いま参加している」と認識している FATE がある。</summary>
    Inside,
}

/// <summary>
/// エリアの区別。インスタンス（エリアの複製の番号）が変わっても別のエリアとして扱う。
/// </summary>
public readonly record struct ZoneKey(uint TerritoryType, uint Instance);

/// <summary>
/// FATE の「発生」を区別する鍵。同じ FateId でも開始時刻が違えば別の発生として扱う。
/// </summary>
public readonly record struct FateOccurrence(ushort FateId, int StartTimeEpoch);

/// <summary>
/// ゲームが認識している FATE 1件の、判断に使う値だけを写したもの。
/// </summary>
public sealed record FateView
{
    public ushort FateId { get; init; }

    /// <summary>開始時刻（Unix 秒）。準備中の FATE では 0 のことがある。</summary>
    public int StartTimeEpoch { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>ゲーム内部の進行状態の生の値（表示と記録用）。</summary>
    public byte StateValue { get; init; }

    /// <summary>進行中（敵が出ている段階）か。</summary>
    public bool IsRunning { get; init; }

    /// <summary>通常の FATE か（ゲーム内部の FateMode が 0）。エウレカ等の特殊な FATE は false。</summary>
    public bool IsStandardMode { get; init; }

    /// <summary>
    /// ゲームの /levelsync が通常とは別の経路で処理する FATE か（季節イベント等）。
    /// null は判定できなかったことを表す。
    /// </summary>
    public bool? HasSpecialSyncPath { get; init; }

    /// <summary>前提クエスト（0 なら無し）。</summary>
    public ushort RequiredQuest { get; init; }

    /// <summary>シンク後の上限レベル。</summary>
    public byte MaxLevel { get; init; }

    /// <summary>いまのクラス・ジョブがこの FATE のジョブ条件に合うか。null は判定できなかったことを表す。</summary>
    public bool? JobAllowed { get; init; }

    public FateOccurrence Occurrence => new(this.FateId, this.StartTimeEpoch);
}

/// <summary>
/// 1回の読み取りで得たゲームの状態。読み取り側（Game\GameStateReader）が作り、判断側（SyncController）が使う。
/// 判断側はこの値だけを見て、ゲームには触らない。
/// </summary>
public sealed record GameSnapshot
{
    public SnapshotKind Kind { get; init; }

    /// <summary>Unknown のときの理由（表示と記録用）。</summary>
    public string? UnknownReason { get; init; }

    /// <summary>
    /// Unknown の理由が「本来は読めるはずの内部の参照が無い」（ゲーム更新との不一致の疑い）か。
    /// ロード中などの普通の理由なら false。
    /// </summary>
    public bool UnknownLooksLikeIncompatibility { get; init; }

    public ZoneKey Zone { get; init; }

    /// <summary>通常のフィールド（ゲーム内部の領域の用途が Overworld）か。</summary>
    public bool TerritoryIsOpenField { get; init; }

    /// <summary>ゲームが認識している FATE。Outside のときは null。</summary>
    public FateView? Fate { get; init; }

    /// <summary>いまの実効レベル（シンク中ならシンク後のレベル）。ゲームのシンク可否の判定が使うのと同じ値。</summary>
    public int PlayerLevel { get; init; }

    /// <summary>何かにレベルシンクしているか（FATE 以外も含む）。</summary>
    public bool IsLevelSynced { get; init; }

    /// <summary>いま認識している FATE にシンクしているか（ゲーム自身の判定と同じ式で求めた値）。</summary>
    public bool SyncedToCurrentFate { get; init; }

    /// <summary>ゲームが記録している「シンク中の FATE の番号」（表示と記録用）。</summary>
    public ushort SyncedFateId { get; init; }

    public bool IsDead { get; init; }

    /// <summary>騎乗中または飛行中。</summary>
    public bool IsMountedOrFlying { get; init; }

    /// <summary>会話・カットシーン・イベントなどで操作がふさがっている。</summary>
    public bool IsOccupied { get; init; }
}
