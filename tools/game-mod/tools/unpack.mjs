import { spawnSync } from 'node:child_process';
import zlib from 'node:zlib';

const ZSTD_MAGIC = Buffer.from([0x28, 0xb5, 0x2f, 0xfd]);

// The outer packer hides the real script in one huge base64 blob (zstd-compressed UTF-8 JS)
// that it decodes and runs via `new Function`. Find the blob by shape, not by position or
// variable name, since those change between builds.
export function unpack(source) {
  const blob = source.match(/[A-Za-z0-9+/=]{100000,}/);
  if (!blob) return { code: source, packed: false };

  const raw = Buffer.from(blob[0], 'base64');
  if (!raw.subarray(0, 4).equals(ZSTD_MAGIC)) {
    throw new Error(
      'Found a large base64 blob but it is not zstd (starts with ' +
        raw.subarray(0, 4).toString('hex') +
        '). The packer changed; update tools/unpack.mjs.'
    );
  }
  return { code: zstdDecompress(raw).toString('utf8'), packed: true };
}

function zstdDecompress(buf) {
  if (typeof zlib.zstdDecompressSync === 'function') return zlib.zstdDecompressSync(buf);
  const r = spawnSync('zstd', ['-d', '-c'], { input: buf, maxBuffer: 1 << 30 });
  if (r.error || r.status !== 0) {
    throw new Error('zstd decompression failed (need Node >= 22.15 or the zstd CLI): ' + (r.error ?? r.stderr));
  }
  return r.stdout;
}
