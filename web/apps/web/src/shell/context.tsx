// The shell's knowledge of the signed-in person and the licence (slice S1.12b Build items 9 to 13): /api/kernel/me (S1.12
// ruling Q9) and /api/kernel/capabilities, the active company (ruling Q10), and the helpers screens use to show or hide
// things. Showing is not deciding: the server checks every request again (FR-XM-32 for approvals, FR-KRN-IAM-10).
import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, ErrorPanel, Skeleton } from '@erp/ui';
import { useText } from '@erp/i18n';
import { ApiProblem } from '../api/client';
import { kernel, type Capabilities, type Me, type SessionCompany } from '../api/kernel';
import { DEFAULT_FORMATS, useDisplay } from '../app/display-context';
import { useSessionState } from '../app/session-context';
import { accessFacts, holds, type AccessFacts } from '../navigation/screens';
import { isWritable } from './licence';
import { activeCompanyOf, rememberCompany } from './memory';

export interface ShellValue {
  me: Me;
  capabilities: Capabilities;
  facts: AccessFacts;
  /** Whether the person holds a permission (for one company, or for any). */
  can: (code: string, companyId?: string | null) => boolean;
  /** False in Read-only and Locked: no create, edit or decide action is offered (ruling Q17). */
  writable: boolean;
  activeCompany: SessionCompany | null;
  setActiveCompany: (id: string) => void;
}

const ShellContext = createContext<ShellValue | null>(null);

export function useShell(): ShellValue {
  const value = useContext(ShellContext);
  if (!value) throw new Error('ShellDataProvider is missing');
  return value;
}

/** For screens of later slices: offer create, edit and decide only while this is true. */
export function useWritable(): boolean {
  return useShell().writable;
}

export const meKey = ['kernel', 'me'] as const;
export const capabilitiesKey = ['kernel', 'capabilities'] as const;

// A sign-in in this page (not a reload or another tab's session): the person's stored language applies once (product owner
// ruling 2026-11-21: each sign-in starts in the stored language; the user menu's choice lasts until the next sign-in).
let freshSignIn = false;

/** Watches the session: notes a fresh sign-in, and forgets everything the shell loaded when the session ends. */
export function SessionWatch() {
  const { status } = useSessionState();
  const previous = useRef(status);
  const client = useQueryClient();
  const { setFormats } = useDisplay();
  useEffect(() => {
    const before = previous.current;
    previous.current = status;
    if (status === 'signedIn' && (before === 'signedOut' || before === 'expired')) freshSignIn = true;
    if (status === 'signedOut' || status === 'expired') {
      client.clear();
      setFormats(DEFAULT_FORMATS);
    }
  }, [status, client, setFormats]);
  return null;
}

export function ShellDataProvider({ children }: { children: ReactNode }) {
  const me = useQuery({ queryKey: meKey, queryFn: () => kernel.me() });
  const capabilities = useQuery({ queryKey: capabilitiesKey, queryFn: kernel.capabilities, refetchInterval: 5 * 60_000 });
  const [chosen, setChosen] = useState<string | null>(null);
  const { display, setDisplay, setFormats } = useDisplay();
  const { t } = useText();

  const person = me.data;
  const activeId = person ? (chosen && person.companies.some((c) => c.id === chosen) ? chosen : activeCompanyOf(person.userId, person.companies, person.defaultCompanyId)) : null;
  // Digits and grouping follow the company in hand (deviation h of CR-063); the default company's come with /me.
  const forCompany = useQuery({
    queryKey: [...meKey, activeId],
    queryFn: () => kernel.me(activeId),
    enabled: !!person && !!activeId && activeId !== person.defaultCompanyId,
  });

  useEffect(() => {
    if (!person) return;
    setFormats({ timeZone: person.timeZone, numbers: (forCompany.data ?? person).numbers });
  }, [person, forCompany.data, setFormats]);

  useEffect(() => {
    if (!person || !freshSignIn) return;
    freshSignIn = false;
    const language = person.language === 'ur' ? 'ur' : 'en';
    if (language !== display.language) setDisplay({ ...display, language });
  }, [person, display, setDisplay]);

  const value = useMemo<ShellValue | null>(() => {
    if (!person || !capabilities.data) return null;
    const facts = accessFacts(capabilities.data.features, person.permissions, capabilities.data.state);
    return {
      me: person,
      capabilities: capabilities.data,
      facts,
      can: (code, companyId) => holds(facts, code, companyId),
      writable: isWritable(capabilities.data.state),
      activeCompany: person.companies.find((c) => c.id === activeId) ?? null,
      setActiveCompany: (id) => { rememberCompany(person.userId, id); setChosen(id); },
    };
  }, [person, capabilities.data, activeId]);

  if (me.isError || capabilities.isError) {
    const problem = me.error ?? capabilities.error;
    return (
      <main className="erp-auth-main" id="main">
        <ErrorPanel level={1} title={t('shell.loadError.title')} message={t('shell.loadError.body')} reference={problem instanceof ApiProblem ? problem.reference : null}
          action={<Button variant="primary" onPress={() => { void me.refetch(); void capabilities.refetch(); }}>{t('common.retry')}</Button>} />
      </main>
    );
  }
  if (!value) {
    return <main className="erp-auth-main" id="main"><div className="erp-shell-loading"><Skeleton lines={4} /></div></main>;
  }
  return <ShellContext.Provider value={value}>{children}</ShellContext.Provider>;
}

/** The name of a company in the person's language (English when the other is missing). */
export function companyName(company: SessionCompany | null, language: string): string {
  if (!company) return '';
  return company.name[language] ?? company.name.en ?? company.code;
}
