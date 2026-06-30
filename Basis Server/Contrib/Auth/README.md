# Authentication

このディレクトリには、第三者 authentication integration をまとめます。

username / display name に関するものは [handles][handles] を参照してください。user permission や role に関するものは、Authorization 用の Basis core API を参照してください。

## Authentication、Authorization、Handles の違い

- Authentication: どのように login するか、自分が主張どおりの本人であることをどう証明するか、機械可読な account identifier などを扱います。
- Authorization: authentication の上に乗る層です。user が account / account ID を持ったあと、その user が何を*許可されているか*を表現する system です。
- Handles: user を識別する、人間が読める system です。handle は変更される可能性があり、永続的な identifier として適さないことが多いため、authentication とは*別の* system として扱います。

[handles]: ../Handles/
