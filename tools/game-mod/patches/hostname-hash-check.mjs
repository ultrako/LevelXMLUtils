import { stubFunctionReturningTrue } from './util.mjs';

// Anti-tamper: on game init the script hashes location.hostname (md5) and, if it isn't one of
// totaljerkface.com / www. / beta., runs a script that deletes every key on `window`. That leaves
// the game stuck at "Loading... 100%" with errors like "setTimeout is not defined" and
// "Cannot read properties of undefined (reading 'setItem')".
// The names are hidden behind md5s: d5189de0... = md5("location"), 0897acf4... = md5("hostname").
// Anchor on those instead of any identifier.
export default stubFunctionReturningTrue({
  name: 'hostname-hash-check',
  description: 'Make the hashed hostname allow-list check pass so the game does not wipe window',
  literal: 'd5189de027922f81005951e6efe0efd5',
  alsoContains: '0897acf49c7c1ea9f76efe59187aa046',
  maxChars: 1500,
});
