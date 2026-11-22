# Corpus de conformidad F3/F4/F5

## Corpus base-14

100 párrafos extraídos de **46 plantillas `.fodt` reales** de
`C:\soles\webfrontend6\WebFrontEnd\wwwroot\Public\Plantillas`.

- 90 entradas en texto plano.
- 10 entradas con marcado inline (`<b>`) para cubrir F4.
- Anchos de caja variados (150/180/220/260/300 pt).
- Fuentes base-14: Helvetica, Times-Roman y Courier.
- Tamaños: 10, 11 y 12 pt.

Cada entrada tiene un PDF de referencia generado con **Prawn Ruby** en
`conformance/reference/<id>.pdf`. El comparador genera el PDF con PrawnSharp, extrae
líneas y posiciones con **PdfPig** y compara:

- número de líneas,
- texto de cada línea,
- posición (`Left`/`Bottom`) de cada palabra, tolerancia **0,05 pt**.

## Corpus TTF

`ttf-corpus.json` contiene **103 párrafos de `C_CapacitacionInicialPTractocamion.fodt`**
con **DejaVu Sans 11 pt**, caja de **540 pt** y `fontPath` relativo a la raíz del repo
(`tests/PrawnSharp.Tests/fonts/DejaVuSans.ttf`). Sirve para cubrir `hmtx`, `kern`, `cmap`
y Unicode fuera de WinAnsi.

Referencias en `conformance/reference-ttf/<id>.pdf`, generadas con Prawn y la misma
fuente TTF.

## Resultado actual

```
Corpus base-14: 100  OK: 100  Cortes: 0  Posición: 0  Delta máx: 0.000 pt
Corpus TTF:     103  OK: 103  Cortes: 0  Posición: 0  Delta máx: 0.000 pt
```

Las pruebas `CorpusMatchesPrawnReference` y `TtfCorpusMatchesPrawnReference` ejecutan lo
mismo dentro de `dotnet test`.

## Regenerar

### 1. Corpus desde las plantillas

```bash
cd PrawnSharp
node tools/extract-corpus.js
```

Opcionalmente:

```bash
node tools/extract-corpus.js "C:/ruta/Plantillas" conformance/corpus.json
```

### 2. PDFs de referencia con Prawn

Requiere Ruby 3.x y el checkout de Prawn (`PRAWN_SOURCES`, por omisión
`C:/projects/ruby/pdf/prawn_sources`):

```bash
cd PrawnSharp
ruby tools/generate-conformance.rb
ruby tools/generate-conformance.rb conformance/ttf-corpus.json conformance/reference-ttf
```

### 3. Comparar

```bash
cd PrawnSharp
dotnet run --project tools/PrawnSharp.Conformance.Cli
dotnet run --project tools/PrawnSharp.Conformance.Cli -- conformance/ttf-corpus.json conformance/reference-ttf
```

Salida distinta de cero y lista de fallos si algún párrafo no coincide.

## Estructura

| Ruta | Qué es |
|---|---|
| `conformance/corpus.json` | Párrafos base-14, fuente, ancho, font, tamaño y formato |
| `conformance/ttf-corpus.json` | Párrafos TTF y `fontPath` |
| `conformance/reference/*.pdf` | Referencias base-14 generadas con Prawn |
| `conformance/reference-ttf/*.pdf` | Referencias TTF generadas con Prawn |
| `tools/extract-corpus.js` | Extrae párrafos de las plantillas reales |
| `tools/generate-conformance.rb` | Genera las referencias con Prawn |
| `src/PrawnSharp.Conformance/` | Runner y modelos del corpus |
| `tools/PrawnSharp.Conformance.Cli/` | CLI de comparación |
| `tests/PrawnSharp.Tests/ConformanceTests.cs` | Pruebas de `dotnet test` |

## Notas

- Las referencias se generan con página de 5000 pt de alto para que el texto no pagine;
  el foco es el corte y las posiciones, no la paginación.
- El corpus base-14 cubre WinAnsi; el corpus TTF cubre acentos, `€`, comillas y cirílico.
- Si se cambia cualquiera de los corpus, hay que regenerar sus referencias antes de
  correr las pruebas.
