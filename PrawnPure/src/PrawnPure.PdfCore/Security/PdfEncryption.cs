using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PrawnPure.PdfCore.Objects;

namespace PrawnPure.PdfCore.Security;

/// <summary>
/// Standard PDF Security Handler (V=2, R=3, RC4-128) — port of Prawn::Document::Security + Arcfour
/// extended to Revision 3 (ISO 32000-1 §7.6.3) so all fine-grained permission bits work.
/// </summary>
public sealed class PdfEncryption
{
    // Standard 32-byte padding string (PDF Reference Algorithm 3.2)
    private static readonly byte[] PasswordPadding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private const int KeyLengthBytes = 16; // 128-bit RC4 (V=2, R=3)

    private readonly byte[] _encryptionKey;

    public byte[] FileId { get; }
    public byte[] OwnerHash { get; }
    public byte[] UserHash { get; }
    public int Permissions { get; }

    public PdfEncryption(
        string? userPassword,
        string? ownerPassword,
        int permissions,
        byte[]? fileId = null)
    {
        var userPwd = userPassword ?? string.Empty;
        var ownerPwd = string.IsNullOrEmpty(ownerPassword) ? userPwd : ownerPassword;

        // Per PDF spec Table 22, bits 1-2 must be 0, bits 7-8 and 13-32 must be 1
        Permissions = (permissions | unchecked((int)0xFFFFF0C0)) & ~0x3;
        FileId = fileId is { Length: 16 } ? fileId : ComputeFileId(userPwd, ownerPwd, Permissions);

        OwnerHash = ComputeOwnerHash(ownerPwd, userPwd);
        _encryptionKey = ComputeEncryptionKey(userPwd, OwnerHash, Permissions, FileId);
        UserHash = ComputeUserHash(_encryptionKey, FileId);
    }

    public static int BuildPermissions(
        bool permitPrint = true,
        bool permitFullQualityPrint = true,
        bool permitModifyDocument = false,
        bool permitExtractContent = false,
        bool permitAnnotations = false,
        bool permitFormsFill = false,
        bool permitAssembleDocument = false)
    {
        var p = unchecked((int)0xFFFFF0C0);
        if (permitPrint) p |= 1 << 2;            // bit 3
        if (permitModifyDocument) p |= 1 << 3;   // bit 4
        if (permitExtractContent) p |= 1 << 4;   // bit 5
        if (permitAnnotations) p |= 1 << 5;      // bit 6
        if (permitFormsFill) p |= 1 << 8;        // bit 9
        if (permitExtractContent) p |= 1 << 9;   // bit 10 (accessibility)
        if (permitAssembleDocument) p |= 1 << 10;// bit 11
        if (permitFullQualityPrint) p |= 1 << 11;// bit 12
        return p;
    }

    public PdfDictionary BuildEncryptDictionary() => new()
    {
        ["Filter"] = new PdfName("Standard"),
        ["V"] = new PdfInt(2),
        ["R"] = new PdfInt(3),
        ["Length"] = new PdfInt(KeyLengthBytes * 8),
        ["O"] = new PdfHexString(OwnerHash, encryptable: false),
        ["U"] = new PdfHexString(UserHash, encryptable: false),
        ["P"] = new PdfInt(Permissions),
    };

    /// <summary>Algorithm 3.1: encrypts a string or stream for object (objId, gen).</summary>
    public byte[] Encrypt(byte[] data, int objId, int gen)
    {
        var ext = new byte[_encryptionKey.Length + 5];
        Buffer.BlockCopy(_encryptionKey, 0, ext, 0, _encryptionKey.Length);
        ext[_encryptionKey.Length] = (byte)(objId & 0xFF);
        ext[_encryptionKey.Length + 1] = (byte)((objId >> 8) & 0xFF);
        ext[_encryptionKey.Length + 2] = (byte)((objId >> 16) & 0xFF);
        ext[_encryptionKey.Length + 3] = (byte)(gen & 0xFF);
        ext[_encryptionKey.Length + 4] = (byte)((gen >> 8) & 0xFF);

        var hash = MD5.HashData(ext);
        var keyLen = Math.Min(_encryptionKey.Length + 5, 16);
        var objKey = new byte[keyLen];
        Buffer.BlockCopy(hash, 0, objKey, 0, keyLen);

        return Rc4(objKey, data);
    }

