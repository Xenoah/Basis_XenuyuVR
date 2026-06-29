# XenuyuVR

[XenuyuVR](https://github.com/Xenoah/Basis_XenuyuVR) は、[BasisVR](https://github.com/BasisVR/Basis) フレームワークをベースにした、チル・ゲーム対応のソーシャル VR / SNS プロジェクトです。

ゆったり過ごせる空間づくりと、みんなで遊べるネットワーク体験の両方を大切にしながら、VR 上での交流、アバター表現、ワールド体験、ゲーム的なインタラクションを組み合わせていくことを目指しています。

<table border="0">
 <tr>
    <td><div align="center"><img src="./Basis/Images/BasisLogo.png" alt="Basis Logo" width="160" height="160"></div></td>
    <td><div align="center"><h3><strong>XenuyuVR</strong></h3>
BasisVR ベースのチル・ゲーム対応 SNS</br>
<a href="https://github.com/Xenoah/Basis_XenuyuVR"><strong>GitHub Repository</strong></a></div></td>
 </tr>
</table>

## 概要

XenuyuVR は、BasisVR のオープンな Social VR / Networked VR 基盤を活用し、ユーザー同士が自然に集まり、話し、遊び、表現できる場所を作るためのプロジェクトです。

主な方向性:

- チルできるソーシャル VR 体験
- ネットワーク対応のゲーム・ミニゲーム体験
- アバターやワールドを中心にしたコミュニケーション
- BasisVR フレームワークを活かした拡張しやすい構成
- 日本語ユーザーにも扱いやすい SNS 的な体験設計

## ベースフレームワーク

このプロジェクトは BasisVR をベースにしています。BasisVR は MIT ライセンスのオープンソース Social VR フレームワークで、VR クリエイターが独自のソーシャル VR やネットワーク対応ゲームを構築するための土台を提供しています。

BasisVR 本体の思想やライセンス、クレジット、第三者ソフトウェア表記、商標ガイドラインは下記の各セクションおよび関連ファイルを参照してください。

## セットアップ

このプロジェクトは Unity 6 を使用しています。Unity Hub からプロジェクトを開き、指定バージョンに合わせてください。

1. リポジトリを clone します。
   ```sh
   git clone https://github.com/Xenoah/Basis_XenuyuVR.git
   ```
2. Unity Hub でプロジェクトを開きます。
3. `Initialisation` シーンを読み込みます。
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

## License

Distributed under the MIT License. See [MIT License](https://opensource.org/licenses/MIT) for more information.

### Built With

This would not be possible without the following:
- [ULipSync](https://github.com/hecomi/uLipSync)
- [UnityJigglePhysics](https://github.com/naelstrof/UnityJigglePhysics)
- [opussharp](https://github.com/AvionBlock/OpusSharp)
- [opus](https://github.com/xiph/opus)
- [Steam Audio](https://github.com/ValveSoftware/steam-audio)
- [Unity Starter Assets - ThirdPerson](https://assetstore.unity.com/packages/essentials/starter-assets-thirdperson-updates-in-new-charactercontroller-pa-196526)
- [RNNoise](https://github.com/xiph/rnnoise?tab=BSD-3-Clause-1-ov-file)
- [RNNoise.Net](https://github.com/Yellow-Dog-Man/RNNoise.Net)
- [unity](https://unity.com/)
- [ionic icons](https://github.com/ionic-team/ionicons?ref=svgrepo.com)
- [LiteNetLib](https://github.com/RevenantX/LiteNetLib)
- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4)
- [cilbox](https://github.com/cnlohr/cilbox)

## Third-Party Code and Trademarks

This project includes third-party software under the following licenses:

### Apache License 2.0
- [Steam Audio](https://github.com/ValveSoftware/steam-audio) - See `Basis/Packages/com.steam.steamaudio/LICENSE.md`
- [OpenLipSync ONNX Runtime](https://github.com/microsoft/onnxruntime) (MIT) - See `Basis/Packages/com.basisvr.openlipsync/THIRD_PARTY_NOTICES.md`

### BSD-3-Clause
- [OpenVR](https://github.com/valvesoftware/openvr) - (C) Valve Corporation. See `Basis/Packages/com.valvesoftware.unity.openvr/LICENSE.md`
- [SteamVR](https://github.com/ValveSoftware/steamvr_unity_plugin) - (C) Valve Corporation. See `Basis/Packages/com.steam.steamvr/LICENSE`

### BSD (Modified/Clear)
- [Opus Codec](https://github.com/xiph/opus) - Copyright 2001-2011 Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry, CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. See `Basis/Packages/com.avionblock.opussharp/Opus_LICENSE_PLEASE_READ.txt`

### MIT
- [uLipSync](https://github.com/hecomi/uLipSync) - Copyright 2021 hecomi. See `Basis/Packages/com.hecomi.ulipsync/LICENSE.md`
- [OpusSharp](https://github.com/AvionBlock/OpusSharp) - Copyright 2026 AvionBlock. See `Basis/Packages/com.avionblock.opussharp/LICENSE.txt`
- [URP Volumetric Fog](https://github.com/cqf2186863072/URP-Volumetric-Fog) - Copyright 2025 Cristian Qiu Felez. See `Basis/Packages/com.cqf.urpvolumetricfog/LICENSE.md`
- [RNNoise.Net](https://github.com/Yellow-Dog-Man/RNNoise.Net) - Copyright 2023 Yellow Dog Man Studios. See `Basis/Packages/com.xiph.rnnoise/LICENSE`
- [HVR Basis Comms](https://github.com/BasisVR/Basis/tree/developer/Basis/Packages/dev.hai-vr.basis.comms) - Copyright 2025 Hai~ and MR LUKE B DOOLAN. See `Basis/Packages/dev.hai-vr.basis.comms/LICENSE`
- [HVR Basis NDMF](https://github.com/BasisVR/Basis/tree/developer/Basis/Packages/dev.hai-vr.basis.ndmf) - Copyright (c) 2025 Haï~. See `Basis/Packages/dev.hai-vr.basis.ndmf/LICENSE`
- [MeaMod.DNS](https://github.com/meamod/MeaMod.DNS) - Copyright 2021 James Weston. See `Basis/Packages/nuget.meamod.dns/LICENSE`
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) - Copyright 2007 James Newton-King. See `Basis/Packages/org.basisvr.newtonsoft.json/LICENSE`
- [BouncyCastle](https://github.com/bcgit/bc-csharp) - Copyright 2000-2024 The Legion of the Bouncy Castle Inc. See `Basis/Packages/org.basisvr.bouncycastle/LICENSE`
- [Base128](https://github.com/Wojmik/Base128) - See `Basis/Packages/org.basisvr.base128/LICENSE`
- [Generator.Equals](https://github.com/diegofrata/Generator.Equals) - Copyright Diego Frata. See `Basis/Packages/org.basisvr.generator.equals/LICENSE`
- [SimpleBase](https://github.com/ssg/SimpleBase) - Copyright Sedat Kapanoglu. See `Basis/Packages/org.basisvr.simplebase/LICENSE`
- [ZeroMessenger](https://github.com/Cysharp/ZeroMessenger) - Copyright 2024 Annulus Games. See `Basis/Packages/com.basis.zeromessenger/LICENSE.md`
- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) - Copyright 2017 Milosz Krajewski. See `Basis/Packages/org.basisvr.k4os.compression.lz4/LICENSE`
- [UnityJigglePhysics](https://github.com/naelstrof/UnityJigglePhysics) - MIT licensed upstream
- [AudioLink](https://github.com/llealloo/vrc-udon-audio-link) - MIT licensed upstream
- [cilbox](https://github.com/cnlohr/cilbox) - MIT licensed upstream

### SIL Open Font License 1.1
- [Inter](https://github.com/rsms/inter) - Copyright 2020 The Inter Project Authors. See `Basis/Packages/com.basis.sdk/LICENSE-Inter-OFL.txt`.
- [Poppins](https://github.com/itfoundry/Poppins) - Copyright 2020 The Poppins Project Authors. See `Basis/Packages/com.basis.sdk/LICENSE-Poppins-OFL.txt`.
- [Noto Sans JP](https://fonts.google.com/noto/specimen/Noto+Sans+JP) - Copyright 2014-2021 Adobe, with Reserved Font Name 'Source'. See `Basis/Packages/com.basis.sdk/LICENSE-NotoSansJP-OFL.txt`.

### Trademarks

"Valve", "Steam", and the associated figurative images are trademarks and/or registered trademarks of Valve Corporation in the US and in various other jurisdictions. All rights reserved. Use of these trademarks must comply with the guidelines outlined in `Basis/Packages/com.steam.steamaudio/TRADEMARK_RIGHTS.md`.

## Basis Trademark Guidelines

"Basis", "BasisVR", "Basis Framework", and the Basis logo are marks representing the
Basis Project. Please see [TRADEMARK.md](./TRADEMARK.md) for our policies
on their usage.
