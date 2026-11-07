// The level browser lists a local folder's levels (the play page's Browse, or serve.mjs --level-dir) in place of the
// site's. Its two list requests (the first set of a list, and later sets when you page past the first 500) both end in
// `<browser>.loader.load(<request>)`, inside the functions that show "loading levels...". Each call becomes
// `window.__modBrowse ? window.__modBrowse(<browser>, <request>) : <browser>.loader.load(<request>)`, and
// __modBrowse (patches/html.mjs) answers the request itself or passes it on. The names come from the AST, so nothing
// here depends on minified names.
export const localBrowser = {
  name: 'local-browser',
  description: 'level browser: let window.__modBrowse answer the level list requests',
  find({ ast, code, traverse }) {
    const edits = [];
    traverse(ast, {
      CallExpression(path) {
        const { callee, arguments: args } = path.node;
        if (callee.type !== 'MemberExpression' || callee.computed || callee.property.name !== 'load' || args.length !== 1) return;
        const target = callee.object;
        if (target.type !== 'MemberExpression' || target.computed || target.property.name !== 'loader') return;
        const fn = path.getFunctionParent();
        if (!fn) return;
        const src = code.slice(fn.node.start, fn.node.end);
        if (src.length > 5000 || !src.includes('"loading levels..."') || !src.includes('"get_all"')) return;
        const owner = code.slice(target.object.start, target.object.end);
        const request = code.slice(args[0].start, args[0].end);
        edits.push({
          start: path.node.start,
          end: path.node.end,
          text: `(window.__modBrowse ? window.__modBrowse(${owner}, ${request}) : ${owner}.loader.load(${request}))`,
        });
      },
    });
    if (edits.length !== 2) throw new Error(`expected the level browser's 2 list requests, found ${edits.length}`);
    return edits;
  },
};
