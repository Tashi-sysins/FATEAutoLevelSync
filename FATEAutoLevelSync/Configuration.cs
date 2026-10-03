using System;
using Dalamud.Configuration;

namespace FATEAutoLevelSync;

/// <summary>
/// 保存する設定。項目名を変えると利用者の値が読まれなくなるので、変えるときは Version を上げて移す。
/// <para>
/// 表示位置の項目は持たない。表示はサーバー情報バー（Umbra を使っていれば Umbra のツールバー）に出るので、
/// 上下の位置はそちらの設定で決まる。
/// </para>
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>利用者が最後に選んだ ON/OFF（バー・コマンド・設定画面で変えたときだけ書く）。</summary>
    public bool LastEnabled { get; set; }

    /// <summary>ログイン時・読み込み時に前回の ON/OFF を戻すか。既定は false（いつも OFF で始める）。</summary>
    public bool RestoreEnabledOnLogin { get; set; }

    /// <summary>
    /// サーバー情報バー（Umbra のツールバーを含む）に「レベシンク：ON/OFF」を出すか。既定は true。
    /// 前の版の設定ファイルにはこの項目が無いが、読み込むと既定値（true）になる。
    /// </summary>
    public bool ShowInServerInfoBar { get; set; } = true;

    /// <summary>騎乗・飛行中でもシンクするか。既定は false（降りるまで待つ）。</summary>
    public bool SyncWhileMounted { get; set; }

    /// <summary>判断の途中経過を記録に書くか。既定は false。</summary>
    public bool DebugLogging { get; set; }
}
