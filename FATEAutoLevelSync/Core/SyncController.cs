using System;

namespace FATEAutoLevelSync.Core;

/// <summary>
/// FATE 自動レベルシンクの判断（状態機械）。ゲームには触らない。
/// <list type="bullet">
/// <item>自動化が ON かどうかの正本（Enabled）はここだけが持つ。バー・コマンド・設定画面はすべてここを通して変える。</item>
/// <item>ゲームの状態は <see cref="GameSnapshot"/> で受け取り、送信は <see cref="ILevelSyncSender"/> に頼む。</item>
/// <item>レベルシンクの解除は一切しない（OFF にしても、プラグインを外しても、FATE から出ても）。</item>
/// </list>
/// 画面のクリック（描画の流れ）と毎フレームの更新の両方から呼ばれるため、すべての操作を1つの錠で守る。
/// </summary>
public sealed class SyncController
{
    private readonly object gate = new();
    private readonly ILevelSyncSender sender;
    private readonly IControllerLog log;
    private readonly ControllerTimings timings;
    private readonly Func<string?>? prerequisiteCheck;

    private bool enabled;
    private ControllerState state = ControllerState.Disabled;
    private string detail = "自動レベルシンクは OFF";
    private long generation;
    private Session? session;
    private int sessionCounter;
    private DateTime? outsideSince;
    private ZoneKey? lastZone;
    private PendingRequest? pending;
    private DateTime? incompatibleSince;
    private string? faultReason;
    private int requestCount;
    private DateTime? lastRequestAt;
    private long revision;
    private int faultCount;

    /// <param name="sender">開始要求を送る口。</param>
    /// <param name="log">記録の口。</param>
    /// <param name="timings">時間の決まり。省略時は既定値。</param>
    /// <param name="prerequisiteCheck">ON にする前の確認。問題があれば理由の文を返す（例：/levelsync がゲームに無い）。</param>
    public SyncController(ILevelSyncSender sender, IControllerLog log, ControllerTimings? timings = null, Func<string?>? prerequisiteCheck = null)
    {
        this.sender = sender;
        this.log = log;
        this.timings = timings ?? new ControllerTimings();
        this.prerequisiteCheck = prerequisiteCheck;
    }

    /// <summary>騎乗・飛行中でもシンクするか（設定）。既定は false＝降りるまで待つ。</summary>
    public bool SyncWhileMounted
    {
        get { lock (this.gate) return this.syncWhileMounted; }
        set { lock (this.gate) this.syncWhileMounted = value; }
    }

    private bool syncWhileMounted;

    /// <summary>自動化が ON か（正本）。</summary>
    public bool Enabled
    {
        get { lock (this.gate) return this.enabled; }
    }

    /// <summary>
    /// 表示に関わる値が変わるたびに増える通し番号。表示側はこれが変わったときだけ文言を作り直す（毎フレーム作らない）。
    /// </summary>
    public long Revision
    {
        get { lock (this.gate) return this.revision; }
    }

    public ControllerStatus GetStatus()
    {
        lock (this.gate)
        {
            return new ControllerStatus
            {
                Enabled = this.enabled,
                State = this.state,
                Detail = this.detail,
                Handled = this.session?.Handled,
                SessionNumber = this.session?.Number,
                FateName = this.session?.FateName,
                FateId = this.session?.Occurrence.FateId,
                FaultReason = this.faultReason,
                FaultCount = this.faultCount,
                RequestCount = this.requestCount,
                LastRequestAt = this.lastRequestAt,
                Generation = this.generation,
            };
        }
    }

    /// <summary>
    /// ON/OFF を変える。同じ値を重ねて指定しても何もしない（ON のまま ON にしても処理済みの侵入を戻さない）。
    /// </summary>
    /// <returns>実際に変わったら true。</returns>
    public bool SetEnabled(bool on, ChangeSource source, DateTime now)
    {
        lock (this.gate)
        {
            return this.SetEnabledLocked(on, source, now);
        }
    }

    /// <summary>ON と OFF を入れ替える（バーの左クリック・toggle コマンド）。</summary>
    public bool Toggle(ChangeSource source, DateTime now)
    {
        lock (this.gate)
        {
            return this.SetEnabledLocked(!this.enabled, source, now);
        }
    }

    /// <summary>
    /// 1回分の処理。判定して、要求すべきなら送る。
    /// </summary>
    public void Tick(GameSnapshot snapshot, DateTime now)
    {
        var ticket = this.Evaluate(snapshot, now);
        if (ticket != null)
            this.Commit(ticket, now);
    }

