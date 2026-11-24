#!/usr/bin/env node
// Collects the server's text catalogues at build time (slice S1.12a, Build item 4): every
// src/**/Localization/<name>.<lang>.json is merged into src/generated/server.<lang>.json (platform) and modules.<lang>.json
// (business modules, S1.20c; platform catalogues marked "_load": "after_start", S1.23b), so problem answers
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
// The platform's texts ship with the shell (sign-in, approvals, every kernel answer); the business modules' texts (src/Modules,
// HR from S1.20c on) go to modules.<lang>.json, loaded as their own chunk right after start-up, so module growth never weighs on the
// shell's script budget (spec 13 FR-UX-PRF-01; R-213 for screen texts, S1.20c for server texts).
// A platform catalogue whose texts are not needed before the first screen (S1.23b: the pack manager) says so with
// "_load": "after_start" and joins the module chunk.
const isModule = (file) => file.includes(`${join('src', 'Modules')}`) || JSON.parse(readFileSync(file, 'utf8'))._load === 'after_start';
for (const lang of LANGUAGES) {
  const merged = {};
  const owner = {};
  for (const file of files.filter((f) => f.endsWith(`.${lang}.json`)).sort()) {
    for (const [key, text] of Object.entries(JSON.parse(readFileSync(file, 'utf8')))) {
      if (typeof text !== 'string' || key.startsWith('_')) continue;
      if (key in merged && merged[key] !== text) {
        console.error(`server text ${key} (${lang}) differs between catalogues (${file})`);
        failed = true;
      }
      merged[key] = text;
      owner[key] = owner[key] === false ? false : isModule(file);
    }
  }
  const sort = (keys) => Object.fromEntries(keys.sort().map((k) => [k, merged[k]]));
  const all = Object.keys(merged);
  writeFileSync(join(out, `server.${lang}.json`), JSON.stringify(sort(all.filter((k) => !owner[k])), null, 1) + '\n');
  writeFileSync(join(out, `modules.${lang}.json`), JSON.stringify(sort(all.filter((k) => owner[k])), null, 1) + '\n');
  console.log(`server.${lang}.json: ${all.length} texts from ${files.filter((f) => f.endsWith(`.${lang}.json`)).length} catalogues (${all.filter((k) => owner[k]).length} of modules, loaded after start-up)`);
}
process.exit(failed ? 1 : 0);
