#!/usr/bin/env node
// Performance budgets of spec 13 section 22 (FR-UX-PRF-01; slice S1.12a): the shell script (every script index.html
// loads at start) under 300 KB compressed, every other script chunk (a route loaded on demand) under 200 KB compressed.
// Run after `vite build`; fails the build when a budget is exceeded.
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { gzipSync } from 'node:zlib';

const dist = process.argv[2] ?? join(import.meta.dirname, '..', 'dist');
const SHELL_KB = 300;
const ROUTE_KB = 200;
const kb = (file) => gzipSync(readFileSync(join(dist, file)), { level: 9 }).length / 1024;

const html = readFileSync(join(dist, 'index.html'), 'utf8');
const shell = [...html.matchAll(/(?:src|href)="\/(assets\/[^"]+\.js)"/g)].map((m) => m[1]);
const chunks = readdirSync(join(dist, 'assets')).filter((f) => f.endsWith('.js')).map((f) => `assets/${f}`);
const shellKb = shell.reduce((sum, f) => sum + kb(f), 0);
const failures = [];
if (shell.length === 0) failures.push('index.html loads no script (build broken?)');
if (shellKb > SHELL_KB) failures.push(`shell scripts ${shellKb.toFixed(1)} KB compressed, budget ${SHELL_KB} KB`);
for (const chunk of chunks.filter((c) => !shell.includes(c))) {
  const size = kb(chunk);
  if (size > ROUTE_KB) failures.push(`${chunk} ${size.toFixed(1)} KB compressed, budget ${ROUTE_KB} KB`);
}
console.log(`Shell: ${shell.length} script(s), ${shellKb.toFixed(1)} KB compressed (budget ${SHELL_KB} KB); ${chunks.length - shell.length} other chunk(s) (budget ${ROUTE_KB} KB each).`);
if (failures.length > 0) {
  console.error('Performance budget FAILED (spec 13 FR-UX-PRF-01):');
  for (const f of failures) console.error('  - ' + f);
  process.exit(1);
}
console.log('Performance budgets passed.');
