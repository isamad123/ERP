// Shared parts of the position screens (slice S1.22b): the company in hand, the choices of units, locations and grades, and the date.
import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { locations as locationApi, orgUnits as unitApi } from '@erp/generated/api/org';
import type { Option } from '@erp/ui';
import { useLocaleSettings } from '@erp/i18n';
import { positionsApi } from '../../api/positions';
import { systemClock } from '../../session/clock';
import { companyName, useShell } from '../../shell/context';
import './positions-messages';
import './positions.css';

export const todayIso = () => new Date(systemClock.now()).toISOString().slice(0, 10);

export const ANY = '__any';

/** The company in hand (the active one, else the person's default, else the first) and the person's other companies. */
export function useCompanyChoice(permission = 'hrm.positions.read') {
  const { me, can, activeCompany } = useShell();
  const { locale } = useLocaleSettings();
  const options = useMemo<Option[]>(() => me.companies.filter((c) => can(permission, c.id)).map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code })), [me.companies, can, permission, locale]);
  const [chosen, setChosen] = useState<string | null>(null);
  const fallback = [activeCompany?.id, me.defaultCompanyId].find((id) => id && options.some((o) => o.id === id)) ?? options[0]?.id ?? null;
  return { companyId: chosen && options.some((o) => o.id === chosen) ? chosen : fallback, setCompanyId: setChosen, options };
}

const name = (names: Record<string, string> | null | undefined, locale: string, code: string) => names?.[locale] ?? names?.en ?? code;

export function useUnitOptions(companyId: string | null) {
  const { locale } = useLocaleSettings();
  const query = useQuery({ queryKey: ['org', 'units', 'choices', companyId], enabled: companyId !== null, staleTime: 60_000,
    queryFn: () => unitApi.list({ companyId: companyId as string, isActive: true, limit: 200, sort: 'code' }) });
  const options = useMemo<Option[]>(() => (query.data?.items ?? []).map((u) => ({ id: u.id, label: name(u.nameI18n, locale, u.code), detail: u.code })), [query.data, locale]);
  return { options, isPending: query.isPending, isError: query.isError };
}

export function useLocationOptions(companyId: string | null) {
  const { locale } = useLocaleSettings();
  const query = useQuery({ queryKey: ['org', 'locations', 'choices', companyId], enabled: companyId !== null, staleTime: 60_000,
    queryFn: () => locationApi.list({ companyId: companyId as string, isActive: true, limit: 200, sort: 'code' }) });
  const options = useMemo<Option[]>(() => (query.data?.items ?? []).map((l) => ({ id: l.id, label: name(l.nameI18n, locale, l.code), detail: l.code })), [query.data, locale]);
  return { options, isPending: query.isPending };
}

export function useGradeOptions(companyId: string | null) {
  const { locale } = useLocaleSettings();
  const query = useQuery({ queryKey: ['hrm', 'grades', 'choices', companyId], enabled: companyId !== null, staleTime: 60_000, queryFn: () => positionsApi.grades(companyId as string) });
  const options = useMemo<Option[]>(() => (query.data?.items ?? []).map((g) => ({ id: g.id, label: name(g.nameI18n, locale, g.code), detail: g.code })), [query.data, locale]);
  return { options, isPending: query.isPending };
}
