// Routes: the sign-in screens and account links (S1.12a), and every module screen inside the shell (S1.12b), each behind
// its gate (licence, locked state, permission). Server data is cached per tab by React Query and forgotten at sign-out.
import { lazy, Suspense, useState } from 'react';
import { createBrowserRouter, createRoutesFromElements, Navigate, Outlet, Route, RouterProvider, useHref, useNavigate } from 'react-router';
import { RouterProvider as AriaRouterProvider } from 'react-aria-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Spinner, ToastProvider } from '@erp/ui';
import { DisplayProvider } from './display-context';
import { RequireSession, SessionProvider, useSessionState } from './session-context';
import { SignInFlowProvider } from '../pages/auth-layout';
import { NewPasswordPage, RecoveryCodesPage, ReminderPage, SetUpPage, SignInPage, VerifyPage } from '../pages/sign-in';
import { ActivatePage, ForgotPasswordPage, ResetPasswordPage } from '../pages/account-links';
import { NotFoundPage } from '../pages/errors';
import { kernelScreens } from '../modules/kernel';
import { hrmScreens } from '../modules/hrm';
import { Screen } from '../navigation/Screen';
import { SessionWatch } from '../shell/context';
import { ShellLayout } from '../shell/Shell';

// The component catalogue exists only in development and test builds (ruling Q19; `vite build --mode e2e`); production
// builds drop it. S1.12b: the test build turns it on by its mode, not by a variable kept in an untracked local file.
const Catalogue = import.meta.env.DEV || import.meta.env.MODE === 'e2e' || import.meta.env.VITE_ERP_CATALOGUE === 'true' ? lazy(() => import('../catalogue/CataloguePage')) : null;

/** Every module's screens, in menu order within each group (ruling Q8). */
const screens = [...kernelScreens, ...hrmScreens];

const queryClient = new QueryClient({
  // The API client already refreshes the session once and retries a busy server once (S1.12a); no second retry layer.
  defaultOptions: { queries: { retry: false, staleTime: 30_000, refetchOnWindowFocus: false } },
});

function AriaRouter({ children }: { children: React.ReactNode }) {
  const navigate = useNavigate();
  return <AriaRouterProvider navigate={(to, options) => { void navigate(to, options as never); }} useHref={useHref}>{children}</AriaRouterProvider>;
}

/** A signed-in person who opens the sign-in page goes on to the start page. */
function SignInEntry() {
  const { status } = useSessionState();
  return status === 'signedIn' ? <Navigate to="/" replace /> : <SignInPage />;
}

/**
 * Providers that need the router (React Aria links, toasts, server data, the session and the sign-in flow) wrap every
 * route. S1.12c: a data router, so a form can warn before any way of leaving it, the browser's Back button included
 * (FR-ADM-UXA-05, `useBlocker`).
 */
function Root() {
  return (
    <AriaRouter>
      <ToastProvider>
        <QueryClientProvider client={queryClient}>
          <SessionProvider>
            <SessionWatch />
            <SignInFlowProvider>
              <Outlet />
            </SignInFlowProvider>
          </SessionProvider>
        </QueryClientProvider>
      </ToastProvider>
    </AriaRouter>
  );
}

const routes = createRoutesFromElements(
  <Route element={<Root />}>
    <Route path="/sign-in" element={<SignInEntry />} />
    <Route path="/sign-in/verify" element={<VerifyPage />} />
    <Route path="/sign-in/new-password" element={<NewPasswordPage />} />
    <Route path="/sign-in/set-up" element={<SetUpPage />} />
    <Route path="/sign-in/set-up-now" element={<RequireSession><SetUpPage signedIn /></RequireSession>} />
    <Route path="/sign-in/recovery-codes" element={<RecoveryCodesPage />} />
    <Route path="/sign-in/reminder" element={<RequireSession><ReminderPage /></RequireSession>} />
    <Route path="/forgot-password" element={<ForgotPasswordPage />} />
    <Route path="/reset-password" element={<ResetPasswordPage />} />
    <Route path="/activate" element={<ActivatePage />} />
    {Catalogue && <Route path="/catalogue" element={<Suspense fallback={<Spinner />}><Catalogue /></Suspense>} />}
    <Route element={<RequireSession><ShellLayout screens={screens} /></RequireSession>}>
      {screens.map((screen) => <Route key={screen.id} path={screen.path} element={<Screen screen={screen} />} />)}
      <Route path="*" element={<NotFoundPage />} />
    </Route>
  </Route>,
);

export function App() {
  // One router per page load (created on first render, so importing this module creates nothing).
  const [router] = useState(() => createBrowserRouter(routes));
  return (
    <DisplayProvider>
      <RouterProvider router={router} />
    </DisplayProvider>
  );
}
