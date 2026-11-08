// Insert `code` at the top of the function assigned to `<anything>.<method> = function () {...}`.
// Property names (enterSession, load, loadData, ...) are not minified, unlike local variables, so
// they make stable anchors. `mustContain` disambiguates when several classes share a method name.
// `code` must only use `this` and its own uniquely named locals: parameter names are minified.
export function prependToMethod({ name, description, method, mustContain, code }) {
  return {
    name,
    description,
    find({ ast, code: src, traverse }) {
      const edits = [];
      const methods = [].concat(method); // several names when a game update moved the code between methods
      traverse(ast, {
        AssignmentExpression(path) {
          const { left, right } = path.node;
          if (left.type !== 'MemberExpression' || !right || !/^(Function|Arrow)/.test(right.type)) return;
          if (right.body.type !== 'BlockStatement') return;
          const prop = left.computed ? left.property.value : left.property.name;
          if (!methods.includes(prop)) return;
          if (mustContain && !src.slice(right.start, right.end).includes(mustContain)) return;
          const at = right.body.start + 1;
          edits.push({ start: at, end: at, text: `\n${code}\n` });
        },
      });
      if (edits.length > 1) throw new Error(`expected 1 "${methods.join('|')}" method, found ${edits.length}; anchor is ambiguous`);
      return edits;
    },
  };
}

// Shared shape for "find the small function containing a known string literal and make it
// `return true`". Identifier names are minified and change per build, so patches anchor on
// literals (and a second literal/substring inside the same function to confirm it's the right one).
export function stubFunctionReturningTrue({ name, description, literal, alsoContains, maxChars }) {
  return {
    name,
    description,
    find({ ast, code, traverse }) {
      const edits = [];
      traverse(ast, {
        StringLiteral(path) {
          if (path.node.value !== literal) return;
          const fn = path.getFunctionParent();
          if (!fn || fn.node.body.type !== 'BlockStatement') return;
          const src = code.slice(fn.node.start, fn.node.end);
          // Size cap: if the anchor drifted into a huge enclosing function, stubbing it would break the game.
          if (src.length > maxChars || !src.includes(alsoContains)) return;
          edits.push({ start: fn.node.body.start, end: fn.node.body.end, text: '{ return true; }' });
        },
      });
      if (edits.length > 1) throw new Error(`expected 1 match, found ${edits.length}; anchor is ambiguous`);
      return edits;
    },
  };
}
