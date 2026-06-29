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

## セットアップ

このプロジェクトは Unity 6 を使用しています。Unity Hub から `Basis/` フォルダーを開き、指定バージョンに合わせてください。

1. リポジトリを clone します。
   ```sh
   git clone https://github.com/Xenoah/Basis_XenuyuVR.git
   ```
2. Unity Hub で `Basis/` フォルダーを開きます。
3. `Packages/com.basis.framework/Scenes/initialization.unity` を読み込みます。
4. Play モードで動作を確認します。

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
