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
      <SignOutFailure manager={manager} />
    </SessionContext.Provider>
  );
}

// A sign-in made in this page (a sign-in step answered with tokens), as opposed to a reload or another tab's session: the
// shell applies the person's stored language once after it (product owner ruling 2026-11-21, S1.12b).
let freshSignIn = false;

export function noteFreshSignIn(): void {
  freshSignIn = true;
}

/** True once after a sign-in made in this page. */
export function takeFreshSignIn(): boolean {
  const fresh = freshSignIn;
  freshSignIn = false;
  return fresh;
}

export function useSession(): SessionManager {
  const manager = useContext(SessionContext);
  if (!manager) throw new Error('SessionProvider is missing');
  return manager;
}

/** Signs out and goes to sign-in only when the session really ended; on a failure the failure message shows (R-071). */
export function useSignOut(): () => void {
  const manager = useSession();
  const navigate = useNavigate();
  return () => { void manager.signOut().then((result) => { if (result === 'ended') navigate('/sign-in?reason=signedOut', { replace: true }); }); };
}

export function useSessionState(): SessionSnapshot {
  const manager = useSession();
  return useSyncExternalStore(manager.subscribe, manager.getSnapshot);
}

/**
 * A safe address to come back to: a path on this site only (never another host). The value is read the way the browser
 * reads an address (it drops tabs and line breaks, treats "\" as "/" and resolves dot segments), so "/\t/evil.example"
 * or "/.//evil.example" cannot become "//evil.example" after the check (review S1.12a finding 1).
 */
export function safeReturn(value: string | null | undefined, origin: string = window.location.origin): string {
  // eslint-disable-next-line no-control-regex -- control characters are exactly what is refused here
  if (!value || !value.startsWith('/') || /[\u0000-\u001f\u007f\\]/.test(value)) return '/';
  let url: URL;
  try {
    url = new URL(value, origin);
  } catch {
    return '/';
  }
  const path = url.pathname + url.search + url.hash;
  if (url.origin !== origin || !path.startsWith('/') || path.startsWith('//')) return '/';
  return path.startsWith('/sign-in') ? '/' : path;
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
  const signOut = useSignOut();
  const { warningRemainingMs, signOutFailed } = useSyncExternalStore(manager.subscribe, manager.getSnapshot);
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
          <Button variant="secondary" onPress={signOut}>{t('session.warning.signOut')}</Button>
          <Button variant="primary" onPress={() => { void manager.staySignedIn(); }}>{t('session.warning.stay')}</Button>
        </>
      )}>
      <p>{t('session.warning.body', { seconds })}</p>
      {signOutFailed ? <p role="alert">{t('session.signOutFailed.body')}</p> : null}
    </Dialog>
  );
}

/** "Sign-out did not work" (R-071, FR-KRN-IAM-04): the person is still signed in; try again, or close the browser. */
function SignOutFailure({ manager }: { manager: SessionManager }) {
  const { t } = useText();
  const signOut = useSignOut();
  const { signOutFailed, warningRemainingMs } = useSyncExternalStore(manager.subscribe, manager.getSnapshot);
  if (!signOutFailed || warningRemainingMs !== null) return null; // the idle warning carries the message itself
  return (
    <Dialog title={t('session.signOutFailed.title')} isOpen role="alertdialog" onOpenChange={(open) => { if (!open) manager.dismissSignOutFailure(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={() => manager.dismissSignOutFailure()}>{t('session.signOutFailed.close')}</Button>
          <Button variant="primary" onPress={signOut}>{t('common.retry')}</Button>
        </>
      )}>
      <p>{t('session.signOutFailed.body')}</p>
    </Dialog>
  );
}
