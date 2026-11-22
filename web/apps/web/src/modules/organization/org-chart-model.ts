// The shape of a hierarchy on one date for the org chart (slice S1.14b Build item 10): children by parent, the units under a unit
// (a unit cannot be moved under one of them) and the rows a tree shows for the nodes that are open. Pure functions, no screen.
import type { OrgTreeNode } from '../../api/org';

export interface ChartModel {
  byId: Map<string, OrgTreeNode>;
  children: Map<string, OrgTreeNode[]>;
  /** Units without a parent on that date, or whose parent is not visible to this person, ordered by company then code. */
  roots: OrgTreeNode[];
}

const byCode = (a: OrgTreeNode, b: OrgTreeNode) => a.code.localeCompare(b.code, 'en');

export function buildModel(nodes: OrgTreeNode[]): ChartModel {
  const byId = new Map(nodes.map((n) => [n.unitId, n]));
  const children = new Map<string, OrgTreeNode[]>();
  const roots: OrgTreeNode[] = [];
  for (const node of nodes) {
    if (node.parentUnitId && byId.has(node.parentUnitId)) {
      const list = children.get(node.parentUnitId) ?? [];
      list.push(node);
      children.set(node.parentUnitId, list);
    } else {
      roots.push(node);
    }
  }
  for (const list of children.values()) list.sort(byCode);
  roots.sort((a, b) => a.companyId.localeCompare(b.companyId, 'en') || byCode(a, b));
  return { byId, children, roots };
}

/** Every unit below a unit, at any depth. */
export function descendantIds(model: ChartModel, unitId: string): Set<string> {
  const found = new Set<string>();
  const stack = [unitId];
  while (stack.length > 0) {
    for (const child of model.children.get(stack.pop() as string) ?? []) {
      if (!found.has(child.unitId)) {
        found.add(child.unitId);
        stack.push(child.unitId);
      }
    }
  }
  return found;
}

/** The units a person may choose as the new parent of a unit: its own company, not itself, not anything below it. */
export function parentChoices(model: ChartModel, unit: OrgTreeNode, all: OrgTreeNode[]): OrgTreeNode[] {
  const below = descendantIds(model, unit.unitId);
  return all.filter((n) => n.companyId === unit.companyId && n.unitId !== unit.unitId && !below.has(n.unitId)).sort(byCode);
}

export interface TreeRow { node: OrgTreeNode; level: number; hasChildren: boolean; expanded: boolean; posInSet: number; setSize: number }

/** The rows of the open part of the tree in reading order; `total` counts them all, `rows` stops at the limit. */
export function visibleRows(model: ChartModel, open: ReadonlySet<string>, limit: number): { rows: TreeRow[]; total: number } {
  const rows: TreeRow[] = [];
  let total = 0;
  const walk = (list: OrgTreeNode[], level: number) => {
    list.forEach((node, index) => {
      const kids = model.children.get(node.unitId) ?? [];
      const expanded = kids.length > 0 && open.has(node.unitId);
      total += 1;
      if (rows.length < limit) rows.push({ node, level, hasChildren: kids.length > 0, expanded, posInSet: index + 1, setSize: list.length });
      if (expanded) walk(kids, level + 1);
    });
  };
  walk(model.roots, 1);
  return { rows, total };
}

/** The units on the path from the top to a unit, for "open down to this unit". */
export function ancestorIds(model: ChartModel, unitId: string): string[] {
  const path: string[] = [];
  let current = model.byId.get(unitId);
  while (current?.parentUnitId && model.byId.has(current.parentUnitId) && !path.includes(current.parentUnitId)) {
    path.push(current.parentUnitId);
    current = model.byId.get(current.parentUnitId);
  }
  return path;
}
