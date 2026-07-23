using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ValleySteward.WinUI.Services;

public sealed class CredentialService
{
    public const string NexusApiKeyTarget = "nexus-api-key.com.summerxdsss.valleysteward";
    public const string AiTranslationApiKeyTarget = "ai-translation-api-key-v1.com.summerxdsss.valleysteward";

    private const int ErrorNotFound = 1168;
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int MaximumSecretBytes = 2_560;
    private static readonly object CredentialLock = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string? Read(string target)
    {
        ValidateTarget(target);
        lock (CredentialLock)
        {
            return ReadUnlocked(target, "无法从 Windows 凭据管理器读取密钥");
        }
    }

    public void Write(string target, string secret, string userName)
    {
        ValidateTarget(target);
        ArgumentNullException.ThrowIfNull(secret);
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length == 0 || bytes.Length > MaximumSecretBytes)
        {
            Array.Clear(bytes);
            throw new ArgumentException($"密钥长度必须为 1-{MaximumSecretBytes} 个 UTF-8 字节", nameof(secret));
        }

        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredentialForWrite
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = userName,
            };

            lock (CredentialLock)
            {
                if (!CredWrite(ref credential, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error, "无法将密钥保存到 Windows 凭据管理器");
                }

                var stored = ReadUnlocked(target, "无法校验 Windows 凭据");
                if (!string.Equals(stored, secret, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("密钥写入后校验失败");
                }
            }
        }
        finally
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                Marshal.WriteByte(blob, index, 0);
            }
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public bool Delete(string target)
    {
        ValidateTarget(target);
        lock (CredentialLock)
        {
            if (CredDelete(target, CredentialTypeGeneric, 0))
            {
                return true;
            }
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return false;
            }
            throw new Win32Exception(error, "无法从 Windows 凭据管理器删除密钥");
        }
    }

    private static string? ReadUnlocked(string target, string errorMessage)
    {
        if (!CredRead(target, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }
            throw new Win32Exception(error, errorMessage);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }
            if (credential.CredentialBlob == IntPtr.Zero
                || credential.CredentialBlobSize > MaximumSecretBytes)
            {
                throw new InvalidDataException("Windows 凭据中的密钥数据无效");
            }
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                return LooksLikeUtf16LittleEndian(bytes)
                    ? Encoding.Unicode.GetString(bytes)
                    : StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException error)
            {
                throw new InvalidDataException("Windows 凭据中的密钥不是有效 UTF-8 文本", error);
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static bool LooksLikeUtf16LittleEndian(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes.Length % 2 != 0)
        {
            return false;
        }
        var zeroHighBytes = 0;
        for (var index = 1; index < bytes.Length; index += 2)
        {
            if (bytes[index] == 0)
            {
                zeroHighBytes++;
            }
        }
        return zeroHighBytes >= Math.Max(1, bytes.Length / 4);
    }

    private static void ValidateTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 32767)
        {
            throw new ArgumentException("凭据目标名称无效", nameof(target));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredentialForWrite
    {
        public uint Flags;
        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        int reservedFlag,
        out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredentialForWrite credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
