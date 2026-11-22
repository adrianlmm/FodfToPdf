// Extrae párrafos largos de las plantillas .fodt reales y genera conformance/corpus.json.
// Uso: node extract-corpus.js [carpetaPlantillas] [salida.json]
const fs = require('fs');
const path = require('path');

const sourceRoot = process.argv[2] || 'C:/soles/webfrontend6/WebFrontEnd/wwwroot/Public/Plantillas';
const output = process.argv[3] || path.join(__dirname, '..', 'conformance', 'corpus.json');

const widths = [150, 180, 220, 260, 300];
const fonts = ['Helvetica', 'Times-Roman', 'Courier'];
const sizes = [10, 11, 12];

function decodeEntities(value) {
  return value
    .replace(/&#x([0-9a-f]+);/gi, (_, hex) => String.fromCodePoint(parseInt(hex, 16)))
    .replace(/&#([0-9]+);/g, (_, dec) => String.fromCodePoint(parseInt(dec, 10)))
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&');
}

function paragraphText(xml) {
  let text = xml
    .replace(/<text:s\b[^>]*\/>/g, ' ')
    .replace(/<text:tab\b[^>]*\/>/g, ' ')
    .replace(/<text:line-break\b[^>]*\/>/g, ' ')
    .replace(/<[^>]+>/g, '');

  text = decodeEntities(text)
    .replace(/\u00a0/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();

  return text;
}

function goodParagraph(text) {
  if (text.length < 80 || text.length > 600) return false;
  if ((text.match(/ /g) || []).length < 12) return false;
  if (/[«»]/.test(text)) return false;
  if (/Table(Start|End)|__|\[\[|\{\{|\$\{/.test(text)) return false;
  if (/^[A-Z0-9 .,:;()\/-]+$/.test(text)) return false;
  // Evita párrafos que son sólo etiquetas o slots.
  if (/(TIPO_DOCUMENTO|DOCTOSAP|RFCEMPRESA|NOMBRE_|DIRECCION_|IMPORTE_|FECHA_)/.test(text)) return false;
  return true;
}

const files = fs.readdirSync(sourceRoot)
  .filter(name => name.toLowerCase().endsWith('.fodt'))
  .sort();

const bySource = new Map();
const seen = new Set();

for (const file of files) {
  const xml = fs.readFileSync(path.join(sourceRoot, file), 'utf8');
  const paragraphs = [...xml.matchAll(/<text:p\b[^>]*>([\s\S]*?)<\/text:p>/g)]
    .map(match => paragraphText(match[1]))
    .filter(goodParagraph);

  const unique = [];
  for (const text of paragraphs) {
    const key = text.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    unique.push(text);
  }

  if (unique.length) bySource.set(file, unique);
}

// Reparte: hasta 2 párrafos por plantilla y luego rellena.
const selected = [];
for (const [source, texts] of bySource) {
  for (const text of texts.slice(0, 2)) {
    selected.push({ source, text });
    if (selected.length >= 100) break;
  }
  if (selected.length >= 100) break;
}

for (const [source, texts] of bySource) {
  if (selected.length >= 100) break;
  for (const text of texts) {
    if (selected.some(item => item.source === source && item.text === text)) continue;
    selected.push({ source, text });
    if (selected.length >= 100) break;
  }
}

const items = selected.slice(0, 100).map((item, index) => {
  const id = `p${String(index + 1).padStart(3, '0')}`;
  const inline = index % 10 === 9; // 10 entradas con formato inline para F4
  let text = item.text;

  if (inline) {
    // Marca en negrita la primera palabra que empiece después del carácter 20.
    const words = item.text.split(' ');
    let position = 0;
    let marked = false;
    for (let i = 0; i < words.length; i++) {
      position += words[i].length + (i > 0 ? 1 : 0);
      if (position >= 20 && /^[A-Za-zÁÉÍÓÚáéíóúÑñ]/.test(words[i])) {
        words[i] = `<b>${words[i]}</b>`;
        marked = true;
        break;
      }
    }
    text = marked ? words.join(' ') : item.text;
  }

  return {
    id,
    source: item.source,
    text,
    width: widths[index % widths.length],
    font: fonts[index % fonts.length],
    size: sizes[index % sizes.length],
    format: inline ? 'inline' : 'plain',
  };
});

fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, JSON.stringify({
  version: 1,
  generated: new Date().toISOString().slice(0, 10),
  sourceRoot,
  items,
}, null, 2));

console.log(`Corpus: ${items.length} párrafos, ${new Set(items.map(i => i.source)).size} plantillas`);
console.log(output);
