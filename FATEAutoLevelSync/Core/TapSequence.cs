using System;

namespace FATEAutoLevelSync.Core;

/// <summary>
/// 「続けて5回クリック」を数える。前のクリックから 1.5 秒以内なら続き、空いたら 1 回目から数え直す。
/// 5 回目で true を返し、数え直す（隠し項目の表示・非表示の切り替えに使う）。
/// </summary>
public sealed class TapSequence
{
    public const int Required = 5;

    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(1.5);

    private int count;
    private DateTime last;

    /// <returns>5 回そろったら true。</returns>
    public bool Tap(DateTime now)
    {
        this.count = this.count > 0 && now - this.last <= MaxGap ? this.count + 1 : 1;
        this.last = now;
        if (this.count < Required)
            return false;

        this.count = 0;
        return true;
    }

    /// <summary>別の場所を押したときなどに数え直す。</summary>
    public void Reset() => this.count = 0;
}
