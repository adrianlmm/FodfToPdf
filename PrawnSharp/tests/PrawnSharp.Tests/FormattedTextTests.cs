using System.Linq;
using PrawnSharp;
using Xunit;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Pig = UglyToad.PdfPig;

namespace PrawnSharp.Tests;

public sealed class FormattedTextTests
{
    [Fact]
    public void ParsesBold()
    {
        var text = FormattedText.Parse("Hola <b>mundo</b>");

        Assert.Equal(2, text.Fragments.Count);
        Assert.Equal("Hola ", text.Fragments[0].Text);
        Assert.Null(text.Fragments[0].Style?.FontStyle);
        Assert.Equal("mundo", text.Fragments[1].Text);
        Assert.Equal(FontStyle.Bold, text.Fragments[1].Style?.FontStyle);
    }

    [Fact]
    public void ParsesNestedStyles()
    {
        var text = FormattedText.Parse("<b>bold <i>both</i></b>");

        Assert.Equal(2, text.Fragments.Count);
        Assert.Equal("bold ", text.Fragments[0].Text);
        Assert.Equal(FontStyle.Bold, text.Fragments[0].Style?.FontStyle);
        Assert.Equal("both", text.Fragments[1].Text);
        Assert.Equal(FontStyle.BoldItalic, text.Fragments[1].Style?.FontStyle);
    }

    [Fact]
    public void ParsesColorFontAndScript()
    {
        var text = FormattedText.Parse(
            "<color rgb=\"#FF0000\"><font size=\"18\" character_spacing=\"2\">rojo</font></color> " +
            "<sub>2</sub><sup>3</sup>");

        var red = text.Fragments.First(f => f.Text == "rojo").Style;
        var sub = text.Fragments.First(f => f.Text == "2").Style;
        var sup = text.Fragments.First(f => f.Text == "3").Style;

        Assert.Equal(PdfColor.Red, red?.Color);
        Assert.Equal(18, red?.Size);
        Assert.Equal(2, red?.CharacterSpacing);
        Assert.Equal(ScriptPosition.Subscript, sub?.Script);
        Assert.Equal(ScriptPosition.Superscript, sup?.Script);
    }

    [Fact]
    public void ParsesLinkAndEntities()
    {
        var text = FormattedText.Parse(
            "<link href=\"https://example.com\">a &lt; b &amp; c</link>");

        Assert.Single(text.Fragments);
        Assert.Equal("a < b & c", text.Fragments[0].Text);
        Assert.Equal("https://example.com", text.Fragments[0].Style?.Link);
    }

    [Fact]
    public void ParsesCmykColor()
    {
        var text = FormattedText.Parse("<color c=\"0\" m=\"100\" y=\"100\" k=\"0\">rojo</color>");

        Assert.Equal(new PdfColor(255, 0, 0), text.Fragments[0].Style?.Color);
    }

    [Fact]
    public void KeepsUnknownTagsAsText()
    {
        var text = FormattedText.Parse("a <foo> b");

        Assert.Equal("a <foo> b", string.Concat(text.Fragments.Select(f => f.Text)));
    }

    [Fact]
    public void BaseStyleAppliesToParsedFragments()
    {
        var baseStyle = new TextStyle { Color = PdfColor.Red, Size = 18 };
        var text = FormattedText.Parse("hola", baseStyle);

        Assert.Single(text.Fragments);
        Assert.Equal(PdfColor.Red, text.Fragments[0].Style?.Color);
        Assert.Equal(18, text.Fragments[0].Style?.Size);
    }

    [Fact]
    public void BrStartsNewLine()
    {
        using var doc = new PrawnDocument();
        doc.Font("Helvetica", 12);
        doc.FormattedText("uno<br>dos");

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        var lines = pdf.GetPage(1).GetWords()
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
            .OrderByDescending(g => g.Key)
            .Select(g => string.Join(' ', g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
            .ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal("uno", lines[0]);
        Assert.Equal("dos", lines[1]);
    }

    [Fact]
    public void FontTagChangesThePdfResource()
    {
        using var doc = new PrawnDocument();
        doc.Font("Helvetica", 12);
        doc.FormattedText("<font name=\"Times-Roman\">Times</font>");

        var bytes = doc.RenderBytes();

        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        var fonts = pdf.Pages[0].Resources.Elements.GetDictionary("/Font");

        Assert.NotNull(fonts);

        var baseFonts = fonts.Elements.Keys
            .Select(key =>
            {
                var item = fonts.Elements[key];
                var dictionary = item as PdfDictionary;

                if (dictionary is null && item is PdfReference reference)
                {
                    dictionary = reference.Value as PdfDictionary;
                }

                return dictionary?.Elements["/BaseFont"]?.ToString() ?? string.Empty;
            })
            .ToList();

        Assert.Contains(baseFonts, name => name.Contains("Times-Roman", StringComparison.Ordinal));
    }

    [Fact]
    public void RendersBoldWithTwoFontResources()
    {
        using var doc = new PrawnDocument();
        doc.Font("Helvetica", 12);
        doc.FormattedText("normal <b>bold</b>");

        var bytes = doc.RenderBytes();

        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        var page = pdf.Pages[0];
        var fonts = page.Resources.Elements.GetDictionary("/Font");

        Assert.NotNull(fonts);
        Assert.True(fonts.Elements.Keys.Count >= 2);
    }

    [Fact]
    public void WrapsAcrossStyles()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = PageSizes.A4,
            Margin = new Thickness(32),
        });

        doc.Font("Helvetica", 12);
        doc.FormattedText("Hello <b>world</b>", new TextOptions { Width = 35 });

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        var lines = pdf.GetPage(1).GetWords()
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
            .OrderByDescending(g => g.Key)
            .Select(g => string.Join(' ', g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
            .ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal("Hello", lines[0]);
        Assert.Equal("world", lines[1]);
    }

    [Fact]
    public void AddsLinkAnnotation()
    {
        using var doc = new PrawnDocument();
        doc.Font("Helvetica", 12);
        doc.FormattedText("<link href=\"https://example.com\">enlace</link>");

        var bytes = doc.RenderBytes();

        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        var page = pdf.Pages[0];
        var annots = page.Elements.GetArray("/Annots");

        Assert.NotNull(annots);
        Assert.True(annots.Elements.Count >= 1);

        var first = annots.Elements[0];
        var dictionary = first as PdfDictionary ?? ((PdfReference)first).Value as PdfDictionary;

        Assert.NotNull(dictionary);

        var action = dictionary.Elements.GetDictionary("/A");

        Assert.NotNull(action);
        Assert.Equal("https://example.com", action.Elements.GetString("/URI"));
    }

    [Fact]
    public void HeightOfFormattedTextAccountsForMixedSizes()
    {
        using var doc = new PrawnDocument();
        doc.Font("Helvetica", 12);

        var normal = doc.HeightOf("hola");
        var big = doc.HeightOf(FormattedText.Parse("<font size=\"24\">hola</font>"));

        Assert.True(big > normal);
    }

    [Fact]
    public void RendersUnderlineSubAndSup()
    {
        using var doc = new PrawnDocument();
        doc.Font("Helvetica", 12);

        doc.FormattedText("a<u>b</u><sub>2</sub><sup>3</sup><strikethrough>c</strikethrough>");

        var bytes = doc.RenderBytes();

        Assert.True(bytes.Length > 200);
    }
}
