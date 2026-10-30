using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Butler;

// Secrets (the Todoist token) in the Windows Credential Manager, per user, never in settings.json.
public static class Secrets
{
    public const string Todoist = "Butler/Todoist";

    public static string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var p))
        {
            if (Marshal.GetLastWin32Error() == ErrorNotFound) return null;
            throw new Win32Exception();
        }
        try
        {
            var c = Marshal.PtrToStructure<Credential>(p);
            return c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2);
        }
        finally { CredFree(p); }
    }

    public static void Write(string target, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var p = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, p, blob.Length);
            var c = new Credential
            {
                Type = CredTypeGeneric, TargetName = target, UserName = Environment.UserName,
                CredentialBlob = p, CredentialBlobSize = (uint)blob.Length, Persist = CredPersistLocalMachine,
            };
            if (!CredWrite(ref c, 0)) throw new Win32Exception();
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    public static void Delete(string target)
    {
        if (!CredDelete(target, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() != ErrorNotFound) throw new Win32Exception();
    }

    const uint CredTypeGeneric = 1, CredPersistLocalMachine = 2;
    const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
