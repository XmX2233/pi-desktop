// Pi Desktop icon: downscale pi-web's official icon (public/icons/icon-512.png)
// to all Windows icon sizes -> app.ico (PNG-in-ICO) + preview PNGs.
// Usage: node make-icon.js <input-512.png>  (defaults to ../assets/pi-icon-512.png)
"use strict";
const fs = require("fs");
const path = require("path");
const zlib = require("zlib");

const OUT_DIR = path.join(__dirname, "..", "assets");
const IN = process.argv[2] || path.join(OUT_DIR, "pi-icon-512.png");

// ---------- PNG decode (RGBA8 only) ----------
function decodePNG(buf) {
  const width = buf.readUInt32BE(16), height = buf.readUInt32BE(20);
  const colorType = buf[25];
  if (buf[24] !== 8 || colorType !== 6) throw new Error("only 8-bit RGBA supported");
  const idat = [];
  let pos = 8;
  while (pos < buf.length) {
    const len = buf.readUInt32BE(pos), type = buf.toString("ascii", pos + 4, pos + 8);
    if (type === "IDAT") idat.push(buf.slice(pos + 8, pos + 8 + len));
    pos += 12 + len;
  }
  const bpp = 4, stride = width * bpp;
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const out = Buffer.alloc(height * stride);
  let prev = Buffer.alloc(stride), p = 0;
  for (let y = 0; y < height; y++) {
    const ft = raw[p++];
    const line = raw.slice(p, p + stride); p += stride;
    for (let x = 0; x < stride; x++) {
      const a = x >= bpp ? line[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
      let v = line[x];
      switch (ft) {
        case 0: break;
        case 1: v += a; break;
        case 2: v += b; break;
        case 3: v += (a + b) >> 1; break;
        case 4: { const p0 = a + b - c, pa = Math.abs(p0 - a), pb = Math.abs(p0 - b), pc = Math.abs(p0 - c); v += (pa <= pb && pa <= pc) ? a : (pb <= pc) ? b : c; break; }
      }
      out[y * stride + x] = v & 0xff;
    }
    prev = out.slice(y * stride, (y + 1) * stride);
  }
  return { width, height, data: out };
}

// ---------- PNG encode (RGBA8) ----------
const CRC_TABLE = (() => { const t = new Int32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; t[n] = c; } return t; })();
function crc32(b) { let c = 0xffffffff; for (let i = 0; i < b.length; i++) c = CRC_TABLE[(c ^ b[i]) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; }
function chunk(t, d) { const T = Buffer.from(t, "ascii"), L = Buffer.alloc(4); L.writeUInt32BE(d.length); const C = Buffer.alloc(4); C.writeUInt32BE(crc32(Buffer.concat([T, d]))); return Buffer.concat([L, T, d, C]); }
function encodePNG(w, h, rgba) {
  const sig = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6;
  const stride = w * 4, raw = Buffer.alloc((stride + 1) * h);
  for (let y = 0; y < h; y++) { raw[y * (stride + 1)] = 0; rgba.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride); }
  return Buffer.concat([sig, chunk("IHDR", ihdr), chunk("IDAT", zlib.deflateSync(raw, { level: 9 })), chunk("IEND", Buffer.alloc(0))]);
}

// ---------- ICO ----------
function buildIco(pngs) {
  const n = pngs.length, header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(n, 4);
  const entries = []; let offset = 6 + 16 * n;
  for (const { size, buf } of pngs) {
    const e = Buffer.alloc(16);
    e[0] = size >= 256 ? 0 : size; e[1] = size >= 256 ? 0 : size;
    e.writeUInt16LE(1, 4); e.writeUInt16LE(32, 6);
    e.writeUInt32LE(buf.length, 8); e.writeUInt32LE(offset, 12);
    offset += buf.length; entries.push(e);
  }
  return Buffer.concat([header, ...entries, ...pngs.map((p) => p.buf)]);
}

// ---------- area-average downscale ----------
function resize(src, sw, sh, dw, dh) {
  const out = Buffer.alloc(dw * dh * 4);
  for (let dy = 0; dy < dh; dy++) {
    const sy0 = dy * sh / dh, sy1 = (dy + 1) * sh / dh;
    for (let dx = 0; dx < dw; dx++) {
      const sx0 = dx * sw / dw, sx1 = (dx + 1) * sw / dw;
      let r = 0, g = 0, b = 0, a = 0;
      const ix0 = Math.floor(sx0), ix1 = Math.ceil(sx1) - 1;
      const iy0 = Math.floor(sy0), iy1 = Math.ceil(sy1) - 1;
      for (let y = iy0; y <= iy1; y++) for (let x = ix0; x <= ix1; x++) {
        const wgt = (Math.min(x + 1, sx1) - Math.max(x, sx0)) * (Math.min(y + 1, sy1) - Math.max(y, sy0));
        const i = (y * sw + x) * 4;
        r += src[i] * wgt; g += src[i + 1] * wgt; b += src[i + 2] * wgt; a += src[i + 3] * wgt;
      }
      const total = (sx1 - sx0) * (sy1 - sy0), o = (dy * dw + dx) * 4;
      out[o] = Math.round(r / total); out[o + 1] = Math.round(g / total); out[o + 2] = Math.round(b / total); out[o + 3] = Math.round(a / total);
    }
  }
  return out;
}

const src = decodePNG(fs.readFileSync(IN));
const sizes = [16, 24, 32, 48, 64, 128, 256];
const pngs = sizes.map((s) => {
  const data = s === src.width ? src.data : resize(src.data, src.width, src.height, s, s);
  return { size: s, buf: encodePNG(s, s, data), data };
});
fs.mkdirSync(OUT_DIR, { recursive: true });
fs.writeFileSync(path.join(OUT_DIR, "app.ico"), buildIco(pngs));
for (const s of [256, 128, 64, 32]) {
  const p = pngs.find((x) => x.size === s);
  fs.writeFileSync(path.join(OUT_DIR, `icon-${s}.png`), p.buf);
}
console.log("OK -> app.ico (" + sizes.join(",") + ") + previews in " + OUT_DIR);
