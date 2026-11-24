// The export button of a screen that is not a generated list (the user list, the audit search; slice S1.16b): the same dialog, loaded on demand.
import { lazy, Suspense } from 'react';
import type { ExportButtonProps } from './ExportButton';

const ExportButton = lazy(() => import('./ExportButton'));

export function LazyExportButton(props: ExportButtonProps) {
  return <Suspense fallback={null}><ExportButton {...props} /></Suspense>;
}
