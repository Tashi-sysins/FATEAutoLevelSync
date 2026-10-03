using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace FATEAutoLevelSync.Game;

/// <summary>
/// ゲームデータ（シート）から実行時に引く値。数値の ID は埋め込まず、すべてここで引く。
/// フレームごとに同じ表を引き直さないよう、結果を覚えておく。
/// </summary>
internal sealed class GameDataLookup
{
    /// <summary>ゲームの /levelsync コマンド（TextCommand シートに実在することを ON の前に確かめる）。</summary>
    public const string LevelSyncCommandName = "/levelsync";

    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Dictionary<ushort, bool?> specialPathByFate = new();
    private readonly Dictionary<(uint Category, uint ClassJob), bool?> jobAllowed = new();
    private string? levelSyncCommandProblem;
    private bool levelSyncCommandChecked;

    public GameDataLookup(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
    }

    /// <summary>
    /// ゲームの /levelsync が通常とは別の経路で処理する FATE か。
    /// <para>
    /// 根拠（2026.09.15 の本体を逆アセンブルして確認）：/levelsync の処理は、FATE の内部の値（ClientStructs の FateContext の
    /// 0x7EC、名前は Unknown4）が 0 でないと別の命令でシンクする。この値は Fate シートの Unknown4 列で、
    /// 0 でないのは季節イベント・蒼天街・コスモ探索などのイベント用 FATE だけだった（通常フィールドでは 3 件、どれも季節イベント）。
    /// </para>
    /// </summary>
    /// <returns>読めなければ null。</returns>
    public bool? HasSpecialSyncPath(ushort fateId)
    {
        if (this.specialPathByFate.TryGetValue(fateId, out var known))
            return known;

        try
        {
            var row = this.data.GetExcelSheet<Fate>().GetRowOrDefault(fateId);
            bool? result = row == null ? null : row.Value.Unknown4 != 0;
            this.specialPathByFate[fateId] = result;
            return result;
        }
        catch (Exception ex)
        {
            // 失敗も覚える（毎回の読み取りで警告を書き続けないため）。エリアが変わったら ForgetCached で読み直す
            this.log.Warning($"Fate シートの {fateId} 行を読めなかった：{ex.GetType().Name}: {ex.Message}");
            this.specialPathByFate[fateId] = null;
            return null;
        }
    }

    /// <summary>
    /// クラス・ジョブがジョブ条件（ClassJobCategory）に含まれるか。
    /// <para>
    /// ClassJobCategory シートは「列 0 が名前、列 n+1 が ClassJob の n 行目を含むか」の並び
    /// （2026.09.15 のデータで、ジョブ1つだけの行 44 件すべてがこの並びに一致することを確かめた）。
    /// 列が足りない・真偽値でないときは並びが変わったとみなして null を返す（シンクしない側に倒す）。
    /// </para>
    /// </summary>
    public bool? IsJobInCategory(uint categoryId, uint classJobId)
    {
        var key = (categoryId, classJobId);
        if (this.jobAllowed.TryGetValue(key, out var known))
            return known;

        try
        {
            var sheet = this.data.GetExcelSheet<RawRow>(name: "ClassJobCategory");
            var column = (int)classJobId + 1;
            var row = sheet.GetRowOrDefault(categoryId);
            bool? result = null;
            if (row != null && column < sheet.Columns.Count && IsBoolColumn(sheet.Columns[column].Type))
                result = row.Value.ReadBoolColumn(column);
            else
                this.log.Warning($"ClassJobCategory の {categoryId} 行・{column} 列を真偽値として読めない（ゲームデータの並びが変わった可能性）");

            this.jobAllowed[key] = result;
            return result;
        }
        catch (Exception ex)
        {
            this.log.Warning($"ClassJobCategory を読めなかった：{ex.GetType().Name}: {ex.Message}");
            this.jobAllowed[key] = null;
            return null;
        }
    }

    /// <summary>
    /// 覚えた結果を捨てる（エリアが変わったときに呼ぶ）。一時的な読み込みの失敗をいつまでも引きずらないため。
    /// </summary>
    public void ForgetCached()
    {
        this.specialPathByFate.Clear();
        this.jobAllowed.Clear();
    }

    /// <summary>
    /// ゲームに /levelsync があるか確かめる。問題が無ければ null、あれば理由の文を返す。
    /// </summary>
    public string? CheckLevelSyncCommand()
    {
        if (this.levelSyncCommandChecked)
            return this.levelSyncCommandProblem;

        try
        {
            var found = false;
            foreach (var row in this.data.GetExcelSheet<TextCommand>())
            {
                if (row.Command.ExtractText() == LevelSyncCommandName)
                {
                    found = true;
                    break;
                }
            }

            this.levelSyncCommandProblem = found ? null : $"ゲームのコマンド一覧（TextCommand）に {LevelSyncCommandName} が見つからない";
        }
        catch (Exception ex)
        {
            this.levelSyncCommandProblem = $"ゲームのコマンド一覧（TextCommand）を読めない：{ex.GetType().Name}: {ex.Message}";
        }

        this.levelSyncCommandChecked = true;
        return this.levelSyncCommandProblem;
    }

    /// <summary>
    /// 指定の色（0xRRGGBB）に最も近い文字色の番号を UIColor シートから選ぶ。見つからなければ null（色なしで表示する）。
    /// サーバー情報バー（と Umbra のバー）の文字色は UIColor の番号で指定するため。
    /// </summary>
    public ushort? FindNearestUiColor(uint targetRgb)
    {
        try
        {
            var tr = (int)((targetRgb >> 16) & 0xFF);
            var tg = (int)((targetRgb >> 8) & 0xFF);
            var tb = (int)(targetRgb & 0xFF);
            ushort? best = null;
            var bestDistance = int.MaxValue;
            foreach (var row in this.data.GetExcelSheet<UIColor>())
            {
                if (row.RowId == 0 || row.RowId > ushort.MaxValue)
                    continue;

                // Dalamud の UIForegroundPayload と同じく Dark の値（0xRRGGBBAA）を使う
                var rgba = row.Dark;
                if ((rgba & 0xFF) == 0)
                    continue;

                var dr = (int)((rgba >> 24) & 0xFF) - tr;
                var dg = (int)((rgba >> 16) & 0xFF) - tg;
                var db = (int)((rgba >> 8) & 0xFF) - tb;
                var distance = (dr * dr) + (dg * dg) + (db * db);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (ushort)row.RowId;
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            this.log.Warning($"UIColor を読めなかった（色なしで表示する）：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool IsBoolColumn(ExcelColumnDataType type)
        => type == ExcelColumnDataType.Bool
           || (type >= ExcelColumnDataType.PackedBool0 && type <= ExcelColumnDataType.PackedBool7);
}
