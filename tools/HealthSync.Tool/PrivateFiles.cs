using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HealthSync.Core;

namespace HealthSync.Tool;

internal static class PrivateFiles
{
    public static async Task WriteAsync(string path, string value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new SyncException("Credential files must not be stored through symbolic links");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        FileStream stream;
        if (OperatingSystem.IsWindows())
        {
            var user = WindowsIdentity.GetCurrent().User ?? throw new SyncException("Unable to identify the credential file owner");
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            stream = new FileInfo(path).Create(FileMode.OpenOrCreate, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.Asynchronous, security);
            try
            {
                stream.SetAccessControl(security);
                var actual = stream.GetAccessControl();
                var rules = actual.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                if (!actual.AreAccessRulesProtected || rules.Length != 1 || rules[0].IdentityReference != user ||
                    rules[0].AccessControlType != AccessControlType.Allow || rules[0].FileSystemRights != FileSystemRights.FullControl)
                    throw new SyncException("Credential file permissions could not be restricted; no new credentials were written");
            }
            catch { await stream.DisposeAsync(); throw; }
        }
        else
        {
            stream = new FileStream(path, options);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        await using var file = stream;
        file.SetLength(0);
        await using var writer = new StreamWriter(file, new UTF8Encoding(false));
        await writer.WriteAsync(value);
    }
}
