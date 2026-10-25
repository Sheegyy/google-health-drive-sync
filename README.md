# Google Health → Drive

Google Healthの記録を毎朝Google Driveへ保存する、C#の個人用ツールです。
Geminiの有料プランを追加せず、普段使っているChatGPTで健康データを見たい人向けに作っています。GeminiやOpenAIのAPIは呼びません。

```text
Pixel Watch / Fitbit → Google Health → Azure Functions → DriveのJSON
                                                           ↑
                                                     ChatGPTで読む
```

歩数、消費カロリー、睡眠、心拍、運動、体重、食事など、読み取り対応39型を対象にします。毎朝7時（日本時間）に直近14日分を取得し、最新ファイルと日付付き履歴を保存します。高頻度データは主に1時間集計です。[取得項目と制限](ACTIVITY_DATA.md)

最新は`health_sync_latest.json`、履歴は非共有の`Health Sync History/health_sync_YYYY-MM-DD.json`です。同日の再実行は同じ履歴を更新し、過去日のファイルは自動削除しません。保存開始前の全履歴を復元する機能ではなく、日々の保存から長期記録を残す仕組みです。Driveの使用量は増えていきます。

Azureの利用料は別途かかります。ChatGPTのDrive連携も、利用プランと対応機能に依存します。ChatGPT側の接続や定期実行は、このコードでは設定しません。

## 構成

| 場所 | 役割 |
| --- | --- |
| [src/HealthSync.Functions](src/HealthSync.Functions) | Timer Triggerとホスト起動 |
| [src/HealthSync.Core](src/HealthSync.Core) | Google認証、データ取得、JSON作成、Drive更新 |
| [tools/HealthSync.Tool](tools/HealthSync.Tool) | 初回OAuth認可とローカル単発実行 |
| [tests](tests) | 模擬HTTP・認証・配置・発行物の検査 |

AzureはFunctions Flex Consumption、.NET 10 isolatedを使います。認証情報はKey Vaultに置き、FunctionsのマネージドIDで読みます。公開HTTPエンドポイントはありません。

## はじめる

必要なのは.NET 10 SDK、Googleアカウント、Google Cloudプロジェクトです。Azure配置にはAzure CLIとPowerShell 7も使います。

1. Google Cloudで**Google Health API**と**Google Drive API**を有効にする。スマートフォン側も同じGoogleアカウントで同期しておく。
2. OAuthクライアントを**Web application**で作り、リダイレクトURIに`http://localhost:8765/`を登録する。作成時のクライアントJSONは`private/client.json`へ置く。
3. 同意画面にHealthの読み取り9権限と`drive.file`を登録する。Testingの場合はAudienceのTest usersに自分のGoogleアカウントも追加する。Healthの対象は`activity_and_fitness`、`health_metrics_and_measurements`、`nutrition`、`sleep`、`ecg`、`irn`、`profile`、`settings`、`location`。完全なスコープ名は[HealthDataCatalog.cs](src/HealthSync.Core/HealthDataCatalog.cs)にあります。
4. リポジトリのルートで実行する。

```sh
dotnet run --project tools/HealthSync.Tool -- bootstrap --client-secrets private/client.json
dotnet run --project tools/HealthSync.Tool -- run
```

初回だけブラウザーで同意します。成功するとDriveに`Health Sync/health_sync_latest.json`を作り、リンクを`private/bootstrap.json`へ保存します。再認可しても既存ファイルは作り直しません。保存先を指定する場合は[DRIVE_FOLDER.md](DRIVE_FOLDER.md)を参照してください。

OAuthがTestingのままだと、今回の権限ではrefresh tokenが7日で失効します。継続運用ではGoogleの条件を確認してIn Productionへ切り替え、その後に再認可します。公開状態の変更とGoogleの審査通過は別です。

## 設定

ローカル設定は初回認可で生成される`src/HealthSync.Functions/local.settings.json`を使います。Azureでは同名のアプリ設定に登録します。

| 環境変数 | 内容・既定値 |
| --- | --- |
| `GOOGLE_CLIENT_ID` | OAuthクライアントID（必須） |
| `GOOGLE_CLIENT_SECRET` | 同じクライアントのシークレット（必須） |
| `GOOGLE_REFRESH_TOKEN` | 初回認可で取得したrefresh token（必須） |
| `DRIVE_FILE_ID` | 更新するJSONのファイルID。フォルダーIDやURLではない（必須） |
| `HEALTH_SYNC_SCHEDULE` | `0 0 22 * * *`。UTC 22時＝翌朝7時JST |
| `HEALTH_TIMEZONE` | `Asia/Tokyo`。取得期間の日付計算用 |
| `HEALTH_LOOKBACK_DAYS` | `14`。範囲は1〜14 |
| `HEALTH_INCLUDE_ALL_DATA` | `true`。`false`で少数の集計項目だけを取得 |
| `HEALTH_ARCHIVE_ENABLED` | `true`。日付付き履歴を残す。`false`で最新ファイルだけ |
| `DRIVE_ARCHIVE_FOLDER_ID` | 任意。省略時は非共有の履歴フォルダーを作成・再利用。指定先にはアプリのアクセス許可が必要 |
| `INCLUDE_HOURLY_HEART_RATE` | `true`。追加の時間別心拍集計を保存 |

Flex Consumptionでは`WEBSITE_TIME_ZONE`や`TZ`を設定しません。上の4つの必須設定は、AzureではKey Vault参照を使います。

## Azureへ配置

1. .NET 10 isolatedのFunctions Flex Consumption、Storage、Key Vaultを用意する。
2. Functionsのシステム割り当てIDを有効にし、必要なVaultシークレットだけに`Key Vault Secrets User`を付ける。
3. OAuthの認証情報とDriveファイルIDをVaultへ登録し、アプリ設定を作る。
4. GitHub ActionsのOIDCを**自分のリポジトリのmainだけ**に設定する。PRのChecksが通ったらmainへマージする。

設定ファイル、Vaultへの登録、GitHub側の項目と配置コマンドは[DEPLOYMENT.md](DEPLOYMENT.md)にまとめています。Azureのリソースは自動作成しません。

## 確認と注意

```sh
dotnet run --project tests/HealthSync.Checks -c Release
dotnet build src/HealthSync.Functions -c Release
```

- `private/`、実際の`local.settings.json`、OAuth JSON、トークン、健康データはGitに入れないでください。
- Driveには心電図や運動の位置情報も入り得ます。共有フォルダーを避け、ファイルの共有設定を確認してください。
- 履歴が保存できてから最新を更新します。履歴は期間が重なるため、複数ファイルの歩数・カロリーを単純に足さないでください。
- 記録なしとAPI利用不可を分けて保存します。通信・認可・容量のエラーでは前回のファイルを保持します。
- ChatGPTでは、まずJSON本文を読めることと`query_completed_at_utc`が新しいことを確認してください。自動ポーリングの設定は別途必要です。

[セキュリティ](SECURITY.md) / [検証手順](VALIDATION.md)