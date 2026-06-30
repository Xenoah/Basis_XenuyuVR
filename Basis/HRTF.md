# HRTF / 空間音声プロファイル

Basis は Steam Audio のバイノーラルレンダラー (HRTF) を使って、プレイヤーの音声を空間化します。この文書では、上下方向の手がかりがどのように働くかと、より良い HRTF プロファイルを追加する方法を説明します。

## 背景

上下方向の定位は、ほぼすべて耳介による 5-10 kHz 帯のスペクトルノッチから得られます。そして、そのノッチは個人差が非常に大きいものです。Steam Audio の**内蔵デフォルト HRTF は汎用の平均化データセット**なので、まさにその手がかりがぼやけます。リスナーの上下にある声は、本来より平たく聞こえます。これは汎用 HRTF の性質であり、音声パイプラインのバグではありません。spatializer plugin、`AudioSource.spatialize`、`SteamAudioSource.directBinaural`、listener までの一連の接続は有効に動作しています。

調整すべきものは HRTF データセットそのものです。Steam Audio は `.sofa` ファイルからカスタム HRTF を読み込めます。実測された dummy-head プロファイルに差し替えると、汎用デフォルトよりも上下方向の反応が明瞭で強くなります。

## デフォルトで含まれるもの

- **HRTF interpolation のデフォルトは Bilinear** です (`General > Networking/Remote Audio > HRTF`)。Bilinear は最も近い測定応答に飛びつくのではなく、隣接する測定応答をブレンドするため、方向や上下方向の遷移が滑らかになります。既存インストールにも新しいデフォルトが適用されるよう、binding key は `ra_interpolation_v2` に上げています。
- 同じグループに **HRTF Profile** dropdown があります。`.sofa` ファイルが import されていない場合は、内蔵の汎用プロファイルである **Default** だけが表示されます。

## カスタム HRTF プロファイルの追加

`.sofa` ファイルは利用者側で用意してください。単一の HRTF が全員に合うわけではなく、binary asset も大きいため、このリポジトリには同梱していません。手順は次のとおりです。

1. **緩やかなライセンスの SOFA HRTF を入手します。** 推奨:
   - **SADIE II** (University of York): Neumann KU100 / KEMAR dummy head、高い空間解像度、優れた上下定位。CC BY 4.0。https://www.york.ac.uk/sadie-project/database.html
   - **MIT KEMAR** (Gardner & Martin): 定番の dummy-head セットです。素直で "less aggressive" な音です。自由に利用できます。

   これらの database では標準的な `SimpleFreeFieldHRIR` convention のファイルを使ってください。商用配布する場合は、CIPIC など研究用途限定のセットを避けてください。

2. **import します。** `.sofa` ファイルを `Assets/` 配下の任意の場所に置きます。Steam Audio の `ScriptedImporter` が自動的に `SOFAFile` asset へ変換します。profile name はファイル名になります。例: `D2_48K_24bit_256tap_FIR_SOFA.sofa`

3. **登録します。** `Assets/Basis/Settings/Resources/SteamAudioSettings.asset` を開き、import した `SOFAFile` を **SOFA Files** list に追加します。Default と比べて音量が小さすぎる/大きすぎる場合は、ファイルごとの `volume` gain (dB) と `normType` も任意で調整できます。

4. **選択します。** アプリ内で `Settings > Remote Audio > HRTF > HRTF Profile` を開き、ファイル名で選びます。選択は `ra_hrtfprofile` に保存され、すべての spatialized audio にライブ適用されます。

## 注意

- HRTF は**global**です。1 つの binaural renderer がすべての spatialized source を駆動するため、このプロファイルは話者ごとではなく listener 側のものです。voice spatialization の設定場所に合わせ、設定は Remote Audio 配下にあります。
- 選択は**名前**で行われるため、SOFA list の並び替えでは保存済みの選択は壊れません。保存済み profile が見つからない場合は Default に戻ります。
- **Android / Quest / Steam Frame で確認してください。** SOFA 読み込みは、その platform の native Steam Audio plugin に SOFA reader が含まれているかに依存します。依存する前に、custom profile が実際にその環境で読み込まれることを確認してください。

## コード上の接点

- `SteamAudioManager.GetHRTFIndexByName` / `SetActiveHRTF`: runtime profile switch (`Packages/com.steam.steamaudio/Runtime/SteamAudioManager.cs`)。
- `SettingsProviderRemoteAudio.ApplyHrtfProfile` / `GetHrtfProfileEntries` と `RAHrtfProfile` / `RAInterpolation` bindings: settings と UI の接続。
