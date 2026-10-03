using System;
using Dalamud.Plugin.Services;
using FATEAutoLevelSync.Core;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace FATEAutoLevelSync.Game;

/// <summary>
/// ゲームの /levelsync on を、チャット欄に入力したときと同じ処理（UIModule.ProcessChatBoxEntry）に渡して実行する。
/// <para>
/// 引数なしの /levelsync は「切り替え」なので使わない（シンク中なら解除してしまう）。
/// 2026.09.15 の本体を逆アセンブルして、/levelsync on の処理は「すでにシンク中なら何もしない／そうでなければシンク可否を確かめてから開始」で、
/// 解除の経路を通らないことを確かめた。ゲームの FateManager.LevelSync() 関数は切り替えなので直接は呼ばない。
/// </para>
/// </summary>
internal sealed unsafe class LevelSyncCommandSender : ILevelSyncSender
{
    private const string StartCommand = GameDataLookup.LevelSyncCommandName + " on";

    private readonly IFramework framework;

    public LevelSyncCommandSender(IFramework framework)
    {
        this.framework = framework;
    }

    public void SendLevelSyncOn()
    {
        // ゲームの処理を主スレッド以外から呼ばない
        if (!this.framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("フレームの更新以外の場所から送ろうとした");

        var uiModule = UIModule.Instance();
        if (uiModule == null)
            throw new InvalidOperationException("UIModule を読めない");

        var text = Utf8String.FromString(StartCommand);
        try
        {
            uiModule->ProcessChatBoxEntry(text);
        }
        finally
        {
            text->Dtor(true);
        }
    }
}
