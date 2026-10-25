# Azureへの配置

初回認可とローカルの単発実行が成功してから進めてください。必要なのは.NET 10 SDK、PowerShell 7、Azure CLIです。

## リソース

Azure Portalで次を用意します。このリポジトリはリソースを自動作成しません。

| リソース | 設定 |
| --- | --- |
| Function App | Linux、Flex Consumption、.NET 10 isolated。デプロイ用Storageも設定 |
| Storage | Timerの状態管理とパッケージ配置用。FunctionsのマネージドIDによるアクセスを設定 |
| Key Vault | Azure RBAC方式 |
| Application Insights | 実行・失敗の確認用 |

Functionsのシステム割り当てIDを有効にします。Google認証情報を登録する自分には、対象Vaultの`Key Vault Secrets Officer`などのデータ操作権限が必要です。サブスクリプションのOwnerだけではシークレットを書けない場合があります。

```powershell
New-Item private -ItemType Directory -Force
Copy-Item infra/production.example.json private/deployment-target.json
```

コピーしたJSONを、自分のsubscription、tenant、リソース名に書き換えてください。`deploymentContainer`はFunction Appの配置ストレージと一致させます。例のGUIDやリソース名をそのまま使うものではありません。

## Key Vaultとアプリ設定

初回認可で生成されたファイルから登録します。秘密値を表示せず、GoogleのシークレットをGitHubへ渡さない手順です。

```powershell
$target = Get-Content private/deployment-target.json -Raw | ConvertFrom-Json
$metadata = Get-Content private/bootstrap.json -Raw | ConvertFrom-Json
$vault = $target.keyVault
$subscription = $target.subscriptionId
az login --tenant $target.tenantId

$principal = az functionapp identity assign --name $target.functionApp --resource-group $target.resourceGroup --subscription $subscription --query principalId --output tsv
$vaultId = az keyvault show --name $vault --subscription $subscription --query id --output tsv
foreach ($name in @('google-client-id','google-client-secret','google-refresh-token','drive-file-id')) {
  az role assignment create --assignee-object-id $principal --assignee-principal-type ServicePrincipal --role 'Key Vault Secrets User' --scope "$vaultId/secrets/$name" --subscription $subscription --output none
}
az keyvault secret set --vault-name $vault --name google-client-secret --file private/google-client-secret.txt --subscription $subscription --output none
az keyvault secret set --vault-name $vault --name google-refresh-token --file private/google-refresh-token.txt --subscription $subscription --output none
az keyvault secret set --vault-name $vault --name google-client-id --value $metadata.google_client_id --subscription $subscription --output none
az keyvault secret set --vault-name $vault --name drive-file-id --value $metadata.drive_file_id --subscription $subscription --output none

dotnet run --project tools/HealthSync.Tool -- cloud-settings --vault $vault --vault-only
az functionapp config appsettings set --name $target.functionApp --resource-group $target.resourceGroup --subscription $subscription --settings '@private/azure.appsettings.json' --output none
```

CLIの終了コードを確認し、失敗した手順のまま先へ進まないでください。既存のホストStorage・監視設定は消しません。Key Vault参照が未解決なら、デプロイスクリプトはパッケージをアップロードする前に停止します。

## GitHub Actions

初期状態ではChecksだけが動きます。自動配置を使う場合だけ、次の設定を行います。

1. Entraに配置専用アプリを作り、クライアントパスワードではなくGitHub OIDCのフェデレーションを設定する。
2. 信頼するsubjectを**自分のリポジトリのmainだけ**に限定する。新形式では`repo:OWNER@OWNER_ID/REPO@REPO_ID:ref:refs/heads/main`。実際のGitHubのsubject形式に合わせ、PRやdevを許可しない。
3. 配置用IDに、対象Function Appの`Website Contributor`、対象配置コンテナーの`Storage Blob Data Contributor`、配置Storageの`Storage Blob Delegator`を付ける。Vaultの読取権限は配置IDではなくFunctionsのIDに付ける。
4. 下記のActions Secretsを登録し、最後にRepository variable `HEALTH_SYNC_DEPLOY_ENABLED`を`true`にする。

| Actions Secret | 内容 |
| --- | --- |
| `AZURE_CLIENT_ID` | 配置用Entraアプリのclient ID |
| `AZURE_TENANT_ID` | tenant ID |
| `AZURE_SUBSCRIPTION_ID` | subscription ID |
| `AZURE_DEPLOYMENT_TARGET_JSON` | 編集した`private/deployment-target.json`の内容 |

AzureのIDは認証秘密ではありませんが、個別環境情報をログに出しにくくするためSecretsへまとめます。Googleのclient secretやrefresh tokenは登録しません。

`dev`から`main`へPRを作り、LinuxとWindowsのChecks成功後にマージします。mainへのpushとmainでの手動実行だけが配置対象です。forkのPRやdevには本番のOIDC権限を渡しません。mainは必須チェック・PR必須・直接push禁止の保護を設定してください。信頼していないPRではワークフロー実行の承認も確認します。

## 手動で配置する場合

GitHub自動配置を使わなくても、OIDCまたは本人のAzure CLIログインで同じスクリプトを実行できます。

```powershell
dotnet publish src/HealthSync.Functions -c Release -r linux-x64 --self-contained false -o artifacts/publish
./tools/Deploy-Production.ps1 -TargetPath private/deployment-target.json -SettingsPath private/azure.appsettings.json
```

ARM OneDeployが、既存の配置Storageに置いたパッケージを取得します。公開アクセス無効のアプリでも、SCMの公開やファイアウォールの緩和を前提にしません。Storage側への到達性と必要なRBACは別途必要です。

単一パッケージを読むSASはHTTPS限定・読取専用・30分です。一時リクエストファイルは成功・失敗のどちらでも削除します。ソースblobは非同期取得に必要なため自動削除しません。実行中の版を確認した後、不要な旧パッケージの保持期間を自分で設定してください。GitHub成果物の保持は3日です。

## 運用確認

翌朝、Application Insightsの`Health sync completed`とDrive内JSONの`query_completed_at_utc`を確認します。Timerの登録成功だけでは、Google認証や実データ取得まで成功したとは限りません。

Googleの再認可やtoken更新後は、Vaultを更新して参照キャッシュを再取得するかFunction Appを再起動します。Google OAuthのIn ProductionとGoogleの審査済み表示は別です。未確認アプリの警告・利用上限や失効条件はGoogleの案内に従ってください。

## 参考

- [Functions Flex Consumptionの配置](https://learn.microsoft.com/azure/azure-functions/flex-consumption-plan)
- [Key Vault参照](https://learn.microsoft.com/azure/app-service/app-service-key-vault-references)
- [GitHub OIDCとAzure](https://docs.github.com/actions/how-tos/secure-your-work/security-harden-deployments/oidc-in-azure)
- [不変IDを含むOIDC subject](https://docs.github.com/actions/reference/security/oidc#immutable-subject-claims)