# XenuyuVR

[XenuyuVR](https://github.com/Xenoah/Basis_XenuyuVR) は、[BasisVR](https://github.com/BasisVR/Basis) フレームワークをベースにした、チル・ゲーム対応の VRSNS「ゼヌユVR」です。

ゆったり過ごせる空間づくりと、みんなで遊べるネットワーク体験の両方を大切にしながら、VR 上での交流、アバター表現、ワールド体験、ゲーム的なインタラクションを組み合わせていくことを目指しています。

<table border="0">
 <tr>
    <td><div align="center"><img src="./Basis/Images/BasisLogo.png" alt="Basis Logo" width="160" height="160"></div></td>
    <td><div align="center"><h3><strong>XenuyuVR</strong></h3>
BasisVR ベースのチル・ゲーム対応 SNS</br>
<a href="https://github.com/Xenoah/Basis_XenuyuVR/releases"><strong>ダウンロード(Windows)</strong></a></div></td>
 </tr>
</table>

## 概要

XenuyuVR は、BasisVR のオープンな Social VR / Networked VR 基盤を活用し、ユーザー同士が自然に集まり、話し、遊び、表現できる場所を作るためのプロジェクトです。

主な方向性:

- チルできるソーシャル VR 体験
- ネットワーク対応のゲーム・ミニゲーム体験
- アバターやワールドを中心にしたコミュニケーション
- BasisVR フレームワークを活かした拡張しやすい構成
- 日本語ユーザーにも使いやすい SNS 的な体験設計

## ベースフレームワーク

このプロジェクトは BasisVR をベースにしています。BasisVR は MIT ライセンスのオープンソース Social VR フレームワークで、VR クリエイターが独自のソーシャル VR やネットワーク対応ゲームを構築するための土台を提供しています。

BasisVR 本体の思想、ライセンス、クレジット、第三者ソフトウェア表記、商標ガイドラインは `LICENSE`、`TRADEMARK.md`、および各関連ファイルを参照してください。

## 開発者向けセットアップ

このリポジトリは Unity プロジェクト本体が `Basis/` にあります。リポジトリのルートではなく、Unity Hub では必ず `Basis/` フォルダーを開いてください。

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

2. Unity Hub で `Basis/` フォルダーを開きます。
3. Unity の初回インポートが終わるまで待ちます。
4. 起動シーン `Packages/com.basis.framework/Scenes/initialization.unity` を開きます。
5. Play モードで起動確認します。

### 開発時の確認

- Unity Editor で Play する前に、`initialization.unity` が開かれていることを確認してください。
- 依存パッケージや Addressables の再インポートが走る場合があります。Unity の処理が止まっていないか Console と Progress を確認してください。
- UI、サーバー接続、ローカライズ、ビルド設定を変更した場合は、Editor Play と Windows Player の両方で確認してください。
- 生成された `Builds/`、`Artifacts/`、Unity のログ類は通常コミットしません。

### Windows ビルド

Unity Editor からビルドする場合:

1. `Basis/` を Unity で開きます。
2. `File > Build Settings` を開きます。
3. Platform を Windows にします。
4. `initialization.unity` が有効なシーンに含まれていることを確認します。
5. Build を実行します。

ヘッドレスビルドを使う場合は、プロジェクト内の `BasisHeadlessBuild` を利用します。Addressables もビルド対象に含める必要があります。

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
