namespace Nullprice.Sheaf.Core.Tests;

/// <summary>Exercises the two hardest parts of the parser together, since real PDF 1.5+
/// writers always pair them: a cross-reference stream (rather than a classic xref table) and
/// compressed object streams. <see cref="PdfWriter"/> only ever emits classic xref, so this
/// fixture is assembled by hand — with every offset computed from actual buffer positions,
/// never counted manually — to get real coverage of the parsing side of both features.</summary>
public class XrefStreamAndObjStmTests
{
    [Fact]
    public void Opens_a_document_whose_objects_live_in_a_compressed_object_stream()
    {
        var bytes = BuildFixture();

        var result = PdfDocument.Open(bytes);

        Assert.Equal(PdfOpenStatus.Success, result.Status);
        var doc = result.Document!;
        Assert.Single(doc.Pages);
        Assert.Equal(150, PdfTestFixtures.MediaBoxWidth(doc, doc.Pages[0]));
    }

    private static byte[] BuildFixture()
    {
        // Objects 1 (Catalog), 2 (Pages), 3 (Page) live compressed inside object 4's ObjStm.
        // Object 5 is the xref stream itself — self-referential, exactly as real writers do it.
        string[] texts =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 150 200] /Resources << >> >>",
        ];
        int[] nums = [1, 2, 3];

        var relativeOffsets = new List<int>();
        var body = new System.Text.StringBuilder();
        foreach (var text in texts)
        {
            relativeOffsets.Add(body.Length);
            body.Append(text).Append('\n');
        }
        var header = string.Join(' ', nums.Select((n, i) => $"{n} {relativeOffsets[i]}")) + "\n";
        var objStmContent = System.Text.Encoding.ASCII.GetBytes(header + body);
        var objStmCompressed = FilterCodec.Encode(objStmContent);

        using var ms = new MemoryStream();
        void WriteText(string s)
        {
            var b = System.Text.Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        WriteText("%PDF-1.7\n");

        var objStmOffset = ms.Position;
        WriteText($"4 0 obj\n<< /Type /ObjStm /N {texts.Length} /First {header.Length} /Filter /FlateDecode /Length {objStmCompressed.Length} >>\nstream\n");
        ms.Write(objStmCompressed, 0, objStmCompressed.Length);
        WriteText("\nendstream\nendobj\n");

        // Cross-reference stream entries (ISO 32000-1 §7.5.8): W = [1 2 1], one byte type +
        // two-byte field 2 (big-endian) + one byte field 3, six entries for objects 0..5.
        var xrefData = new byte[6 * 4];
        void WriteEntry(int objNum, byte type, int f2, int f3)
        {
            xrefData[objNum * 4] = type;
            xrefData[objNum * 4 + 1] = (byte)(f2 >> 8);
            xrefData[objNum * 4 + 2] = (byte)f2;
            xrefData[objNum * 4 + 3] = (byte)f3;
        }
        WriteEntry(0, 0, 0, 0);                     // free-list head
        WriteEntry(1, 2, 4, 0);                      // compressed, in ObjStm 4, index 0
        WriteEntry(2, 2, 4, 1);                      // compressed, in ObjStm 4, index 1
        WriteEntry(3, 2, 4, 2);                      // compressed, in ObjStm 4, index 2
        WriteEntry(4, 1, (int)objStmOffset, 0);       // the ObjStm itself, direct offset

        var xrefStreamOffset = ms.Position;
        WriteEntry(5, 1, (int)xrefStreamOffset, 0);   // the xref stream, pointing at itself

        var xrefCompressed = FilterCodec.Encode(xrefData);
        WriteText($"5 0 obj\n<< /Type /XRef /Size 6 /W [1 2 1] /Index [0 6] /Root 1 0 R /Filter /FlateDecode /Length {xrefCompressed.Length} >>\nstream\n");
        ms.Write(xrefCompressed, 0, xrefCompressed.Length);
        WriteText("\nendstream\nendobj\n");

        WriteText($"startxref\n{xrefStreamOffset}\n%%EOF");

        return ms.ToArray();
    }
}
