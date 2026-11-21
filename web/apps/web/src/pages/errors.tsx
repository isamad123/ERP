// Error and state pages (slice S1.12b Build item 13; S1.12 rulings Q17, Q18; spec 13 FR-UX-STA-02, FR-UX-STA-08,
// FR-UX-CNT-03): each says what happened and what the person can do, in their language, with nothing technical; the server
// error shows the reference for support. "Not found" is the same page for a nonsense address and an unlicensed screen
// (FR-KRN-ENT-03).
import { Component, useEffect, type ErrorInfo, type ReactNode } from 'react';
import { useNavigate } from 'react-router';
import { Button, EmptyState, ErrorPanel, Link } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';

export function usePageTitle(title: string): void {
  const { t } = useText();
  useEffect(() => { document.title = `${title} – ${t('product.name')}`; }, [title, t]);
}

export function NotFoundPage() {
  const { t } = useText();
  usePageTitle(t('error.notFound.title'));
  return (
    <EmptyState level={1} title={t('error.notFound.title')} action={<Link href="/">{t('error.notFound.home')}</Link>}>
      {t('error.notFound.body')}
    </EmptyState>
  );
}

/** Permission denied: names the missing permission when it has a text, and offers the way back (ruling Q18). */
export function DeniedPage({ permission }: { permission?: string | undefined }) {
  const { t, server } = useText();
  const navigate = useNavigate();
  usePageTitle(t('error.denied.title'));
  const missing = permission ? server(`permissions.${permission}`) : null;
  return (
    <EmptyState level={1} title={t('error.denied.title')}
      action={(
        <div className="erp-row">
          <Button variant="secondary" onPress={() => { void navigate(-1); }}>{t('common.back')}</Button>
          <Link href="/">{t('error.notFound.home')}</Link>
        </div>
      )}>
      <p>{t('error.denied.body')}</p>
      {missing && <p>{t('error.denied.missing', { permission: missing })}</p>}
      <p>{t('error.denied.ask')}</p>
    </EmptyState>
  );
}

/** A screen that a later update brings (its links already work; nothing pretends to be there). */
export function NotYetPage({ titleKey }: { titleKey: MessageKey }) {
  const { t } = useText();
  usePageTitle(t(titleKey));
  return (
    <EmptyState level={1} title={t(titleKey)} action={<Link href="/">{t('error.notFound.home')}</Link>}>
      {t('error.notYet.body')}
    </EmptyState>
  );
}

/** Licence locked (FR-KRN-ENT-04, FR-KRN-TEN-09, ruling Q17): everyone sees why; licence readers and exporters get their way out. */
export function LockedPage({ canReadLicence, canExport }: { canReadLicence: boolean; canExport: boolean }) {
  const { t } = useText();
  usePageTitle(t('licence.locked.title'));
  return (
    <ErrorPanel level={1} title={t('licence.locked.title')} message={canReadLicence ? t('licence.locked.admin') : t('licence.locked.body')}
      action={canReadLicence || canExport ? (
        <div className="erp-row">
          {canReadLicence && <Link href="/settings/licence">{t('licence.locked.open')}</Link>}
          {canExport && <Link href="/admin/exports">{t('licence.locked.export')}</Link>}
        </div>
      ) : undefined} />
  );
}

/** A screen that failed while showing: what to do, and a way back; the page itself stays usable. */
export class ScreenErrorBoundary extends Component<{ children: ReactNode; fallback: (reset: () => void) => ReactNode; resetKey: string }, { failed: boolean }> {
  override state = { failed: false };

  static getDerivedStateFromError(): { failed: boolean } {
    return { failed: true };
  }

  override componentDidUpdate(previous: { resetKey: string }): void {
    if (previous.resetKey !== this.props.resetKey && this.state.failed) this.setState({ failed: false });
  }

  override componentDidCatch(error: unknown, info: ErrorInfo): void {
    void error;
    void info;
  }

  override render(): ReactNode {
    return this.state.failed ? this.props.fallback(() => this.setState({ failed: false })) : this.props.children;
  }
}

export function ServerErrorPage({ reference, onRetry }: { reference?: string | null; onRetry?: () => void }) {
  const { t } = useText();
  usePageTitle(t('ui.error.title'));
  return (
    <ErrorPanel level={1} message={t('error.generic')} reference={reference ?? null}
      action={onRetry ? <Button variant="primary" onPress={onRetry}>{t('common.retry')}</Button> : <Link href="/">{t('error.notFound.home')}</Link>} />
  );
}
