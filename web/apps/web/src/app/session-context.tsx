// The session for React (slice S1.12a, Build item 6): one SessionManager per tab, the idle warning dialog, and the
// guard that sends a signed-out person to sign-in with the address to come back to.
import { createContext, useContext, useEffect, useMemo, useState, useSyncExternalStore, type ReactNode } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router';
import { Button, Dialog, Spinner } from '@erp/ui';
import { useText } from '@erp/i18n';
import { setTokenSource } from '../api/client';
import { identity } from '../api/identity';
import { browserEnvironment, SessionManager, type SessionSnapshot } from '../session/session';

const SessionContext = createContext<SessionManager | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const manager = useMemo(() => new SessionManager(browserEnvironment(identity.refresh, async () => { await identity.signOut(); })), []);
  useEffect(() => {
    setTokenSource(manager);
    void manager.resume();
    return () => manager.dispose();
  }, [manager]);
  return (
    <SessionContext.Provider value={manager}>
      {children}
      <IdleWarning manager={manager} />
    </SessionContext.Provider>
  );
}

export function useSession(): SessionManager {
  const manager = useContext(SessionContext);
  if (!manager) throw new Error('SessionProvider is missing');
  return manager;
}

export function useSessionState(): SessionSnapshot {
  const manager = useSession();
  return useSyncExternalStore(manager.subscribe, manager.getSnapshot);
}

/** A safe address to come back to: a path on this site only (never another host). */
export function safeReturn(value: string | null | undefined): string {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) return '/';
  return value.startsWith('/sign-in') ? '/' : value;
}

/** Screens that need a session: wait while it is resumed, else go to sign-in and come back here afterwards. */
export function RequireSession({ children }: { children: ReactNode }) {
  const { status } = useSessionState();
  const location = useLocation();
  if (status === 'unknown') return <main className="erp-center"><Spinner /></main>;
  if (status !== 'signedIn') {
    const back = encodeURIComponent(location.pathname + location.search);
    return <Navigate to={`/sign-in?return=${back}${status === 'expired' ? '&reason=expired' : ''}`} replace />;
  }
  return <>{children}</>;
}

/** Two minutes before the idle end with no activity: "Stay signed in" or sign out now (spec 13 FR-UX-SEC-04). */
function IdleWarning({ manager }: { manager: SessionManager }) {
  const { t } = useText();
  const navigate = useNavigate();
  const { warningRemainingMs } = useSyncExternalStore(manager.subscribe, manager.getSnapshot);
  const [, setTick] = useState(0);
  useEffect(() => {
    if (warningRemainingMs === null) return undefined;
    const timer = window.setInterval(() => setTick((n) => n + 1), 1000);
    return () => window.clearInterval(timer);
  }, [warningRemainingMs]);
  if (warningRemainingMs === null) return null;
  const seconds = Math.max(0, Math.ceil(warningRemainingMs / 1000));
  return (
    <Dialog title={t('session.warning.title')} isOpen role="alertdialog"
      actions={() => (
        <>
          <Button variant="secondary" onPress={() => { void manager.signOut().then(() => navigate('/sign-in?reason=signedOut')); }}>{t('session.warning.signOut')}</Button>
          <Button variant="primary" onPress={() => { void manager.staySignedIn(); }}>{t('session.warning.stay')}</Button>
        </>
      )}>
      <p>{t('session.warning.body', { seconds })}</p>
    </Dialog>
  );
}
