// Generates the synthetic samples used for manual acceptance and as test fixtures.
// Everything here is produced byte by byte on purpose: the files must be genuinely valid so a
// browser and the page counter agree on what they are, and no binary is committed by hand.
//
//   node scripts/make-samples.mjs
//
// Output: samples/synthetic/

import { deflateSync } from "node:zlib";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const outputDir = join(here, "..", "samples", "synthetic");

// ---------------------------------------------------------------------------------------------
// PDF
// ---------------------------------------------------------------------------------------------

/**
 * Builds a valid PDF with one text line per page and a correct cross reference table.
 * @param {number} pageCount
 * @param {string} title
 */
function buildPdf(pageCount, title) {
  const objects = [];

  // 1: catalog, 2: page tree, 3: font. Pages and contents follow in pairs.
  const firstPageObject = 4;
  const pageIds = [];
  for (let index = 0; index < pageCount; index++) {
    pageIds.push(firstPageObject + index * 2);
  }

  objects[1] = "<< /Type /Catalog /Pages 2 0 R >>";
  objects[2] =
    `<< /Type /Pages /Count ${pageCount} /Kids [${pageIds.map((id) => `${id} 0 R`).join(" ")}] >>`;
  objects[3] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

  for (let index = 0; index < pageCount; index++) {
    const pageId = pageIds[index];
    const contentId = pageId + 1;
    const text = `${title} - pagina ${index + 1} de ${pageCount}`;
    const stream = `BT /F1 18 Tf 62 760 Td (${text.replace(/([()\\])/g, "\\$1")}) Tj ET\n`;

    objects[pageId] =
      "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
      `/Resources << /Font << /F1 3 0 R >> >> /Contents ${contentId} 0 R >>`;
    objects[contentId] = `<< /Length ${Buffer.byteLength(stream, "latin1")} >>\nstream\n${stream}endstream`;
  }

  const highestId = objects.length - 1;
  const chunks = [];
  let offset = 0;
  const offsets = new Array(highestId + 1).fill(0);

  const push = (text) => {
    const buffer = Buffer.from(text, "latin1");
    chunks.push(buffer);
    offset += buffer.length;
  };

  push("%PDF-1.7\n%âãÏÓ\n");

  for (let id = 1; id <= highestId; id++) {
    if (objects[id] === undefined) continue;
    offsets[id] = offset;
    push(`${id} 0 obj\n${objects[id]}\nendobj\n`);
  }

  const xrefOffset = offset;
  const size = highestId + 1;
  let xref = `xref\n0 ${size}\n0000000000 65535 f \n`;
  for (let id = 1; id <= highestId; id++) {
    xref += `${String(offsets[id]).padStart(10, "0")} 00000 n \n`;
  }
  push(xref);
  push(`trailer\n<< /Size ${size} /Root 1 0 R >>\nstartxref\n${xrefOffset}\n%%EOF\n`);

  return Buffer.concat(chunks);
}

// ---------------------------------------------------------------------------------------------
// PNG
// ---------------------------------------------------------------------------------------------

const crcTable = (() => {
  const table = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    table[n] = c;
  }
  return table;
})();

function crc32(buffer) {
  let c = 0xffffffff;
  for (const byte of buffer) {
    c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
  }
  return (c ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const typeAndData = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(typeAndData));
  return Buffer.concat([length, typeAndData, crc]);
}

/** Builds a valid grayscale PNG with a simple gradient. */
function buildPng(width, height) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 0; // grayscale
  ihdr[10] = 0; // deflate
  ihdr[11] = 0; // adaptive filtering
  ihdr[12] = 0; // no interlace

  const raw = Buffer.alloc((width + 1) * height);
  for (let y = 0; y < height; y++) {
    const rowStart = y * (width + 1);
    raw[rowStart] = 0; // filter type none
    for (let x = 0; x < width; x++) {
      raw[rowStart + 1 + x] = (x * 255) / Math.max(1, width - 1);
    }
  }

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    pngChunk("IHDR", ihdr),
    pngChunk("IDAT", deflateSync(raw)),
    pngChunk("IEND", Buffer.alloc(0)),
  ]);
}

// ---------------------------------------------------------------------------------------------
// TIFF
// ---------------------------------------------------------------------------------------------

