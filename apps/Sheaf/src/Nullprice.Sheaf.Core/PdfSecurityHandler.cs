namespace Nullprice.Sheaf.Core;

/// <summary>Everything needed to derive an encryption key from an <c>/Encrypt</c> dictionary
/// (ISO 32000-1 §7.6.3.2) plus the trailer's first <c>/ID</c> element.</summary>
public sealed record PdfSecuritySettings(
    int Revision, int KeyLengthBytes, int Permissions, bool EncryptMetadata,
    byte[] O, byte[] U, byte[] Id0, bool UseAes)
{
    public static (PdfSecuritySettings? Settings, string? UnsupportedReason) FromEncryptDictionary(PdfDictionary encrypt, byte[] id0)
    {
        var filter = (encrypt.Get("Filter") as PdfName)?.Value;
        if (filter is not (null or "Standard"))
            return (null, $"Unsupported security handler: {filter}");

        var v = (encrypt.Get("V") as PdfNumber)?.AsInt ?? 0;
        var r = (encrypt.Get("R") as PdfNumber)?.AsInt ?? 0;

        if (r is < 2 or > 4)
            return (null, $"This PDF uses encryption revision {r}, which isn't supported yet " +
                "(only revisions 2-4 are — revision 6/AES-256 needs a different key-derivation algorithm).");

        var lengthBits = (encrypt.Get("Length") as PdfNumber)?.AsInt ?? 40;
        var keyLengthBytes = lengthBits / 8;

        var useAes = false;
        if (v == 4)
        {
            var stdCf = (encrypt.Get("CF") as PdfDictionary)?.Get("StdCF") as PdfDictionary;
            var cfm = (stdCf?.Get("CFM") as PdfName)?.Value;
            switch (cfm)
            {
                case "AESV2": useAes = true; break;
                case "V2" or null: useAes = false; break;
                default: return (null, $"This PDF's crypt filter ({cfm}) isn't supported yet.");
            }
        }
        else if (v is not (1 or 2))
        {
            return (null, $"This PDF's encryption algorithm (V={v}) isn't supported yet.");
        }

        var o = (encrypt.Get("O") as PdfString)?.Bytes ?? [];
        var u = (encrypt.Get("U") as PdfString)?.Bytes ?? [];
        if (o.Length < 32 || u.Length < 32)
            return (null, "This PDF's encryption dictionary looks malformed (O/U entries too short).");

        var permissions = (encrypt.Get("P") as PdfNumber)?.AsInt ?? 0;
        var encryptMetadata = (encrypt.Get("EncryptMetadata") as PdfBoolean)?.Value ?? true;

        return (new PdfSecuritySettings(r, keyLengthBytes, permissions, encryptMetadata, o, u, id0, useAes), null);
    }
}

