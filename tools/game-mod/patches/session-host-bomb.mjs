// Anti-tamper: in the session's init() the game decodes location.hostname (the names are built from
// number.toString(base) pieces), and if its last two labels aren't "totaljerkface.com" it starts a memory bomb:
// 15 staggered setTimeouts start setIntervals that keep doubling a string (`bP += bP + "_*-!"`) every 35-50 ms.
// On localhost that leaks about 40 MB/s of JS heap (the process passed 8 GB in about a minute) even with a tiny
// level, and is what made big levels freeze the browser. Unlike hostname-hash-check (which wipes `window`) and
// enter-session-gate (which never opens the menu), this one is silent, so it is easy to mistake for a leak in a
// level. Anchor on the string literal the bomb builds, not on any identifier, and make the `if` never run.
export default {
  name: 'session-host-bomb',
  description: 'Never run the memory bomb that init() starts when the hostname is not totaljerkface.com',
  find({ ast, code, traverse }) {
    const edits = [];
    traverse(ast, {
      IfStatement(path) {
        const { test, consequent, alternate } = path.node;
        if (alternate || test.type !== 'BinaryExpression' || test.operator !== '!==') return;
        const body = code.slice(consequent.start, consequent.end);
        if (!body.includes('"_*-!"') || !body.includes('setInterval') || body.length > 1500) return;
        edits.push({ start: test.start, end: test.end, text: 'false' });
      },
    });
    if (edits.length > 1) throw new Error(`expected 1 bomb, found ${edits.length}; anchor is ambiguous`);
    return edits;
  },
};
