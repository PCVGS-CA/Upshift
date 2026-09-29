using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Upshift.Core.Install;

/// <summary>
/// Checks a DLL's Authenticode signature: Windows must accept it (WinVerifyTrust, the same check Explorer's "Digital
/// Signatures" tab uses) and the signing certificate must name one of the expected companies (its O or CN).
/// Revocation isn't checked online, so the check also works offline.
/// </summary>
public static class SignatureCheck
{
    /// <summary>Null when the file is validly signed by one of <paramref name="signers"/>; otherwise why not.</summary>
    public static string? Problem(string path, IReadOnlyCollection<string> signers)
    {
        if (!File.Exists(path)) return $"{Path.GetFileName(path)} is missing.";
        var trust = Verify(path);
        if (trust != 0)
            return trust == TrustENoSignature
                ? $"{Path.GetFileName(path)} isn't digitally signed."
                : $"{Path.GetFileName(path)}'s digital signature isn't valid (0x{trust:X8}).";

        var signer = Signer(path);
        if (signer is null) return $"{Path.GetFileName(path)}'s signer couldn't be read.";
        return signers.Any(s => s.Equals(signer.Value.Organization, StringComparison.OrdinalIgnoreCase)
                                || s.Equals(signer.Value.CommonName, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"{Path.GetFileName(path)} is signed by {signer.Value.CommonName}, not {string.Join(" or ", signers.Distinct())}.";
    }

    /// <summary>The signing certificate's organisation and common name, or null.</summary>
    public static (string? Organization, string? CommonName)? Signer(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the documented way to read an Authenticode signer.
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return (Part(cert.SubjectName, OrganizationOid), cert.GetNameInfo(X509NameType.SimpleName, false));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            return null;
        }
    }

    private const string OrganizationOid = "2.5.4.10";

    private static string? Part(X500DistinguishedName name, string oid)
    {
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
            if (!rdn.HasMultipleElements && rdn.GetSingleElementType().Value == oid)
                return rdn.GetSingleElementValue();
        return null;
    }

    // ---------------- WinVerifyTrust ----------------

    private const uint TrustENoSignature = 0x800B0100;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static uint Verify(string path)
    {
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 1,         // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x1000       // WTD_CACHE_ONLY_URL_RETRIEVAL: never go online
            };
            var guid = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref guid, ref data);
            data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref guid, ref data);
            return unchecked((uint)result);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData data);
}
