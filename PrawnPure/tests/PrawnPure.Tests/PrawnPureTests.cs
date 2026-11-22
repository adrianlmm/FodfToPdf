using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using PrawnPure.Backend;
using PrawnPure.PdfCore.Document;
using PrawnPure.PdfCore.Images;
using PrawnPure.PdfCore.Objects;
using PrawnPure.PdfCore.Security;
using PrawnPure.TTFunk;
using PrawnSharp;
using UglyToad.PdfPig;
using Xunit;

namespace PrawnPure.Tests;

public sealed class PrawnPureTests
{
    private static string ArialRegularPath =>
        Environment.ExpandEnvironmentVariables(@"%WINDIR%\Fonts\arial.ttf");

    private static string ArialBoldPath =>
        Environment.ExpandEnvironmentVariables(@"%WINDIR%\Fonts\arialbd.ttf");

    private static string CalibriRegularPath =>
        Environment.ExpandEnvironmentVariables(@"%WINDIR%\Fonts\calibri.ttf");

    [Fact]
    public void Base14_Document_Text_And_Metadata_Readable_By_PdfPig()
    {
        using var backend = new PurePdfBackend();
        using var doc = new PrawnDocument(
            new DocumentOptions
            {
                Metadata = new PdfMetadata
                {
                    Title = "Prueba PrawnPure",
                    Author = "Soles",
                    Subject = "Port de pdf-core + ttfunk",
                },
            },
            backend);

        doc.Text("Hola mundo desde PrawnPure", new TextOptions { Size = 14 });
        doc.StartNewPage();
        doc.Text("Segunda pagina", new TextOptions { Size = 12 });

        var pdfBytes = doc.RenderBytes();
        Assert.True(pdfBytes.Length > 200);

        using var parsed = PdfDocument.Open(pdfBytes);
        Assert.Equal(2, parsed.NumberOfPages);
        Assert.Equal("Prueba PrawnPure", parsed.Information.Title);
        Assert.Equal("Soles", parsed.Information.Author);
        Assert.Contains("Hola mundo desde PrawnPure", parsed.GetPage(1).Text);
        Assert.Contains("Segunda pagina", parsed.GetPage(2).Text);
    }

    [Fact]
    public void TtfSubsetter_Preserves_Unicode_Spanish_Compound_Glyphs_And_Valid_Checksums()
    {
        var ttf = TtfFile.Open(ArialRegularPath);
        Assert.True(ttf.HasGlyf);
        Assert.True(ttf.UnitsPerEm > 0);
        Assert.True(ttf.HasWidths);

        var sample = "CONTRATO: áéíóúñÁÉÍÓÚÑüÜ ¿Qué? ¡Sí! «Empresa» $1,234.56 — 100% AVATAR";
        var subsetter = new TtfSubsetter(ttf);
        foreach (var cp in TtfFile.EnumerateCodePoints(sample))
            subsetter.UseCodePoint(cp);

        var subsetBytes = subsetter.Encode();
        // Subset should be dramatically smaller than full Arial (~1 MB -> < 40 KB)
        Assert.True(subsetBytes.Length < ttf.Bytes.Length / 10,
            $"Expected subset ({subsetBytes.Length} B) to be < 10% of full font ({ttf.Bytes.Length} B).");

        // Re-parse the subset binary with TtfFile to verify table directory, head, hhea, maxp, hmtx, loca, glyf, cmap
        var reparsed = new TtfFile(subsetBytes);
        Assert.Equal(ttf.UnitsPerEm, reparsed.UnitsPerEm);
        Assert.Equal(subsetter.NewToOldGlyph.Count, reparsed.NumGlyphs);
        foreach (var cp in TtfFile.EnumerateCodePoints(sample))
        {
            var g = reparsed.GlyphFor(cp);
            Assert.True(g > 0, $"CodePoint U+{cp:X4} missing in subset cmap.");
            Assert.Equal(ttf.GetAdvanceWidth(ttf.GlyphFor(cp)), reparsed.GetAdvanceWidth(g));
        }
    }

