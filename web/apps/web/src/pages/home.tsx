// The signed-in landing page of S1.12a and the not-found page. The shell, menus and the "waiting for you" home page are
// S1.12b (Build items 10 and 11); until then the landing page confirms the session and offers sign-out.
import { useEffect, useRef } from 'react';
import { useNavigate } from 'react-router';
import { Button, EmptyState, Link } from '@erp/ui';
import { useText } from '@erp/i18n';
import { useSession } from '../app/session-context';

export function HomePage() {
  const { t } = useText();
  const session = useSession();
  const navigate = useNavigate();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    document.title = `${t('home.title')} – ${t('product.name')}`;
    heading.current?.focus();
  }, [t]);
  return (
    <div className="erp-auth">
      <header className="erp-auth-top">
        <span className="erp-auth-product">{t('product.name')}</span>
        <Button variant="secondary" onPress={() => { void session.signOut().then(() => navigate('/sign-in?reason=signedOut', { replace: true })); }}>
          {t('home.signOut')}
        </Button>
      </header>
      <main className="erp-auth-main" id="main">
        <section className="erp-auth-card erp-stack">
          <h1 ref={heading} tabIndex={-1}>{t('home.title')}</h1>
          <p>{t('home.body')}</p>
        </section>
      </main>
    </div>
  );
}

export function NotFoundPage() {
  const { t } = useText();
  useEffect(() => { document.title = `${t('error.notFound.title')} – ${t('product.name')}`; }, [t]);
  return (
    <main className="erp-auth-main" id="main">
      <EmptyState level={1} title={t('error.notFound.title')} action={<Link href="/">{t('error.notFound.home')}</Link>}>
        {t('error.notFound.body')}
      </EmptyState>
    </main>
  );
}
