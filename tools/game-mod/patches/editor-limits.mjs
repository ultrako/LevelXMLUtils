// The level editor refuses to test or save a level once its shape count passes maxShapes (900) or its art count passes
// maxArt (10000): every check is `count > Canvas.maxShapes` / `count > Canvas.maxArt` (tooManyShapes, tooMuchArt), set once
// as static properties next to the canvas size (`E.maxShapes = 900; E.maxArt = 10000;`). Making both Infinity turns off the
// "shape limit" / "art limit" warnings and the save and test refusals; the counters still run, and the corner text reads
// "shapes left: Infinity". The game's own load-time limits (physics proxies, the pair table) are unaffected.
export default {
  name: 'editor-limits',
  description: 'Remove the level editor\'s shape limit (900) and art limit (10000)',
  find({ ast, traverse }) {
    const edits = [];
    traverse(ast, {
      AssignmentExpression(path) {
        const { left, right } = path.node;
        if (left.type !== 'MemberExpression' || left.computed || right.type !== 'NumericLiteral') return;
        const name = left.property.name;
        if ((name === 'maxShapes' && right.value === 900) || (name === 'maxArt' && right.value === 10000)) {
          edits.push({ start: right.start, end: right.end, text: 'Infinity', name });
        }
      },
    });
    const names = edits.map((e) => e.name).sort().join(',');
    if (names !== 'maxArt,maxShapes') throw new Error(`expected one maxShapes = 900 and one maxArt = 10000, found [${names}]`);
    return edits.map(({ name, ...edit }) => edit);
  },
};
