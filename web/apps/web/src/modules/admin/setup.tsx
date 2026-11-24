// The first-run set-up guide and the readiness checklist (slice S1.18b; FR-ADM-ONB-01 to -04; S1.18 rulings Q1, Q4, Q5). Both views are over the
// readiness API of S1.18a: the server decides every status, every change goes through it, and the guide links to the normal screens for the work
// itself, so it has the same validation and the same audit entries as set-up done by hand (FR-ADM-ONB-02).
import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { useDocumentTitle } from '@erp/engine';
import { Button, ErrorPanel, Link, PageHeader, SelectField, Skeleton, type Option } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { setupApi } from '../../api/setup';
import { companyName, useShell } from '../../shell/context';
import { SetupWizard } from './setup-wizard';
import { SetupChecklist } from './setup-checklist';
import './setup-messages';
import './users.css';
import './setup.css';

export const setupKey = (companyId: string | null) => ['cfg', 'readiness', companyId ?? ''] as const;

export default function SetupScreen() {
  const { t } = useText();
  useDocumentTitle(t('setup.title'));
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  const [params, setParams] = useSearchParams();
  const companyId = params.get('company') ?? me.defaultCompanyId ?? me.companies[0]?.id ?? null;
  const view = params.get('view') === 'checklist' ? 'checklist' : 'wizard';
  const patch = (changes: Record<string, string | null>) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [k, v] of Object.entries(changes)) { if (v) next.set(k, v); else next.delete(k); }
    return next;
  }, { replace: true });
  const overview = useQuery({ queryKey: setupKey(companyId), queryFn: () => setupApi.overview(companyId), refetchOnMount: 'always' });
  const problem = overview.error instanceof ApiProblem ? overview.error : null;
  const companyOptions: Option[] = me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }));
  return (
    <div className="erp-page">
      <PageHeader title={t('setup.title')} description={t('setup.description')} />
      <nav className="erp-row erp-wrap" aria-label={t('setup.nav')}>
        <Button variant={view === 'wizard' ? 'primary' : 'secondary'} aria-pressed={view === 'wizard'} onPress={() => patch({ view: null })}>{t('setup.tab.wizard')}</Button>
        <Button variant={view === 'checklist' ? 'primary' : 'secondary'} aria-pressed={view === 'checklist'} onPress={() => patch({ view: 'checklist' })}>{t('setup.tab.checklist')}</Button>
      </nav>
      {me.companies.length > 0 && (
        <div className="erp-row erp-wrap erp-end">
          <SelectField label={t('setup.company.label')} help={t('setup.company.help')} options={companyOptions} value={companyId ?? ''} onChange={(id) => patch({ company: id, step: null })} />
          <Link href="/organization/companies">{t('setup.another')}</Link>
        </div>
      )}
      {overview.isPending && !problem && <Skeleton lines={6} />}
      {problem && (
        <ErrorPanel message={problem.status === 403 ? t('setup.denied') : t('setup.loadError')} reference={problem.reference}
          action={<Button variant="primary" onPress={() => { void overview.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {overview.isSuccess && view === 'wizard' && <SetupWizard overview={overview.data} companyId={companyId} step={params.get('step')} onStep={(key) => patch({ step: key })} onChecklist={() => patch({ view: 'checklist' })} />}
      {overview.isSuccess && view === 'checklist' && <SetupChecklist overview={overview.data} companyId={companyId} />}
    </div>
  );
}
