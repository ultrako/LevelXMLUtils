// End of initSettingsAndEnterSession:
//   if (location.hostname.split(".").reverse().splice(0, 2).reverse().join(".") === "totaljerkface.com")
//     this.enterSession();
// with every string built via JSFuck-style coercion (`([] < [+[]]) + ...`), which webcrack leaves alone.
// On any other host the game silently never opens the main menu (blank canvas, no errors).
// Anchor: an `if` with no else whose consequent calls enterSession and whose test reads `location`.
// `enterSession` is a property name, so it survives minification; local variable names do not.
export default {
  name: 'enter-session-gate',
  description: 'Always enter the session (main menu / level) regardless of hostname',
  find({ ast, code, traverse }) {
    const edits = [];
    traverse(ast, {
      IfStatement(path) {
        const { test, consequent, alternate } = path.node;
        if (alternate) return;
        if (!code.slice(consequent.start, consequent.end).includes('enterSession')) return;
        if (test.type !== 'BinaryExpression' || test.operator !== '===') return;
        if (!/^location\b/.test(code.slice(test.left.start, test.left.end))) return;
        edits.push({ start: test.start, end: test.end, text: 'true' });
      },
    });
    if (edits.length > 1) throw new Error(`expected 1 gate, found ${edits.length}; anchor is ambiguous`);
    return edits;
  },
};
