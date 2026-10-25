# Use an Existing Drive Folder

## Folder ID and File ID

The ID in `https://drive.google.com/drive/folders/FOLDER_ID` identifies a folder. It is not the value of `DRIVE_FILE_ID` or the vault entry `drive-file-id`.

The bootstrap tool creates `health_sync_latest.json` inside the requested folder and records the resulting JSON file ID. Daily runs update that same file; they do not create another file every morning.

## Bootstrap

Use a web OAuth client belonging to this app with `http://localhost:8765/` registered as an authorized redirect URI. The downloaded client JSON must include the same URI. Run from the repository root with .NET 10:

```powershell
dotnet run --project tools/HealthSync.Tool -- bootstrap --client-secrets PATH_TO_CLIENT_JSON --drive-folder FOLDER_ID
```

Pass the folder ID only, not the full folder URL. With no `--drive-folder` option, the tool retains its original behavior and creates a private `Health Sync` folder.

After consent, the client secret and refresh token are saved under the Git-ignored `private/` directory before checking Google Health linkage. A later Health or Drive permission error does not discard the newly obtained refresh token. Saving credentials alone does not mark the account as connected. Do not post these files or values into chat or GitHub.

Successful Drive setup also writes `private/bootstrap.json`, including `drive_file_id`, `drive_folder_id`, and the JSON file URL. The Function App's `DRIVE_FILE_ID` must resolve to `drive_file_id`, not `drive_folder_id`.

Re-running bootstrap reuses the saved JSON without resetting its contents. If that JSON is outside the requested folder, the tool stops rather than moving it or silently replacing the target.

If consent succeeded but a later step failed, reuse the saved refresh token without another browser consent:

```powershell
dotnet run --project tools/HealthSync.Tool -- bootstrap --client-secrets PATH_TO_CLIENT_JSON --resume --drive-folder FOLDER_ID
```

## API-Specific Access Tokens

Consent requests nine Health read-only scopes and `drive.file`. The refresh token is shared, but access tokens are not. Health and Drive use separately scoped access tokens to avoid sending unrelated permissions to either API.

`GoogleTokenProvider` uses Google's `RefreshTokenRequest` with an explicit `Scope` and retains separate `UserCredential` caches for Health and Drive. Health calls receive only Health read scopes; Drive metadata and upload calls receive only `drive.file`. It rejects unexpected returned scope sets and refuses to send tokens to unapproved endpoints. No additional Key Vault secrets are needed.

## Existing Folder Permissions

The OAuth scope remains `drive.file`; this change does not request access to the user's entire Drive. The Google account must be allowed to add files to the target folder, and the app must be authorized to access it. Pasting a folder ID does not grant that app access.

The tool checks the folder MIME type, deletion state, and `canAddChildren` capability before creation. The new JSON's returned parent must match the requested folder. Creation and content updates include `supportsAllDrives=true`, but this option does not grant membership or bypass shared-drive permissions.

If an existing folder returns 403 or 404 with `drive.file`, authorize that folder through Google Picker in an app that uses this OAuth project. This CLI does not implement Google Picker. Alternatively:

1. Run bootstrap without `--drive-folder` to create the app's own JSON.
2. In the normal Google Drive UI, move that app-created JSON into the desired folder using the same Google account.
3. Run bootstrap with `--drive-folder` to verify the saved JSON's parent. Its file ID stays the same.

Do not manually create an unrelated file and assume `drive.file` can access it. Do not broaden the scope to all Drive files just to avoid the selection step.

## Sharing and Health Data

An existing folder can confer inherited access to files created inside it. The tool does not add public sharing permissions, but it also does not remove or override the folder's existing sharing settings. Review the selected folder's access before storing health records. A user-selected folder must not automatically be described as private.

## Verification

The folder-setup checks cover the requested parent, folder/file ID confusion, API and permission failures, no unintended fallback, read-only and deleted targets, safe existing-file reuse, and unexpected create responses. Additional checks verify API-specific token scopes, separate caches, endpoint restrictions, and sanitized Google error classification. These are mocked HTTP checks, not proof of access to an arbitrary real folder. Verify the final parent and sharing permissions in your own account.

The deployment workflow retains its private source ZIP rather than deleting it while an asynchronous package fetch could still be in progress. It waits for timer registration, but registration alone is not proof that the newest package is active. Confirm the deployed revision and a real sync separately before declaring production healthy. The single-blob read URL expires after 30 minutes; source packages contain compiled code only, not local settings or health data.

The current Functions SDK uses generated worker indexing. The root `global.json` maps `Azure.Functions.Sdk` to version 1.0.0 so its unversioned generated extension project resolves consistently during the expanded test build.

## References

- https://developers.google.com/workspace/drive/api/guides/folder
- https://developers.google.com/workspace/drive/api/guides/api-specific-auth
