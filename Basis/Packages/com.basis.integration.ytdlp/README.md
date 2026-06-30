# Basis yt-dlp Integration

in-process で実行される yt-dlp を使い、YouTube や Twitch の watch page である**page URL**を、[Basis Media Player](../com.basis.mediaplayer/README.md) が開ける実際の stream に解決します。これは**任意の追加機能**です。player はこれなしでも動作し、この package は主要 site の解決を追加するだけです。

## 動作

load 時に、player の `BasisMediaUrlRouter` へ resolver を登録します。その後、`BasisMediaPlayerStreaming.StreamUrl` などの player URL field は、各 URL を次のように振り分けます。

- **直接再生可能**な URL、つまり transport scheme、または path が `.mp4` / `.m4s` / `.ts` / `.m3u8` / `.mpd` で終わる HTTP URL は、そのまま直接 load します。
- **page URL**、つまり media extension を持たない HTTP URL は yt-dlp に渡されます。yt-dlp は再生可能な最適 format を抽出し、その結果が player に load されます。

player core はこの package を参照しません。唯一の接点は router seam なので、きれいに追加・削除できます。詳しくは *削除方法* を参照してください。

## 解決できるもの

| Source | yt-dlp の戻り値 | 再生方式 |
|---|---|---|
| YouTube VOD (>360p) | separate H.264 video-only + AAC audio-only | split stream、real-time paced (on-demand) |
| YouTube / Twitch live | single HLS playlist | live |
| Progressive / muxed (≤360p) | one muxed stream | delivery auto-detected |

**codec 上限: H.264 + AAC、約 1080p。** 4K YouTube は VP9 / AV1 のみで、player は decode できません。そのため format selection は、選択する video を 1080p `avc1` + `mp4a` audio までに制限します。約 360p を超える YouTube は video と audio を別々に配信するため、player はそれらを [split stream](../com.basis.mediaplayer/README.md#split-stream-separate-video--audio) として解決し、1 つの clock で同期します。

## 使い方

YouTube / Twitch link を `BasisMediaPlayerStreaming` component の `StreamUrl` field に入れてください。または `player.LoadUrl(pageUrl)` を直接呼んでも、同じ routing が行われます。解決と load は自動です。

resolver を直接使う必要がある場合の programmatic usage:

```csharp
// page URL を解決し、player に load します。
BasisYtDlpResolver.ResolveAndPlay(player, "https://www.youtube.com/watch?v=…");

// load せずに解決します。結果の確認や cache に使えます。
BasisMediaSource source = await BasisYtDlpResolver.ResolveSourceAsync(pageUrl);
```

## 要件

- **`com.basis.mediaplayer`** (player) と **`com.yewnyx.ytdlp`** (in-process yt-dlp native plugin。embedded CPython runtime + yt-dlp)。両方が存在しない場合、この package は何も compile しません。asmdef define constraint は `BASIS_MEDIAPLAYER_EXISTS` + `YTDLP_EXISTS` です。
- 現時点では **Windows**。yt-dlp native plugin は Windows-first です。
- 初回解決では、同梱 Python runtime (数十 MB) を persistent storage へ展開するため、目に見えて遅くなります。2 回目以降は高速です。解決処理は main thread 外で実行されます。

## 削除方法

package を削除すると、page-URL resolution だけがなくなります。player は引き続き direct stream URL を再生できますが、YouTube / Twitch link は解決されなくなります。そのような URL を load すると、黙って失敗するのではなく resolver が必要であることを報告します。他の挙動は変わりません。

## trust

解決された stream URL は、他の URL と同様に player の host trust gate (`BasisMediaPlayerSecurity`) を通ります。page URL 自体に対する interactive consent は UI layer の責務であり、ここからは駆動しません。

## 既知の不足

**file extension を持たない** direct HTTP stream は page URL と区別できません。そのため、この package が install されている場合は yt-dlp に送られ、直接 load されるのではなく失敗します。direct HTTP stream には認識可能な extension を付けるか、`rtsp` / `rtmp` のような transport scheme を使ってください。[player README](../com.basis.mediaplayer/README.md#page-urls-optional-resolver-package) も参照してください。
