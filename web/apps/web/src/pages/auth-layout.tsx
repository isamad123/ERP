// The frame of every sign-in screen (slice S1.12a, Build item 7; spec 13 sections 5, 12, 15, 17): the product name,
// the language switch, one card with a heading that receives focus when the step changes, and the problem banner.
// Messages never reveal whether an account exists (FR-UX-SEC-07).
import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { Banner, Button } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../api/client';
import { useBranding } from '../app/branding';
import { useDisplay } from '../app/display-context';

export function AuthLayout({ titleKey: title, children }: { titleKey: MessageKey; children: ReactNode }) {
  const { t } = useText();
  const heading = useRef<HTMLHeadingElement>(null);
  const { display, setDisplay } = useDisplay();
  const brand = useBranding();
  useEffect(() => {
    document.title = `${t(title)} – ${brand.productName}`;
    heading.current?.focus();
  }, [t, title, brand.productName]);
  const other = display.language === 'en' ? 'ur' : 'en';
  return (
    <div className="erp-auth">
      <header className="erp-auth-top">
        <span className="erp-auth-brand">
          {brand.logoUrl && <img className="erp-auth-logo" src={brand.logoUrl} alt="" />}
          <span className="erp-auth-product">{brand.productName}</span>
        </span>
        <Button variant="quiet" onPress={() => setDisplay({ ...display, language: other })}>
          <span lang={other}>{t(other === 'ur' ? 'language.ur' : 'language.en')}</span>
        </Button>
      </header>
      <main className="erp-auth-main" id="main">
        <section className="erp-auth-card erp-stack" aria-labelledby="auth-title">
          <h1 id="auth-title" ref={heading} tabIndex={-1}>{t(title)}</h1>
          {brand.banner && <Banner tone="info">{brand.banner}</Banner>}
          {children}
          {brand.terms && <p className="erp-auth-terms erp-small">{brand.terms}</p>}
        </section>
      </main>
    </div>
  );
}

/** The text for a failed call: the server's text for its key, else a plain web text; never anything technical. */
export function useProblemText() {
  const { t, server } = useText();
  return (error: unknown): { message: string; reference: string | null } => {
    if (error instanceof ApiProblem) {
      if (error.status === 0) return { message: t('error.network'), reference: null };
      return { message: server(error.key) ?? (error.status === 503 ? t('error.busy') : t('error.generic')), reference: error.reference };
    }
    return { message: t('error.generic'), reference: null };
  };
}

export function ProblemBanner({ error }: { error: unknown }) {
  const { t } = useText();
  const text = useProblemText();
  if (!error) return null;
  const { message, reference } = text(error);
  return (
    <Banner tone="danger">
      <span>{message}</span>
      {reference && <span className="erp-small erp-auth-reference">{t('common.reference', { reference })}</span>}
    </Banner>
  );
}

/** The password-rule keys of a 400 answer (errors.password.*), as server texts for the new-password field. */
export function usePasswordErrors() {
  const { server } = useText();
  return (error: unknown): string | null => {
    if (!(error instanceof ApiProblem) || error.status !== 400 || error.errors.length === 0) return null;
    return error.errors.map((key) => server(key) ?? key).join(' ');
  };
}

/** State of one sign-in, kept in memory only: a reload starts again at the first step. */
export interface SignInState {
  challengeToken: string | null;
  methods: string[];
  rememberDays: number;
  enrolBy: string | null;
  recoveryCodes: string[] | null;
  returnTo: string;
}

const empty: SignInState = { challengeToken: null, methods: [], rememberDays: 0, enrolBy: null, recoveryCodes: null, returnTo: '/' };

const FlowContext = createContext<{ flow: SignInState; setFlow: (next: Partial<SignInState>) => void; reset: () => void }>({
  flow: empty, setFlow: () => undefined, reset: () => undefined,
});

export function SignInFlowProvider({ children }: { children: ReactNode }) {
  const [flow, setState] = useState(empty);
  return (
    <FlowContext.Provider value={{ flow, setFlow: (next) => setState((s) => ({ ...s, ...next })), reset: () => setState(empty) }}>
      {children}
    </FlowContext.Provider>
  );
}

export function useSignInFlow() {
  return useContext(FlowContext);
}
