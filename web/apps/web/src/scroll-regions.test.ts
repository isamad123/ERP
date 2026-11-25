// R-318: a table that scrolls sideways is always wrapped in the design system's ScrollRegion (a named, keyboard-reachable region), never in a
// plain div with a "scroll" class: such a wrapper cannot be reached by keyboard when no cell inside takes focus (WCAG 2.1.1; axe rule
// scrollable-region-focusable), and the failure shows only when the data is wide enough, so the browser tests find it by chance. This test
// finds every screen file, so a new screen cannot bring the old pattern back.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return sourceFiles(path);
    return /\.tsx$/.test(name) && !/\.test\.tsx$/.test(name) ? [path] : [];
  });
}

describe('sideways scrolling regions', () => {
  const files = sourceFiles(join(__dirname, 'modules'));

  it('are the ScrollRegion component, never a div with a scroll class', () => {
    // The leave team calendar is an ARIA grid with its own roving focus (every cell can take focus, and a region around it would add a second tab stop).
    const GRID = join('modules', 'hrm', 'leave-team.tsx');
    const offenders = files.filter((f) => !f.endsWith(GRID) && /<div\b[^>]*className="[^"]*scroll[^"]*"/.test(readFileSync(f, 'utf8'))).map((f) => f.slice(__dirname.length + 1));
    expect(offenders).toEqual([]);
  });

  it('are named by the screen and found in the screens that have wide tables', () => {
    const using = files.filter((f) => readFileSync(f, 'utf8').includes('<ScrollRegion'));
    expect(using.length).toBeGreaterThanOrEqual(20);
    for (const f of using) {
      for (const tag of readFileSync(f, 'utf8').match(/<ScrollRegion\b[^>]*>/g) ?? []) {
        expect(tag, f).toMatch(/\blabel=\{/);
      }
    }
  });
});
