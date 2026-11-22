# PrawnSharp

Librería .NET para generar PDFs con una API inspirada en [Prawn](https://prawnpdf.org/),
construida sobre **PDFsharp 6.2.4** como escritor de bajo nivel y con la capa de layout
reimplementada en C#.

> **Estado:** F0/F1/F2 + F3/F4 + F5 + F6/F7 + F8/F9 + F10 (empaquetado base).
> Genera PDFs con fuentes base-14 y TTF/OTF, layout de texto, texto formateado,
> gráficos, imágenes, tablas, bookmarks/outlines, enlaces internos, stamps, grid/repeater
> y seguridad PDF estándar. El paquete NuGet compila y se instala desde un feed local; los
> benchmarks, CI y la licencia definitiva del paquete quedan pendientes. `MeasureText` y
> los cortes coinciden con Prawn en los corpus. Ver `../PLAN-prawn-csharp-opcion3.md`.

## Decisión de backend (importante)

PDFsharp se fija en **6.2.4**, la última versión **MIT**. A partir de **6.5.0** el paquete
pasó a **AGPL-3.0-only** y exige una licencia en runtime
(`PdfSharpLicenseException`). No actualizar sin revisar licencia y presupuesto.

El paquete está fijado en `Directory.Packages.props` con `Version="[6.2.4]"`. Hay un test
(`PdfSharpVersionTests`) que falla si la versión resuelta deja de ser 6.2.4.

## Paquete NuGet

El paquete base se genera con todos sus proyectos dependientes:

```bash
dotnet pack PrawnSharp.sln -c Release -o C:\tmp\prawnsharp-pack
```

Se producen:

- `PrawnSharp.1.0.0.nupkg`
- `PrawnSharp.Abstractions.1.0.0.nupkg`
- `PrawnSharp.Fonts.1.0.0.nupkg`
- `PrawnSharp.Backend.PdfSharp.1.0.0.nupkg`

Instalación desde un feed local:

```bash
dotnet add package PrawnSharp --version 1.0.0 --source C:\tmp\prawnsharp-pack
```

La instalación y la generación de un PDF se verificaron con un proyecto de consola
temporal. El paquete incluye `README.md` y `THIRD-PARTY-NOTICES.md`. La expresión de
licencia del paquete queda pendiente de decisión legal.

## Estructura

```
PrawnSharp.sln
├── src/
│   ├── PrawnSharp.Abstractions/      Contratos, geometría, unidades, opciones
│   ├── PrawnSharp.Fonts/             Registro de familias y base-14
│   ├── PrawnSharp.Backend.PdfSharp/  Implementación de IPdfBackend con PDFsharp
│   ├── PrawnSharp.Conformance/       Runner y modelos del corpus
│   └── PrawnSharp/                   API pública (PrawnDocument)
├── tests/PrawnSharp.Tests/           xUnit + PdfPig + conformidad
├── samples/PrawnSharp.Samples.Hello/ Ejemplo
├── conformance/                      corpus.json (100) + ttf-corpus.json (103) + referencias
└── tools/
    ├── PrawnSharp.Conformance.Cli/   CLI de comparación
    ├── PrawnSharp.Reference/         Generación puntual contra Prawn
    ├── extract-corpus.js             Extrae párrafos de las plantillas reales
    └── generate-conformance.rb       Genera las referencias con Prawn
```

## Requisitos

- .NET SDK 10 (el proyecto multi-target está en `net8.0`; el runtime 8 está instalado).
- No requiere Windows: PDFsharp 6.2.4 es cross-platform.

## Compilar y probar

```bash
cd PrawnSharp
dotnet restore
dotnet build
dotnet test
```

## Ejemplo

```csharp
using PrawnSharp;

using var doc = new PrawnDocument(new DocumentOptions
{
    PageSize = PageSizes.A4,
    Margin = new Thickness(32),
});

doc.Font("Helvetica", 12);
doc.Text("Hola mundo");
doc.MoveDown(12);
doc.Text("Generado con PrawnSharp");

doc.RenderFile("hola.pdf");
```

Ejecutar el sample:

```bash
dotnet run --project samples/PrawnSharp.Samples.Hello
```

Fuente TTF/OTF:

```csharp
doc.RegisterFontFamily(
    "DejaVu",
    "DejaVuSans.ttf",
    boldPath: "DejaVuSans-Bold.ttf");

doc.Font("DejaVu", 12);
doc.Text("Hola ñ á € — Привет");
```

Gráficos:

```csharp
var gradient = Gradient.Linear(
    new Point(20, 20),
    new Point(240, 120),
    new GradientStop(0, PdfColor.Red),
    new GradientStop(1, PdfColor.Blue));

doc.FillGradient(Path.Create().Rectangle(20, 20, 220, 100), gradient);

doc.SaveGraphicsState();
doc.Translate(40, 40);
doc.SetClip(Path.Create().Circle(150, 150, 80));
doc.SetAlpha(0.6, 0.6, BlendMode.Multiply);
doc.Fill(Path.Create().Rectangle(0, 0, 300, 300), PdfColor.Green);
doc.RestoreGraphicsState();
```

Imágenes:

```csharp
doc.Image("foto.jpg", new ImageOptions { AtX = 20, AtY = 20, Width = 220, Fit = true });

doc.ImageAtDpi(
    ImageData.FromFile("logo.png"),
    x: 20,
    y: 260,
    dpi: 144);
```

Tablas:

```csharp
var table = new Table(
    TableRow.Header("Concepto", "Importe"),
    new TableRow("Servicio", "$ 1,250.00"),
    new TableRow("IVA", "$ 200.00"))
{
    ColumnWidths = [260, 140],
    HeaderRows = 1,
    DefaultCellStyle = new TableCellStyle
    {
        Padding = new Thickness(5),
        BorderColor = PdfColor.Black,
    },
};

doc.Table(table);
```

Bookmarks y enlaces internos:

```csharp
doc.AddOutline("Inicio", pageIndex: 0);
doc.AddLinkToPage(new Rect(32, 120, 180, 18), pageIndex: 1);

var cm = 1.0.Cm();   // 28.3464 pt
var inch = 1.0.In(); // 72 pt
```

## Diseño actual

- **Coordenadas:** la API usa el modelo de Prawn (origen arriba-izquierda, Y hacia abajo).
  El backend convierte a coordenadas PDF (origen abajo-izquierda) al dibujar.
- **Bounding boxes:** `doc.BoundingBox(...)` y `doc.Canvas(...)` cambian el área y el cursor
  durante un bloque y los restauran al salir (como en Prawn). Las transformaciones
  (`Translate`, `Rotate`, `Scale`, `Skew`) se aplican a paths, clips y gradientes y se
  guardan con `SaveGraphicsState`/`RestoreGraphicsState`.
- **Fuentes base-14:** se crea a mano un diccionario Type1 (`/BaseFont /Helvetica`,
  `/Encoding /WinAnsiEncoding`) y se escribe el texto con operadores `BT/Tf/Tm/Tj/ET`.
  El PDF resultante no incrusta la fuente (2-3 KB).
- **Alineación y corte:** `TextLayout` corta por ancho en espacios, tabuladores, guiones
  y zero-width spaces; parte palabras largas y respeta `\n`. `Align` soporta left, center,
  right y justify (con `Tw`); `IndentParagraphs`, `Width`, `FinalGap`, `Rotate` y `Mode`
  están disponibles. La paginación automática avanza de hoja cuando la línea no cabe.
- **Texto formateado:** `FormattedText` acepta fragmentos con estilo o marcado inline
  (`<b>`, `<i>`, `<u>`, `<strikethrough>`, `<sub>`, `<sup>`, `<font>`, `<color>`, `<link>`,
  `<br>`), con `BaseStyle` y entidades HTML. El layout corta entre estilos, dibuja cada
  run con su fuente/color y crea anotaciones de enlace; subrayado/tachado se dibujan con
  líneas.
- **Caja de texto:** `Width`, `Height`, `Valign` (top/center/bottom), `IndentParagraphs`,
  `FinalGap`, `Rotate` y `Mode` se aplican tanto a `Text` como a `FormattedText`.
- **Métricas AFM:** los 14 `.afm` van incrustados en `PrawnSharp.Fonts`; se parsean una
  vez y se cachean. `MeasureText("Hola mundo", 12)` da **64,692 pt**, igual que Prawn.
  La primera línea dibujada (`Hola` x=32,000 w=24,672; `mundo` x=60,008 w=36,564) coincide
  con Prawn. El kerning AFM se emite como `TJ` y está activo por omisión, como en Prawn
  (`DocumentOptions.DefaultKerning`). La altura de línea sale del AFM (`font.height`):
  Helvetica 12 = **13,872 pt**, y el espaciado entre líneas coincide con Prawn.
- **TTF/OTF:** `doc.RegisterFontFamily("DejaVu", "DejaVuSans.ttf", boldPath: ...)` registra
  la familia; PDFsharp incrusta y subsetea la fuente en Type0/Unicode. Las métricas
  verticales se leen de `head`/`hhea`/`OS/2`/`post` con el mismo criterio de ttfunk, así
  que el ancho y la línea base coinciden con Prawn (DejaVu 12: ancho 72,73 vs 72,636;
  altura 14,388). Unicode y `ToUnicode` se extraen correctamente (acentos, `€`, cirílico).
- **Kerning TTF:** se parsean `cmap`, `hmtx` y la tabla `kern` (formato 0); `WidthOf`
  replica la truncación a milésimas de ttfunk y el dibujo posiciona carácter a carácter.
  En los 103 párrafos de `C_CapacitacionInicialPTractocamion.fodt` con Arial 11 y caja de
  540 pt, los cortes de línea coinciden 103/103 con Prawn. El mismo corpus está versionado
  con DejaVu (`conformance/ttf-corpus.json`).
- **Alineación y corte:** `TextLayout` corta por ancho (espacios, tabs, zero-width),
  soporta left/center/right/justify, `Width`/`Height`/`Valign`, `IndentParagraphs` y
  paginación automática. La altura de línea sale de las métricas de la fuente.
- **Texto:** el cursor avanza por la altura de línea de la fuente (`font.height`), no por
  un `1,2 × tamaño` fijo.
- **Backend desacoplado:** el layout sólo habla con `IPdfBackend`; se puede sustituir por
  iText o por un escritor propio sin tocar la API.

## Conformidad contra Prawn

El corpus de `conformance/` tiene **100 párrafos de 46 plantillas `.fodt` reales**
(90 en texto plano y 10 con `<b>` inline). Cada uno se compara contra un PDF generado
con Prawn Ruby: líneas, palabras y posiciones.

```bash
# Regenerar corpus/referencias (sólo si cambian las plantillas)
node tools/extract-corpus.js
ruby tools/generate-conformance.rb

# Comparar
dotnet run --project tools/PrawnSharp.Conformance.Cli
```

Resultado actual:

```
Corpus: 100  OK: 100  Cortes: 0  Posición: 0  Delta máx: 0.000 pt
```

`dotnet test` ejecuta la misma comparación en `ConformanceTests`. Ver
`conformance/README.md` para el detalle.

## Limitaciones de esta fase

- Fuentes: base-14 y TTF/OTF registradas con `RegisterFontFamily`. `Symbol` y
  `ZapfDingbats` cargan su AFM pero su codificación propia aún no se mide/dibuja.
- TTF/OTF: se aplica kerning de la tabla `kern`, pero no hay shaping/ligaduras, ni
  modos de renderizado/rotación de texto TTF, ni soporte para TTC/dfont. Si se usa
  `<b>`/`<i>` sin registrar la cara correspondiente, se cae a la regular.
- Sin cifrado ni seguridad PDF; seguridad estándar mediante `PdfSecurityOptions`
  (contraseñas y permisos). `Stamp` es un callback reutilizable, no un XObject compartido;
  `Grid` es una cuadrícula básica sin paginación automática.
- F10: benchmarks formales, CI Windows/Linux y la expresión de licencia del paquete
  quedan pendientes; el pack y la instalación local ya están verificados.
- Outline/bookmarks, enlaces internos, stamps, grid/repeater y seguridad están disponibles;
  los enlaces de texto siguen siendo anotaciones externas `http/https`.
- Gráficos: gradientes lineales nativos; los radiales se aproximan con 48 anillos porque
  `XRadialGradientBrush` de PDFsharp 6.2.4 pinta negro. Sin patrones/shadings avanzados;
  la transparencia y los blend modes usan `ExtGState`.
- Imágenes: PNG/JPEG con alfa, paleta e indexado; los PNG de 16 bits se convierten a 8 bits
  con SkiaSharp porque PDFsharp 6.2.4 no los decodifica. JPEG se conserva sin recomprimir.
  Sin WebP, TIFF ni BMP.
- Tablas: `Table` cubre filas, celdas, colspan/rowspan, anchos explícitos o automáticos,
  bordes/fondos/padding, alineación, paginación y repetición de encabezados. Sin tablas
  anidadas; un rowspan que cruce un salto de página no se divide de forma especial.
- Los enlaces son anotaciones de URL (`http/https`); no hay enlaces internos a páginas.
- El corpus versionado incluye base-14 (100 párrafos) y TTF (103 párrafos de la
  plantilla del tractocamión con DejaVu); ambos están en 100/100 y 103/103.

## Próximas fases

| Fase | Contenido | Estado |
|---|---|---|
| F2 | AFM: anchos, kerning, `MeasureText` | **Hecho** (54 pruebas) |
| F3 | Layout de texto: corte de línea, justificación, paginación | **Hecho** (113 pruebas; corpus 100/100) |
| F4 | Texto formateado (runs, `<b>`, `<color>`, `<link>`) | **Hecho** (113 pruebas; corpus 100/100) |
| F5 | TTF/OTF + subsetting + Unicode | **Hecho (núcleo)** (113 pruebas; corpus TTF 103/103) |
| F6 | Gráficos: paths, gradientes, transparencia, clipping | **Hecho (núcleo)** (113 pruebas; muestra visual) |
| F7 | Imágenes PNG/JPEG, alfa/SMask | **Hecho (núcleo)** (113 pruebas; muestra visual) |
| F8 | Tablas (`colspan`, `rowspan`, encabezados repetidos) | **Hecho (núcleo)** (113 pruebas; muestra visual) |
| F9 | Outline, enlaces internos, stamps, seguridad | **Hecho (núcleo: outline y enlaces)** (113 pruebas; muestra F9) |
| F10 | Conformidad, rendimiento, NuGet | **Empaquetado base verificado** (110 pruebas; instalación local) |

## Referencia contra Prawn

El script `tools/PrawnSharp.Reference/generate_reference.rb` usa el checkout local de
Prawn (`C:\projects\ruby\pdf\prawn_sources`) para generar un PDF y el ancho de
`Hola mundo` a 12 pt:

```bash
cd tools/PrawnSharp.Reference
ruby generate_reference.rb prawn_reference.pdf
```

Sirve como oráculo para comparar cortes y posiciones en F2/F3.

## Licencia

El código de este repositorio está pendiente de definir su licencia. **PDFsharp 6.2.4 es
MIT**; **Prawn es GPLv2/GPLv3 o licencia Ruby**, y se usa sólo como referencia de
comportamiento en `tools/PrawnSharp.Reference` (no se distribuye con la librería). Ver
`THIRD-PARTY-NOTICES.md`.
