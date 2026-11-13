// ?gc=<MONO_GC_DEBUG>  ?params=<MONO_GC_PARAMS>  ?rounds=N  ?mode=holder|build|both
import { dotnet } from './_framework/dotnet.js';
Error.stackTraceLimit = 100;
const q = new URLSearchParams(location.search);
const out = document.getElementById('out'); const lines = [];
const log = (s) => { lines.push(s); out.textContent = lines.join('\n'); console.log(s); };
if (q.get('gc')) dotnet.withEnvironmentVariable('MONO_GC_DEBUG', q.get('gc'));
if (q.get('params')) dotnet.withEnvironmentVariable('MONO_GC_PARAMS', q.get('params'));
dotnet.withConfig({ printOutput: true }).withModuleConfig({ print: log, printErr: log });
try {
  const code = await dotnet.withApplicationArguments(q.get('rounds') ?? '200000', q.get('mode') ?? 'both').run();
  log(`EXIT ${code}`);
} catch (e) { log(`TRAP ${e?.stack ?? e}`); }
