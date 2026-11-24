// The import centre and the template designer (slice S1.16a Build items 4 and 5; FR-ADM-DAT-01, FR-ADM-INT-04). Routes: the centre
// (`/admin/imports`), one batch (`batches/:id`) and the designer (`templates`, for people who may design templates).
import { Route, Routes } from 'react-router';
import { ErrorPanel } from '@erp/ui';
import { useText } from '@erp/i18n';
import { useShell } from '../../shell/context';
import { ImportCentre } from './imports-centre';
import { BatchPage } from './imports-batch';
import { TemplateDesigner } from './imports-designer';
import './imports-messages';

function Designer() {
  const { t } = useText();
  const { can } = useShell();
  return can('itg.imports.design') ? <TemplateDesigner /> : <div className="erp-page"><ErrorPanel level={1} message={t('imp.designer.denied')} /></div>;
}

export default function ImportsScreen() {
  const { t } = useText();
  const { can } = useShell();
  if (!can('itg.imports.manage')) return <div className="erp-page"><ErrorPanel level={1} message={t('imp.denied')} /></div>;
  return (
    <Routes>
      <Route index element={<ImportCentre />} />
      <Route path="batches/:id" element={<BatchPage />} />
      <Route path="templates" element={<Designer />} />
    </Routes>
  );
}
