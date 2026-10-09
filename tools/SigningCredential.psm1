# Stores/reads the signing .pfx password in Windows Credential Manager (generic credential, current user only).
# The password is never written to the repository, logs or command lines (docs/10 "Code signing").

$source = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class KakitomeCred
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags; public int Type; public string TargetName; public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist;
        public int AttributeCount; public IntPtr Attributes; public string TargetAlias; public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static void Write(string target, string user, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL { Type = 1, TargetName = target, UserName = user, CredentialBlob = blob, CredentialBlobSize = bytes.Length, Persist = 2 };
            if (!CredWrite(ref c, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    public static string Read(string target)
    {
        IntPtr p;
        if (!CredRead(target, 1, 0, out p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            return Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
        }
        finally { CredFree(p); }
    }
}
'@
if (-not ('KakitomeCred' -as [type])) { Add-Type -TypeDefinition $source -Language CSharp }

$script:Target = 'Kakitome:code-signing'

function Set-SigningPassword([string]$Password) { [KakitomeCred]::Write($script:Target, 'Kakitome', $Password) }

function Get-SigningPassword { [KakitomeCred]::Read($script:Target) }

function Get-SigningDirectory { Join-Path $env:USERPROFILE 'Kakitome-signing' }

Export-ModuleMember -Function Set-SigningPassword, Get-SigningPassword, Get-SigningDirectory
