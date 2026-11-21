#!/usr/bin/env node
// Collects the server's text catalogues at build time (slice S1.12a, Build item 4): every
// src/**/Localization/<name>.<lang>.json is merged into src/generated/server.<lang>.json, so problem answers
// (RFC 9457, the key in `title`) show in the person's language. A key defined twice with different texts fails.
import { readdirSync, readFileSync, writeFileSync, mkdirSync, statSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..', '..', '..', '..');
const out = join(here, '..', 'src', 'generated');
const LANGUAGES = ['en', 'ur'];

function walk(dir, found = []) {
  for (const name of readdirSync(dir)) {
    if (name === 'bin' || name === 'obj' || name === 'node_modules') continue;
    const path = join(dir, name);
    if (statSync(path).isDirectory()) walk(path, found);
    else if (dir.endsWith('Localization') && /\.(en|ur)\.json$/.test(name)) found.push(path);
  }
  return found;
}

const files = walk(join(repo, 'src'));
mkdirSync(out, { recursive: true });
let failed = false;
for (const lang of LANGUAGES) {
  const merged = {};
  for (const file of files.filter((f) => f.endsWith(`.${lang}.json`)).sort()) {
    for (const [key, text] of Object.entries(JSON.parse(readFileSync(file, 'utf8')))) {
      if (typeof text !== 'string') continue;
      if (key in merged && merged[key] !== text) {
        console.error(`server text ${key} (${lang}) differs between catalogues (${file})`);
        failed = true;
      }
      merged[key] = text;
    }
  }
  const sorted = Object.fromEntries(Object.entries(merged).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)));
  writeFileSync(join(out, `server.${lang}.json`), JSON.stringify(sorted, null, 1) + '\n');
  console.log(`server.${lang}.json: ${Object.keys(sorted).length} texts from ${files.filter((f) => f.endsWith(`.${lang}.json`)).length} catalogues`);
}
process.exit(failed ? 1 : 0);
