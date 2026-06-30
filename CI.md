# CI セットアップ

## Secrets

クライアントを自動ビルドするには、GitHub Actions で使用する Unity ライセンスを設定する必要があります。手順は <https://game.ci/docs/github/activation> に従ってください。

さらに、CI で Android クライアントをビルドする場合は、次の secrets を GitHub Actions から利用できるようにする必要があります。

- ANDROID_KEYSTORE_BASE64: keystore を Base64 化した値
- ANDROID_KEYSTORE_PASS: keystore のパスワード
- ANDROID_KEYALIAS_NAME: keystore 内の alias 名
- ANDROID_KEYALIAS_PASS: alias のパスワード

まだ keystore がない場合は、<https://game.ci/docs/github/deployment/android/#3-generate-an-upload-key-and-keystore> の手順に従って生成できます。

## キャッシュ戦略

キャッシュ管理方針は次のとおりです。

- 常にキャッシュの復元を試みます。ただし対象は現在のプラットフォームのみです。
  - 同じプラットフォームのキャッシュが複数エントリに重複することを防ぎます。
- ビルドの成否にかかわらずキャッシュを保存します。ただし `developer` ブランチ上の場合のみです。
  - `developer` ブランチがデフォルトブランチなので、キャッシュを*利用*できる範囲が最大になります。
- このプラットフォームで最新ではないキャッシュエントリを自動的に削除します。
  - キャッシュが 10GB を超えると、GitHub は最終アクセス日時に基づいて自動削除します。
  - こちらで管理することで、*直前*のキャッシュではなく*最新*のキャッシュを残せます。

残念ながら、現在使用されていないエントリを Unity に Library フォルダーから削除させる方法は把握していません。
このままだとキャッシュは時間とともに大きくなり、定期的なリセットが必要になる可能性があります。その場合、ビルド時間が長くなります。
