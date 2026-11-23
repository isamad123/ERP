// The shell's knowledge of the signed-in person and the licence (slice S1.12b Build items 9 to 13): /api/kernel/me (S1.12
// ruling Q9) and /api/kernel/capabilities, the active company (ruling Q10), and the helpers screens use to show or hide
// things. Showing is not deciding: the server checks every request again (FR-XM-32 for approvals, FR-KRN-IAM-10).
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, EmptyState, ErrorPanel, Skeleton } from '@erp/ui';
import { useText } from '@erp/i18n';
import { ApiProblem } from '../api/client';
import { kernel, type Capabilities, type Me, type SessionCompany } from '../api/kernel';
import { DEFAULT_FORMATS, useDisplay } from '../app/display-context';
import { takeFreshSignIn, useSession, useSessionState } from '../app/session-context';
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

/** Gives a screen a fixed shell value (component tests of screens, S1.12d); the application uses ShellDataProvider. */
export const ShellValueProvider = ShellContext.Provider;

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

/** Watches the session: forgets everything the shell loaded when the session ends (another person may sign in next). */
export function SessionWatch() {
  const { status } = useSessionState();
  const client = useQueryClient();
  const { setFormats, setOverrides } = useDisplay();
  useEffect(() => {
    if (status === 'signedOut' || status === 'expired') {
      client.clear();
      setFormats(DEFAULT_FORMATS);
      setOverrides({});
    }
  }, [status, client, setFormats, setOverrides]);
  return null;
}

/** Label changes of other administrators reach every open screen within a minute (S1.15b, FR-KRN-MET-22). */
const LABEL_REFRESH_MS = 60_000;

export function ShellDataProvider({ children }: { children: ReactNode }) {
  const me = useQuery({ queryKey: meKey, queryFn: () => kernel.me() });
  const capabilities = useQuery({ queryKey: capabilitiesKey, queryFn: kernel.capabilities, refetchInterval: 5 * 60_000 });
  const [chosen, setChosen] = useState<string | null>(null);
  const { display, setDisplay, setFormats, setOverrides } = useDisplay();
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

  // The customer's and the company's label overrides apply to every label shown (S1.12c Build item 15, deviation i of
  // CR-063). They only change words: a failed load keeps the product's own labels.
  const labels = useQuery({ queryKey: ['kernel', 'labels', activeId], queryFn: () => kernel.labels(activeId), enabled: !!person, staleTime: 5 * 60_000, refetchInterval: LABEL_REFRESH_MS });
  useEffect(() => {
    if (labels.data) setOverrides(labels.data);
  }, [labels.data, setOverrides]);

  // Each sign-in starts in the person's stored language; the user menu's choice lasts until the next sign-in (product owner
  // ruling 2026-11-21).
  useEffect(() => {
    if (!person || !takeFreshSignIn()) return;
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

  if (!me.data && me.error instanceof ApiProblem && me.error.status === 403) {
    // Signed in, but no role gives the person anything yet (not even their own account): say so and offer sign-out.
    return <NoAccess />;
  }
  // Only a first load that failed stops the shell; a background refresh that fails (an idle tab whose access token ran
  // out, S1.12a review finding 2) keeps the data already shown.
  if ((me.isError && !me.data) || (capabilities.isError && !capabilities.data)) {
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

/** A signed-in person whom no role gives anything yet (FR-UX-STA-08: say what is missing and what to do). */
function NoAccess() {
  const { t } = useText();
  const session = useSession();
  const navigate = useNavigate();
  useEffect(() => { document.title = `${t('shell.noAccess.title')} – ${t('product.name')}`; }, [t]);
  return (
    <main className="erp-auth-main" id="main">
      <EmptyState level={1} title={t('shell.noAccess.title')}
        action={<Button variant="secondary" onPress={() => { void session.signOut().then(() => navigate('/sign-in?reason=signedOut', { replace: true })); }}>{t('home.signOut')}</Button>}>
        {t('shell.noAccess.body')}
      </EmptyState>
    </main>
  );
}
