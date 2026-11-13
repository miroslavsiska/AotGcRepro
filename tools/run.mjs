// node tools/run.mjs [rounds] [variants...]  - serves AotGcRepro/bin/Release/net10.0/publish/wwwroot and loads it in headed Chrome
// once per variant; prints the page's REPRO/EXIT/TRAP lines. Variants: default, oracle, clear, big (nursery 128m).
// Env: CHROME (browser executable), ROOT (wwwroot to serve, relative to the solution folder).
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join, resolve, dirname } from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL, fileURLToPath } from 'node:url';
const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..', process.env.ROOT ?? 'AotGcRepro/bin/Release/net10.0/publish/wwwroot');
const rounds = process.argv[2] ?? '20000';
const variants = process.argv.slice(3).length ? process.argv.slice(3) : ['default', 'big', 'clear', 'oracle'];
const gc = { default: '', oracle: 'gc=collect-before-allocs=1,clear-nursery-at-gc', clear: 'gc=clear-nursery-at-gc', big: 'params=nursery-size=128m' };
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.wasm': 'application/wasm', '.json': 'application/json', '.dat': 'application/octet-stream' };
const server = createServer(async (req, res) => {
  const path = join(root, decodeURIComponent(new URL(req.url, 'http://x').pathname).replace(/\/$/, '/index.html'));
  try {
    const body = await readFile(path);
    res.writeHead(200, { 'Content-Type': types[extname(path)] ?? 'application/octet-stream', 'Cache-Control': 'no-store',
      'Cross-Origin-Opener-Policy': 'same-origin', 'Cross-Origin-Embedder-Policy': 'require-corp' });
    res.end(body);
  } catch { res.writeHead(404); res.end(); }
}).listen(0);
const port = server.address().port;
const req = createRequire(join(process.env.PUPPETEER_FROM ?? here, 'package.json'));
const puppeteer = (await import(pathToFileURL(req.resolve('puppeteer-core')).href)).default;
const b = await puppeteer.launch({ executablePath: process.env.CHROME ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe', headless: false });
for (const v of variants) {
  const p = await b.newPage();
  const seen = [];
  const done = new Promise((r) => p.on('console', (m) => { const t = m.text(); if (/^(REPRO|EXIT|TRAP)/.test(t)) { seen.push(t); if (/^(EXIT|TRAP)/.test(t)) r(); } }));
  p.on('pageerror', (e) => seen.push('PAGEERROR ' + e.message));
  await p.goto(`http://localhost:${port}/?rounds=${rounds}&${gc[v] ?? v}`);
  await Promise.race([done, new Promise((r) => setTimeout(r, 300000))]);
  console.log(`=== ${v} (${rounds} rounds)`); console.log((seen.length ? seen : ['TIMEOUT']).map((s) => '  ' + s.slice(0, 1500)).join('\n'));
  await p.close();
}
await b.close(); server.close();
