# Google Health → Drive

[English](#english) | [日本語](#日本語)

## English

A personal C# tool that saves your Google Health records to Google Drive every morning.
It is designed for people who want to review their health data with the ChatGPT or Claude they already use, without adding a paid Gemini plan. The sync job does not call AI model APIs; analysis happens in those assistants.

```text
Pixel Watch / Fitbit → Google Health → Azure Functions → JSON on Drive
                                                              ↑
                                                        ChatGPT / Claude
```

The tool covers all 39 supported read-only data types, including steps, calories burned, sleep, heart rate, exercise, weight, and nutrition. Every morning at 7:00 JST, it fetches the most recent 14 days of data and saves both a latest file and a dated snapshot. High-frequency data is mostly aggregated into hourly intervals. See [data coverage and limitations](ACTIVITY_DATA.md).

The latest file is `health_sync_latest.json`; dated snapshots are stored at `Health Sync History/health_sync_YYYY-MM-DD.json` in an unshared folder. Running the tool again on the same day updates that day's snapshot, and older snapshots are not automatically deleted. This is not a way to recover your entire history from before you started saving: it builds a long-term record through daily snapshots. Drive storage usage will grow over time.

Azure usage is billed separately; see [running costs](#running-costs). File uploads and Drive integrations in ChatGPT or Claude depend on your plan and available features. This code does not configure connections or scheduled analysis in either assistant.

### Project structure

| Location | Purpose |
| --- | --- |
| [src/HealthSync.Functions](src/HealthSync.Functions) | Timer trigger and host startup |
| [src/HealthSync.Core](src/HealthSync.Core) | Google authentication, data retrieval, JSON generation, and Drive updates |
| [tools/HealthSync.Tool](tools/HealthSync.Tool) | Initial OAuth authorization and local one-off runs |
| [tests](tests) | Checks for mocked HTTP, authentication, deployment, and published artifacts |

The Azure deployment uses Functions Flex Consumption with .NET 10 isolated. Credentials are stored in Key Vault and read using the Functions managed identity. There are no public HTTP endpoints.

### Getting started

You need the .NET 10 SDK, a Google account, and a Google Cloud project. Azure deployment also requires the Azure CLI and PowerShell 7.

#### 1. Enable the Google APIs

1. Open [Google Cloud Console](https://console.cloud.google.com/) and create or select a project. Use this same project for both APIs and the OAuth client below.
2. Go to **APIs & Services > Library**, enable **Google Health API** and **Google Drive API**, and confirm that both appear under **Enabled APIs & services**.
3. Make sure your phone and Pixel Watch / Fitbit data sync to the Google account you will authorize. The bootstrap tool checks Google Health linkage before setting up the Drive file.

This tool uses user OAuth authorization, not a Google service account or an API key.

#### 2. Configure the consent screen and OAuth client

1. Open **Google Auth Platform** (or **APIs & Services > OAuth consent screen**) and complete **Branding**, including the app name, support email, and developer contact email.
2. In **Audience**, choose **External** for a personal Google account. While the app is in **Testing**, add the Google account from step 1 under **Test users**. An **Internal** app is only for users within the relevant Google Workspace organization.
3. In **Data Access > Add or remove scopes**, add the following nine Health read scopes and `drive.file`. If they are not listed, use the option to add scopes manually. These match [HealthDataCatalog.cs](src/HealthSync.Core/HealthDataCatalog.cs).

```text
https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly
https://www.googleapis.com/auth/googlehealth.health_metrics_and_measurements.readonly
https://www.googleapis.com/auth/googlehealth.nutrition.readonly
https://www.googleapis.com/auth/googlehealth.sleep.readonly
https://www.googleapis.com/auth/googlehealth.ecg.readonly
https://www.googleapis.com/auth/googlehealth.irn.readonly
https://www.googleapis.com/auth/googlehealth.profile.readonly
https://www.googleapis.com/auth/googlehealth.settings.readonly
https://www.googleapis.com/auth/googlehealth.location.readonly
https://www.googleapis.com/auth/drive.file
```

4. Under **Clients**, create an OAuth client with application type **Web application**, not Desktop app. Add `http://localhost:8765/` to **Authorized redirect URIs**, including the trailing slash. Adding it only to **Authorized JavaScript origins** is not sufficient.
5. Save the client, download its JSON, create the Git-ignored `private/` directory, and save the file as `private/client.json`. The JSON must contain a `web` section with that exact redirect URI; download it again if you changed the client configuration.

#### 3. Authorize and try a local sync

Run these commands from the repository root on the computer where you will open the browser. Port `8765` must be available for the local OAuth callback.

```sh
dotnet run --project tools/HealthSync.Tool -- bootstrap --client-secrets private/client.json
dotnet run --project tools/HealthSync.Tool -- run
```

During `bootstrap`, select the same Google account you added as a test user and grant all requested permissions. On success, the tool creates `Health Sync/health_sync_latest.json` in Drive and saves its link to `private/bootstrap.json`. The `run` command then uploads your health data. Open the saved link and check that `query_completed_at_utc` is recent.

Bootstrap also generates `private/google-client-secret.txt`, `private/google-refresh-token.txt`, and `src/HealthSync.Functions/local.settings.json`. Keep these files private: they are used for local runs and the Azure setup below. Reauthorizing does not recreate the existing Drive file. To choose a destination folder or resume after a later permission error, see [DRIVE_FOLDER.md](DRIVE_FOLDER.md).

While the OAuth app remains in Testing, refresh tokens expire after seven days with these scopes. For ongoing use, review Google's requirements, switch the publishing status to In Production, and authorize again. Changing the publishing status is not the same as passing Google's verification.

| Problem | What to check |
| --- | --- |
| `redirect_uri_mismatch` | Use a Web application client with exactly `http://localhost:8765/`, and download the updated client JSON |
| Consent is blocked or access is denied | Check the selected Google account, Audience / Test users, and all ten requested scopes |
| `invalid_grant` after previously working | The refresh token may have expired or been revoked. Check Testing status and authorize again; update Key Vault too if already deployed |
| Drive folder returns 403 or 404 | A folder ID alone does not grant `drive.file` access. Follow [DRIVE_FOLDER.md](DRIVE_FOLDER.md) rather than requesting full Drive access |

### Configuration

Local runs use `src/HealthSync.Functions/local.settings.json`, generated during initial authorization. In Azure, register application settings with the same names.

| Environment variable | Description and default |
| --- | --- |
| `GOOGLE_CLIENT_ID` | OAuth client ID (required) |
| `GOOGLE_CLIENT_SECRET` | Secret for the same OAuth client (required) |
| `GOOGLE_REFRESH_TOKEN` | Refresh token obtained during initial authorization (required) |
| `DRIVE_FILE_ID` | ID of the JSON file to update, not a folder ID or URL (required) |
| `HEALTH_SYNC_SCHEDULE` | `0 0 22 * * *`. 22:00 UTC = 7:00 JST the following morning |
| `HEALTH_TIMEZONE` | `Asia/Tokyo`. Used to calculate dates for the query period |
| `HEALTH_LOOKBACK_DAYS` | `14`. Allowed range: 1–14 |
| `HEALTH_INCLUDE_ALL_DATA` | `true`. Set to `false` to fetch only a small set of summary metrics |
| `HEALTH_ARCHIVE_ENABLED` | `true`. Save dated snapshots. Set to `false` to keep only the latest file |
| `DRIVE_ARCHIVE_FOLDER_ID` | Optional. If omitted, create or reuse an unshared history folder. The app must have access to any folder you specify |
| `INCLUDE_HOURLY_HEART_RATE` | `true`. Save additional hourly heart rate aggregates |

Do not set `WEBSITE_TIME_ZONE` or `TZ` on Flex Consumption. In Azure, use Key Vault references for the four required settings above.

### Deploying to Azure

Complete the local authorization and sync first. Run the commands below in **PowerShell 7**, from the repository root. The repository does not create Azure resources automatically.

#### 1. Create the resources and enable managed identity

| Resource | Required setup |
| --- | --- |
| Function App | **Linux**, **Flex Consumption**, **.NET 10 isolated**. Enable **Identity > System assigned** and keep **Always ready instances at 0** for scale-to-zero operation |
| Storage account | Configure the Function App's host storage and a private deployment blob container. Configure managed-identity access for both; Key Vault permissions do not grant storage access |
| Key Vault | Select **Azure role-based access control** under **Access configuration** |
| Application Insights | Connect it to the Function App so you can inspect sync results and failures |

For storage identity settings and permissions, follow the [Functions host storage guidance](https://learn.microsoft.com/azure/azure-functions/functions-reference#connecting-to-host-storage-with-an-identity) and [Flex Consumption deployment guidance](https://learn.microsoft.com/azure/azure-functions/flex-consumption-plan).

Your setup account needs permission to create role assignments and write vault secrets. For secret writes, grant it a data-plane role such as `Key Vault Secrets Officer` on the vault; subscription `Owner` alone does not automatically grant secret data access. The Function App gets read-only access separately below.

#### 2. Record your deployment target

```powershell
New-Item private -ItemType Directory -Force
Copy-Item infra/production.example.json private/deployment-target.json
```

Edit `private/deployment-target.json`. Every value in the example is a placeholder.

| JSON field | Value to enter |
| --- | --- |
| `subscriptionId` | Your Azure subscription ID |
| `tenantId` | Your Microsoft Entra tenant ID |
| `resourceGroup` | Resource group containing the Function App |
| `functionApp` | Function App name, not its URL |
| `keyVault` | Key Vault name, not its URL |
| `storageAccount` | Deployment storage account name |
| `deploymentContainer` | Existing private blob container configured as the Function App's deployment storage |

#### 3. Register secrets and application settings

The following commands read the files generated by bootstrap without printing the Google secrets. They store the four required values in Key Vault, grant the Function identity access only to those secrets, and generate versionless Key Vault references.

```powershell
$target = Get-Content private/deployment-target.json -Raw | ConvertFrom-Json
$metadata = Get-Content private/bootstrap.json -Raw | ConvertFrom-Json
$vault = $target.keyVault
$subscription = $target.subscriptionId
az login --tenant $target.tenantId

az keyvault secret set --vault-name $vault --name google-client-id --value $metadata.google_client_id --subscription $subscription --output none
az keyvault secret set --vault-name $vault --name google-client-secret --file private/google-client-secret.txt --subscription $subscription --output none
az keyvault secret set --vault-name $vault --name google-refresh-token --file private/google-refresh-token.txt --subscription $subscription --output none
az keyvault secret set --vault-name $vault --name drive-file-id --value $metadata.drive_file_id --subscription $subscription --output none

$principal = az functionapp identity assign --name $target.functionApp --resource-group $target.resourceGroup --subscription $subscription --query principalId --output tsv
$vaultId = az keyvault show --name $vault --subscription $subscription --query id --output tsv
foreach ($name in @('google-client-id','google-client-secret','google-refresh-token','drive-file-id')) {
  az role assignment create --assignee-object-id $principal --assignee-principal-type ServicePrincipal --role 'Key Vault Secrets User' --scope "$vaultId/secrets/$name" --subscription $subscription --output none
}

dotnet run --project tools/HealthSync.Tool -- cloud-settings --vault $vault --vault-only
az functionapp config appsettings set --name $target.functionApp --resource-group $target.resourceGroup --subscription $subscription --settings '@private/azure.appsettings.json' --output none
```

Check each command's exit status and stop if it fails. Allow time for RBAC propagation and check that all four Key Vault references resolve in the Function App's application settings. Keep the existing host storage and monitoring settings; do not upload the local `UseDevelopmentStorage=true` setting to Azure. Do not set `WEBSITE_TIME_ZONE` or `TZ`: the default timer uses UTC, with `0 0 22 * * *` corresponding to 7:00 JST the next morning.

#### 4. Deploy manually and verify a real run

With an Azure CLI login that has deployment permissions, publish and deploy:

```powershell
dotnet publish src/HealthSync.Functions -c Release -r linux-x64 --self-contained false -o artifacts/publish
./tools/Deploy-Production.ps1 -TargetPath private/deployment-target.json -SettingsPath private/azure.appsettings.json
```

The deployment identity needs the roles listed in the next section, whether it is your own account or a GitHub identity. The script uses ARM OneDeploy and stops before uploading if Key Vault references are unresolved; making the SCM endpoint public is not required.

After the next scheduled run, check for `Health sync completed` in Application Insights and a new `query_completed_at_utc` in Drive. Timer registration alone does not prove that Google authorization or data retrieval succeeded. After reauthorizing Google, update the vault secrets and refresh the Key Vault references or restart the Function App.

#### 5. Optional: enable GitHub Actions deployment

Initially, only checks run. To enable automatic deployment, create a dedicated app under **Microsoft Entra ID > App registrations**, then add a **GitHub Actions federated credential** instead of a client password. Trust **only the `main` branch of your own repository**. Use issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and your repository's actual OIDC subject, including immutable IDs if applicable. See [DEPLOYMENT.md](DEPLOYMENT.md) for the subject format.

Assign the deployment identity these roles at the indicated scopes. Do not give this identity access to the Google secrets.

| Role | Scope |
| --- | --- |
| `Website Contributor` | Target Function App |
| `Storage Blob Data Contributor` | Target deployment blob container |
| `Storage Blob Delegator` | Deployment storage account |

Under your GitHub repository's **Settings > Secrets and variables > Actions**, add these **repository secrets**:

| Actions secret | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | Application (client) ID of the deployment app registration |
| `AZURE_TENANT_ID` | Tenant ID matching `deployment-target.json` |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID matching `deployment-target.json` |
| `AZURE_DEPLOYMENT_TARGET_JSON` | The edited JSON file's contents, not its path |

Do not put the Google client secret or refresh token in GitHub. Finally, add the **repository variable** `HEALTH_SYNC_DEPLOY_ENABLED` with value `true`. Merge a PR after the Linux and Windows checks pass; deployment runs only for pushes to `main` or manual workflow runs on `main`, not for PRs or `dev`. Protect `main` with required checks and PR reviews.

See [DEPLOYMENT.md](DEPLOYMENT.md) for additional deployment and package-retention details.

### Running costs

With **Always ready instances set to 0**, Flex Consumption can **scale to zero** between runs. This app performs its sync when the daily Timer fires rather than keeping a server running all day. For a short, once-a-day personal workload, Functions execution charges can be **close to zero**, especially within any applicable monthly free grants.

The total Azure bill is not guaranteed to be zero. **Storage capacity and transactions, Key Vault operations**, and potentially Application Insights log ingestion still incur charges. Enabling Always ready instances also adds idle-instance charges. Actual costs depend on the region, memory, execution time, usage, and free grants; check [Azure Functions pricing](https://azure.microsoft.com/pricing/details/functions/) and your Azure Cost Management usage.

### Using ChatGPT or Claude

Download `health_sync_latest.json` from Drive and attach it to a conversation in ChatGPT or Claude. If your plan's Drive integration can read the actual JSON body, you can use that instead; pasting a link alone may not be enough. Review the assistant's data-handling settings before sharing health records.

This short prompt works with either assistant:

```text
Read the attached health_sync_latest.json.
First report query_completed_at_utc, timezone, and range, and flag stale or partial data.
Compare the most recent 7 days with the preceding 7 days for steps, sleep, and resting heart rate, using only the dates actually available.
Follow notes and collection_statuses. Do not treat missing data as zero, double-count overlapping records, or make medical diagnoses.
Summarize the main changes and suggest up to 3 realistic habits to try.
```

The query timestamp is not the device's last sync time. Use the latest JSON for this comparison; daily history files overlap and must not simply be added together. Connecting an assistant or scheduling these reviews is a separate setup task.

### Checks and precautions

```sh
dotnet run --project tests/HealthSync.Checks -c Release
dotnet build src/HealthSync.Functions -c Release
```

- Do not commit `private/`, your actual `local.settings.json`, OAuth JSON, tokens, or health data to Git.
- Drive files may contain ECG and exercise location data. Avoid shared folders and check the file's sharing settings.
- The latest file is updated only after the snapshot is saved. Snapshots cover overlapping periods, so do not simply add up steps or calories across multiple files.
- Missing records and unavailable APIs are recorded separately. Network, authorization, or storage quota errors leave the previous file intact.
- In ChatGPT or Claude, first confirm that it can read the JSON body and that `query_completed_at_utc` is recent. Automatic polling must be configured separately.

[Security](SECURITY.md) / [Validation](VALIDATION.md)

---

## 日本語

Google Healthの記録を毎朝Google Driveへ保存する、C#の個人用ツールです。
Geminiの有料プランを追加せず、普段使っているChatGPTやClaudeで健康データを見たい人向けに作っています。同期処理はAIモデルのAPIを呼ばず、分析は各サービス側で行います。

```text
Pixel Watch / Fitbit → Google Health → Azure Functions → DriveのJSON
                                                           ↑
                                                 ChatGPT / Claudeで読む
```

歩数、消費カロリー、睡眠、心拍、運動、体重、食事など、読み取り対応39型を対象にします。毎朝7時（日本時間）に直近14日分を取得し、最新ファイルと日付付き履歴を保存します。高頻度データは主に1時間集計です。[取得項目と制限](ACTIVITY_DATA.md)

最新は`health_sync_latest.json`、履歴は非共有の`Health Sync History/health_sync_YYYY-MM-DD.json`です。同日の再実行は同じ履歴を更新し、過去日のファイルは自動削除しません。保存開始前の全履歴を復元する機能ではなく、日々の保存から長期記録を残す仕組みです。Driveの使用量は増えていきます。

Azureの利用料は別途かかります。[運用コスト](#運用コスト)も確認してください。ChatGPTやClaudeへのファイル添付・Drive連携は、利用プランと対応機能に依存します。各サービス側の接続や定期分析は、このコードでは設定しません。

### 構成

| 場所 | 役割 |
| --- | --- |
| [src/HealthSync.Functions](src/HealthSync.Functions) | Timer Triggerとホスト起動 |
| [src/HealthSync.Core](src/HealthSync.Core) | Google認証、データ取得、JSON作成、Drive更新 |
| [tools/HealthSync.Tool](tools/HealthSync.Tool) | 初回OAuth認可とローカル単発実行 |
| [tests](tests) | 模擬HTTP・認証・配置・発行物の検査 |

AzureはFunctions Flex Consumption、.NET 10 isolatedを使います。認証情報はKey Vaultに置き、FunctionsのマネージドIDで読みます。公開HTTPエンドポイントはありません。

### はじめる

必要なのは.NET 10 SDK、Googleアカウント、Google Cloudプロジェクトです。Azure配置にはAzure CLIとPowerShell 7も使います。

#### 1. Google APIを有効にする

1. [Google Cloud Console](https://console.cloud.google.com/)でプロジェクトを作成、または選択する。2つのAPIと、後で作るOAuthクライアントには同じプロジェクトを使う。
2. **APIs & Services（APIとサービス）> Library（ライブラリ）**で**Google Health API**と**Google Drive API**を有効にし、**Enabled APIs & services（有効なAPIとサービス）**に両方が表示されることを確認する。
3. スマートフォンとPixel Watch / Fitbitのデータを、認可に使うGoogleアカウントへ同期しておく。初回認可ツールは、Driveファイルを準備する前にGoogle Healthとの連携を確認します。

このツールは利用者本人のOAuth認可を使います。GoogleのサービスアカウントやAPIキーは使いません。

#### 2. 同意画面とOAuthクライアントを設定する

1. **Google Auth Platform（Google認証プラットフォーム）**、または**APIとサービス > OAuth同意画面**を開き、**Branding（ブランディング）**でアプリ名、サポート用メール、デベロッパーの連絡先メールを登録する。
2. 個人のGoogleアカウントなら**Audience（対象）**を**External（外部）**にする。公開ステータスが**Testing（テスト）**の間は、**Test users（テストユーザー）**へ手順1のGoogleアカウントを追加する。**Internal（内部）**は対象のGoogle Workspace組織内の利用者向けです。
3. **Data Access（データアクセス）> Add or remove scopes（スコープを追加または削除）**で、下記のHealth読み取り9権限と`drive.file`を追加する。一覧にない場合は、スコープを手動追加する欄を使う。[HealthDataCatalog.cs](src/HealthSync.Core/HealthDataCatalog.cs)と同じ内容です。

```text
https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly
https://www.googleapis.com/auth/googlehealth.health_metrics_and_measurements.readonly
https://www.googleapis.com/auth/googlehealth.nutrition.readonly
https://www.googleapis.com/auth/googlehealth.sleep.readonly
https://www.googleapis.com/auth/googlehealth.ecg.readonly
https://www.googleapis.com/auth/googlehealth.irn.readonly
https://www.googleapis.com/auth/googlehealth.profile.readonly
https://www.googleapis.com/auth/googlehealth.settings.readonly
https://www.googleapis.com/auth/googlehealth.location.readonly
https://www.googleapis.com/auth/drive.file
```

4. **Clients（クライアント）**でOAuthクライアントを作り、アプリの種類を**Web application（ウェブアプリケーション）**にする。Desktop appではありません。**Authorized redirect URIs（承認済みのリダイレクトURI）**に、末尾の`/`も含めて`http://localhost:8765/`を登録する。**承認済みのJavaScript生成元**に追加するだけでは動きません。
5. 保存後にクライアントJSONをダウンロードし、Gitの追跡対象外である`private/`ディレクトリを作って`private/client.json`として置く。JSON内の`web`に上記のリダイレクトURIが必要です。クライアント設定を変更した場合は、JSONも再ダウンロードしてください。

#### 3. 初回認可とローカル実行を試す

ブラウザーを開くパソコン上で、リポジトリのルートから実行します。OAuthの折り返し先としてローカルの`8765`番ポートを使うため、空けておいてください。

```sh
dotnet run --project tools/HealthSync.Tool -- bootstrap --client-secrets private/client.json
dotnet run --project tools/HealthSync.Tool -- run
```

`bootstrap`では、テストユーザーに登録したGoogleアカウントを選び、要求された権限をすべて許可します。成功するとDriveに`Health Sync/health_sync_latest.json`を作り、リンクを`private/bootstrap.json`へ保存します。続く`run`で健康データをアップロードします。保存されたリンクを開き、`query_completed_at_utc`が新しいことを確認してください。

初回認可では`private/google-client-secret.txt`、`private/google-refresh-token.txt`、`src/HealthSync.Functions/local.settings.json`も生成します。ローカル実行と後述のAzure設定に使うので、外部へ公開しないでください。再認可しても既存のDriveファイルは作り直しません。保存先の指定や、認可後の権限エラーからの再開方法は[DRIVE_FOLDER.md](DRIVE_FOLDER.md)を参照してください。

OAuthがTestingのままだと、今回の権限ではrefresh tokenが7日で失効します。継続運用ではGoogleの条件を確認してIn Productionへ切り替え、その後に再認可します。公開状態の変更とGoogleの審査通過は別です。

| 症状 | 確認すること |
| --- | --- |
| `redirect_uri_mismatch` | Web applicationのクライアントに`http://localhost:8765/`を完全一致で登録し、更新後のJSONをダウンロードする |
| 同意画面でブロックされる・アクセスを拒否される | 選択したGoogleアカウント、Audience / Test users、要求される10権限を確認する |
| 以前は動いたのに`invalid_grant`になる | refresh tokenの期限切れや失効の可能性がある。Testingの状態を確認して再認可し、配置済みならKey Vaultも更新する |
| Driveフォルダーで403・404になる | フォルダーIDだけでは`drive.file`のアクセス権は付かない。Drive全体の権限を追加せず、[DRIVE_FOLDER.md](DRIVE_FOLDER.md)の手順で対応する |

### 設定

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

### Azureへ配置

先にローカルの初回認可と同期を成功させてください。以降のコマンドは、リポジトリのルートで**PowerShell 7**から実行します。このリポジトリはAzureリソースを自動作成しません。

#### 1. リソースを作り、マネージドIDを有効にする

| リソース | 必要な設定 |
| --- | --- |
| Function App | **Linux**、**Flex Consumption**、**.NET 10 isolated**。**Identity（ID）> System assigned（システム割り当て済み）**を有効にし、ゼロスケールのために**Always ready instancesは0**にする |
| Storage account | Functionsのホスト用Storageと、配置用の非公開Blobコンテナーを設定する。両方にマネージドIDのアクセスを設定する。Key Vaultの権限だけではStorageへアクセスできない |
| Key Vault | **Access configuration（アクセス構成）**で**Azure role-based access control（Azureロールベースのアクセス制御）**を選ぶ |
| Application Insights | 同期の成否やエラーを確認できるよう、Function Appに接続する |

StorageのID設定と必要な権限は、[FunctionsのホストStorageの説明](https://learn.microsoft.com/azure/azure-functions/functions-reference#connecting-to-host-storage-with-an-identity)と[Flex Consumptionの配置の説明](https://learn.microsoft.com/azure/azure-functions/flex-consumption-plan)に従ってください。

設定する自分のアカウントには、ロールを割り当てる権限とVaultへシークレットを書き込む権限が必要です。書き込みには、対象Vaultの`Key Vault Secrets Officer`などのデータ操作ロールを付けます。サブスクリプションの`Owner`だけでは、シークレットのデータ操作権限は自動で付きません。Function Appには、後の手順で読み取り専用の権限を別途付けます。

#### 2. 配置先の情報を記録する

```powershell
New-Item private -ItemType Directory -Force
Copy-Item infra/production.example.json private/deployment-target.json
```

`private/deployment-target.json`を編集します。サンプルの値はすべて置き換えてください。

| JSONの項目 | 入れる値 |
| --- | --- |
| `subscriptionId` | 自分のAzureサブスクリプションID |
| `tenantId` | 自分のMicrosoft EntraテナントID |
| `resourceGroup` | Function Appが属するリソースグループ |
| `functionApp` | Function Appの名前。URLではない |
| `keyVault` | Key Vaultの名前。URLではない |
| `storageAccount` | 配置用Storageアカウントの名前 |
| `deploymentContainer` | Function Appの配置Storageに設定した、作成済みの非公開Blobコンテナー |

#### 3. シークレットとアプリ設定を登録する

次のコマンドは、初回認可で生成したファイルからGoogleの秘密値を表示せずに読み込みます。必須の4項目をKey Vaultへ保存し、そのシークレットだけにFunctionsのIDの読み取り権限を付け、バージョンを固定しないKey Vault参照を生成します。

```powershell
$target = Get-Content private/deployment-target.json -Raw | ConvertFrom-Json
$metadata = Get-Content private/bootstrap.json -Raw | ConvertFrom-Json
$vault = $target.keyVault
$subscription = $target.subscriptionId
az login --tenant $target.tenantId

az keyvault secret set --vault-name $vault --name google-client-id --value $metadata.google_client_id --subscription $subscription --output none
az keyvault secret set --vault-name $vault --name google-client-secret --file private/google-client-secret.txt --subscription $subscription --output none
az keyvault secret set --vault-name $vault --name google-refresh-token --file private/google-refresh-token.txt --subscription $subscription --output none
az keyvault secret set --vault-name $vault --name drive-file-id --value $metadata.drive_file_id --subscription $subscription --output none

$principal = az functionapp identity assign --name $target.functionApp --resource-group $target.resourceGroup --subscription $subscription --query principalId --output tsv
$vaultId = az keyvault show --name $vault --subscription $subscription --query id --output tsv
foreach ($name in @('google-client-id','google-client-secret','google-refresh-token','drive-file-id')) {
  az role assignment create --assignee-object-id $principal --assignee-principal-type ServicePrincipal --role 'Key Vault Secrets User' --scope "$vaultId/secrets/$name" --subscription $subscription --output none
}

dotnet run --project tools/HealthSync.Tool -- cloud-settings --vault $vault --vault-only
az functionapp config appsettings set --name $target.functionApp --resource-group $target.resourceGroup --subscription $subscription --settings '@private/azure.appsettings.json' --output none
```

各コマンドの終了コードを確認し、失敗したまま先へ進めないでください。RBACの反映を待ち、Function Appのアプリ設定で4つのKey Vault参照が解決していることを確認します。既存のホストStorageや監視設定は残し、ローカル用の`UseDevelopmentStorage=true`をAzureへ登録しないでください。`WEBSITE_TIME_ZONE`と`TZ`も設定しません。既定のTimerはUTCで動き、`0 0 22 * * *`が翌朝7時JSTに当たります。

#### 4. 手動で配置し、実際の同期を確認する

配置権限を持つアカウントでAzure CLIにログインした状態で、発行と配置を行います。

```powershell
dotnet publish src/HealthSync.Functions -c Release -r linux-x64 --self-contained false -o artifacts/publish
./tools/Deploy-Production.ps1 -TargetPath private/deployment-target.json -SettingsPath private/azure.appsettings.json
```

配置に使うIDには、自分のアカウントでもGitHub用のIDでも、次の節にある配置用ロールが必要です。スクリプトはARM OneDeployを使い、Key Vault参照が未解決ならアップロード前に停止します。配置のためにSCMエンドポイントを公開する必要はありません。

次の定刻実行後に、Application Insightsの`Health sync completed`と、Driveの`query_completed_at_utc`の更新を確認します。Timerの登録成功だけでは、Google認可やデータ取得の成功までは確認できません。Googleを再認可した後はVaultのシークレットを更新し、Key Vault参照を再取得するかFunction Appを再起動してください。

#### 5. 任意：GitHub Actionsからの自動配置を有効にする

初期状態ではChecksだけが動きます。自動配置には、**Microsoft Entra ID > App registrations（アプリの登録）**で配置専用アプリを作り、クライアントパスワードではなく**GitHub Actionsのフェデレーション資格情報**を追加します。信頼する対象は**自分のリポジトリのmainブランチだけ**に限定してください。issuerは`https://token.actions.githubusercontent.com`、audienceは`api://AzureADTokenExchange`とし、subjectは不変IDを使う場合も含め、リポジトリの実際のOIDC subjectに合わせます。形式は[DEPLOYMENT.md](DEPLOYMENT.md)を参照してください。

配置用IDには、次のスコープでロールを付けます。このIDにはGoogleのシークレットを読む権限を付けません。

| ロール | スコープ |
| --- | --- |
| `Website Contributor` | 対象のFunction App |
| `Storage Blob Data Contributor` | 対象の配置用Blobコンテナー |
| `Storage Blob Delegator` | 配置用Storageアカウント |

GitHubリポジトリの**Settings > Secrets and variables > Actions**で、次の**Repository secrets**を登録します。

| Actions Secret | 入れる値 |
| --- | --- |
| `AZURE_CLIENT_ID` | 配置用アプリ登録のApplication (client) ID |
| `AZURE_TENANT_ID` | `deployment-target.json`と同じtenant ID |
| `AZURE_SUBSCRIPTION_ID` | `deployment-target.json`と同じsubscription ID |
| `AZURE_DEPLOYMENT_TARGET_JSON` | 編集したJSONの内容。ファイルパスではない |

Googleのclient secretやrefresh tokenはGitHubへ登録しません。最後に**Repository variable**として`HEALTH_SYNC_DEPLOY_ENABLED`を値`true`で追加します。LinuxとWindowsのChecksが通ったPRをマージすると配置されます。対象は`main`へのpushと`main`での手動実行だけで、PRや`dev`では配置しません。`main`には必須チェックとPRレビューの保護も設定してください。

配置やパッケージの保持期間について、詳しくは[DEPLOYMENT.md](DEPLOYMENT.md)を参照してください。

### 運用コスト

Flex Consumptionは、**Always ready instancesを0**にしておけば、実行していない間は**ゼロスケール**できます。このアプリはサーバーを24時間動かし続けるのではなく、毎日のTimer起動時に同期処理を行います。個人用に1日1回、短時間だけ動かす使い方なら、適用される月間無料枠も含めて、**Functionsの実行料金はほぼゼロに抑えやすい構成**です。

ただし、Azure全体が必ず無料になるわけではありません。**Storageの保存容量・トランザクション、Key Vaultの操作**には料金がかかり、Application Insightsのログ取り込みにも料金が発生する場合があります。Always ready instancesを有効にすると、待機中のインスタンス料金も加わります。実際の金額はリージョン、メモリ、実行時間、利用量、無料枠によって変わるため、[Azure Functionsの料金](https://azure.microsoft.com/pricing/details/functions/)とAzure Cost Managementの使用量を確認してください。

### ChatGPT・Claudeで読む

Driveから`health_sync_latest.json`をダウンロードし、ChatGPTまたはClaudeの会話に添付します。利用中のプランのDrive連携でJSON本文を読める場合は、そちらでも構いません。リンクを貼るだけでは本文を読めないことがあります。健康データを渡す前に、各サービスのデータ取り扱い設定も確認してください。

どちらのサービスでも、例えば次のプロンプトを使えます。

```text
添付したhealth_sync_latest.jsonを読んでください。
最初にquery_completed_at_utc、timezone、rangeを確認し、データが古い場合や期間が途中の場合は教えてください。
実際に取得できている日付だけを使い、直近7日とその前の7日の歩数・睡眠・安静時心拍を比較してください。
notesとcollection_statusesに従い、欠測をゼロ扱いしたり、重複する記録を二重集計したり、医学的な診断をしたりしないでください。
主な変化を短くまとめ、無理なく試せる生活習慣を3つまで提案してください。
```

取得完了時刻は、端末の最終同期時刻とは異なります。この比較には最新のJSONを使ってください。日付付き履歴は期間が重なるため、単純に足し合わせないでください。各サービスへの接続や、この分析の定期実行は別途設定が必要です。

### 確認と注意

```sh
dotnet run --project tests/HealthSync.Checks -c Release
dotnet build src/HealthSync.Functions -c Release
```

- `private/`、実際の`local.settings.json`、OAuth JSON、トークン、健康データはGitに入れないでください。
- Driveには心電図や運動の位置情報も入り得ます。共有フォルダーを避け、ファイルの共有設定を確認してください。
- 履歴が保存できてから最新を更新します。履歴は期間が重なるため、複数ファイルの歩数・カロリーを単純に足さないでください。
- 記録なしとAPI利用不可を分けて保存します。通信・認可・容量のエラーでは前回のファイルを保持します。
- ChatGPTやClaudeでは、まずJSON本文を読めることと`query_completed_at_utc`が新しいことを確認してください。自動ポーリングの設定は別途必要です。

[セキュリティ](SECURITY.md) / [検証手順](VALIDATION.md)