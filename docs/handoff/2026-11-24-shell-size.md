# 2026-11-24 Web shell size fix (defect fix, no slice)
Sonnet 5.5, medium effort. Register R-291; R-267, R-272 and R-274 closed by it. No feature, test or budget changed (the budget stays 300 KB).

## What was wrong and what changed
The shell (the scripts `index.html` loads: `index`, `context`, runtime) was 299.8 of 300 KB compressed. Measured with a source map: the biggest parts were React DOM, React Aria, the router and the **two** core catalogues. Both languages were bundled: Urdu web texts (22.7 KB) and Urdu platform server texts (14.7 KB) were in the shell although an English session never reads them.
Now only English is in the shell. `@erp/i18n` `loadLocale('ur')` fetches three lazy chunks (web texts, platform server texts, module server texts). `I18nRoot` draws nothing until the start-up language is in memory (no flash of keys); `setDisplay` (language switch) awaits the load first, so the open screen keeps its state. Tests that read Urdu texts directly call `await loadLocale('ur')` (only `i18n.test.ts` needed it).

| Size, compressed | Before | After |
|---|---|---|
| Shell total | 299.8 KB | **263.5 KB** (36.5 KB headroom) |
| `context` chunk | 216.4 | 179.1 |
| `index` chunk | 93.0 | 93.0 |
| New lazy chunks | – | `ur` 22.7, `server.ur` 14.7 (`modules.ur` 18.9 was already lazy) |

## Rule for later screen authors
1. A screen's own texts go in its lazy catalogue (`messages/<screen>.{en,ur}.json`, `registerMessages`, R-213). Never in `en.json`/`ur.json`.
2. A **menu text** is a core text (the menu draws before any screen loads). Add the English text to `en.json` and the Urdu text to `ur.json`: the Urdu file is no longer in the shell, so a menu text now costs about 0.1 KB, not 0.2. Keep the shell under **285 KB** by choice; `pnpm build` prints the number.
3. Do not import a screen's component or its message file from `navigation`, `modules/<module>.ts`, `shell` or `app`; use the lazy declarations already there. A dynamic import of something the shell also imports statically splits a shared chunk and grows the shell (S1.16b).
4. A new shared dependency: check `pnpm build` before and after; if it adds more than 3 KB to the shell, stop and report.

## Open
R-291: an Urdu user fetches one extra request (about 56 KB compressed, then cached) before the first screen. Proposed addition to the CLAUDE.md front-end rules (coordinator decides): "Only English texts ship in the shell; Urdu and every screen's texts are lazy catalogues; the shell stays under 285 KB by convention."
