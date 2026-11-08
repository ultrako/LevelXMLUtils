import { stubFunctionReturningTrue } from './util.mjs';

// The level editor only creates its canvas when location.hostname ends in totaljerkface.com,
// so it is broken on any other host. The real check is ~250 chars.
export default stubFunctionReturningTrue({
  name: 'editor-domain-lock',
  description: 'Make the hostname check (used by the level editor) always return true',
  literal: 'totaljerkface.com',
  alsoContains: 'hostname',
  maxChars: 600,
});
