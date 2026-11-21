// Routes of S1.12a (sign-in screens, the account links, the landing page). S1.12b adds the shell and the module routes.
import { lazy, Suspense } from 'react';
import { BrowserRouter, Navigate, Route, Routes, useHref, useNavigate } from 'react-router';
import { RouterProvider } from 'react-aria-components';
import { Spinner, ToastProvider } from '@erp/ui';
import { DisplayProvider } from './display-context';
import { RequireSession, SessionProvider, useSessionState } from './session-context';
import { SignInFlowProvider } from '../pages/auth-layout';
import { NewPasswordPage, RecoveryCodesPage, ReminderPage, SetUpPage, SignInPage, VerifyPage } from '../pages/sign-in';
import { ActivatePage, ForgotPasswordPage, ResetPasswordPage } from '../pages/account-links';
import { HomePage, NotFoundPage } from '../pages/home';

// The component catalogue exists only in development and test builds (ruling Q19); production builds drop it.
const Catalogue = import.meta.env.DEV || import.meta.env.VITE_ERP_CATALOGUE === 'true' ? lazy(() => import('../catalogue/CataloguePage')) : null;

function AriaRouter({ children }: { children: React.ReactNode }) {
  const navigate = useNavigate();
  return <RouterProvider navigate={(to, options) => { void navigate(to, options as never); }} useHref={useHref}>{children}</RouterProvider>;
}

/** A signed-in person who opens the sign-in page goes on to the start page. */
function SignInEntry() {
  const { status } = useSessionState();
  return status === 'signedIn' ? <Navigate to="/" replace /> : <SignInPage />;
}

export function App() {
  return (
    <DisplayProvider>
      <BrowserRouter>
        <AriaRouter>
          <ToastProvider>
            <SessionProvider>
              <SignInFlowProvider>
                <Routes>
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
                  <Route path="/" element={<RequireSession><HomePage /></RequireSession>} />
                  {Catalogue && <Route path="/catalogue" element={<Suspense fallback={<Spinner />}><Catalogue /></Suspense>} />}
                  <Route path="*" element={<NotFoundPage />} />
                </Routes>
              </SignInFlowProvider>
            </SessionProvider>
          </ToastProvider>
        </AriaRouter>
      </BrowserRouter>
    </DisplayProvider>
  );
}