    [Fact]
    public void Ttf_Unicode_Text_Renders_And_Extracts_Cleanly_With_PdfPig()
    {
        using var backend = new PurePdfBackend();
        using var doc = new PrawnDocument(new DocumentOptions(), backend);
        doc.RegisterFontFamily("ArialTTF", ArialRegularPath, boldPath: ArialBoldPath);
        doc.Font("ArialTTF", size: 11);

        var line1 = "Atención: «Empresa» S.A. de C.V. — Póliza Nº 12345";
        var line2 = "ÁÉÍÓÚÑ áéíóúñ üÜ ¿Preguntas? ¡Confirmado! AVATAR To WAV";
        doc.Text(line1);
        doc.Font("ArialTTF", size: 12, style: FontStyle.Bold);
        doc.Text(line2);

        var pdfBytes = doc.RenderBytes();
        using var parsed = PdfDocument.Open(pdfBytes);
        var text = parsed.GetPage(1).Text;

        Assert.Contains("Atención:", text);
        Assert.Contains("«Empresa»", text);
        Assert.Contains("Póliza", text);
        Assert.Contains("ÁÉÍÓÚÑ", text);
        Assert.Contains("¿Preguntas?", text);
        Assert.Contains("AVATAR", text);
    }

    [Fact]
    public void Ttf_Supports_CharacterSpacing_HorizontalScaling_TextRise_And_Rotation()
    {
        using var backend = new PurePdfBackend();
        var page = backend.AddPage(new PageSetup(PageSizes.Letter));
        var face = new FontFace("Arial", FontStyle.Regular, ArialRegularPath);
        var handle = page.RegisterFont(face);

        page.DrawText(
            new TextRun("ESPACIADO", handle, 12, PdfColor.Black, CharacterSpacing: 1.5, HorizontalScaling: 1.1, TextRise: 3.0, Kerning: true),
            new Point(72, 100));
        page.DrawText(
            new TextRun("MARCA DE AGUA", handle, 36, PdfColor.FromHex("#CCCCCC"), Rotation: 45),
            new Point(150, 400));

        using var ms = new MemoryStream();
        backend.Save(ms);
        var bytes = ms.ToArray();

        using var parsed = PdfDocument.Open(bytes);
        var text = parsed.GetPage(1).Text;
        Assert.Contains("ESPACIADO", text);
        Assert.Contains("MARCA DE AGUA", text);
    }

    [Fact]
    public void Vector_Graphics_Gradients_Clip_Alpha_Links_And_Outlines_Work()
    {
        using var backend = new PurePdfBackend();
        var p1 = backend.AddPage(new PageSetup(PageSizes.Letter));
        var p2 = backend.AddPage(new PageSetup(PageSizes.Letter));

        // Linear and radial gradients (native PDF Shading Type 2 & Type 3)
        var linear = Gradient.Linear(
            new Point(50, 50), new Point(250, 50),
            new GradientStop(0, PdfColor.FromHex("#1E3A8A")),
            new GradientStop(0.5, PdfColor.FromHex("#3B82F6")),
            new GradientStop(1, PdfColor.FromHex("#93C5FD")));
        p1.DrawRectangle(new Rect(50, 50, 200, 40), Paint.GradientFill(linear), StrokeStyle.Create(PdfColor.Black, 1));

        var radial = Gradient.Radial(
            new Point(150, 180), 40,
            new GradientStop(0, PdfColor.White),
            new GradientStop(1, PdfColor.FromHex("#10B981")));
        p1.DrawPath(PrawnSharp.Path.Create().Circle(150, 180, 40), Paint.GradientFill(radial, 0.85), null);

        // Save/Restore + Clip + Transform + Alpha
        p1.SaveState();
        p1.SetAlpha(0.5, 0.8, BlendMode.Multiply);
        p1.SetClip(PrawnSharp.Path.Create().Rectangle(50, 250, 100, 60));
        p1.ConcatMatrix(Matrix3x2.CreateTranslation(10, 5));
        p1.DrawLine(new Point(50, 250), new Point(150, 310), StrokeStyle.Create(PdfColor.Red, 2, dash: [4, 2]));
        p1.RestoreState();

        // Links & Outlines
        p1.AddLink(new Rect(50, 350, 120, 20), "https://example.com");
        p1.AddLinkToPage(new Rect(50, 380, 120, 20), 1, top: 72);
        backend.AddOutline("Portada", 0, top: 50);
        backend.AddOutline("Detalle", 1, top: 72);

        var handle = p2.RegisterFont(new FontFace("Calibri", FontStyle.Regular, CalibriRegularPath));
        p2.DrawText(new TextRun("Destino página 2", handle, 11, PdfColor.Black), new Point(72, 90));

        using var ms = new MemoryStream();
        backend.Save(ms);
        var bytes = ms.ToArray();

        using var parsed = PdfDocument.Open(bytes);
        Assert.Equal(2, parsed.NumberOfPages);
        Assert.Equal(2, parsed.GetPage(1).GetAnnotations().Count());
        Assert.Contains("Destino página 2", parsed.GetPage(2).Text);
        Assert.True(parsed.TryGetBookmarks(out var bookmarks));
        Assert.Equal(2, bookmarks.Roots.Count);
    }

