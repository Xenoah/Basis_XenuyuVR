# Basis Server Docker セットアップ

この文書では、Docker と Docker Compose を使って Basis server をセットアップし、実行する方法を説明します。

## 目次

- [前提条件](#前提条件)
- [ディレクトリ構成](#ディレクトリ構成)
- [設定](#設定)
- [Docker Compose 設定](#docker-compose-設定)
- [はじめ方](#はじめ方)
- [設定のカスタマイズ](#設定のカスタマイズ)
- [volume と永続化](#volume-と永続化)
- [ログと監視](#ログと監視)
- [トラブルシューティング](#トラブルシューティング)
- [ライセンス](#ライセンス)

## 前提条件

- Docker (最新版を推奨)
- Docker Compose (v2 構文: `docker compose`)

## ディレクトリ構成

`docker compose up` で初めてサーバーを実行すると、存在しない場合は `Docker/` フォルダー内に次のディレクトリが作成されます。

```text
Docker/
├── config/
└── initialresources/
```

- `config/`: サーバー用の XML 設定ファイルを格納します。例: メイン設定、管理者リスト、BAN リスト。サーバーは内部デフォルトや環境変数に基づいて、これらを自動生成または更新する場合があります。
- `initialresources/`: サーバー実行時に必要な static asset を格納します。通常はコンテナへ read-only で mount されます。

## 設定

Docker で実行する場合、サーバーの挙動は主に環境変数で制御されます。環境変数は、XML 設定ファイルから読み込まれる値や XML 設定ファイルへ書き込まれる値を上書きできます。

### 設定ファイル

起動時、またはファイルが存在しない場合、サーバーは `config/` ディレクトリにデフォルト設定ファイルを生成することがあります。`docker-compose.yml` では `./config` から mount されます。

- `config/config.xml`: メインのサーバー設定。port、timeout、peer limit、認証など。
- `config/admins.xml`: 管理者ユーザー識別子のリスト。
- `config/banned_players.xml`: BAN 済みユーザー識別子のリスト。

`config/config.xml` の例です。ここにある値は、環境変数が適用される前のデフォルトである可能性があります。

```xml
<Configuration>
  <PeerLimit>1024</PeerLimit>
  <SetPort>4296</SetPort>
  <EnableConsole>true</EnableConsole>
  <DisallowHeadless>false</DisallowHeadless>
  <!-- ... other settings ... -->
</Configuration>
```

### 環境変数

主要な設定は、`docker-compose.yml` または `docker run` コマンドの環境変数で上書き/設定できます。環境変数が優先されます。

よく使う環境変数:

| 環境変数 | `docker-compose.yml` でのデフォルト | 説明 |
| -------------------- | ------------------------------- | ------------------------------------------------- |
| `SetPort` | `4296` | game client traffic 用の UDP port。 |
| `HealthCheckPort` | `10666` | server health check 用の TCP port。 |
| `PromethusPort` | `1234` | Prometheus metrics 用の TCP port。 |
| `PeerLimit` | `1024` | 同時接続 peer 数の上限。 |
| `Password` | `default_password` | client 接続用 password。**必ず変更してください。** |
| `EnableStatistics` | `true` | statistics module を有効にします。 |
| `EnableConsole` | `false` | interactive server console (CLI) を有効にします。 |
| `DisallowHeadless` | `false` | 接続済み headless client を切断し、新規 headless client を拒否します。 |

設定可能な項目のより包括的な一覧は、通常は初回実行後に生成された `config/config.xml` を確認するか、利用できる場合はサーバー内部ドキュメントを確認することで把握できます。

## Docker Compose 設定

`docker-compose.yml` はサーバーの deployment をまとめて扱います。提供されている例は `Docker/docker-compose.yml` です。

```yaml
services:
  basis-server:
    build:
      context: ../ # build context は親ディレクトリ (Basis Server/)
      dockerfile: Docker/Dockerfile # Dockerfile への path
    image: basis-server:latest # build された image の名前と tag
    container_name: basis-server # 実行中 container の custom name
    restart: unless-stopped # container の restart policy
    environment:
      # サーバー設定用の環境変数
      SetPort: 4296
      HealthCheckPort: 10666
      PromethusPort: 1234
      Password: default_password # 重要: production では変更してください
      PeerLimit: 1024
      EnableStatistics: true
      EnableConsole: false # interactive console debugging では true にします
    ports:
      # host port と container port の mapping
      - "4296:4296/udp"    # game traffic
      - "10666:10666/tcp"  # health check
      - "1234:1234/tcp"    # Prometheus metrics
    volumes:
      # host directory を container に mount
      - ./initialresources:/app/initialresources:ro # read-only static assets
      - ./config:/app/config                         # read-write server configuration
```

**セキュリティメモ:** セットアップしやすいよう、デフォルト password は `default_password` になっています。local 以外、または production deployment では、`docker-compose.yml` 内で**必ず変更してください**。

## はじめ方

1. **Docker ディレクトリへ移動します。**
    terminal を開き、`docker-compose.yml` があるディレクトリへ移動します。

    ```bash
    cd path/to/your/project/Basis\ Server/Docker/
    ```

2. **Docker image を build します。**
    このコマンドは `Dockerfile` を使って server image を build します。

    ```bash
    docker compose build
    ```

3. **サーバーを起動します。**
    このコマンドは detached mode (`-d`) でサーバーを起動します。つまり background で実行されます。

    ```bash
    docker compose up -d
    ```

    **重要:** 初回実行時は、`config/` と `initialresources/` ディレクトリが存在するか、この処理で作成されることを確認してください。

4. **サーバーログを表示します。**
    サーバー出力を監視し、error を確認します。

    ```bash
    docker compose logs -f basis-server
    ```

    ログ追跡を止めるには `Ctrl+C` を使います。

5. **サーバーを停止します。**
    このコマンドは `docker-compose.yml` で定義された container を停止し、削除します。

    ```bash
    docker compose down
    ```

## 設定のカスタマイズ

- **環境変数 (Docker では推奨):**
  port、password、peer limit などの設定を変更するには、`docker-compose.yml` の `environment` section を編集します。変更後は rebuild や restart が必要になる場合があります。

    ```bash
    docker compose up -d --build # rebuild して restart
    # or
    docker compose restart basis-server # ENV var のみを変えた場合は service だけ restart
    ```

- **XML 設定ファイル (`config/`):**
  環境変数として公開されていない高度な設定は、場合によっては *server 停止中* に `config/` ディレクトリ内の XML ファイルを編集できます。次回起動時にサーバーが読み込みます。ただし、環境変数がこれらの値を上書きする場合がある点に注意してください。

  `config/` 内のファイルを変更したら、service を restart します。

    ```bash
    docker compose restart basis-server
    ```

## volume と永続化

- `./config:/app/config`: host 側の `Docker/config/` ディレクトリを、container 内の `/app/config` に mount します。これにより、container restart をまたいで server configuration が保持されます。サーバーはこれらのファイルを読み書きできます。
- `./initialresources:/app/initialresources:ro`: `Docker/initialresources/` を read-only で container に mount します。これはサーバーが必要とする static asset です。

## ログと監視

- **Docker Logs:** `docker compose logs -f basis-server` で real-time log を確認できます。
- **Metrics Endpoint:** 有効かつ設定済みの場合、デフォルトでは `PromethusPort: 1234` により、Prometheus-compatible metrics が `http://<host_ip>:1234/metrics` で利用できるはずです。
- **Health Check Endpoint:** 有効かつ設定済みの場合、デフォルトでは `HealthCheckPort: 10666` により、health check endpoint が `http://<host_ip>:10666/health` で利用できるはずです。

## トラブルシューティング

- **サーバーが起動しない:**
  - log を確認してください: `docker compose logs basis-server`。port binding、configuration loading、missing file に関する error message を探します。
  - Docker daemon が実行中であることを確認してください。

- **port conflict:**
  - "port is already allocated" のような error が出る場合、host 上の別 service がいずれかの port (4296/udp, 10666/tcp, 1234/tcp) を使用しています。
  - `docker-compose.yml` の競合する port mapping を変更してください。例: game traffic に host port 8080 を使うなら `"8080:4296/udp"`。

- **設定変更が反映されない:**
  - `docker-compose.yml` を変更した場合、たとえば環境変数を変更した場合は、service を停止して再起動する必要があります: `docker compose down && docker compose up -d`。場合によっては `docker compose up -d --force-recreate` または `docker compose restart basis-server` で十分です。
  - `Dockerfile` を変更した場合は image の rebuild が必要です: `docker compose build` の後、restart してください。
  - `config/` volume 内のファイルを手動編集した場合は、server を停止してから編集し、その後 restart したことを確認してください: `docker compose restart basis-server`。

- **interactive console が動かない:**
  - `docker-compose.yml` の `EnableConsole` 環境変数を `true` にする必要があります。
  - 使用するには container に attach する必要があります: `docker attach basis-server`。Compose version が対応している場合は `docker compose attach basis-server` も使えます。detach は `Ctrl+P` のあと `Ctrl+Q` です。

## ライセンス

このプロジェクトは MIT License の下でライセンスされています。詳細は [LICENSE](../../LICENSE) ファイルを参照してください。
