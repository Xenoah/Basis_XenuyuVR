# Basis MediaPipe Tracking

desktop Basis 向けの webcam 駆動 avatar tracking です。通常の webcam を "fake VR" input に変換し、head / neck / upper-body **tracker**、**finger** curl / splay、**eye** gaze + blink、**face** blendshape / viseme を動かします。これにより desktop user も、tracked VR user のように感情表現や動きができます。

inference には [MediaPipe Unity Plugin (homuler)](https://github.com/homuler/MediaPipeUnityPlugin) (Apache-2.0) を使います。この plugin は**任意 dependency**です。plugin なしでもこの package は compile / 配布されますが、**inert** な状態になります。install されると自動的に有効化されます。

## 状態

| Milestone | Scope | 状態 |
|-----------|-------|-------|
| **M0** | package、asmdef、webcam capture、device-source lifecycle、backend seam | **done** |
| **M1** | FaceLandmarker → eye gaze / blink + 52 ARKit face blendshape (Basis.Comms 経由) | **done** |
| **M2** | HandLandmarker → finger curl / splay (BasisLocalHandDriver 経由) | **done** |
| **M4 (partial)** | Settings tab: enable + camera select + feature toggles | **done** |
| M3 | Head / neck pose + upper-body tracker + calibration + desktop head hand-off | planned |
| M4 | en.json localization、debug overlay、platform ごとの packaging、perf | planned |

homuler backend は FaceLandmarker + HandLandmarker を **VIDEO mode** で実行します。これは main thread 上の synchronous 実行です。M4 では inference を off-thread に移します (LIVE_STREAM + TextureFrame)。

## Basis との関係

- `BasisMediaPipeManagement : BasisBaseTypeManagement` は **device source** です。`BasisDeviceManagement` GameObject に追加し、その object の **`BaseTypes`** list に含めます。frame ごとの処理は central tick である `BasisDeviceManagement.Simulate()` から実行されるため、新しい `Update()` loop は追加しません。
- body pose は、framework 既存の fake device である `BasisInputXRSimulate` tracker として publish されます。`InitalizeTracking(..., ForceAssignTrackedRole: true, role)` により、`Head`、`LeftHand`、`Hips` などの固定 role が割り当てられます。下流の FBIK、muscle / finger bitstream、bone networking はそのまま再利用されます。
- finger は `BasisLocalHandDriver.LeftHand/RightHand` を駆動します。eye + face blendshape は `HVR.Basis.Comms` の `AcquisitionService` を通ります。これはすでに remote へ network されています。

```
WebCamTexture ─► BasisMediaPipeCamera ─► IBasisMediaPipeBackend (homuler) ─► BasisMediaPipeResult
                                                                                   │
                                       BasisMediaPipeManagement.ApplyResult ◄──────┘
                                       ├─ trackers (Head/hands/upper body)
                                       ├─ BasisLocalHandDriver (fingers)
                                       └─ AcquisitionService (gaze/blink/blendshapes)
```

## セットアップ (この project では設定済み)

以下の手順はこの repo では**完了済み**です。再現できるように記載しています。

1. **Plugin** `com.github.homuler.mediapipe` **0.16.3** は `Packages/manifest.json` から install されています。`"file:com.github.homuler.mediapipe-0.16.3.tgz"` として、他の `*.tgz` dependency と並ぶ tarball を参照します。Unity は `BasisMediaPipe.Homuler.asmdef` の `versionDefines` により `BASIS_MEDIAPIPE` を自動定義し、homuler backend assembly を有効化します。もし自動定義されない場合は、**Project Settings → Player → Scripting Define Symbols** に `BASIS_MEDIAPIPE` を追加してください。
2. **Models** は `Packages/com.basis.mediapipe/Models/` 内の Addressable `TextAsset` (`.bytes`) として同梱されます。`face_landmarker.task.bytes`、`hand_landmarker.task.bytes`、`pose_landmarker_lite.task.bytes` が対象です。*Basis ▸ Addressables ▸ Organize Model Groups* と、その folder 上の importer により、専用の **Basis MediaPipe Models** group (PackSeparately) に整理されます。loader は `Addressables.LoadAssetAsync<TextAsset>(...).WaitForCompletion()` で読み込み、raw bytes を MediaPipe の `modelAssetBuffer` に渡します。group layout と tooling 全体は `com.basis.framework.editor/Editor/ADDRESSABLES.md` を参照してください。
3. **Manager**: `BasisMediaPipeManagement` は `BasisDeviceManagement` object 上にあり、その `BaseTypes` list に入っています。Settings tab も、見つからない場合は自己接続します。
4. **face / eyes で avatar を動かすには**、avatar 側に HVR Basis Comms `AutomaticFaceTracking`、ARKit または Unified Expressions 名の blendshape、eye bone が必要です。これは VRCFaceTracking-over-OSC と同じ要件です。**finger には不要**です。finger は `BasisLocalHandDriver` を直接駆動します。

## 使い方

**Settings → Webcam Tracking** を開きます。

- **Enable Webcam Tracking**: on / off を切り替えます。
- **Camera**: 使用する webcam を選びます。live device list です。
- **Face & Eyes**、**Hands & Fingers**、**Mirror Camera**: feature ごとの toggle です。

何かが反転して見える場合の tuning knob です。avatar rebuild は不要です。

- blink が反転している → `MediaPipeFaceConverter.EyeLidIsOpenness` を切り替えます。
- hand が入れ替わっている → `HomulerMediaPipeBackend.SwapHands`。
- finger splay の方向 / 強さ → `MediaPipeHandConverter.SplayGain` / `MaxSplayDegrees`。

> inference は main thread 上で実行されます (VIDEO mode)。M4 の off-thread pass までは、有効化中に frame-time cost がある前提で見てください。必要に応じて `BasisMediaPipeConfig.TargetFps` で camera FPS を制限してください。

## platform notes

- **Windows / Linux / macOS desktop:** primary target です。capture path は `WebCamTexture` です。
- **Android phone:** selfie camera で動作します。package 対象として native-lib target がもう 1 つ必要です。
- **Quest / standalone HMD:** **実用的ではありません**。user-facing camera がなく、passthrough camera は app から利用できないよう制限されています。代わりに external / USB camera、または phone を source として使ってください。

## notes

- `.meta` file は初回 import 時に Unity が生成します。
- 単眼 webcam tracking は depth を測定するのではなく推定します。rotation と expression は強く出ますが、absolute position は近似です。M3 calibration で緩和します。seated webcam を対象とするため、leg / lower body は意図的に scope 外です。
