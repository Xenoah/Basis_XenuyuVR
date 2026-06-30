using System;

/// <summary>
/// player と上位 resolver (例: yt-dlp integration package) の間に置く任意の
/// URL 解決接点。player core は integration へ直接参照を持たないため、
/// URL field が player 側へ依存を増やさず page URL を resolver 経由へ誘導するための入口になる。
///
/// integration は load 時に <see cref="Resolver"/> を登録する
/// (例: <c>RuntimeInitializeOnLoadMethod</c>)。raw URL を持つ caller は
/// <see cref="TryResolveAndLoad"/> に渡す。resolver は load を引き受ける
/// (page URL を stream へ解決して読み込む。async の可能性あり) 場合に true を返し、
/// caller に URL を直接読ませる場合は false を返す。integration が無い場合
/// <see cref="Resolver"/> は null で、全 URL が直接読み込まれる。integration が無い状態と同じ。
/// ここは route だけを行い、URL を block しない (host trust は BasisMediaPlayerSecurity が別途 enforcing する)。
/// </summary>
public static class BasisMediaUrlRouter
{
    /// <summary>
    /// 任意の integration が登録する。指定 URL の load を引き受けた場合は true、
    /// caller に直接 load させる場合は false を返す。integration が無い場合は null。
    /// </summary>
    public static Func<BasisMediaPlayer, string, bool> Resolver;

    // URL の path 部分を終端する delimiter (query / fragment)。
    // IsDirectlyPlayable が呼び出しごとに char[] を allocate しないよう外へ出している。
    private static readonly char[] PathEnd = { '?', '#' };

    /// <summary>
    /// 登録済み resolver があれば <paramref name="url"/> を通す。
    /// resolver が引き受けた場合は true (caller は重ねて load しない)、
    /// resolver が無い、または辞退した場合は false (caller が直接 load する)。
    /// </summary>
    public static bool TryResolveAndLoad(BasisMediaPlayer player, string url)
        => Resolver != null && Resolver(player, url);

    /// <summary>
    /// player が resolver 無しで <paramref name="url"/> を直接開ける場合 true。
    /// 非 HTTP scheme (rtsp/rtmp/rist などの transport、または local file)、
    /// または path が media container 拡張子 (.mp4/.m4s/.ts/.m2ts/.mts/.m3u8)
    /// で終わる http(s) URL が該当する。media 拡張子の無い http(s) URL は
    /// page URL (例: YouTube/Twitch watch page) なので resolver が必要。
    /// live と resolve の振り分けに関する single source of truth であり、
    /// resolver と caller の両方が参照する。分類だけを行い、block はしない (host trust は別系統)。
    /// </summary>
    public static bool IsDirectlyPlayable(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        bool isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!isHttp) return true; // transport scheme または local file は直接開く。

        // "…/stream.m3u8?token=…" も拡張子で一致するよう query/fragment を外す。
        string path = url;
        int cut = path.IndexOfAny(PathEnd);
        if (cut >= 0) path = path.Substring(0, cut);

        // .mpd は対象外。native engine には DASH demuxer が無いため、
        // raw MPD は直接再生扱いではなく resolver (yt-dlp) を通す必要がある。
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase)   // Blu-ray 系 MPEG-TS
            || path.EndsWith(".mts", StringComparison.OrdinalIgnoreCase)    // AVCHD 系 MPEG-TS
            || path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }
}
