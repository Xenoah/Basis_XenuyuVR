# Basis 第三者コントリビューション

このディレクトリは、Basis へのコミュニティコントリビューションのうち、育成中のもの、十分に汎用化されていないもの、または core project へ直接含めない実務上の理由があるものを置く場所です。`contrib` に入るコードの例は次のとおりです。これは網羅的な一覧ではありません。

* 第三者 cloud API / service との integration。
* "十分に汎用化されていない" コード、またはすべての Basis 派生プロジェクトで有用と言うには用途が具体的すぎるコード。
* すべての Basis 派生プロジェクトへ一般的に含めることについて、まだ合意されていないコード。

## ディレクトリとプロジェクト構成

コントリビューションは "category" にまとめます。category は、認証 integration 用の `contrib/auth` や、asset 関連 integration 用の `contrib/assets` のようなディレクトリです。

各 category の下には、個別のコントリビューションごとに 1 つのディレクトリを置きます。それぞれは 1 つ以上の C# class library で構成され、独自の `.csproj` ファイルを持ちます。これにより、そのコードを使いたい application は、[`<ProjectReference>`][ProjectReference] property またはその他の手段で、MSBuild 経由の依存関係として扱えます。

## 免責事項

* `contrib` 内の project、または `contrib` 自体は、将来移動される可能性があります。core project に取り込まれる、外部 repository に移動される、または部分的にその両方が行われる場合があります。
* 事前の議論が望ましいものの、十分な事前通知なく行われる可能性があります。
* API 破壊の観点では、`contrib` 内のコードは "unsupported" API と見なしてください。
* `contrib` library が Basis Demo に取り込まれていても、それは core project へ merge する意図を意味しません。
* contrib に含めるうえで、core project と方向性が一致していることが必ずしも要件になるわけではありません。
* contributor には、可能な場合は外部 repository で host できる modular な解決策を探すことを推奨します。

[ProjectReference]: https://learn.microsoft.com/en-us/visualstudio/msbuild/common-msbuild-project-items?view=vs-2022#projectreference