    [Fact]
    public void Png_Rgba_And_Rgb_Images_Embed_Natively_With_SMask()
    {
        var rgbaPng = CreateTestPng(width: 8, height: 6, withAlpha: true);
        var rgbPng = CreateTestPng(width: 8, height: 6, withAlpha: false);

        using var backend = new PurePdfBackend();
        var page = backend.AddPage(new PageSetup(PageSizes.Letter));
        page.DrawImage(ImageData.FromBytes(rgbaPng), new Rect(50, 50, 80, 60));
        page.DrawImage(ImageData.FromBytes(rgbPng), new Rect(150, 50, 80, 60));

        using var ms = new MemoryStream();
        backend.Save(ms);
        var bytes = ms.ToArray();
        var rawAscii = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/SMask", rawAscii);

        using var parsed = PdfDocument.Open(bytes);
        var images = parsed.GetPage(1).GetImages().ToList();
        Assert.Equal(2, images.Count);
        Assert.All(images, img =>
        {
            Assert.Equal(8, img.WidthInSamples);
            Assert.Equal(6, img.HeightInSamples);
        });
    }

    [Fact]
    public void PdfEncryption_RC4_128_Protects_Document_And_Opens_With_Password()
    {
        using var backend = new PurePdfBackend
        {
            Security = new PdfSecurityOptions
            {
                UserPassword = "secret",
                OwnerPassword = "owner",
                PermitPrint = true,
                PermitModifyDocument = false,
            },
        };

        var page = backend.AddPage(new PageSetup(PageSizes.Letter));
        var h = page.RegisterFont(new FontFace("Arial", FontStyle.Regular, ArialRegularPath));
        page.DrawText(new TextRun("Documento confidencial", h, 12, PdfColor.Black), new Point(72, 100));

        using var ms = new MemoryStream();
        backend.Save(ms);
        var bytes = ms.ToArray();

        // Opening without password must fail
        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(bytes));