    /// <summary>
    /// 判定だけを行う。要求すべきなら札を返す（まだ送らない）。
    /// </summary>
    public SyncTicket? Evaluate(GameSnapshot s, DateTime now)
    {
        lock (this.gate)
        {
            if (!this.enabled)
                return null;

            if (s.Kind == SnapshotKind.Unknown)
            {
                // 読めないことを「FATE から出た」とはみなさない。出たかどうかの数え直しだけをする
                this.outsideSince = null;
                if (s.UnknownLooksLikeIncompatibility)
                {
                    this.incompatibleSince ??= now;
                    if (now - this.incompatibleSince.Value >= this.timings.IncompatibilityLimit)
                    {
                        this.FaultLocked($"ゲーム内部の状態を {this.timings.IncompatibilityLimit.TotalSeconds:0} 秒以上読めない（{s.UnknownReason}）。ゲームの更新と合っていない可能性がある");
                        return null;
                    }
                }
                else
                {
                    this.incompatibleSince = null;
                }

                this.SetState(ControllerState.Suspended, $"一時停止中：{s.UnknownReason ?? "状態を読めない"}");
                return null;
            }

            this.incompatibleSince = null;

            // エリアが変わったら、古い侵入と送信済みの記録を捨てる（古い要求を後から実行しないため）
            if (this.lastZone != s.Zone)
            {
                if (this.lastZone != null)
                {
                    this.generation++;
                    this.log.Debug($"エリアが変わった（{this.lastZone.Value.TerritoryType}/{this.lastZone.Value.Instance} → {s.Zone.TerritoryType}/{s.Zone.Instance}）。世代 {this.generation}");
                }

                this.lastZone = s.Zone;
                this.session = null;
                this.outsideSince = null;
                this.pending = null;
                this.revision++;
            }

            if (s.Kind == SnapshotKind.Outside || s.Fate == null)
            {
                if (this.session != null)
                {
                    // 境界の揺れで出入りを繰り返さないよう、範囲外が続いたときだけ「出た」と確定する
                    this.outsideSince ??= now;
                    if (now - this.outsideSince.Value >= this.timings.ExitDebounce)
                    {
                        this.log.Info($"FATE「{this.session.FateName}」から出た（侵入 #{this.session.Number} を終了）");
                        this.session = null;
                        this.outsideSince = null;
                        this.revision++;
                    }
                }

                if (this.session == null)
                    this.SetState(ControllerState.Waiting, "FATE の範囲外");
                return null;
            }

            this.outsideSince = null;
            var fate = s.Fate;

            if (this.session == null || this.session.Occurrence != fate.Occurrence)
            {
                if (this.session != null)
                    this.log.Info($"別の FATE（または同じ FATE の別の発生）を認識した。侵入 #{this.session.Number} を終了");

                this.session = new Session(++this.sessionCounter, fate.Occurrence, fate.Name, s.Zone);
                this.revision++;
                this.log.Info($"FATE「{fate.Name}」（ID {fate.FateId}）を認識した（侵入 #{this.session.Number}）");

                // 直前に同じ発生へ送った要求の結果がまだ出ていなければ、送り直さずに結果を待つ
                if (this.pending is { } p && p.Occurrence == fate.Occurrence && p.Zone == s.Zone && now - p.SentAt < this.timings.ConfirmTimeout)
                {
                    this.session.RequestSentAt = p.SentAt;
                    this.log.Debug($"侵入 #{this.session.Number}：{(now - p.SentAt).TotalSeconds:0.0} 秒前に送った要求の結果を待つ（再送しない）");
                }
            }

            var ses = this.session;

            // 処理済みを先に見る（送信済みの侵入も、成立・諦めの後はここで止める。毎回記録し直さないため）
            if (ses.Handled != null)
            {
                this.SetState(ControllerState.Handled, ses.HandledText);
                return null;
            }

            if (ses.RequestSentAt is { } sentAt)
            {
                if (s.SyncedToCurrentFate)
                {
                    this.pending = null;
                    this.MarkHandled(ses, HandledReason.Confirmed, $"レベルシンクの成立を確かめた（要求から {(now - sentAt).TotalSeconds:0.0} 秒）", warn: false);
                }
                else if (now - sentAt >= this.timings.ConfirmTimeout)
                {
                    this.pending = null;
                    this.MarkHandled(ses, HandledReason.TimedOut, $"要求から {this.timings.ConfirmTimeout.TotalSeconds:0} 秒たってもシンクを確かめられなかった（再送しない）", warn: true);
                }
                else
                {
                    this.SetState(ControllerState.Requested, "要求を送った。成立を待っている");
                }

                return null;
            }

            var unsupported = UnsupportedReason(s, fate);
            if (unsupported != null)
            {
                this.MarkHandled(ses, HandledReason.Unsupported, unsupported, warn: false);
                return null;
            }

            // 手動でシンクした場合もここで処理済みにする（あとで手動で解除しても、この侵入では掛け直さない）
            if (s.SyncedToCurrentFate)
            {
                this.MarkHandled(ses, HandledReason.AlreadySynced, "すでにこの FATE にシンクしている", warn: false);
                return null;
            }

            // 別のシンク（前の FATE のシンクが残っている等）の最中はレベルの比較が正しくできないので、解けるのを待つ
            if (s.IsLevelSynced)
            {
                this.SetState(ControllerState.Ready, "別のレベルシンクが効いている。解けるのを待っている");
                return null;
            }

            if (s.PlayerLevel <= fate.MaxLevel)
            {
                this.MarkHandled(ses, HandledReason.NotNeeded, $"レベル {s.PlayerLevel} は上限 {fate.MaxLevel} 以下なのでシンク不要", warn: false);
                return null;
            }

            if (fate.JobAllowed == false)
            {
                this.MarkHandled(ses, HandledReason.JobNotAllowed, "いまのクラス・ジョブはこの FATE のシンクの対象外", warn: false);
                return null;
            }

            if (!fate.IsRunning)
            {
                this.SetState(ControllerState.Ready, $"FATE が進行中になるのを待っている（状態 {fate.StateValue}）");
                return null;
            }

            var blocked = this.BlockReason(s);
            if (blocked != null)
            {
                this.SetState(ControllerState.Ready, blocked);
                return null;
            }

            this.SetState(ControllerState.Ready, "要求を出す");
            return new SyncTicket(ses.Number, fate.Occurrence, s.Zone, this.generation);
        }
    }

