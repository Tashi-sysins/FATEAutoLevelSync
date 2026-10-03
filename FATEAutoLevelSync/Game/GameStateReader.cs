using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FATEAutoLevelSync.Core;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using CSPlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace FATEAutoLevelSync.Game;

/// <summary>
/// ゲームの状態を読み、判断に使う値だけを <see cref="GameSnapshot"/> に写す。ゲームには何も書かない。
/// <para>
/// 必ずフレームの更新（主スレッド）から呼ぶ。内部の参照（FateContext 等）はこの呼び出しの中で値を写し取り、次のフレームへ持ち越さない。
/// </para>
/// <para>
/// 「いまの FATE」はゲームが認識しているもの（FateManager.CurrentFate）だけを使う。近くの FATE を距離で推し量ることはしない。
/// </para>
/// </summary>
internal sealed unsafe class GameStateReader
{
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly GameDataLookup lookup;

    private FateOccurrence cachedNameFor;
    private string cachedName = string.Empty;
    private uint lastTerritory = uint.MaxValue;

    public GameStateReader(IClientState clientState, IObjectTable objectTable, IPlayerState playerState, ICondition condition, GameDataLookup lookup)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.playerState = playerState;
        this.condition = condition;
        this.lookup = lookup;
    }

    public GameSnapshot Read()
    {
        if (!this.clientState.IsLoggedIn)
            return Unknown("ログインしていない");
        if (this.condition[ConditionFlag.BetweenAreas] || this.condition[ConditionFlag.BetweenAreas51])
            return Unknown("エリア移動中");
        if (this.condition[ConditionFlag.LoggingOut])
            return Unknown("ログアウト中");
        if (!this.playerState.IsLoaded)
            return Unknown("プレイヤー情報の読み込み中");

        var player = this.objectTable.LocalPlayer;
        if (player == null)
            return Unknown("自分のキャラクターがまだ出ていない");

        // ここから先はログイン済み・移動中でない。それでも読めなければ、ゲームの更新と合っていない疑いがある
        var gameMain = GameMain.Instance();
        if (gameMain == null)
            return Incompatible("GameMain を読めない");
        var fateManager = FateManager.Instance();
        if (fateManager == null)
            return Incompatible("FateManager を読めない");
        var ps = CSPlayerState.Instance();
        if (ps == null)
            return Incompatible("PlayerState を読めない");

        var territory = this.clientState.TerritoryType;
        if (territory != this.lastTerritory)
        {
            this.lastTerritory = territory;
            this.lookup.ForgetCached();
        }

        var syncedFateId = fateManager->SyncedFateId;
        var snapshot = new GameSnapshot
        {
            Kind = SnapshotKind.Outside,
            Zone = new ZoneKey(territory, this.clientState.Instance),
            TerritoryIsOpenField = gameMain->CurrentTerritoryIntendedUseId == TerritoryIntendedUse.Overworld,

            // ゲームのシンク可否の判定は、キャラクターの実効レベル（シンク中ならシンク後）と FATE の上限を比べている
            PlayerLevel = player.Level,
            IsLevelSynced = ps->IsLevelSynced,
            SyncedFateId = syncedFateId,
            IsDead = player.IsDead || this.condition[ConditionFlag.Unconscious],
            IsMountedOrFlying = this.condition[ConditionFlag.Mounted]
                                || this.condition[ConditionFlag.Mounting]
                                || this.condition[ConditionFlag.InFlight],
            IsOccupied = this.condition[ConditionFlag.Occupied]
                         || this.condition[ConditionFlag.OccupiedInEvent]
                         || this.condition[ConditionFlag.OccupiedInQuestEvent]
                         || this.condition[ConditionFlag.OccupiedInCutSceneEvent]
                         || this.condition[ConditionFlag.WatchingCutscene]
                         || this.condition[ConditionFlag.WatchingCutscene78],
        };

        var fate = fateManager->CurrentFate;
        if (fate == null)
            return snapshot;

        var fateId = fate->FateId;
        if (fateId == 0)
            return Unknown("FATE の番号を読めない");

        var occurrence = new FateOccurrence(fateId, fate->StartTimeEpoch);
        if (occurrence != this.cachedNameFor)
        {
            this.cachedNameFor = occurrence;
            this.cachedName = fate->Name.ToString();
        }

        return snapshot with
        {
            Kind = SnapshotKind.Inside,
            Fate = new FateView
            {
                FateId = fateId,
                StartTimeEpoch = fate->StartTimeEpoch,
                Name = this.cachedName,
                StateValue = (byte)fate->State,
                IsRunning = fate->State == FateState.Running,
                IsStandardMode = fate->FateMode == 0,
                HasSpecialSyncPath = this.lookup.HasSpecialSyncPath(fateId),
                RequiredQuest = fate->RequiredQuest2,
                MaxLevel = fate->MaxLevel,
                JobAllowed = this.lookup.IsJobInCategory(fate->RequiredClassJobCategory, player.ClassJob.RowId),
            },

            // ゲーム自身の「この FATE にシンクしているか」（FateManager.IsSyncedToFate）は、通常の FATE では
            // 「シンク中の FATE の番号が一致」かつ「PlayerState.SyncedLevel が 0 でない」（2026.09.15 の本体で確認）
            SyncedToCurrentFate = syncedFateId == fateId && ps->SyncedLevel != 0,
        };
    }

    private static GameSnapshot Unknown(string reason)
        => new() { Kind = SnapshotKind.Unknown, UnknownReason = reason };

    private static GameSnapshot Incompatible(string reason)
        => new() { Kind = SnapshotKind.Unknown, UnknownReason = reason, UnknownLooksLikeIncompatibility = true };
}
