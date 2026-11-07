import editorDomainLock from './editor-domain-lock.mjs';
import hostnameHashCheck from './hostname-hash-check.mjs';
import enterSessionGate from './enter-session-gate.mjs';
import sessionHostBomb from './session-host-bomb.mjs';
import editorLimits from './editor-limits.mjs';
import { localLevelEnter, localLevelMeta, localLevelData } from './local-level.mjs';
import { localBrowser } from './local-browser.mjs';
import { probeSession, probeTrigger, probeKeys } from './probe.mjs';

export const patches = [
  editorDomainLock,
  hostnameHashCheck,
  enterSessionGate,
  sessionHostBomb,
  editorLimits,
  localLevelEnter,
  localLevelMeta,
  localLevelData,
  localBrowser,
  probeSession,
  probeTrigger,
  probeKeys,
];
