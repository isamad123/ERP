// The shape of the position chart on one date (slice S1.22b Build item 8; FR-HRM-POS-04): children by parent, the rows an open tree shows,
// the boxes a search finds and the boxes that must be opened to show them. Pure functions, no screen.
import type { ChartNode } from '../../api/positions';

export interface ChartTree {
  byId: Map<string, ChartNode>;
  children: Map<string, ChartNode[]>;
  /** Boxes without a line above them on this date, or whose line leaves the filtered chart. */
  roots: ChartNode[];
}

const byCode = (a: ChartNode, b: ChartNode) => a.code.localeCompare(b.code, 'en', { numeric: true });

export function buildTree(nodes: ChartNode[]): ChartTree {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const children = new Map<string, ChartNode[]>();
  const roots: ChartNode[] = [];
  for (const node of nodes) {
    if (node.parentId && node.parentId !== node.id && byId.has(node.parentId)) {
      const list = children.get(node.parentId) ?? [];
      list.push(node);
      children.set(node.parentId, list);
    } else {
      roots.push(node);
    }
  }
  for (const list of children.values()) list.sort(byCode);
  roots.sort(byCode);
  return { byId, children, roots };
}

export interface ChartRow { node: ChartNode; level: number; hasChildren: boolean; expanded: boolean; posInSet: number; setSize: number }

/** The rows of the open part of the tree in reading order. A box on a reporting cycle (bad data) is never walked twice. */
export function visibleRows(tree: ChartTree, open: ReadonlySet<string>, limit: number): { rows: ChartRow[]; total: number } {
  const rows: ChartRow[] = [];
  const seen = new Set<string>();
  let total = 0;
  const walk = (list: ChartNode[], level: number) => {
    list.forEach((node, index) => {
      if (seen.has(node.id)) return;
      seen.add(node.id);
      const kids = tree.children.get(node.id) ?? [];
      const expanded = kids.length > 0 && open.has(node.id);
      total += 1;
      if (rows.length < limit) rows.push({ node, level, hasChildren: kids.length > 0, expanded, posInSet: index + 1, setSize: list.length });
      if (expanded) walk(kids, level + 1);
    });
  };
  walk(tree.roots, 1);
  return { rows, total };
}

const fold = (text: string) => text.toLocaleLowerCase('en');

/** The boxes whose title, code, unit, or holders' names and numbers contain the text (case-insensitive). Empty text finds nothing. */
export function searchMatches(nodes: ChartNode[], text: string): Set<string> {
  const needle = fold(text.trim());
  const found = new Set<string>();
  if (needle === '') return found;
  for (const n of nodes) {
    const hay = [n.title, n.code, n.unitCode ?? '', ...n.holders.flatMap((h) => [h.name ?? '', h.nameLocal ?? '', h.employeeNo])].map(fold);
    if (hay.some((h) => h.includes(needle))) found.add(n.id);
  }
  return found;
}

/** The boxes above a box, nearest first (what to open to show it). */
export function ancestorIds(tree: ChartTree, id: string): string[] {
  const path: string[] = [];
  let current = tree.byId.get(id);
  while (current?.parentId && tree.byId.has(current.parentId) && !path.includes(current.parentId)) {
    path.push(current.parentId);
    current = tree.byId.get(current.parentId);
  }
  return path;
}

/** Everything that has boxes below it (for "open all"). */
export function allParents(tree: ChartTree): Set<string> {
  return new Set([...tree.children.keys()]);
}

/** Initials of a name for the avatar (the first letters of the first two words), or of the code when there is no name. */
export function initialsOf(name: string | null, fallback: string): string {
  const words = (name ?? '').trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) return fallback.slice(0, 2).toLocaleUpperCase('en');
  return words.slice(0, 2).map((w) => Array.from(w)[0] ?? '').join('').toLocaleUpperCase('en');
}

/** Zoom steps in percent; the chart's text scales with them. */
export const ZOOMS = [60, 80, 100, 125, 150, 200] as const;
export const nextZoom = (current: number, direction: 1 | -1): number => {
  const index = ZOOMS.findIndex((z) => z === current);
  const at = index < 0 ? ZOOMS.indexOf(100) : index;
  return ZOOMS[Math.min(ZOOMS.length - 1, Math.max(0, at + direction))] ?? 100;
};
