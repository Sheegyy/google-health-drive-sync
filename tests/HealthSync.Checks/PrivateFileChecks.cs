using System.Security.AccessControl;
using System.Security.Principal;
using HealthSync.Tool;

internal static class PrivateFileChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"healthsync-permissions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "credential.txt");
        try
        {
            await File.WriteAllTextAsync(path, "old test value");
            if (OperatingSystem.IsWindows())
            {
                var security = new FileInfo(path).GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.Read, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(security);
            }
            await PrivateFiles.WriteAsync(path, "new test value");
            if (await File.ReadAllTextAsync(path) != "new test value") throw new Exception("Private file write failed");
            if (OperatingSystem.IsWindows())
            {
                var user = WindowsIdentity.GetCurrent().User!;
                var security = new FileInfo(path).GetAccessControl();
                var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                if (!security.AreAccessRulesProtected || rules.Length != 1 || rules[0].IdentityReference != user)
                    throw new Exception("Private file retains another user's access");
            }
            else if (File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
                throw new Exception("Private file must have owner-only Unix permissions");
            await PrivateFiles.WriteAsync(path, "replacement");
            if (await File.ReadAllTextAsync(path) != "replacement") throw new Exception("Private file replacement failed");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}