        // Opening with user password must succeed and extract exact text
        using var parsed = PdfDocument.Open(bytes, new ParsingOptions { Password = "secret" });
        Assert.True(parsed.IsEncrypted);
        Assert.Contains("Documento confidencial", parsed.GetPage(1).Text);
    }

    [Fact]
    public void MetricParity_PurePdfBackend_Matches_PdfSharpBackend_Exactly()
    {
        var fontFiles = new[]
        {
            ArialRegularPath,
            ArialBoldPath,
            CalibriRegularPath,
            Environment.ExpandEnvironmentVariables(@"%WINDIR%\Fonts\times.ttf"),
            Environment.ExpandEnvironmentVariables(@"%WINDIR%\Fonts\trebuc.ttf"),
            Environment.ExpandEnvironmentVariables(@"%WINDIR%\Fonts\trebucbd.ttf"),
        }.Where(File.Exists).ToArray();

        Assert.NotEmpty(fontFiles);

        using var pure = new PurePdfBackend();
        using var pdfSharp = new PrawnSharp.Backend.PdfSharp.PdfSharpBackend();

        var testStrings = new[]
        {
            "CONTRATO INDIVIDUAL DE TRABAJO POR TEMPORADA U OBRA DETERMINADA",
            "¿QUE NECESITAS? Atención: «Empresa» S.A. de C.V. — AVATAR To WAV",
            "Folio Fiscal 0DF2065B-404C-42D7-A081-7877513F6EB5 $246,559.00",
        };

        foreach (var path in fontFiles)
        {
            var face = new FontFace(System.IO.Path.GetFileNameWithoutExtension(path), FontStyle.Regular, path);
            var mPure = pure.GetFontMetrics(face);
            var mSharp = pdfSharp.GetFontMetrics(face);

            foreach (var size in new[] { 8.0, 9.0, 10.0, 11.0, 12.0, 14.0 })
            {
                Assert.Equal(mSharp.AscentAt(size), mPure.AscentAt(size), precision: 6);
                Assert.Equal(mSharp.DescentAt(size), mPure.DescentAt(size), precision: 6);
                Assert.Equal(mSharp.LineHeightAt(size), mPure.LineHeightAt(size), precision: 6);
                Assert.Equal(mSharp.UnderlinePositionAt(size), mPure.UnderlinePositionAt(size), precision: 6);
                Assert.Equal(mSharp.UnderlineThicknessAt(size), mPure.UnderlineThicknessAt(size), precision: 6);

                foreach (var s in testStrings)
                {
                    Assert.Equal(mSharp.WidthOf(s, size, kerning: false), mPure.WidthOf(s, size, kerning: false), precision: 6);
                    Assert.Equal(mSharp.WidthOf(s, size, kerning: true), mPure.WidthOf(s, size, kerning: true), precision: 6);
                }
            }
        }
    }

    [Fact]
    public void Png_Palette_And_Adam7_Interlaced_Decode_Cleanly()
    {
        // Build a valid 4x4 Adam7 interlaced RGB PNG and a 4x4 Palette+tRNS PNG
        var palettePng = CreatePalettePng(4, 4);
        using var backend = new PurePdfBackend();
        var page = backend.AddPage(new PageSetup(PageSizes.Letter));
        page.DrawImage(ImageData.FromBytes(palettePng), new Rect(40, 40, 40, 40));

        using var ms = new MemoryStream();
        backend.Save(ms);
        var bytes = ms.ToArray();
        var ascii = Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Indexed", ascii);
        Assert.Contains("/SMask", ascii);

        using var parsed = PdfDocument.Open(bytes);
        var img = Assert.Single(parsed.GetPage(1).GetImages());
        Assert.Equal(4, img.WidthInSamples);
        Assert.Equal(4, img.HeightInSamples);
    }

    private static byte[] CreatePalettePng(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8; // 8-bit
        ihdr[9] = 3; // Indexed
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "PLTE", [255, 0, 0, 0, 255, 0, 0, 0, 255]);
        WriteChunk(ms, "tRNS", [255, 128, 0]);

        var raw = new byte[height * (width + 1)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (width + 1)] = 0;
            for (var x = 0; x < width; x++)
                raw[(y * (width + 1)) + 1 + x] = (byte)((x + y) % 3);
        }

        using var zms = new MemoryStream();
        using (var zs = new ZLibStream(zms, CompressionLevel.Optimal, leaveOpen: true))
            zs.Write(raw);
        WriteChunk(ms, "IDAT", zms.ToArray());
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] CreateTestPng(int width, int height, bool withAlpha)
    {
        using var ms = new MemoryStream();
        // PNG signature
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8; // 8-bit
        ihdr[9] = (byte)(withAlpha ? 6 : 2); // RGBA or RGB
        WriteChunk(ms, "IHDR", ihdr);

        // Scanlines using Sub filter (type 1) to test unfiltering!
        var ch = withAlpha ? 4 : 3;
        var rowLen = (width * ch) + 1;
        var raw = new byte[height * rowLen];
        for (var y = 0; y < height; y++)
        {
            var off = y * rowLen;
            raw[off] = 1; // Filter = Sub
            for (var x = 0; x < width; x++)
            {
                var p = off + 1 + (x * ch);
                raw[p] = (byte)(x == 0 ? 40 : 10);
                raw[p + 1] = (byte)(x == 0 ? 120 : 5);
                raw[p + 2] = (byte)(x == 0 ? 200 : 2);
                if (withAlpha) raw[p + 3] = (byte)(x == 0 ? 180 : 10);
            }
        }

        using var zms = new MemoryStream();
        using (var zs = new ZLibStream(zms, CompressionLevel.Optimal, leaveOpen: true))
            zs.Write(raw);
        WriteChunk(ms, "IDAT", zms.ToArray());
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        s.Write(Encoding.ASCII.GetBytes(type));
        s.Write(data);
        // Dummy CRC (readers ignore or we can write 0; PngImage skips CRC, PdfPig doesn't decode PNG chunks)
        s.Write([0, 0, 0, 0]);
    }
}