/// <summary>
/// The PDF standard security handler (ISO 32000-1 §7.6.3) — RC4 and AES-128-CBC only,
/// revisions 2-4. AES-256/revision 6 uses a materially different key-derivation algorithm and
/// is explicitly out of scope (see <see cref="PdfSecuritySettings.FromEncryptDictionary"/>).
/// <para>
/// This only ever computes keys to <b>decrypt</b> an already-encrypted document — Sheaf's
/// "password removal" feature never re-adds a password, it only ever produces plain output.
/// <see cref="ComputeO"/>/<see cref="ComputeU"/> exist to build a real encrypted PDF as a test
/// fixture (the same self-consistency bootstrap trick used for the parser/writer round-trip
/// elsewhere in this project), not because Sheaf ships an encryption feature of its own.
/// </para>
/// </summary>
public static class PdfSecurityHandler
{
    private static readonly byte[] PaddingBytes =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    public static byte[] PadPassword(string password)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(password);
        var result = new byte[32];
        var n = Math.Min(bytes.Length, 32);
        Array.Copy(bytes, result, n);
        Array.Copy(PaddingBytes, 0, result, n, 32 - n);
        return result;
    }

    /// <summary>
    /// Tries the supplied password both as a user password and, failing that, as an owner
    /// password (recovering the real user password from it via <see cref="RecoverUserPasswordPadded"/>)
    /// — covers "I have the document's real password" regardless of which of the two PDF
    /// distinguishes internally, which is what "password removal where you already hold the
    /// password" actually means to someone who isn't a PDF spec reader.
    /// </summary>
    public static bool TryComputeFileKey(PdfSecuritySettings settings, string? password, out byte[] fileKey)
    {
        var padded = PadPassword(password ?? "");

        var asUserKey = ComputeEncryptionKey(padded, settings.O, settings.Permissions, settings.Id0, settings.Revision, settings.KeyLengthBytes, settings.EncryptMetadata);
        if (ValidateUserPassword(asUserKey, settings.U, settings.Revision, settings.Id0))
        {
            fileKey = asUserKey;
            return true;
        }

        var recoveredUserPassword = RecoverUserPasswordPadded(padded, settings.O, settings.Revision, settings.KeyLengthBytes);
        var asOwnerKey = ComputeEncryptionKey(recoveredUserPassword, settings.O, settings.Permissions, settings.Id0, settings.Revision, settings.KeyLengthBytes, settings.EncryptMetadata);
        if (ValidateUserPassword(asOwnerKey, settings.U, settings.Revision, settings.Id0))
        {
            fileKey = asOwnerKey;
            return true;
        }

        fileKey = [];
        return false;
    }

    /// <summary>Algorithm 2 — computing an encryption key from a (padded) candidate user
    /// password plus the document's security parameters.</summary>
    public static byte[] ComputeEncryptionKey(byte[] paddedPassword, byte[] o, int permissions, byte[] id0, int revision, int keyLengthBytes, bool encryptMetadata)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var buffer = new MemoryStream();
        buffer.Write(paddedPassword, 0, 32);
        buffer.Write(o, 0, Math.Min(o.Length, 32));
        buffer.WriteByte((byte)permissions);
        buffer.WriteByte((byte)(permissions >> 8));
        buffer.WriteByte((byte)(permissions >> 16));
        buffer.WriteByte((byte)(permissions >> 24));
        buffer.Write(id0, 0, id0.Length);
        if (revision >= 4 && !encryptMetadata)
        {
            buffer.Write([0xFF, 0xFF, 0xFF, 0xFF], 0, 4);
        }

        var hash = md5.ComputeHash(buffer.ToArray());
        var n = revision == 2 ? 5 : keyLengthBytes;

        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++) hash = md5.ComputeHash(hash[..n]);
        }

        return hash[..n];
    }

    /// <summary>Algorithm 6 — does this encryption key actually unlock the document? Compares
    /// against the stored <c>/U</c> value rather than trusting whatever key Algorithm 2 handed
    /// back, since a wrong password still produces *a* key, just not the right one.</summary>
    public static bool ValidateUserPassword(byte[] encryptionKey, byte[] u, int revision, byte[] id0)
    {
        var computed = ComputeU(encryptionKey, revision, id0);
        var compareLength = revision == 2 ? 32 : 16;
        if (u.Length < compareLength || computed.Length < compareLength) return false;
        for (var i = 0; i < compareLength; i++)
        {
            if (u[i] != computed[i]) return false;
        }
        return true;
    }

    /// <summary>Algorithm 4 (revision 2) / Algorithm 5 (revision 3-4) — computing <c>/U</c>.</summary>
    public static byte[] ComputeU(byte[] encryptionKey, int revision, byte[] id0)
    {
        if (revision == 2)
        {
            return Rc4(encryptionKey, PaddingBytes);
        }

        using var md5 = System.Security.Cryptography.MD5.Create();
        using var buffer = new MemoryStream();
        buffer.Write(PaddingBytes, 0, 32);
        buffer.Write(id0, 0, id0.Length);
        var hash = md5.ComputeHash(buffer.ToArray());

        var result = Rc4(encryptionKey, hash);
        for (var i = 1; i <= 19; i++)
        {
            result = Rc4(XorKey(encryptionKey, i), result);
        }

        var padded = new byte[32];
        Array.Copy(result, padded, Math.Min(result.Length, 32));
        return padded;
    }

    /// <summary>Algorithm 3 — computing <c>/O</c>. Only used to build encrypted test fixtures;
    /// Sheaf never writes an <c>/Encrypt</c> dictionary of its own.</summary>
    public static byte[] ComputeO(byte[] paddedOwnerPassword, byte[] paddedUserPassword, int revision, int keyLengthBytes)
    {
        var rc4Key = HashPasswordForOwnerKey(paddedOwnerPassword, revision, keyLengthBytes);
        var result = Rc4(rc4Key, paddedUserPassword);

        if (revision >= 3)
        {
            for (var i = 1; i <= 19; i++) result = Rc4(XorKey(rc4Key, i), result);
        }

        return result;
    }

    /// <summary>Algorithm 7 — recovering the (padded) user password from a candidate owner
    /// password, by running Algorithm 3's RC4 rounds against <c>/O</c> in reverse.</summary>
    public static byte[] RecoverUserPasswordPadded(byte[] paddedOwnerPassword, byte[] o, int revision, int keyLengthBytes)
    {
        var rc4Key = HashPasswordForOwnerKey(paddedOwnerPassword, revision, keyLengthBytes);
        var result = new byte[Math.Min(o.Length, 32)];
        Array.Copy(o, result, result.Length);

        if (revision == 2)
        {
            result = Rc4(rc4Key, result);
        }
        else
        {
            for (var i = 19; i >= 0; i--) result = Rc4(XorKey(rc4Key, i), result);
        }

        var padded = new byte[32];
        Array.Copy(result, padded, Math.Min(result.Length, 32));
        return padded;
    }

    private static byte[] HashPasswordForOwnerKey(byte[] paddedOwnerPassword, int revision, int keyLengthBytes)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(paddedOwnerPassword);
        var n = revision == 2 ? 5 : keyLengthBytes;

        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++) hash = md5.ComputeHash(hash[..n]);
        }

        return hash[..n];
    }

    private static byte[] XorKey(byte[] key, int round)
    {
        var result = new byte[key.Length];
        for (var i = 0; i < key.Length; i++) result[i] = (byte)(key[i] ^ round);
        return result;
    }

    /// <summary>Algorithm 1 — deriving the per-object key an individual string or stream is
    /// encrypted with, from the file-wide key plus that object's own number/generation (and,
    /// for AES, the constant "sAlT" salt).</summary>
    public static byte[] ComputeObjectKey(byte[] fileKey, int objectNumber, int generation, bool useAes)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var buffer = new MemoryStream();
        buffer.Write(fileKey, 0, fileKey.Length);
        buffer.WriteByte((byte)objectNumber);
        buffer.WriteByte((byte)(objectNumber >> 8));
        buffer.WriteByte((byte)(objectNumber >> 16));
        buffer.WriteByte((byte)generation);
        buffer.WriteByte((byte)(generation >> 8));
        if (useAes)
        {
            buffer.Write([0x73, 0x41, 0x6C, 0x54], 0, 4); // "sAlT"
        }

        var hash = md5.ComputeHash(buffer.ToArray());
        var n = Math.Min(fileKey.Length + 5, 16);
        return hash[..n];
    }

    public static byte[] Decrypt(byte[] fileKey, int objectNumber, int generation, bool useAes, byte[] data)
    {
        if (data.Length == 0) return data;
        var objectKey = ComputeObjectKey(fileKey, objectNumber, generation, useAes);
        return useAes ? DecryptAes128Cbc(objectKey, data) : Rc4(objectKey, data);
    }

    private static byte[] DecryptAes128Cbc(byte[] key, byte[] data)
    {
        if (data.Length < 16) return [];

        using var aes = System.Security.Cryptography.Aes.Create();
        aes.KeySize = 128;
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = data[..16];

        try
        {
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(data, 16, data.Length - 16);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Malformed padding — a corrupt file, or a key that (rarely) passed the U-value
            // check but is still wrong. Return the raw ciphertext rather than throwing; the
            // result is garbage either way, same as any other malformed-content case in the
            // parser, and a throw here would abort opening the whole document over one stream.
            return data[16..];
        }
    }

    public static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        int a = 0, b = 0;
        for (var k = 0; k < data.Length; k++)
        {
            a = (a + 1) & 0xFF;
            b = (b + s[a]) & 0xFF;
            (s[a], s[b]) = (s[b], s[a]);
            output[k] = (byte)(data[k] ^ s[(s[a] + s[b]) & 0xFF]);
        }

        return output;
    }
}
