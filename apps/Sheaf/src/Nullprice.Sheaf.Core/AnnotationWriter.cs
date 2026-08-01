namespace Nullprice.Sheaf.Core;

/// <summary>
/// Turns an <see cref="AnnotationEdit"/> into a real PDF annotation (ISO 32000-1 §12.5) and
/// appends it to a page's <c>/Annots</c> array. Every annotation gets its own <c>/AP /N</c>
/// appearance stream built here rather than left to a viewer's optional default-appearance
/// generation, so a mark looks the same everywhere it's opened — see the M6 section of the
/// plan for why annotations were chosen over content-stream edits at all.
///
/// Every appearance stream's Form XObject uses <c>/BBox</c> equal to the annotation's own
/// <c>/Rect</c> with an identity <c>/Matrix</c>, so drawing operators can use the same
/// page-space coordinates the geometry was computed from directly — no separate
/// local-origin coordinate space to translate into.
/// </summary>
public static class AnnotationWriter
{
    public static void Apply(PdfObjectTable destination, PdfReference pageRef, AnnotationEdit edit)
    {
        if (!destination.TryGet(pageRef.Number, pageRef.Generation, out var pageObj) || pageObj is not PdfDictionary pageDict)
            return;

        // FreeText needs its own path: its Rect and appearance stream depend on subsetting and
        // embedding a font first (see ApplyFreeText), which none of the other edit types do and
        // which needs `destination` to allocate font objects into — the generic dispatch below
        // has no reason to thread that through for every other case.
        if (edit is FreeTextEdit freeText)
        {
            ApplyFreeText(destination, pageRef, pageDict, freeText);
            return;
        }

        var rect = RectOf(edit);
        var (subtypeEntries, apOps, apResources) = BuildSubtypeSpecifics(edit);

        PdfReference? apRef = null;
        if (apOps is not null)
        {
            var apDict = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Form"),
                ["BBox"] = RectArray(rect),
                ["Resources"] = apResources ?? PdfDictionary.Empty,
            });
            var apNum = destination.Allocate();
            destination.Set(apNum, 0, new PdfStream(apDict, ContentStreamWriter.Write(apOps)));
            apRef = new PdfReference(apNum, 0);
        }

        var entries = new Dictionary<string, PdfObject>(subtypeEntries)
        {
            ["Type"] = new PdfName("Annot"),
            ["Rect"] = RectArray(rect),
            ["F"] = new PdfNumber(4), // Print flag — an annotation with this unset is invisible when printed/flattened.
        };
        if (apRef is not null)
            entries["AP"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["N"] = apRef });

        AppendAnnotation(destination, pageRef, pageDict, new PdfDictionary(entries));
    }

    /// <summary>Subsets and embeds <see cref="FreeTextEdit.FontBytes"/> down to exactly the
    /// typed text's glyphs (<see cref="TrueTypeSubsetter"/>), then draws that text with the
    /// embedded CID font in an appearance stream whose <c>/Resources</c> are local to this one
    /// Form XObject — so the font resource name it picks (<c>/F1</c>) can never collide with
    /// names the page's own content already uses for its own fonts.</summary>
    private static void ApplyFreeText(PdfObjectTable destination, PdfReference pageRef, PdfDictionary pageDict, FreeTextEdit edit)
    {
        var parsed = TrueTypeFont.Parse(edit.FontBytes);
        if (parsed.Font is null) return; // the App layer is expected to validate the font choice before ever queuing this edit

        var font = parsed.Font;
        var codepoints = edit.Text.Select(c => (int)c).Distinct().ToList();
        var subset = TrueTypeSubsetter.Subset(font, codepoints, edit.FontFamilyName);
        var fontRef = CidFontBuilder.Embed(destination, subset);

        double widthUnits = 0;
        var cidBytes = new List<byte>();
        foreach (var c in edit.Text)
        {
            var cid = subset.CodepointToCid.GetValueOrDefault(c, 0);
            cidBytes.Add((byte)(cid >> 8));
            cidBytes.Add((byte)cid);
            if (subset.CidToWidthPdfUnits.TryGetValue(cid, out var w)) widthUnits += w;
        }

        var widthPt = widthUnits / 1000.0 * edit.FontSize;
        var ascentPt = subset.Ascent / 1000.0 * edit.FontSize;
        var descentPt = subset.Descent / 1000.0 * edit.FontSize;
        var rect = (edit.X, edit.Y + descentPt, edit.X + widthPt, edit.Y + ascentPt);

        var (r, g, b) = ParseColor(edit.ColorHex);
        var apOps = new List<ContentOp>
        {
            new("q", []),
            new("BT", []),
            new("rg", [Num(r), Num(g), Num(b)]),
            new("Tf", [new PdfName("F1"), Num(edit.FontSize)]),
            new("Tm", [new PdfNumber(1), new PdfNumber(0), new PdfNumber(0), new PdfNumber(1), Num(edit.X), Num(edit.Y)]),
            new("Tj", [new PdfString(cidBytes.ToArray())]),
            new("ET", []),
            new("Q", []),
        };

        var apResources = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Font"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["F1"] = fontRef }),
        });

        var apDict = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Form"),
            ["BBox"] = RectArray(rect),
            ["Resources"] = apResources,
        });
        var apNum = destination.Allocate();
        destination.Set(apNum, 0, new PdfStream(apDict, ContentStreamWriter.Write(apOps)));
        var apRef = new PdfReference(apNum, 0);

        var entries = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Annot"),
            ["Subtype"] = new PdfName("FreeText"),
            ["Rect"] = RectArray(rect),
            ["Contents"] = new PdfString(EncodeTextString(edit.Text)),
            ["DA"] = new PdfString(System.Text.Encoding.ASCII.GetBytes("0 0 0 rg")), // required by spec; superseded by our own /AP
            ["C"] = ColorArray(edit.ColorHex),
            ["F"] = new PdfNumber(4),
            ["AP"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["N"] = apRef }),
        };

        AppendAnnotation(destination, pageRef, pageDict, new PdfDictionary(entries));
    }

    private static void AppendAnnotation(PdfObjectTable destination, PdfReference pageRef, PdfDictionary pageDict, PdfDictionary annotDict)
    {
        var annotNum = destination.Allocate();
        destination.Set(annotNum, 0, annotDict);
        var annotRef = new PdfReference(annotNum, 0);

        var existingAnnots = destination.Resolve(pageDict.Get("Annots")) as PdfArray;
        var items = (existingAnnots?.Items ?? Array.Empty<PdfObject>()).Append(annotRef).ToList();
        destination.Set(pageRef.Number, pageRef.Generation, pageDict.With("Annots", new PdfArray(items)));
    }

    // ---- geometry ---------------------------------------------------------------

    private static (double X0, double Y0, double X1, double Y1) RectOf(AnnotationEdit edit) => edit switch
    {
        HighlightEdit e => Normalize(e.X, e.Y, e.X + e.W, e.Y + e.H),
        UnderlineEdit e => Normalize(e.X, e.Y, e.X + e.W, e.Y + e.H),
        StrikeOutEdit e => Normalize(e.X, e.Y, e.X + e.W, e.Y + e.H),
        StickyNoteEdit e => Normalize(e.X, e.Y, e.X + 24, e.Y + 24),
        LineShapeEdit e => Pad(Normalize(e.X1, e.Y1, e.X2, e.Y2), e.LineWidth + (e.Arrow ? Math.Max(8, e.LineWidth * 4) : 0)),
        RectShapeEdit e => Pad(Normalize(e.X, e.Y, e.X + e.W, e.Y + e.H), e.LineWidth),
        EllipseShapeEdit e => Pad(Normalize(e.X, e.Y, e.X + e.W, e.Y + e.H), e.LineWidth),
        InkEdit e => Pad(BoundsOf(e.Strokes), e.LineWidth),
        _ => throw new NotSupportedException($"Unknown annotation edit type: {edit.GetType().Name}"),
    };

    private static (double, double, double, double) Normalize(double x0, double y0, double x1, double y1) =>
        (Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));

    private static (double, double, double, double) Pad((double X0, double Y0, double X1, double Y1) r, double amount)
    {
        var half = amount / 2 + 1;
        return (r.X0 - half, r.Y0 - half, r.X1 + half, r.Y1 + half);
    }

    private static (double, double, double, double) BoundsOf(IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes)
    {
        var allPoints = strokes.SelectMany(s => s).ToList();
        if (allPoints.Count == 0) return (0, 0, 0, 0);
        return (allPoints.Min(p => p.X), allPoints.Min(p => p.Y), allPoints.Max(p => p.X), allPoints.Max(p => p.Y));
    }

    private static PdfArray RectArray((double X0, double Y0, double X1, double Y1) rect) =>
        new([Num(rect.X0), Num(rect.Y0), Num(rect.X1), Num(rect.Y1)]);

    // ---- per-type dictionary entries + appearance stream ---------------------------------

    private static (Dictionary<string, PdfObject> Entries, List<ContentOp>? ApOps, PdfDictionary? ApResources) BuildSubtypeSpecifics(AnnotationEdit edit) => edit switch
    {
        HighlightEdit e => BuildHighlight(e),
        UnderlineEdit e => (
            new Dictionary<string, PdfObject>
            {
                ["Subtype"] = new PdfName("Underline"),
                ["C"] = ColorArray(e.ColorHex),
                ["QuadPoints"] = QuadPointsFor(e.X, e.Y, e.W, e.H),
            },
            LineOps(e.X, e.Y + e.H * 0.08, e.X + e.W, e.Y + e.H * 0.08, e.ColorHex, Math.Max(1, e.H * 0.06)),
            null),
        StrikeOutEdit e => (
            new Dictionary<string, PdfObject>
            {
                ["Subtype"] = new PdfName("StrikeOut"),
                ["C"] = ColorArray(e.ColorHex),
                ["QuadPoints"] = QuadPointsFor(e.X, e.Y, e.W, e.H),
            },
            LineOps(e.X, e.Y + e.H * 0.5, e.X + e.W, e.Y + e.H * 0.5, e.ColorHex, Math.Max(1, e.H * 0.06)),
            null),
        StickyNoteEdit e => (
            new Dictionary<string, PdfObject>
            {
                ["Subtype"] = new PdfName("Text"),
                ["Contents"] = new PdfString(EncodeTextString(e.Text)),
                ["Name"] = new PdfName("Comment"),
                ["Open"] = new PdfBoolean(false),
            },
            null, // /Subtype /Text is the one annotation type every viewer renders its own standard icon for.
            null),
        LineShapeEdit e => (
            new Dictionary<string, PdfObject>
            {
                ["Subtype"] = new PdfName("Line"),
                ["C"] = ColorArray(e.ColorHex),
                ["L"] = new PdfArray([Num(e.X1), Num(e.Y1), Num(e.X2), Num(e.Y2)]),
                ["BS"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["W"] = Num(e.LineWidth) }),
                ["LE"] = new PdfArray([new PdfName("None"), new PdfName(e.Arrow ? "OpenArrow" : "None")]),
            },
            LineShapeAppearance(e.X1, e.Y1, e.X2, e.Y2, e.ColorHex, e.LineWidth, e.Arrow),
            null),
        RectShapeEdit e => BuildRectLike("Square", e.X, e.Y, e.W, e.H, e.ColorHex, e.LineWidth, e.FillHex, isEllipse: false),
        EllipseShapeEdit e => BuildRectLike("Circle", e.X, e.Y, e.W, e.H, e.ColorHex, e.LineWidth, e.FillHex, isEllipse: true),
        InkEdit e => (
            new Dictionary<string, PdfObject>
            {
                ["Subtype"] = new PdfName("Ink"),
                ["C"] = ColorArray(e.ColorHex),
                ["BS"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["W"] = Num(e.LineWidth) }),
                ["InkList"] = InkListArray(e.Strokes),
            },
            InkAppearance(e.Strokes, e.ColorHex, e.LineWidth),
            null),
        _ => throw new NotSupportedException($"Unknown annotation edit type: {edit.GetType().Name}"),
    };

    private static (Dictionary<string, PdfObject>, List<ContentOp>?, PdfDictionary?) BuildHighlight(HighlightEdit e)
    {
        var entries = new Dictionary<string, PdfObject>
        {
            ["Subtype"] = new PdfName("Highlight"),
            ["C"] = ColorArray(e.ColorHex),
            ["QuadPoints"] = QuadPointsFor(e.X, e.Y, e.W, e.H),
        };

        var (r, g, b) = ParseColor(e.ColorHex);
        var ops = new List<ContentOp>
        {
            new("q", []),
            new("gs", [new PdfName("Alpha")]),
            new("rg", [Num(r), Num(g), Num(b)]),
            new("re", [Num(e.X), Num(e.Y), Num(e.W), Num(e.H)]),
            new("f", []),
            new("Q", []),
        };

        // Constant fill alpha rather than a real /BM /Multiply blend mode: markedly simpler (one
        // ExtGState number, no blend-mode enum) and visually close enough that the text underneath
        // still reads through the tint, which is the whole point of a highlight vs. a redaction box.
        var resources = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["ExtGState"] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Alpha"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["ca"] = new PdfNumber(0.4, IsInteger: false) }),
            }),
        });

        return (entries, ops, resources);
    }

    private static (Dictionary<string, PdfObject>, List<ContentOp>?, PdfDictionary?) BuildRectLike(
        string subtype, double x, double y, double w, double h, string colorHex, double lineWidth, string? fillHex, bool isEllipse)
    {
        var entries = new Dictionary<string, PdfObject>
        {
            ["Subtype"] = new PdfName(subtype),
            ["C"] = ColorArray(colorHex),
            ["BS"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["W"] = Num(lineWidth) }),
        };
        if (fillHex is not null) entries["IC"] = ColorArray(fillHex);

        var ops = isEllipse
            ? EllipseAppearance(x, y, w, h, colorHex, lineWidth, fillHex)
            : RectAppearance(x, y, w, h, colorHex, lineWidth, fillHex);

        return (entries, ops, null);
    }

    // ---- appearance-stream content builders ---------------------------------------------

    private static List<ContentOp> LineOps(double x1, double y1, double x2, double y2, string colorHex, double width)
    {
        var (r, g, b) = ParseColor(colorHex);
        return
        [
            new ContentOp("q", []),
            new ContentOp("RG", [Num(r), Num(g), Num(b)]),
            new ContentOp("w", [Num(width)]),
            new ContentOp("m", [Num(x1), Num(y1)]),
            new ContentOp("l", [Num(x2), Num(y2)]),
            new ContentOp("S", []),
            new ContentOp("Q", []),
        ];
    }

    private static List<ContentOp> LineShapeAppearance(double x1, double y1, double x2, double y2, string colorHex, double width, bool arrow)
    {
        var (r, g, b) = ParseColor(colorHex);
        var ops = new List<ContentOp>
        {
            new("q", []),
            new("RG", [Num(r), Num(g), Num(b)]),
            new("rg", [Num(r), Num(g), Num(b)]),
            new("w", [Num(width)]),
            new("m", [Num(x1), Num(y1)]),
            new("l", [Num(x2), Num(y2)]),
            new("S", []),
        };

        if (arrow)
        {
            var angle = Math.Atan2(y2 - y1, x2 - x1);
            var arrowLength = Math.Max(8, width * 4);
            const double arrowAngle = Math.PI / 7;
            var ax1 = x2 - arrowLength * Math.Cos(angle - arrowAngle);
            var ay1 = y2 - arrowLength * Math.Sin(angle - arrowAngle);
            var ax2 = x2 - arrowLength * Math.Cos(angle + arrowAngle);
            var ay2 = y2 - arrowLength * Math.Sin(angle + arrowAngle);

            ops.Add(new ContentOp("m", [Num(x2), Num(y2)]));
            ops.Add(new ContentOp("l", [Num(ax1), Num(ay1)]));
            ops.Add(new ContentOp("l", [Num(ax2), Num(ay2)]));
            ops.Add(new ContentOp("h", []));
            ops.Add(new ContentOp("f", []));
        }

        ops.Add(new ContentOp("Q", []));
        return ops;
    }

    private static List<ContentOp> RectAppearance(double x, double y, double w, double h, string colorHex, double lineWidth, string? fillHex)
    {
        var (r, g, b) = ParseColor(colorHex);
        var ops = new List<ContentOp> { new("q", []), new("RG", [Num(r), Num(g), Num(b)]), new("w", [Num(lineWidth)]) };

        // Inset by half the stroke width so the border doesn't get clipped by the appearance's own /BBox (== /Rect).
        var inset = lineWidth / 2;
        var re = new ContentOp("re", [Num(x + inset), Num(y + inset), Num(w - lineWidth), Num(h - lineWidth)]);

        if (fillHex is not null)
        {
            var (fr, fg, fb) = ParseColor(fillHex);
            ops.Add(new ContentOp("rg", [Num(fr), Num(fg), Num(fb)]));
            ops.Add(re);
            ops.Add(new ContentOp("B", []));
        }
        else
        {
            ops.Add(re);
            ops.Add(new ContentOp("S", []));
        }

        ops.Add(new ContentOp("Q", []));
        return ops;
    }

    private static List<ContentOp> EllipseAppearance(double x, double y, double w, double h, string colorHex, double lineWidth, string? fillHex)
    {
        // PDF has no native ellipse operator — approximated with four cubic Beziers using the
        // standard "kappa" constant (4/3 * (sqrt(2)-1)) for a close circular/elliptical fit.
        const double kappa = 0.5522847498;
        var (r, g, b) = ParseColor(colorHex);
        var inset = lineWidth / 2;
        var cx = x + w / 2;
        var cy = y + h / 2;
        var rx = w / 2 - inset;
        var ry = h / 2 - inset;
        var ox = rx * kappa;
        var oy = ry * kappa;

        var ops = new List<ContentOp> { new("q", []), new("RG", [Num(r), Num(g), Num(b)]), new("w", [Num(lineWidth)]) };
        if (fillHex is not null)
        {
            var (fr, fg, fb) = ParseColor(fillHex);
            ops.Add(new ContentOp("rg", [Num(fr), Num(fg), Num(fb)]));
        }

        ops.Add(new ContentOp("m", [Num(cx + rx), Num(cy)]));
        ops.Add(new ContentOp("c", [Num(cx + rx), Num(cy + oy), Num(cx + ox), Num(cy + ry), Num(cx), Num(cy + ry)]));
        ops.Add(new ContentOp("c", [Num(cx - ox), Num(cy + ry), Num(cx - rx), Num(cy + oy), Num(cx - rx), Num(cy)]));
        ops.Add(new ContentOp("c", [Num(cx - rx), Num(cy - oy), Num(cx - ox), Num(cy - ry), Num(cx), Num(cy - ry)]));
        ops.Add(new ContentOp("c", [Num(cx + ox), Num(cy - ry), Num(cx + rx), Num(cy - oy), Num(cx + rx), Num(cy)]));
        ops.Add(new ContentOp("h", []));
        ops.Add(new ContentOp(fillHex is not null ? "B" : "S", []));
        ops.Add(new ContentOp("Q", []));
        return ops;
    }

    private static List<ContentOp> InkAppearance(IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes, string colorHex, double lineWidth)
    {
        var (r, g, b) = ParseColor(colorHex);
        var ops = new List<ContentOp>
        {
            new("q", []),
            new("RG", [Num(r), Num(g), Num(b)]),
            new("w", [Num(lineWidth)]),
            new("J", [new PdfNumber(1)]), // round caps
            new("j", [new PdfNumber(1)]), // round joins — smoother freehand strokes
        };

        foreach (var stroke in strokes)
        {
            if (stroke.Count == 0) continue;
            ops.Add(new ContentOp("m", [Num(stroke[0].X), Num(stroke[0].Y)]));
            foreach (var point in stroke.Skip(1))
                ops.Add(new ContentOp("l", [Num(point.X), Num(point.Y)]));
            ops.Add(new ContentOp("S", []));
        }

        ops.Add(new ContentOp("Q", []));
        return ops;
    }

    // ---- shared helpers -----------------------------------------------------------------

    /// <summary>PDF's numeric operand syntax has no separate int/float lexeme — this only
    /// matters here because <see cref="PdfNumber"/>'s own default (<c>IsInteger: true</c>) would
    /// silently truncate a fractional value like 0.4 to "0" if left unset, since the writer
    /// trusts that flag before checking whether the value is actually whole.</summary>
    private static PdfNumber Num(double v) => new(v, v == Math.Floor(v));

    private static (double R, double G, double B) ParseColor(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6) return (0, 0, 0);
        var r = Convert.ToInt32(h[..2], 16) / 255.0;
        var g = Convert.ToInt32(h.Substring(2, 2), 16) / 255.0;
        var b = Convert.ToInt32(h.Substring(4, 2), 16) / 255.0;
        return (r, g, b);
    }

    private static PdfArray ColorArray(string hex)
    {
        var (r, g, b) = ParseColor(hex);
        return new PdfArray([Num(r), Num(g), Num(b)]);
    }

    /// <summary>Quad order per ISO 32000-1 Table 179: top-left, top-right, bottom-left,
    /// bottom-right — not the geometric perimeter order it would be easy to assume instead.</summary>
    private static PdfArray QuadPointsFor(double x, double y, double w, double h)
    {
        var top = y + h;
        var bottom = y;
        var left = x;
        var right = x + w;
        return new PdfArray(
        [
            Num(left), Num(top),
            Num(right), Num(top),
            Num(left), Num(bottom),
            Num(right), Num(bottom),
        ]);
    }

    private static PdfArray InkListArray(IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes) =>
        new(strokes.Select(stroke =>
            (PdfObject)new PdfArray(stroke.SelectMany(p => new PdfObject[] { Num(p.X), Num(p.Y) }).ToList())).ToList());

    /// <summary>PDF "text strings" (as opposed to content-stream strings, which ride whatever
    /// font encoding is active) may be UTF-16BE with a leading BOM for full Unicode — used here
    /// so a sticky note's typed comment survives round-tripping without lossy re-encoding.</summary>
    private static byte[] EncodeTextString(string text)
    {
        var utf16be = System.Text.Encoding.BigEndianUnicode.GetBytes(text);
        return [0xFE, 0xFF, .. utf16be];
    }
}