    private static byte[] PadPassword(string password)
    {
        var raw = Encoding.Latin1.GetBytes(password);
        var padded = new byte[32];
        var copyLen = Math.Min(raw.Length, 32);
        Buffer.BlockCopy(raw, 0, padded, 0, copyLen);
        if (copyLen < 32)
            Buffer.BlockCopy(PasswordPadding, 0, padded, copyLen, 32 - copyLen);
        return padded;
    }

    /// <summary>Algorithm 3.3 (Revision 3).</summary>
    private static byte[] ComputeOwnerHash(string ownerPwd, string userPwd)
    {
        var key = MD5.HashData(PadPassword(ownerPwd));
        for (var i = 0; i < 50; i++)
            key = MD5.HashData(key);

        var rc4Key = new byte[KeyLengthBytes];
        Buffer.BlockCopy(key, 0, rc4Key, 0, KeyLengthBytes);

        var result = Rc4(rc4Key, PadPassword(userPwd));
        var iterKey = new byte[KeyLengthBytes];
        for (var i = 1; i <= 19; i++)
        {
            for (var j = 0; j < KeyLengthBytes; j++)
                iterKey[j] = (byte)(rc4Key[j] ^ i);
            result = Rc4(iterKey, result);
        }
        return result;
    }

    /// <summary>Algorithm 3.2 (Revision 3).</summary>
    private static byte[] ComputeEncryptionKey(string userPwd, byte[] ownerHash, int permissions, byte[] fileId)
    {
        var permBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(permBytes, permissions);

        using var ms = new MemoryStream();
        ms.Write(PadPassword(userPwd));
        ms.Write(ownerHash);
        ms.Write(permBytes);
        ms.Write(fileId);

        var hash = MD5.HashData(ms.ToArray());
        var key = new byte[KeyLengthBytes];
        Buffer.BlockCopy(hash, 0, key, 0, KeyLengthBytes);

        for (var i = 0; i < 50; i++)
        {
            hash = MD5.HashData(key);
            Buffer.BlockCopy(hash, 0, key, 0, KeyLengthBytes);
        }
        return key;
    }

    /// <summary>Algorithm 3.5 (Revision 3).</summary>
    private static byte[] ComputeUserHash(byte[] encryptionKey, byte[] fileId)
    {
        var input = new byte[PasswordPadding.Length + fileId.Length];
        Buffer.BlockCopy(PasswordPadding, 0, input, 0, PasswordPadding.Length);
        Buffer.BlockCopy(fileId, 0, input, PasswordPadding.Length, fileId.Length);

        var hash = MD5.HashData(input);
        var result = Rc4(encryptionKey, hash);
        var iterKey = new byte[KeyLengthBytes];
        for (var i = 1; i <= 19; i++)
        {
            for (var j = 0; j < KeyLengthBytes; j++)
                iterKey[j] = (byte)(encryptionKey[j] ^ i);
            result = Rc4(iterKey, result);
        }

        // Pad to 32 bytes
        var finalHash = new byte[32];
        Buffer.BlockCopy(result, 0, finalHash, 0, 16);
        return finalHash;
    }

    private static byte[] ComputeFileId(string userPwd, string ownerPwd, int permissions)
    {
        var seed = Encoding.UTF8.GetBytes($"PrawnPure:{userPwd}:{ownerPwd}:{permissions}");
        return MD5.HashData(seed);
    }

    /// <summary>ARC4 / RC4 stream cipher (port of prawn/security/arcfour.rb).</summary>
    internal static byte[] Rc4(byte[] key, byte[] input)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[input.Length];
        int x = 0, y = 0;
        for (var k = 0; k < input.Length; k++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            output[k] = (byte)(input[k] ^ s[(s[x] + s[y]) & 0xFF]);
        }
        return output;
    }
}
