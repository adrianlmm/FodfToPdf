# PrawnPure

Portación 100% C# puro de las dependencias de generación PDF de **Prawn (Ruby)** (`pdf-core`, `ttfunk`, `prawn/images`, `prawn/fonts/ttf`, `prawn/security`), sin dependencia de **PDFsharp** ni **SkiaSharp**.

## Proyectos

| Proyecto | Equivalente Ruby | Dependencias externas |
|---|---|---|
| `src/PrawnPure.PdfCore` | `pdf-core`, `prawn/images` (`jpg.rb`, `png.rb`), `prawn/security` | Ninguna (solo .NET BCL) |
| `src/PrawnPure.TTFunk` | `ttfunk` (`file`, `subset/unicode`, `ttf_encoder`, `glyf/compound`), `prawn/fonts/ttf.rb`, `to_unicode_cmap.rb` | Solo `PrawnPure.PdfCore` |
| `src/PrawnPure.Backend` | Implementación directa de `PrawnSharp.IPdfBackend` / `IPdfPage` / `IFontMetrics` | `PrawnPure.PdfCore`, `PrawnPure.TTFunk`, `PrawnSharp.Abstractions`, `PrawnSharp.Fonts` |
| `tests/PrawnPure.Tests` | Pruebas unitarias y de paridad métrica contra `PdfSharpBackend` | xUnit + PdfPig |

## Uso con PrawnSharp

```csharp
using PrawnPure.Backend;
using PrawnSharp;

using var backend = new PurePdfBackend { Compress = true };
using var doc = new PrawnDocument(new DocumentOptions(), backend);
doc.RegisterFontFamily("Arial", @"C:\Windows\Fonts\arial.ttf", boldPath: @"C:\Windows\Fonts\arialbd.ttf");
doc.Font("Arial", size: 11);
doc.Text("Generado con PrawnPure (sin PDFsharp ni SkiaSharp)");
byte[] pdf = doc.RenderBytes();
```

## Capacidades portadas
- Escritor PDF nativo (`%PDF-1.4` / `1.5`, tabla `xref`, trailer, streams `FlateDecode` vía `ZLibStream`).
- Parser TrueType/OpenType (`.ttf` y `.ttc`) + subsetter Unicode con cierre recursivo de glifos compuestos (`glyf`/`loca`/`hmtx`/`hhea`/`maxp`/`head`/`cmap`/`post` + checksums) + fuentes compuestas PDF `Type0`/`CIDFontType2` con `/Identity-H` y `/ToUnicode` CMap.
- Operadores nativos de texto para Base-14 y TTF: `TJ` kerning, `Tc` (`CharacterSpacing`), `Tw` (`WordSpacing`), `Tz` (`HorizontalScaling`), `Ts` (`TextRise`), `Tr` (`RenderMode`) y rotación arbitraria.
- Gráficos vectoriales, `ExtGState` (alfa y blend modes), recorte (`W`/`W*`), matrices de transformación, gradientes nativos PDF Shading (`Type 2` lineal y `Type 3` radial multi-stop).
- Imágenes nativas JPEG (`/DCTDecode`, Gray/RGB/CMYK) y PNG (tipos 0/2/3/4/6, 1/2/4/8/16 bits, desfiltrado completo, Adam7, `PLTE`, `tRNS` y canal alfa `/SMask`).
- Enlaces URI e internos entre páginas, marcadores (`Outlines`), metadatos (`Info`) y cifrado RC4-128 (`V=2, R=3`).
