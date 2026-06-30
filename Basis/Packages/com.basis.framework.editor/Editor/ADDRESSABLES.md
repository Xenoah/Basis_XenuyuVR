# Basis Addressables

Basis が Addressable group をどう整理し、それを editor tooling がどう維持するかを説明します。目的は**runtime memory**です。大きな asset や独立して load される asset は個別の bundle に置き、共有 dependency が bundle 間で重複しないようにします。

## 原則: category ではなく dependency で group 化する

1 つの group は 1 つの AssetBundle になります。bundle が参照している asset のうち、**それ自体が addressable ではない**ものは、その bundle に*コピー*されます。そのため、別々の group にある 2 つの prefab が non-addressable な font / material / shader を共有していると、その asset は両方の bundle に複製され、memory と disk を消費します。

目安:

1. **自己完結した cluster**。他 group と共有する dependency がないものは、専用 group にして安全です。
2. **共有 dependency**。2 つ以上の group から参照されるものは、shared bundle で addressable にし、コピーではなく 1 回だけ参照されるようにします。font は **Basis Fonts**、それ以外は **Basis Shared** に入れます。
3. model やその他の大きな on-demand asset は、単独で load / unload できるよう専用 group にします。

すべての Basis group は **LZ4** compression と local build/load path を使います。

## Groups

以下の group は下記 tool によって作成・維持されます。rule に一致しないものは Foundation、つまり catch-all に残ります。

| Group | Packing | 内容 |
|-------|---------|------|
| Built In Data | - | Unity built-in / local player data |
| Basis Foundation Assets | PackTogether | catch-all: player、orb、mirror、scene、data asset |
| Basis UI Assets | PackTogether | UI prefab (Panel Elements)、icon texture / sprite |
| Basis Fonts | PackTogether | Inter family + TMP fallback font (分離された shared dependency) |
| Basis Shared | PackTogether | 共有 UI material / shader / sprite (分離された shared dependency) |
| Basis Gizmos | PackTogether | com.basis.gizmos debug-draw cluster |
| Basis MediaPipe Models | PackSeparately | *.task.bytes face / hand / pose model (独立 load) |
| Basis OpenLipSync | PackTogether | OpenLipSync model + config |
| Basis Localization | PackTogether | language JSON (Addressable label `language`) |

## Tools

tool はすべて `com.basis.framework.editor/Editor/` にあり、公式の `AddressableAssetSettings` API を使います。各 entry の**解決済み asset path**で分類するため、`GizmoMaterial` や `LocalPlayer` のような friendly name の entry も正しい group に入ります。`Assets/AddressableAssetsData/` 配下の group YAML は手動編集しないでください。

| Menu | File | 役割 |
|------|------|------|
| Basis > Addressables > Dependency Report | `BasisAddressableDependencyReport.cs` | project root に `BasisAddressableDependencyReport.txt` を書き出します。group ごとの footprint と、group 間で共有される dependency、つまり重複 hot spot を示します。 |
| Basis > Addressables > Organize Groups | `BasisAddressableOrganizer.cs` | path rule (gizmos、fonts) を適用し、cross-group shared dependency を Basis Fonts / Basis Shared へ分離します。 |
| Basis > Addressables > Organize Model Groups | `BasisModelAddressableSetup.cs` | MediaPipe `*.task.bytes` と OpenLipSync model + config を専用 group へ移動します。importer としても動作します。 |
| Basis > Localization > Register Languages as Addressable | `BasisLocalizationAddressableSetup.cs` | language JSON を Basis Localization に登録します。address は `Languages/{code}`、label は `language` です。importer としても動作します。 |
| _(helper, menu なし)_ | `BasisAddressableGroups.cs` | `GetOrCreate(settings, name, packing)`。LZ4 と local path を設定します。 |

## Workflow

1. **Organize Model Groups**、**Organize Groups**、**Register Languages as Addressable** を実行します。matching asset が import された時は importer からも自動実行されます。
2. **Dependency Report** を実行し、cross-group shared dependency がほぼ 0 であることを確認します。TMP の `Editor Resources/*.psd` icon が表示される場合がありますが、editor-only で build からは除外されるため無視して構いません。
3. player build 用に **Build Addressables content** を実行します。場所は Window > Asset Management > Addressables > Groups > Build > New Build > Default Build Script です。

再実行しても安全です。処理は idempotent です。

## 使用 package と license

| Package | Version | License | ここでの役割 |
|---------|---------|---------|--------------|
| com.unity.addressables | 2.9.1 | Unity Companion License | Addressables system (`Unity.Addressables`, `Unity.ResourceManager`) |
| com.basis.framework / com.basis.framework.editor | embedded | MIT | tooling + runtime localization loader |
| com.basis.sdk | embedded | MIT | UI prefab、sprite、material、Inter font (notes 参照) |
| com.basis.textmeshpro | embedded | Unity Companion License | TMP shader + LiberationSans fallback (shared dependency) |
| com.basis.gizmos | embedded | MIT | Gizmo prefab / material / shader |
| com.basis.mediapipe | embedded | MIT | MediaPipe `.task.bytes` model (notes 参照) |
| com.github.homuler.mediapipe | 0.16.3 | Apache-2.0 | MediaPipe inference plugin。MediaPipe と native lib を同梱します。 |
| com.basis.openlipsync / com.basisvr.openlipsync | 0.2.0 | Apache-2.0 | OpenLipSync driver + model/config |
| com.basis.tests | embedded | MIT (Basis repo) | test prefab。現在は Foundation に入っています。 |

asset 単位の notes:

- **Inter** typeface (`com.basis.sdk/Fonts/`、Basis Fonts の大部分) は **SIL Open Font License 1.1** です。
- **MediaPipe** face / hand / pose model は Google のもので、**Apache-2.0** です。MediaPipe + homuler の完全な notice は `com.basis.mediapipe/THIRD_PARTY_NOTICES.md` を参照してください。
- "embedded" は `Packages/` 配下の local package を意味します。manifest version はありません。license は各 package の `package.json` / `LICENSE` から取得しています。
