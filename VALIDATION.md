# 検証

.NET 10 SDKとPowerShell 7を使い、リポジトリのルートで実行します。

```powershell
dotnet restore src/HealthSync.Functions
dotnet run --project tests/HealthSync.Checks -c Release
dotnet publish src/HealthSync.Functions -c Release -r linux-x64 --self-contained false -o artifacts/publish
./tools/Test-Publish.ps1
./tests/Publish.Checks.ps1
./tests/Deployment.Checks.ps1
./tools/Test-Dependencies.ps1
./tools/Test-Secrets.ps1
```

## 自動検査の範囲

- C#の80チェック。取得期間・39型・API別認証・ページング・再試行・欠測・部分失敗・容量上限・TCX XML・Drive更新・認証ファイル権限を検証する。
- 履歴を最新より先に保存し、同日再実行は再利用、別日は別ファイルになることを検査する。年末の日付境界、指定履歴フォルダー、共有フォルダー・共有ドライブ・不完全検索・重複・保存失敗時の停止も対象。
- Windowsでは、他ユーザーの読み取りを許可したダミーファイルからアクセス権を除去できることを確認する。Linuxではファイルの所有者限定モードを確認する。
- Functions SDKが生成したメタデータに、監視有効・起動時実行無効のTimerが登録されることを確認する。
- 発行物に実設定・トークン・秘密鍵・健康JSONが混ざると失敗することを確認する。必要な`.azurefunctions`は残す。
- 架空の配置設定で、正常配置、参照再取得、シークレット不足、配置失敗、Timer登録失敗を模擬する。署名付きURLの表示や一時ファイルの残存も検査する。
- Gitleaksで到達可能なGit履歴を検査し、NuGetの推移的依存まで既知脆弱性を照会する。照会自体に失敗した場合は正常扱いしない。

CIはWindowsとLinuxで実行し、週次でも再検査します。依存パッケージとActionsの更新はDependabotがPRを作成します。

## 実環境で別に確認すること

模擬HTTPテストには、利用者のGoogleアカウントやAzureへのアクセス権を証明する効果はありません。自分の環境では次を確認してください。

1. 初回認可で必要なHealth読み取り権限と`drive.file`が許可されたか。
2. ローカル単発実行後、対象Driveファイルの親フォルダー・共有範囲・完了時刻が正しいか。
3. Vaultの4参照が解決し、Timerが登録されているか。
4. 実際のクラウド実行でDriveが更新されるか。古いファイルを新しい記録として扱っていないか。
5. ChatGPTのDrive連携がJSON本文を読めるか。定期実行にその連携を使えるか。

公開版には実アカウントの測定値、取得件数、端末情報、個別Azure環境の識別子を含めていません。検査の限界は[SECURITY.md](SECURITY.md)を参照してください。