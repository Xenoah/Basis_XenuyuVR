# Basis Media Player

Basis 向けの live / on-demand video player です。**operating-system hardware codec** で decode し、Unity texture へ **zero-copy** で表示します。transcode server、VP9、`UnityEngine.Video.MediaPlayer` は使いません。

- **Windows (PC / VR)**: DXVA D3D11 device 上の Media Foundation H.264 / H.265 + AAC を使います。NV12 は D3D11 video processor 経由で BGRA に変換され、Unity が sample する texture に入ります。**D3D11** を主対象とし、shared-handle interop により **D3D12** でも動作します。
- **Android (Quest)**: `AMediaCodec` / `AMediaExtractor` を使います。decoded frame は `AHardwareBuffer` として届き、Unity が sample する **Vulkan** `VkImage` として import されます。

## 対応 URL (VRCDN など)

| Scheme | 用途 | 例 |
|---|---|---|
| `rtspt://` | PC / VR low latency (RTP interleaved over TCP) | `rtspt://stream.vrcdn.live/live/vrcdn` |
| `rtmp://`  | RTMP pull | `rtmp://stream.vrcdn.live/live/vrcdn` |
| `rist://`  | RIST live ingest (UDP、loss recovery + optional AES) | `rist://stream.example:5000?secret=KEY&aes-type=128` |
| `https://…​.mp4` | HTTPS 上の fragmented MP4 | `https://stream.vrcdn.live/live/vrcdn.live.mp4` |
| `https://…​.ts`  | MPEG-TS over HTTPS (Quest) | `https://stream.vrcdn.live/live/vrcdn.live.ts` |
| `https://…​.m3u8` | HLS / Low-Latency HLS (Windows) | `https://stream.example/live/index.m3u8` |

protocol / demux core (RTSP/RTP、RTMP/FLV、MPEG-TS、fMP4) は portable C です。OS backend は decode + present のみを行います。

### HLS / Low-Latency HLS

`.m3u8` URL は `protocol/basis_hls.c` が扱います。これは demuxer ではありません。playlist を parse し、1 つの rendition を選択し、live edge から開始し、segment、LL-HLS では partial segment (`EXT-X-PART`) も含めて、既存の MPEG-TS / fMP4 demuxer が消費する 1 本の byte stream に連結します。origin が parts 付きで `EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD` を advertise する場合、client は blocking `_HLS_msn` / `_HLS_part` playlist reload を使い、parts に乗っておおよそ `PART-HOLD-BACK` latency、約 5 秒を狙います。**約 5 秒の目標には LL-HLS origin が必要です**。通常の HLS origin では、5 秒ではなく segment に縛られた latency になります。

現在は **Windows** (WinHTTP fetch)、**clear stream**、**single rendition** で動作します。Android / Quest support は予定中です。

### RIST

`rist://` は RIST stream を ingest します。librist 経由の UDP 上 MPEG-TS で、packet-loss recovery と optional AES encryption に対応します。librist は connection option を URL query から直接読みます。encryption は `?secret=<key>&aes-type=128` または `256`、recovery buffer size は `?buffer=<ms>` です。buffer は C# から `BasisMediaSource.Options["buffer"]` でも設定でき、自動的に URL へ折り込まれます。復元された transport stream は、HTTP/TS path と同じ MPEG-TS demuxer に供給されます。

RIST は **build time opt-in** です。default plugin は OS framework だけを link します。prebuilt librist に対して `-DBASIS_WITH_RIST=ON` で build してください。詳しくは下の *native plugin の build* を参照してください。

## live と on-demand

すべての source は **live** か **on-demand** のどちらかです。live は live edge で表示され、latency が最小です。on-demand は VOD で、real time に pace されるため、再生より速く届いた file が fast-forward されることはありません。どちらを使うかは `BasisMediaSource.Delivery` が選択します。

| `Delivery` | 挙動 |
|---|---|
| `Auto` (default) | open 時に source から決定します。下記参照。 |
| `Live` | live-edge clock を強制します。 |
| `OnDemand` | real-time pacing を強制します。 |

`Auto` は source を読みます。non-HTTP transport (`rtsp` / `rtmp` / `rist`) は live です。既知の `Content-Length` と byte-range support を持つ HTTP response、または `EXT-X-ENDLIST` を持つ HLS playlist は on-demand です。終端のない HTTP response は live です。on-demand では delivery を throttle し、固定 1x clock で表示します。bursty な CDN delivery は compressed read-ahead buffer が吸収します。

