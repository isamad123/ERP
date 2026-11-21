// One declared screen behind its gate (slice S1.12b Build item 11): licence, locked state and permission first, then the
// screen, or "not available yet" for a screen of a later slice. Deep links from notifications and e-mails arrive here too,
// so a link to a screen the person may not open shows "permission denied", never the screen (FR-HRM-NTF-07).
import { Suspense } from 'react';
import { useLocation } from 'react-router';
import { Skeleton } from '@erp/ui';
import { useShell } from '../shell/context';
import { DeniedPage, LockedPage, NotFoundPage, NotYetPage, ScreenErrorBoundary, ServerErrorPage } from '../pages/errors';
import { decide, type ScreenDeclaration } from './screens';

export function Screen({ screen }: { screen: ScreenDeclaration }) {
  const { facts, can } = useShell();
  const location = useLocation();
  switch (decide(screen, facts)) {
    case 'notFound':
      return <NotFoundPage />;
    case 'locked':
      return <LockedPage canReadLicence={can('cfg.licence.read')} canExport={can('aud.export')} />;
    case 'denied':
      return <DeniedPage permission={screen.permission} />;
    case 'notYet':
      return <NotYetPage titleKey={screen.labelKey} />;
    case 'show': {
      const Component = screen.component;
      if (!Component) return <NotYetPage titleKey={screen.labelKey} />;
      return (
        <ScreenErrorBoundary resetKey={location.pathname} fallback={(reset) => <ServerErrorPage onRetry={reset} />}>
          <Suspense fallback={<Skeleton lines={4} />}><Component /></Suspense>
        </ScreenErrorBoundary>
      );
    }
  }
}
