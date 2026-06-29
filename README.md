# XenuyuVR

[XenuyuVR](https://github.com/Xenoah/Basis_XenuyuVR) は、チル・ゲーム対応の VRSNS「ゼヌユVR」です。

ゆったり過ごせる空間づくりと、みんなで遊べるネットワーク体験の両方を大切にしながら、VR 上での交流、アバター表現、ワールド体験、ゲーム的なインタラクションを組み合わせていくことを目指しています。

<p align="center">
  <img src="./1782709126879.png" alt="XenuyuVR Logo" width="160" height="160"><br>
  <strong>XenuyuVR</strong><br>
  チル・ゲーム対応 VRSNS<br>
  <a href="https://github.com/Xenoah/Basis_XenuyuVR/releases"><strong>ダウンロード(Windows)</strong></a>
</p>

**Built with Basis**

## 概要

XenuyuVR は、Social VR / Networked VR の仕組みを活用し、ユーザー同士が自然に集まり、話し、遊び、表現できる場所を作るためのプロジェクトです。

主な方向性:

- チルできるソーシャル VR 体験
- ネットワーク対応のゲーム・ミニゲーム体験
- アバターやワールドを中心にしたコミュニケーション
- 拡張しやすい構成
- 日本語ユーザーにも使いやすい SNS 的な体験設計

## 実装予定

- ペン
- 使いやすい動画プレーヤー
- カラオケ向け遅延調整システム
- フェイスミラー
- 姿勢変更ツール
- 撫で音
- VRM アバター変換ツール
- チルワールド
- ゲームワールド

## 利用規約

成人向けコンテンツは、関係するすべての参加者が成人であり、内容を理解したうえで明確に同意している場合のみ許可されます。

同意のない成人向け表現、未成年が関係する成人向け表現、強要・嫌がらせ・なりすまし・無断撮影や無断共有を含む行為は禁止します。各ユーザーは、自分が参加する地域の法律、プラットフォーム規約、サーバーやワールドごとのルールを守る必要があります。

## 開発者向けセットアップ

このリポジトリは Unity プロジェクトを含んでいます。Unity Hub ではリポジトリのルートではなく、Unity プロジェクト本体のフォルダーを開いてください。

### 必要環境

- Git
- Unity Hub
- Unity `6000.5.1f1`
- Windows ビルドを作成する場合は Unity の Windows Build Support / IL2CPP Support

推奨 Unity バージョン:

```txt
m_EditorVersion: 6000.5.1f1
m_EditorVersionWithRevision: 6000.5.1f1 (0d9463e84828)
```

### 初回セットアップ

1. リポジトリを clone します。

   ```sh
   git clone https://github.com/Xenoah/Basis_XenuyuVR.git
   cd Basis_XenuyuVR
   ```

2. Unity Hub で Unity プロジェクト本体のフォルダーを開きます。
3. Unity の初回インポートが終わるまで待ちます。
4. 起動シーン `initialization.unity` を開きます。
5. Play モードで起動確認します。

### 開発時の確認

- Unity Editor で Play する前に、`initialization.unity` が開かれていることを確認してください。
- 依存パッケージや Addressables の再インポートが走る場合があります。Unity の処理が止まっていないか Console と Progress を確認してください。
- UI、サーバー接続、ローカライズ、ビルド設定を変更した場合は、Editor Play と Windows Player の両方で確認してください。
- 生成された `Builds/`、`Artifacts/`、Unity のログ類は通常コミットしません。

### Windows ビルド

Unity Editor からビルドする場合:

1. Unity プロジェクト本体のフォルダーを Unity で開きます。
2. `File > Build Settings` を開きます。
3. Platform を Windows にします。
4. `initialization.unity` が有効なシーンに含まれていることを確認します。
5. Build を実行します。

ヘッドレスビルドを使う場合は、プロジェクト内のビルド用 Editor スクリプトを利用します。Addressables もビルド対象に含める必要があります。

### 起動オプション

VR モードの起動を無効化する場合:

```sh
--disable-OpenVRLoader
--disable-OpenXRLoader
```

VR モードを起動時に強制する場合:

```sh
--force-OpenXRLoader
--force-OpenVRLoader
```

## ライセンス

ライセンス、第三者クレジット、商標に関する表記は [LICENSE](./LICENSE) にまとめています。