`LoadUrl(url)` は `Auto` を使います。明示的に制御したい場合は `BasisMediaSource` を load してください。

```csharp
player.LoadSource(new BasisMediaSource { Uri = url, Delivery = BasisMediaDelivery.OnDemand });
```

live jitter buffer は `BasisMediaPlayer.BufferMilliseconds` / `BufferMode` で調整できます。Fixed、または auto-tuning の Dynamic を選べます。低いほど低 latency、高いほど滑らかです。on-demand は現在、固定 internal buffer で表示します。`BufferMilliseconds` は live path のみに適用されます。

## Split-stream (video + audio の分離)

adaptive source は、高解像度 video と audio を**別々**の stream として配信することがよくあります。例: H.264 video-only + AAC audio-only。`Uri` とあわせて `BasisMediaSource.AudioUri` を設定すると、engine は 2 本目の demux thread を動かし、同じ decoder に供給します。両方は 1 つの clock 上で同期表示されます。

```csharp
player.LoadSource(new BasisMediaSource {
    Uri = videoOnlyUrl, AudioUri = audioOnlyUrl, Delivery = BasisMediaDelivery.OnDemand,
});
```

`AudioUri` が null の場合、つまり default では通常の single muxed stream です。

## Page URL (任意 resolver package)

player は上記 scheme の **stream** URL を直接開きます。YouTube や Twitch の watch page のような **page** URL を stream に変換する機能は player 自体にはありません。その解決は、`BasisMediaUrlRouter` に自己登録する**別 package の任意機能**、つまり yt-dlp-based resolver が提供します。player core はそれに依存せず、参照もしません。

**resolver package が install されている場合**、`BasisMediaPlayerStreaming.StreamUrl` のような URL field は各 URL を自動的に振り分けます。

- **直接再生可能**な URL、つまり transport scheme、または path が media extension (`.mp4` / `.m4s` / `.ts` / `.m2ts` / `.mts` / `.m3u8`) で終わる HTTP URL は直接 load されます。
- **それ以外**、つまり media extension を持たない HTTP page URL は resolver に渡されます。resolver は再生可能な stream endpoint に変換して load します。

**resolver package がない場合**、router は inert です。すべての URL は直接 load されるため、上記の stream URL はそのまま動作します。ただし page URL は解決されないので、**YouTube、Twitch、類似 link は再生されません**。そのような URL を load すると、黙って失敗するのではなく resolver package が必要であることを報告します。package を削除することは support される選択です。失われるのは common-site resolution だけです。この処理は振り分けるだけで、URL を block しません。host trust は別に enforcement されます。

> **既知の不足。** 振り分けは URL の形に基づくため、**file extension のない direct HTTP stream**、たとえば `.ts` / `.mp4` を持たない `https://host/live/feed` は page URL と区別できません。resolver が install されている場合は resolver に送られ、抽出可能な stream が見つからず error になります。その結果、直接 load されず playback は失敗します。回避するには direct HTTP stream に認識可能な extension を付けるか、`rtsp` / `rtmp` のような transport scheme を使ってください。

## 使い方

```csharp
var player = gameObject.AddComponent<BasisMediaPlayer>();
gameObject.AddComponent<BasisVideoMaterialOutput>().TargetRenderer = quadRenderer;
player.LoadUrl("rtspt://stream.vrcdn.live/live/vrcdn"); // 自動再生
```

または `Prefabs/MediaPlayerStreaming` prefab を scene に置き、`BasisMediaPlayerStreaming` に URL を設定します。PC では RTSPT、Quest では MPEG-TS を自動選択できます。音声には `BasisMediaPlayerAudio` と `AudioSource` を追加します。`BasisMediaPlayerNetworking` は room 内で URL / state を同期します。

CPU `IBasisFrameSource` path、たとえば `BasisSyntheticTestSource` は、`player.Source` を直接 assign することで引き続き利用できます。feed のない test に便利です。

### Audio (stereo と multichannel)

audio は player GameObject 上の `BasisMediaPlayerAudio` を通ります。`Outputs` に `AudioSource` を列挙し、それぞれが再生対象を選ぶ `BasisMediaAudioChannel` を持ちます。対象は単一 decoded channel、または stream 全体の stereo downmix です。stereo では `Stereo` に設定した単一の `Output` を使います。これは `Prefabs/MediaPlayerStreaming` prefab です。surround では channel ごとに 1 つの `Output` を使い、5.1 / 7.1 mix、最大 8 channel を world 内で speaker ごとに配置できます。これは `Prefabs/MediaPlayerMultiChannelStreaming` prefab です。

