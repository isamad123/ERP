// Numbering (slice S1.15c Build item 9; FR-KRN-NUM-01 to -03, -05; S1.15 rulings Q9, Q11): per company, each numbered document with its scheme (pattern, when
// it starts again, gap-free or not, starting number), the next number, change before first use, "retire and replace" after it, and the void report.
// The screen is a view over the S1.08 endpoints: it never hands out a number and never renumbers (FR-XM-05, FR-XM-06).
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { useDocumentTitle } from '@erp/engine';
import { Plus } from 'lucide-react';
import { Banner, Button, Dialog, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, useToast, type Option, ScrollRegion } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { allSchemes, branchesOf, numberingApi, type NumberedType, type SchemeRow } from '../../api/numbering';
import { settingsApi } from '../../api/settings';
import { nameOf } from '../../api/workflow';
import { companyName, useShell } from '../../shell/context';
import { useRefused } from './config-parts';
import { nextNumber, todayIn } from './numbering-model';
import { SchemeForm, type FormMode } from './numbering-form';
import { VoidReportDialog } from './numbering-report';
import './numbering-messages';
import './users.css';
import './configure.css';

const FISCAL_KEY = 'cfg.numbering.fiscal_year_start_month';

export default function NumberingScreen() {
  const { t } = useText();
  useDocumentTitle(t('num.title'));
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  const [params, setParams] = useSearchParams();
  const companyId = params.get('company') ?? me.defaultCompanyId ?? me.companies[0]?.id ?? '';
  const open = params.get('type');
  const patch = (changes: Record<string, string | null>) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [k, v] of Object.entries(changes)) { if (v) next.set(k, v); else next.delete(k); }
    return next;
  }, { replace: true });
  const types = useQuery({ queryKey: ['cfg', 'numbering', 'types', companyId], queryFn: () => numberingApi.types(companyId || null), refetchOnMount: 'always' });
  const schemes = useQuery({ queryKey: ['cfg', 'numbering', 'schemes'], queryFn: allSchemes, refetchOnMount: 'always' });
  const problem = [types.error, schemes.error].find((e) => e instanceof ApiProblem) as ApiProblem | undefined;
  const companyOptions: Option[] = me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }));
  const scoped = (type: NumberedType) => (schemes.data ?? []).filter((s) => s.documentType === type.code && (type.tenantWide ? s.companyId === null : s.companyId === companyId));
  const selected = types.data?.find((x) => x.code === open) ?? null;
  return (
    <div className="erp-page">
      <PageHeader title={t('num.title')} description={t('num.description')} />
      {me.companies.length > 1 && <SelectField label={t('num.company.label')} help={t('num.company.help')} options={companyOptions} value={companyId} onChange={(id) => patch({ company: id, type: null })} />}
      {(types.isPending || schemes.isPending) && !problem && <Skeleton lines={6} />}
      {problem && (
        <ErrorPanel message={problem.status === 403 ? t('num.denied') : t('num.loadError')} reference={problem.reference}
          action={<Button variant="primary" onPress={() => { void types.refetch(); void schemes.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {types.isSuccess && schemes.isSuccess && types.data.length === 0 && <EmptyState title={t('num.empty')}>{t('num.empty.body')}</EmptyState>}
      {types.isSuccess && schemes.isSuccess && types.data.length > 0 && (
        <div className="erp-stack">
          <ScrollRegion className="erp-table-scroll" label={t('num.types.title')}>
            <table className="erp-table" aria-label={t('num.types.title')}>
              <thead><tr>
                <th scope="col">{t('num.col.type')}</th><th scope="col">{t('num.col.pattern')}</th><th scope="col">{t('num.col.reset')}</th>
                <th scope="col">{t('num.col.state')}</th><th scope="col"><span className="erp-visually-hidden">{t('num.col.actions')}</span></th>
              </tr></thead>
              <tbody>
                {types.data.map((type) => {
                  const active = scoped(type).find((s) => s.status === 'active' && s.branchId === null);
                  return (
                    <tr key={type.code} aria-selected={open === type.code}>
                      <th scope="row"><span className="erp-code"><bdi>{type.code}</bdi></span>{type.tenantWide && <div className="erp-small erp-muted">{t('num.wholeOrg')}</div>}</th>
                      <td><span className={active ? 'erp-code' : 'erp-code erp-muted'}><bdi>{active?.pattern ?? type.defaultPattern}</bdi></span></td>
                      <td>{t(`num.reset.${active?.resetRule ?? type.defaultReset}` as MessageKey)}</td>
                      <td><StatusChip status={active ? 'active' : 'draft'} label={t(active ? 'num.state.set' : 'num.state.default')} /></td>
                      <td>
                        <Button variant={open === type.code ? 'primary' : 'secondary'} aria-pressed={open === type.code} aria-label={t('num.open.aria', { name: type.code })} onPress={() => patch({ type: type.code })}>{t('num.open')}</Button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </ScrollRegion>
          {selected
            ? <TypeDetail key={`${selected.code}:${companyId}`} type={selected} companyId={companyId} schemes={scoped(selected)} />
            : <EmptyState title={t('num.pick')}>{t('num.pick.body')}</EmptyState>}
        </div>
      )}
    </div>
  );
}

function TypeDetail({ type, companyId, schemes }: { type: NumberedType; companyId: string; schemes: SchemeRow[] }) {
  const { t } = useText();
  const { can, writable, me } = useShell();
  const toast = useToast();
  const [form, setForm] = useState<{ mode: FormMode; scheme?: SchemeRow } | null>(null);
  const canCreate = writable && can('cfg.numbering_schemes.create');
  const fiscal = useQuery({ queryKey: ['cfg', 'numbering', 'fiscal', companyId], queryFn: () => settingsApi.detail(FISCAL_KEY, companyId), retry: false });
  const fiscalStartMonth = typeof fiscal.data?.setting.value === 'number' ? fiscal.data.setting.value : 1;
  const branches = useQuery({ queryKey: ['cfg', 'numbering', 'branches', companyId], queryFn: () => branchesOf(companyId), enabled: type.hasBranch && !type.tenantWide, retry: false });
  const { locale } = useLocaleSettings();
  const branchOptions = (branches.data ?? []).map((b) => ({ id: b.id, code: b.code, name: nameOf(b.nameI18n, locale) || b.code }));
  const active = schemes.filter((s) => s.status === 'active');
  const retired = schemes.filter((s) => s.status === 'retired');
  const companyWide = active.find((s) => s.branchId === null);
  const done = (mode: FormMode) => { setForm(null); toast.success(t(mode === 'replace' ? 'num.replaced' : 'num.saved')); };
  return (
    <section className="erp-stack" aria-labelledby="num-detail-title">
      <h2 id="num-detail-title" className="erp-h3"><bdi className="erp-code">{type.code}</bdi></h2>
      <p className="erp-small erp-muted">
        {t(type.assign === 'post' ? 'num.type.assign.post' : 'num.type.assign.create')}
        {type.gapFreeAllowed && <> · {t('num.type.gapFreeAllowed')}</>}
        {type.overrideAllowed && <> · {t('num.type.override')}</>}
      </p>
      {active.length === 0 && <Banner tone="info">{t('num.scheme.none', { pattern: type.defaultPattern })}</Banner>}
      {active.map((s) => (
        <SchemeCard key={s.id} scheme={s} fiscalStartMonth={fiscalStartMonth} branchName={branchOptions.find((b) => b.id === s.branchId)?.code ?? ''} companyCode={me.companies.find((c) => c.id === companyId)?.code ?? ''}
          onChange={(mode) => setForm({ mode, scheme: s })} />
      ))}
      {canCreate && (
        <div className="erp-row erp-wrap">
          {!companyWide && <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={() => setForm({ mode: 'new' })}>{t('num.action.new')}</Button>}
          {type.hasBranch && !type.tenantWide && branchOptions.length > 0 && <Button variant="secondary" onPress={() => setForm({ mode: 'new' })}>{t('num.action.newBranch')}</Button>}
        </div>
      )}
      <h3 className="erp-h3">{t('num.scheme.retired.title')}</h3>
      {retired.length === 0 ? <p className="erp-small erp-muted">{t('num.scheme.retired.none')}</p> : (
        <ul className="erp-config-list" aria-label={t('num.scheme.retired.title')}>
          {retired.map((s) => <li key={s.id} className="erp-small"><span className="erp-code"><bdi>{s.pattern}</bdi></span> · {t(`num.reset.${s.resetRule}` as MessageKey)}</li>)}
        </ul>
      )}
      {form && (
        <SchemeForm mode={form.mode} type={type} typeName={type.code} companyId={companyId} branches={branchOptions} fiscalStartMonth={fiscalStartMonth}
          {...(form.scheme ? { scheme: form.scheme } : {})} onClose={() => setForm(null)} onDone={done} />
      )}
    </section>
  );
}

function SchemeCard({ scheme, fiscalStartMonth, branchName, companyCode, onChange }: {
  scheme: SchemeRow; fiscalStartMonth: number; branchName: string; companyCode: string; onChange: (mode: FormMode) => void;
}) {
  const { t } = useText();
  const { can, writable, me } = useShell();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refused = useRefused();
  const [reporting, setReporting] = useState(false);
  const [retiring, setRetiring] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const canReport = can('cfg.numbering.void_report');
  const report = useQuery({ queryKey: ['cfg', 'numbering', 'report', scheme.id, '', ''], queryFn: () => numberingApi.voidReport(scheme.id), enabled: canReport, retry: false });
  const used: boolean | null = report.isSuccess ? report.data.issued.length > 0 : null;
  const next = report.isSuccess
    ? nextNumber({ pattern: scheme.pattern, reset: scheme.resetRule, start: scheme.startValue, ranges: report.data.issued, today: todayIn(me.timeZone), fiscalStartMonth, company: companyCode, branch: branchName })
    : null;
  const last = report.isSuccess ? report.data.issued.at(-1) : undefined;
  const canUpdate = writable && can('cfg.numbering_schemes.update');
  const canRetire = writable && can('cfg.numbering_schemes.retire');
  const retire = async () => {
    try {
      await numberingApi.retire(scheme.id, scheme.rowVersion);
      await queryClient.invalidateQueries({ queryKey: ['cfg', 'numbering'] });
      toast.success(t('num.retired'));
      setRetiring(false);
    } catch (error) {
      setFailure(refused(error).summary);
      setRetiring(false);
    }
  };
  return (
    <div className="erp-config-card">
      <span className="erp-stack">
        <span className="erp-settings-name">{scheme.branchId ? t('num.scheme.branch', { branch: branchName }) : t('num.scheme.company')}</span>
        <span className="erp-code"><bdi>{scheme.pattern}</bdi></span>
      </span>
      <dl className="erp-num-facts">
        <div><dt>{t('num.scheme.reset')}</dt><dd>{t(`num.reset.${scheme.resetRule}` as MessageKey)}</dd></div>
        <div><dt>{t('num.scheme.gapFree')}</dt><dd>{t(scheme.gapFree ? 'num.scheme.gapFree' : 'num.scheme.gapAllowed')}</dd></div>
        <div><dt>{t('num.scheme.start')}</dt><dd>{scheme.startValue}</dd></div>
        <div><dt>{t('num.scheme.next')}</dt><dd>{next ? <span className="erp-code"><bdi>{next}</bdi></span> : t('num.scheme.next.unknown')}</dd></div>
      </dl>
      {report.isSuccess && <p className="erp-small erp-muted">{last ? t('num.scheme.last', { number: `⁦${last.lastNumber}⁩` }) : t('num.scheme.unused')}</p>}
      <p className="erp-small erp-muted">{t(scheme.gapFree ? 'num.scheme.next.note.gapFree' : 'num.scheme.next.note.gaps')}</p>
      {used === true && <p className="erp-small">{t('num.explain.used')}</p>}
      {failure && <Banner tone="danger">{failure}</Banner>}
      <div className="erp-config-actions">
        {canUpdate && used !== true && <Button variant="secondary" aria-label={t('num.action.edit.aria', { pattern: scheme.pattern })} onPress={() => onChange('edit')}>{t('num.action.edit')}</Button>}
        {canUpdate && can('cfg.numbering_schemes.create') && used !== false && <Button variant="secondary" aria-label={t('num.action.replace.aria', { pattern: scheme.pattern })} onPress={() => onChange('replace')}>{t('num.action.replace')}</Button>}
        {canRetire && <Button variant="quiet" aria-label={t('num.action.retire.aria', { pattern: scheme.pattern })} onPress={() => setRetiring(true)}>{t('num.action.retire')}</Button>}
        {canReport && <Button variant="quiet" aria-label={t('num.action.report.aria', { pattern: scheme.pattern })} onPress={() => setReporting(true)}>{t('num.action.report')}</Button>}
      </div>
      {reporting && <VoidReportDialog scheme={scheme} onClose={() => setReporting(false)} />}
      {retiring && (
        <Dialog title={t('num.retire.title')} role="alertdialog" isOpen onOpenChange={(open) => { if (!open) setRetiring(false); }}
          actions={() => (<><Button variant="secondary" onPress={() => setRetiring(false)}>{t('common.cancel')}</Button><Button variant="danger" onPress={() => { void retire(); }}>{t('num.retire.confirm')}</Button></>)}>
          <p>{t('num.retire.body')}</p>
        </Dialog>
      )}
    </div>
  );
}