    /// <summary>
    /// 札に従って開始要求を送る。送る直前に、ON のままか・世代が同じか・同じ侵入か・未送信かを確かめ直す。
    /// 送信済みの印は送る前に付ける（送信中の再入や次の回での二重送信を防ぐため）。
    /// </summary>
    /// <returns>送ったら true。</returns>
    public bool Commit(SyncTicket ticket, DateTime now)
    {
        lock (this.gate)
        {
            if (!this.enabled)
            {
                this.log.Debug($"侵入 #{ticket.SessionNumber}：送る前に OFF になったので送らない");
                return false;
            }

            if (ticket.Generation != this.generation)
            {
                this.log.Debug($"侵入 #{ticket.SessionNumber}：送る前に世代が変わった（{ticket.Generation} → {this.generation}）ので送らない");
                return false;
            }

            var ses = this.session;
            if (ses == null || ses.Number != ticket.SessionNumber || ses.Occurrence != ticket.Occurrence || this.lastZone != ticket.Zone)
            {
                this.log.Debug($"侵入 #{ticket.SessionNumber}：送る前に対象が変わったので送らない");
                return false;
            }

            if (ses.RequestSentAt != null || ses.Handled != null)
                return false;

            if (this.pending is { } p && p.Occurrence == ticket.Occurrence && now - p.SentAt < this.timings.ConfirmTimeout)
                return false;

            ses.RequestSentAt = now;
            this.pending = new PendingRequest(ticket.Occurrence, ticket.Zone, now, this.generation);
            this.requestCount++;
            this.lastRequestAt = now;
            this.revision++;
            this.SetState(ControllerState.Requested, "要求を送った。成立を待っている");
            this.log.Info($"/levelsync on を送る（FATE「{ses.FateName}」・侵入 #{ses.Number}・世代 {this.generation}）");

            try
            {
                this.sender.SendLevelSyncOn();
            }
            catch (Exception ex)
            {
                this.FaultLocked($"レベルシンクの要求を送れなかった：{ex.GetType().Name}: {ex.Message}");
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 異常で止める。自動化を OFF にし、理由を残す。自動では戻らない（利用者が ON にし直せば再び動く）。
    /// </summary>
    public void Fault(string reason)
    {
        lock (this.gate)
        {
            this.FaultLocked(reason);
        }
    }

    /// <summary>
    /// プラグインを外すときに呼ぶ。以後は何も送らない。シンクは解除しない。
    /// </summary>
    public void Shutdown()
    {
        lock (this.gate)
        {
            this.enabled = false;
            this.generation++;
            this.revision++;
            this.session = null;
            this.pending = null;
            this.outsideSince = null;
            this.state = ControllerState.Disabled;
            this.detail = "プラグインを外した";
        }
    }

    private bool SetEnabledLocked(bool on, ChangeSource source, DateTime now)
    {
        if (on)
        {
            if (this.enabled)
            {
                this.log.Debug($"すでに ON（{source}）。何もしない");
                return false;
            }

            var problem = this.prerequisiteCheck?.Invoke();
            if (problem != null)
            {
                this.FaultLocked($"ON にできない：{problem}");
                return false;
            }

            this.enabled = true;
            this.generation++;
            this.revision++;
            this.session = null;
            this.outsideSince = null;
            this.incompatibleSince = null;
            this.faultReason = null;
            this.state = ControllerState.Waiting;
            this.detail = "FATE を待っている";
            this.log.Info($"自動レベルシンクを ON にした（{source}・世代 {this.generation}）");
            return true;
        }

        if (!this.enabled)
            return false;

        this.enabled = false;
        this.generation++;
        this.revision++;
        this.session = null;
        this.outsideSince = null;
        this.state = ControllerState.Disabled;
        this.detail = "自動レベルシンクは OFF";

        // 送ってしまった要求は取り消せない（後から成立することがある）。順番が追えるよう記録に残す
        var note = this.pending is { } p ? $"。{(now - p.SentAt).TotalSeconds:0.0} 秒前に送った要求は取り消さない" : string.Empty;
        this.log.Info($"自動レベルシンクを OFF にした（{source}・世代 {this.generation}）{note}。いまのシンクは解除しない");
        return true;
    }

    private void FaultLocked(string reason)
    {
        this.enabled = false;
        this.generation++;
        this.revision++;
        this.session = null;
        this.outsideSince = null;
        this.incompatibleSince = null;
        this.faultReason = reason;
        this.faultCount++;
        this.state = ControllerState.Faulted;
        this.detail = $"異常で停止：{reason}";
        this.log.Error($"異常で停止して OFF にした：{reason}");
    }

    private void MarkHandled(Session ses, HandledReason reason, string text, bool warn)
    {
        ses.Handled = reason;
        ses.HandledText = text;
        this.SetState(ControllerState.Handled, text);
        var message = $"侵入 #{ses.Number}（FATE「{ses.FateName}」）：{text}";
        if (warn)
            this.log.Warn(message);
        else
            this.log.Info(message);
    }

    /// <summary>
    /// 状態か説明が変わったときだけ詳細ログに書く（同じ待ち理由を毎回書かない）。
    /// </summary>
    private void SetState(ControllerState newState, string newDetail)
    {
        if (this.state == newState && this.detail == newDetail)
            return;

        this.state = newState;
        this.detail = newDetail;
        this.revision++;
        this.log.Debug($"状態 {newState}：{newDetail}");
    }

    /// <summary>
    /// 確かめていない種類なら理由を返す。ここに当たる侵入では要求を出さない。
    /// </summary>
    private static string? UnsupportedReason(GameSnapshot s, FateView fate)
    {
        if (!s.TerritoryIsOpenField)
            return "通常のフィールド以外の FATE（確かめていない種類なので自動ではシンクしない）";
        if (!fate.IsStandardMode)
            return "特殊な FATE（エウレカ等。確かめていない種類なので自動ではシンクしない）";
        if (fate.HasSpecialSyncPath == null)
            return "FATE の種類を判定できない（ゲームデータを読めなかった）";
        if (fate.HasSpecialSyncPath == true)
            return "イベント用などの特殊な FATE（ゲームが別の手順でシンクする種類なので自動ではシンクしない）";
        if (fate.RequiredQuest != 0)
            return "前提クエストつきの FATE（確かめていない種類なので自動ではシンクしない）";
        if (fate.MaxLevel == 0)
            return "上限レベルが無い FATE";
        if (fate.JobAllowed == null)
            return "ジョブ条件を判定できない（ゲームデータを読めなかった）";
        return null;
    }

    /// <summary>
    /// 一時的に送れない理由。解ければ同じ侵入のうちに送る。
    /// </summary>
    private string? BlockReason(GameSnapshot s)
    {
        if (s.IsDead)
            return "戦闘不能。起き上がるのを待っている";
        if (s.IsOccupied)
            return "会話・イベント中。終わるのを待っている";
        if (s.IsMountedOrFlying && !this.syncWhileMounted)
            return "騎乗・飛行中。降りるのを待っている（設定で変えられる）";
        return null;
    }

    private sealed class Session(int number, FateOccurrence occurrence, string fateName, ZoneKey zone)
    {
        public int Number { get; } = number;

        public FateOccurrence Occurrence { get; } = occurrence;

        public string FateName { get; } = fateName;

        public ZoneKey Zone { get; } = zone;

        public DateTime? RequestSentAt { get; set; }

        public HandledReason? Handled { get; set; }

        public string HandledText { get; set; } = string.Empty;
    }

    private sealed record PendingRequest(FateOccurrence Occurrence, ZoneKey Zone, DateTime SentAt, long Generation);
}
