## 概要
<!-- この PR は何を、なぜ変更しますか。関連 issue があればリンクしてください。 -->

## 必須チェック
この PR を merge する前に、以下の box はすべてチェックする必要があります。本当に N/A の項目がある場合もチェックを入れ、理由を **Notes** に書いてください。

<!-- required-checks-start -->
<!-- box はこの場でチェックしてください。required-checks-start/end marker は変更しないでください。pr-checklist workflow はこの block の未チェック box を数えます。PR ごとの補足は Notes に書きます。 -->
- [ ] **テスト済み**: ローカルで build して実行しました。変更は editor で動作し、該当する場合は build 済み player でも動作します。
- [ ] **Transform access は結合され、範囲が限定されている**: hot path では transform の読み書きを `TransformAccessArray` 経由にするか、別の形で batch 化しています。loop 内で frame ごとの `transform.position` / `transform.rotation` / `transform.localPosition` call は追加していません。position と rotation の両方が必要な場合、write は `SetPositionAndRotation` / `SetLocalPositionAndRotation`、read は `GetPositionAndRotation` / `GetLocalPositionAndRotation` を使い、個別 property access を 2 回行いません。結合 API は local-to-world matrix traversal を 2 回ではなく 1 回で済ませます。
- [ ] **asset / memory loading には Addressables を使っている**: 新しい asset load は Addressables 経由です。新しい `Resources.Load` はなく、scene load 時に大きな content を memory に引き込む direct asset reference もありません。
- [ ] **避けられる場所で新しい `GetComponent` / `AddComponent` を追加していない**: やむを得ない場合、結果は field に cache し、`GetComponent<T>` は `TryGetComponent<T>(out var x)` に置き換えています。素の `GetComponent` は拒否されます。`TryGetComponent` は modern API (Unity 2019.2+) で、component が見つからない時に `GetComponent` が発生させる Editor-only GC allocation を避けます。Unity は `null` return を managed の "fake null" object で包み、overloaded `==` operator が破棄済み C++ object を検出できるようにしますが、その wrapper 作成が allocation になります。`TryGetComponent` は `bool` と `out` parameter を返し、wrapper を作りません。これらの call は `Update`、`LateUpdate`、`FixedUpdate`、job、その他 frame ごとの code path 内では実行しません。
- [ ] **frame ごとの処理は `BasisEventDriver` を通して schedule している**: 新しい frame ごとの処理は、MonoBehaviour に単独の `Update` / `LateUpdate` / `FixedUpdate` callback を追加せず、`BasisEventDriver` に接続しています。
- [ ] **`BasisEventDriver` に追加したものは例外を投げないか、`try`/`catch` で守っている**: `BasisEventDriver` は framework 全体を駆動する単一の frame tick を 1 本の逐次 chain として実行します。network apply、local player sim、blendshape、JigglePhysics、nameplate などが含まれます。その chain のどこかで未処理例外が出ると、その frame では以降の step が黙って skip されます。driver に追加する新しい処理は、例外を投げないことが保証されているか、failure を閉じ込めて `BasisDebug` へ表面化する `try`/`catch` で wrap してください。log は once / rate-limited にし、毎 frame 出さないでください。既存の `HVRBasisBuiltInAddresses.Simulate()` guard を pattern として参照してください。review では特に細かく見られます。
- [ ] **Job 化を検討した**: この作業を Unity Job に移せるか、可能なら Burst compile できるかを検討しました。移せるなら移しています。移せない場合は理由を **Notes** に書いています。
- [ ] **不要な `{ get; set; }` property や access 制限を追加していない**: public field は問題ありません。Basis は自由に読まれ、変更されることを前提にしています。実際の理由なしに `private` / `internal` で囲い込まないでください。何もしない accessor のために field を `{ get; set; }` で包まないでください。property accessor は direct field access と比べて実際の performance cost があり、lead maintainer は noop getter pair より plain field、または setter にだけ logic が必要な場合の method / setter-only property を好みます。`.Instance` singleton では、caller が `Type.Instance` を再代入することは許可されています。それでコードが壊れるなら warning を log するか throw してください。代入を block しないでください。access を閉じる判断を勝手にしないでください。
- [ ] **camera access は `BasisLocalCameraDriver` 経由**: local camera が必要な code は、transform、projection、rig data などを `BasisLocalCameraDriver` から取得し、自前で探索しません。別の camera discovery path を作らないでください。
- [ ] **logging は `BasisDebug` を使っている**: 新しい logging call はすべて、`UnityEngine.Debug.Log` / `Debug.LogWarning` / `Debug.LogError` ではなく、適切な `LogTag` を付けた `BasisDebug.Log` / `BasisDebug.LogWarning` / `BasisDebug.LogError` 経由です。`BasisDebug` は Basis の tagged / color-coded logger を通り、project-wide な `LoggingDisabled` toggle を尊重するため、runtime で logging を止められます。素の `Debug.Log` はそれを bypass するため拒否されます。
- [ ] **依存関係のために scene-wide discovery をしていない**: 新しい code は依存対象を探すために `FindObjectOfType` / `FindObjectsOfType` / `GameObject.Find` / `FindGameObjectsWithTag` を必要としない設計です。reference は既存 manager / driver への登録、init 時の injection、caller からの受け渡しなどで明示的につないでいます。scene scan が本当に避けられない場合は、**Notes** で正当化してください。
- [ ] **hot path で allocation していない**: frame ごとの code、つまり Update / LateUpdate / FixedUpdate、simulation loop、job、frame に 1 回以上呼ばれるものは allocation しません。reference type の `new`、LINQ、`string` の連結/補間、boxing、interface-typed collection に対する `foreach` はありません。init 時に一度だけ allocate し、buffer を再利用します。
- [ ] **hot path に debug を入れていない**: frame ごとの path には、`BasisDebug` を含めていかなる log call もありません。hot-path logging は console を埋め、message が下流で filter されるかどうかに関係なく毎 frame cost が発生します。反復中に hot-path log が必要な場合は `#if UNITY_EDITOR` で gate し、merge 前に削除するか gate したままにしてください。
- [ ] **hot-path collection access を最適化している**: loop 前に list の `.Count` や array の `.Length` を local `int` に cache し、iteration ごとに property を読み直しません。data が hot な場所では、`List<T>` より `T[]` を優先します。array が oversized の場合は別途 length int を持ちます。Unity の mono BCL は `CollectionsMarshal.AsSpan(List<T>)` を公開していないため、list は `Span<T>` / unsafe path にきれいに渡せません。performance 上の理由が十分にある場合は、bounds check や copy を避けるために `Span<T>` / `ref` local / `Unsafe.As` / `unsafe` pointer code へ降りても構いません。その場合、依存している invariant を **Notes** に明記し、reviewer が妥当性を確認できるようにしてください。
<!-- required-checks-end -->

## テスト詳細
実際にテストした platform だけをチェックしてください。残りは未チェックのままで構いません。これらは情報用であり、merge を block しません。

- [ ] Windows
- [ ] Linux
- [ ] Android
- [ ] iOS
- [ ] macOS

入力 / control mode の確認範囲:

- [ ] VR でテスト済み。headset は **Notes** に記載してください。
- [ ] desktop / non-VR mode でテスト済み。
- [ ] phone controls (mobile touch input) でテスト済み。
- [ ] N/A: player / XR / input code に触れていません。

該当する場合、変更後も次の flow が動作することを確認してください。

- [ ] hot-switching (runtime での desktop ↔ VR mode swap)
- [ ] avatar swapping
- [ ] server swapping (joining / leaving / changing servers)
- [ ] N/A: 上記のいずれにも触れていません。

## Notes
<!-- reviewer 向けの任意の補足です。headset model、必須 box が N/A である理由、その他知っておくべきことを書いてください。 -->
