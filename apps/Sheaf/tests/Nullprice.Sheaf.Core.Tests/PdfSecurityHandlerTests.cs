namespace Nullprice.Sheaf.Core.Tests;

public class PdfSecurityHandlerTests
{
    [Fact]
    public void Rc4_matches_a_published_test_vector()
    {
        // Widely-cited RC4 reference vector: key "Key", plaintext "Plaintext".
        var key = "Key"u8.ToArray();
        var plaintext = "Plaintext"u8.ToArray();
        var expected = Convert.FromHexString("BBF316E8D940AF0AD3");

        Assert.Equal(expected, PdfSecurityHandler.Rc4(key, plaintext));
    }

    [Fact]
    public void Rc4_round_trips()
    {
        var key = "some-key"u8.ToArray();
        var plaintext = "round trip me"u8.ToArray();

        var ciphertext = PdfSecurityHandler.Rc4(key, plaintext);
        var decrypted = PdfSecurityHandler.Rc4(key, ciphertext);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Opens_an_rc4_encrypted_pdf_with_the_correct_user_password()
    {
        var bytes = EncryptedPdfFixture.Build(userPassword: "secret", ownerPassword: "owner", useAes: false);

        var result = PdfDocument.Open(bytes, "secret");

        Assert.Equal(PdfOpenStatus.Success, result.Status);
        Assert.Contains("Hello encrypted", ContentOf(result.Document!));
    }

    [Fact]
    public void Opens_an_aes128_encrypted_pdf_with_the_correct_user_password()
    {
        var bytes = EncryptedPdfFixture.Build(userPassword: "secret", ownerPassword: "owner", useAes: true);

        var result = PdfDocument.Open(bytes, "secret");

        Assert.Equal(PdfOpenStatus.Success, result.Status);
        Assert.Contains("Hello encrypted", ContentOf(result.Document!));
    }

    [Fact]
    public void Wrong_password_is_refused()
    {
        var bytes = EncryptedPdfFixture.Build(userPassword: "secret", ownerPassword: "owner", useAes: false);

        var result = PdfDocument.Open(bytes, "wrong");

        Assert.Equal(PdfOpenStatus.WrongPassword, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public void Owner_password_also_unlocks_a_document_with_a_distinct_user_password()
    {
        var bytes = EncryptedPdfFixture.Build(userPassword: "secret", ownerPassword: "owner", useAes: false);

        var result = PdfDocument.Open(bytes, "owner");

        Assert.Equal(PdfOpenStatus.Success, result.Status);
    }

    [Fact]
    public void Empty_user_password_owner_restricted_document_opens_without_any_password()
    {
        var bytes = EncryptedPdfFixture.Build(userPassword: "", ownerPassword: "owner", useAes: false);

        var result = PdfDocument.Open(bytes, null);

        Assert.Equal(PdfOpenStatus.Success, result.Status);
    }

    [Fact]
    public void Password_removal_writes_plain_unencrypted_output()
    {
        var bytes = EncryptedPdfFixture.Build(userPassword: "secret", ownerPassword: "owner", useAes: false);
        var opened = PdfDocument.Open(bytes, "secret").Document!;

        var rewritten = PdfWriter.Write(opened);
        var reopened = PdfDocument.Open(rewritten); // no password this time

        Assert.Equal(PdfOpenStatus.Success, reopened.Status);
        Assert.Contains("Hello encrypted", ContentOf(reopened.Document!));
    }

    private static string ContentOf(PdfDocument doc)
    {
        var stream = (PdfStream)doc.Objects.Resolve(doc.Pages[0].Dictionary.Get("Contents"));
        return System.Text.Encoding.ASCII.GetString(doc.GetStreamData(stream));
    }
}

/// <summary>Hand-assembles a real RC4- or AES-128-encrypted PDF using
/// <see cref="PdfSecurityHandler"/>'s own Algorithm 3/4 helpers — the same
/// self-consistency bootstrap trick <c>XrefStreamAndObjStmTests</c> uses, needed because
/// <see cref="PdfWriter"/> deliberately never writes an <c>/Encrypt</c> dictionary of its own.</summary>
internal static class EncryptedPdfFixture
{
    public static byte[] Build(string userPassword, string ownerPassword, bool useAes)
    {
        var revision = useAes ? 4 : 2;
        var keyLengthBytes = useAes ? 16 : 5;
        var id0 = "0123456789ABCDEF"u8.ToArray();

        var paddedUser = PdfSecurityHandler.PadPassword(userPassword);
        var paddedOwner = PdfSecurityHandler.PadPassword(ownerPassword);
        var o = PdfSecurityHandler.ComputeO(paddedOwner, paddedUser, revision, keyLengthBytes);
        const int permissions = -4;
        var fileKey = PdfSecurityHandler.ComputeEncryptionKey(paddedUser, o, permissions, id0, revision, keyLengthBytes, encryptMetadata: true);
        var u = PdfSecurityHandler.ComputeU(fileKey, revision, id0);

        var contentPlain = System.Text.Encoding.ASCII.GetBytes("BT /F1 12 Tf 10 10 Td (Hello encrypted) Tj ET");
        var contentKey = PdfSecurityHandler.ComputeObjectKey(fileKey, 4, 0, useAes);
        var contentCipher = useAes ? EncryptAes128Cbc(contentKey, contentPlain) : PdfSecurityHandler.Rc4(contentKey, contentPlain);

        using var ms = new MemoryStream();
        void WriteText(string s)
        {
            var b = System.Text.Encoding.Latin1.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }
        void WriteRaw(byte[] b) => ms.Write(b, 0, b.Length);
        string Hex(byte[] b) => "<" + Convert.ToHexString(b) + ">";

        var offsets = new long[6];
        WriteText("%PDF-1.4\n");

        offsets[1] = ms.Position;
        WriteText("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = ms.Position;
        WriteText("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = ms.Position;
        WriteText("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>\nendobj\n");

        offsets[4] = ms.Position;
        WriteText($"4 0 obj\n<< /Length {contentCipher.Length} >>\nstream\n");
        WriteRaw(contentCipher);
        WriteText("\nendstream\nendobj\n");

        offsets[5] = ms.Position;
        var encryptDictText = useAes
            ? $"<< /Filter /Standard /V 4 /R 4 /Length 128 /CF << /StdCF << /CFM /AESV2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF /O {Hex(o)} /U {Hex(u)} /P {permissions} >>"
            : $"<< /Filter /Standard /V 1 /R {revision} /O {Hex(o)} /U {Hex(u)} /P {permissions} >>";
        WriteText($"5 0 obj\n{encryptDictText}\nendobj\n");

        var xrefOffset = ms.Position;
        WriteText("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++) WriteText($"{offsets[i]:D10} 00000 n \n");
        WriteText($"trailer\n<< /Size 6 /Root 1 0 R /Encrypt 5 0 R /ID [{Hex(id0)} {Hex(id0)}] >>\n");
        WriteText($"startxref\n{xrefOffset}\n%%EOF");

        return ms.ToArray();
    }

    private static byte[] EncryptAes128Cbc(byte[] key, byte[] plaintext)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.KeySize = 128;
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        aes.Key = key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        return [.. aes.IV, .. encrypted];
    }
}