/**
 * Builds a little endian, uncompressed grayscale TIFF with one image file directory per page, which
 * is exactly what the page counter walks.
 * @param {number} pageCount
 */
function buildTiff(pageCount, width = 16, height = 16) {
  const pixelsPerPage = width * height;
  const tagCount = 9;
  const ifdSize = 2 + tagCount * 12 + 4;
  const header = Buffer.alloc(8);
  header.write("II", 0, "ascii");
  header.writeUInt16LE(42, 2);
  header.writeUInt32LE(8, 4);

  const parts = [header];
  // Layout: header, then for each page its IFD followed by its pixel data.
  let cursor = 8;
  const pageOffsets = [];
  for (let page = 0; page < pageCount; page++) {
    pageOffsets.push({ ifd: cursor, pixels: cursor + ifdSize });
    cursor += ifdSize + pixelsPerPage;
  }

  for (let page = 0; page < pageCount; page++) {
    const { pixels } = pageOffsets[page];
    const nextIfd = page + 1 < pageCount ? pageOffsets[page + 1].ifd : 0;

    const ifd = Buffer.alloc(ifdSize);
    ifd.writeUInt16LE(tagCount, 0);

    let position = 2;
    const writeTag = (tag, type, count, value) => {
      ifd.writeUInt16LE(tag, position);
      ifd.writeUInt16LE(type, position + 2);
      ifd.writeUInt32LE(count, position + 4);
      if (type === 3 && count === 1) {
        ifd.writeUInt16LE(value, position + 8);
        ifd.writeUInt16LE(0, position + 10);
      } else {
        ifd.writeUInt32LE(value, position + 8);
      }
      position += 12;
    };

    writeTag(0x00fe, 4, 1, 0); // NewSubfileType
    writeTag(0x0100, 4, 1, width); // ImageWidth
    writeTag(0x0101, 4, 1, height); // ImageLength
    writeTag(0x0102, 3, 1, 8); // BitsPerSample
    writeTag(0x0103, 3, 1, 1); // Compression: none
    writeTag(0x0106, 3, 1, 1); // PhotometricInterpretation: black is zero
    writeTag(0x0111, 4, 1, pixels); // StripOffsets
    writeTag(0x0116, 4, 1, height); // RowsPerStrip
    writeTag(0x0117, 4, 1, pixelsPerPage); // StripByteCounts
    ifd.writeUInt32LE(nextIfd, 2 + tagCount * 12);

    const data = Buffer.alloc(pixelsPerPage);
    for (let index = 0; index < pixelsPerPage; index++) {
      data[index] = (index * 7 + page * 40) % 256;
    }

    parts.push(ifd, data);
  }

  return Buffer.concat(parts);
}

// ---------------------------------------------------------------------------------------------

mkdirSync(outputDir, { recursive: true });

const files = {
  "comprovante-1-pagina.pdf": buildPdf(1, "DocReader PoC - comprovante sintetico"),
  "contrato-3-paginas.pdf": buildPdf(3, "DocReader PoC - contrato sintetico"),
  "dossie-60-paginas.pdf": buildPdf(60, "DocReader PoC - dossie sintetico"),
  "documento-digitalizado.png": buildPng(320, 180),
  "documento-frente-verso.tif": buildTiff(2, 64, 64),
  // Deliberately invalid: a Windows executable wearing a .pdf name, used to prove the 415 path.
  "executavel-disfarcado.pdf": Buffer.concat([
    Buffer.from("MZ", "ascii"),
    Buffer.alloc(510, 0x90),
    Buffer.from("This program cannot be run in DOS mode.", "ascii"),
  ]),
  // Deliberately invalid: right signature, broken body, used to prove the 422 path.
  "pdf-corrompido.pdf": Buffer.concat([
    Buffer.from("%PDF-1.7\n", "latin1"),
    Buffer.from("este arquivo tem a assinatura de PDF mas nao tem estrutura valida\n", "latin1"),
  ]),
};

for (const [name, buffer] of Object.entries(files)) {
  const path = join(outputDir, name);
  writeFileSync(path, buffer);
  console.log(`${name.padEnd(32)} ${String(buffer.length).padStart(9)} bytes`);
}

console.log(`\nWrote ${Object.keys(files).length} samples to samples/synthetic/`);