channel 上限は source に依存します。**MPEG-TS 上の LPCM** は full 7.1、つまり 8 channel を運べます。**Windows の AAC** は Media Foundation decoder の制限により最大 5.1 まで decode します。

## networked sync

`BasisMediaPlayerNetworking` は room 内の playback を揃えます。同期するのは、解決後の stream ではなく、入力された **input URL**、つまり page URL と、play / pause / stop です。page URL は client ごとに期限付き CDN URL へ解決され、それを共有することはできません。そのため各 client が共有 page URL を自分で解決します。direct stream URL はそのまま送られます。shared load を揃えるため、owner は再生後ではなく事前に page URL を broadcast し、peer が並列で解決できるようにします。URL の re-load は同じ URL であっても、全 client を一緒に restart します。

> **on-demand position sync は catch-up ではなく start-together です。** client は同じ source を同じ時刻に開始することで揃います。on-demand playhead の継続的な re-sync はありません。遅れて join した client や load が遅く完了した client は冒頭から開始し、次の共有 (re)load まではすでに進行中の playhead と一致しません。これは native decode backend の現在の制限です。absolute seek や forward seek が公開されておらず、relative rewind のみのため、遅れている client を進めて追いつかせることができません。live stream は影響を受けません。live edge に収束します。direct かつ seekable な source では、load 時に position / pause が適用されます。

## native plugin の build

source は `Native~/` 配下です。default では**OS framework のみ**を link し、third-party lib は link しません。任意の RIST transport (`-DBASIS_WITH_RIST=ON`) は、`Native~/third_party/` の prebuilt librist を static link します。librist は自身の mbedTLS を vendor しています。この archive は Windows では `Native~/build-librist.ps1`、Linux / Android では `build-librist.sh` で build するか、**media-native (RIST)** CI workflow の artifact から download してください。詳細は `Native~/third_party/README.md` を参照してください。その後、下の cmake configure step に `-DBASIS_WITH_RIST=ON` を追加します。Unity の PluginAPI header も必要です。`Native~/unity/README.md` を参照してください。

**Windows → `Plugins/Windows/x86_64/basis_media_native.dll`**
```
cmake -S Native~ -B Native~/build -A x64 -DUNITY_PLUGIN_API_DIR="<UnityEditor>/Editor/Data/PluginAPI"
cmake --build Native~/build --config Release
```

**Android (arm64, Vulkan) → `Plugins/Android/arm64-v8a/libbasis_media_native.so`**
```
cmake -S Native~ -B Native~/build-android \
  -DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-29 \
  -DUNITY_PLUGIN_API_DIR=<UnityEditor>/Editor/Data/PluginAPI
cmake --build Native~/build-android --config Release
```

build 後は Unity import settings で plugin の platform / CPU を設定します。`Texture2D.CreateExternalTexture` format は `SystemInfo.graphicsDeviceType` に従います。D3D11 / D3D12 では BGRA32、Vulkan では RGBA32 です。これは `BasisNativeVideoSource` で処理されます。

## 状態 / iteration が必要な箇所

これは大きな native change であり、build structure のみで検証されています。on-device iteration が必要になる可能性が高い箇所は次のとおりです。

- **Android Vulkan**: YCbCr→RGBA resolve pass、つまり sampler ycbcr-conversion + fullscreen pipeline + `IUnityGraphicsVulkan` 経由の Unity-queue coordination は scaffold 済みですが未完成です。`Native~/android/basis_android_vk.c` の `TODO(on-device)` を参照してください。AHB import は実装済みです。
- **D3D12**: present は shared BGRA を `ID3D12Resource` として開きます。D3D11 video processor と D3D12 sampler の cross-API GPU sync には shared fence を使うべきです。tearing がないか検証してください。`Native~/windows/basis_win_decode.cpp` の note を参照してください。
- **RTMP**: handshake / AMF は最小実装です。simple handshake のみで、Digest auth と rtmps はありません。RTSPT と MPEG-TS が主要かつより完成度の高い path です。
- **Windows の HEVC** には system HEVC decoder MFT、つまり HEVC Video Extensions が必要です。
