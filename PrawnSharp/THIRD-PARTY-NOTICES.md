# Avisos de terceros

## PDFsharp 6.2.4

- **Licencia:** MIT.
- **Uso:** escritor PDF de bajo nivel (`IPdfBackend`).
- **Fijado en:** `Directory.Packages.props` (`Version="[6.2.4]"`).
- **Advertencia:** PDFsharp **6.5.0 y posteriores** son **AGPL-3.0-only** y validan una
  licencia en runtime (`PdfSharpLicenseException`). No actualizar sin revisión legal y
  presupuesto.
- **Dependencias transitivas:** `System.Security.Cryptography.Pkcs` 8.0.1 tiene avisos
  NU1902/NU1903. Se silencian en `Directory.Build.props` para el esqueleto; revisar antes
  de publicar.

## PdfPig 0.1.16

- **Licencia:** Apache-2.0.
- **Uso:** sólo tests (`tests/PrawnSharp.Tests`), para extraer texto y posiciones de los
  PDFs generados y compararlos.

## xUnit / Microsoft.NET.Test.Sdk / coverlet

- **Licencia:** Apache-2.0 / MIT / MIT.
- **Uso:** sólo tests.

## Prawn (referencia, no distribuido)

- **Licencia:** GPLv2, GPLv3 o licencia Ruby (ver el checkout local).
- **Uso:** `tools/PrawnSharp.Reference` ejecuta el Prawn local para generar PDFs de
  referencia y anchos de texto. **No** se copia código de Prawn en `src/`; si el producto
  se distribuye de forma propietaria, la reimplementación debe ser clean-room.

## Métricas AFM (Adobe Core 14)

- **Archivos:** `src/PrawnSharp.Fonts/Afm/*.afm` (14 fuentes base-14).
- **Origen:** métricas AFM de Adobe, distribuidas con Prawn y con la mayoría de los
  motores PDF (incluyen el aviso `Copyright (c) 1985... Adobe Systems Incorporated`).
- **Uso:** sólo datos de anchos/kerning; no son código de Prawn.
- **Acción pendiente:** revisar los términos de redistribución de los AFM antes de
  publicar el paquete NuGet. Si no se pueden redistribuir, sustituirlos por una tabla
  generada desde una fuente con licencia compatible.

## SkiaSharp 3.119.0

- **Licencia:** MIT.
- **Uso:** fallback de decodificación para PNG de 16 bits que PDFsharp 6.2.4 no soporta;
  se convierten a PNG de 8 bits antes de incrustarlos.
- **Dependencia:** `PrawnSharp.Backend.PdfSharp`.

## Imágenes de prueba

- `tests/PrawnSharp.Tests/images/*`: archivos de los datos de Prawn (PNG/JPEG); **sólo
  para pruebas**, revisar su licencia antes de redistribuirlos.
- No se incluyen en el paquete NuGet de la librería.

## Fuentes de prueba TTF/OTF

- `tests/PrawnSharp.Tests/fonts/DejaVuSans.ttf`: DejaVu (licencia Bitstream Vera/DejaVu).
- `tests/PrawnSharp.Tests/fonts/Bodoni-Book.otf`: fuente de los datos de Prawn; **sólo
  para pruebas**, revisar su licencia antes de redistribuirla.
- No se incluyen en el paquete NuGet de la librería.


