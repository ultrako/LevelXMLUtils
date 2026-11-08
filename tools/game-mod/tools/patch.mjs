import { parse } from '@babel/parser';
import _traverse from '@babel/traverse';

const traverse = _traverse.default ?? _traverse;

// A patch is { name, description, find({ ast, code, traverse, meta }) -> Edit[] } where
// Edit = { start, end, text }. Edits are spliced into the original text by offset, so the
// rest of the file stays byte-identical and the diff against the deobfuscated file is tiny.
// A patch whose anchor is missing must throw or return no edits: the build then fails loudly
// so a new game version can't silently ship without the mod.
export function applyPatches(code, patches, meta = {}) {
  const ast = parse(code, { sourceType: 'unambiguous' });
  const edits = [];
  const report = [];

  for (const patch of patches) {
    let found;
    try {
      found = patch.find({ ast, code, traverse, meta });
    } catch (err) {
      throw new Error(`patch "${patch.name}" failed: ${err.message}`);
    }
    if (found.length === 0) {
      throw new Error(`patch "${patch.name}" found nothing to change; the anchor in patches/ needs updating`);
    }
    edits.push(...found);
    report.push({ name: patch.name, edits: found.length });
  }

  edits.sort((a, b) => b.start - a.start || b.end - a.end);
  for (let i = 1; i < edits.length; i++) {
    if (edits[i].end > edits[i - 1].start) throw new Error('two patches overlap; make their anchors disjoint');
  }
  let out = code;
  for (const e of edits) out = out.slice(0, e.start) + e.text + out.slice(e.end);
  return { code: out, report };
}
