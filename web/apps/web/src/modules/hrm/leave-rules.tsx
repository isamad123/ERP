// Leave rules (slice S1.33b Build item 6; FR-HRM-LVE-03): the rules of the company's `leave` policy, one block for each leave type the policy lists, shown as grouped sections made from the policy
// type's schema. A version in force is read-only; "Start a new draft" copies it and HR edits and saves the draft (the server checks it against the schema and every refusal is shown beside its
// field). Activation, the effective date and approval stay in Policies (S1.23). "Who does this apply to" shows the assignment set in words and the employees who would get no leave policy.
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, EmptyState, ErrorPanel, Link, PageHeader, SelectField, Skeleton, StatusChip, useToast, type Option } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { policiesApi, type AssignmentSet, type JsonObject, type ParamSchema, type PolicyVersion, type PolicyView } from '../../api/policies';
import { leaveTypesApi, type LeaveTypeRow } from '../../api/leave-types';
import { nameOf } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { useRefused } from '../admin/config-parts';
import { CompanyPicker, POLICIES_PATH, RESOLUTION_PATH, useCompany, useConditionText, useDay } from '../hr/policies-common';
import { draftOf, newestFirst, toInputs } from '../hr/policies-model';
import { isDenied } from './live-parts';
import { RuleBlocks } from './leave-rules-parts';
import '../admin/config-messages';
import './leave-types-messages';
import './leave-types.css';

const POLICY_TYPE = 'leave';
const SHOWN = 20;

export default function LeaveRulesScreen() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { can, writable } = useShell();
  useDocumentTitle(t('lrl.title'));
  const companyId = useCompany();
  const allowed = can('hrm.policies.read');
  const canManage = writable && can('hrm.policies.manage', companyId);
  const types = useQuery({ queryKey: ['hrm', 'policy-types'], queryFn: policiesApi.types, staleTime: 300_000, enabled: allowed });
  const type = types.data?.items.find((x) => x.code === POLICY_TYPE);
  const list = useQuery({ queryKey: ['hrm', 'policies', companyId], queryFn: () => policiesApi.list(companyId), enabled: allowed && !!companyId, refetchOnMount: 'always' });
  const policies = (list.data ?? []).filter((p) => p.policyType === POLICY_TYPE);
  const [chosen, setChosen] = useState('');
  const policyId = policies.some((p) => p.id === chosen) ? chosen : policies.find((p) => p.isActive)?.id ?? policies[0]?.id ?? '';
  const error = list.error ?? types.error;

  if (!allowed) return <div className="erp-page"><PageHeader title={t('lrl.title')} /><EmptyState title={t('lrl.denied')} /></div>;
  const policyOptions: Option[] = policies.map((p) => ({ id: p.id, label: nameOf(p.nameI18n, locale) || p.code, detail: p.code }));
  return (
    <div className="erp-page">
      <PageHeader title={t('lrl.title')} description={t('lrl.description')} />
      <div className="erp-stack">
        {!writable && <Banner tone="info">{t('lrl.readOnly')}</Banner>}
        <div className="erp-ltype-filters">
          <CompanyPicker onChange={() => setChosen('')} />
          {policies.length > 1 && <SelectField label={t('lrl.policy')} options={policyOptions} value={policyId} onChange={setChosen} />}
        </div>
        {(list.isPending || types.isPending) && !error && <Skeleton lines={6} />}
        {error && (
          <ErrorPanel title={t('lrl.loadError')} message={isDenied(error) ? t('lrl.denied') : t('lrl.loadError.body')}
            action={isDenied(error) ? null : <Button variant="primary" onPress={() => { void list.refetch(); void types.refetch(); }}>{t('common.retry')}</Button>} />
        )}
        {types.isSuccess && !type && <EmptyState title={t('lrl.noType')}>{t('lrl.noType.body')}</EmptyState>}
        {list.isSuccess && types.isSuccess && type && policies.length === 0 && (
          <EmptyState title={t('lrl.empty')} action={<Link href={`${POLICIES_PATH}?company=${encodeURIComponent(companyId)}&type=${POLICY_TYPE}`}>{t('lrl.empty.link')}</Link>}>{t('lrl.empty.body')}</EmptyState>
        )}
        {type && policyId && <PolicyRules key={policyId} policyId={policyId} companyId={companyId} schema={type.schema} canManage={canManage} />}
        {type && policies.length > 0 && <AppliesTo companyId={companyId} policyId={policyId} canManage={canManage} />}
      </div>
    </div>
  );
}

function PolicyRules({ policyId, companyId, schema, canManage }: { policyId: string; companyId: string; schema: ParamSchema; canManage: boolean }) {
  const { t } = useText();
  const day = useDay();
  const toast = useToast();
  const refused = useRefused();
  const queryClient = useQueryClient();
  const detail = useQuery({ queryKey: ['hrm', 'policy', policyId], queryFn: () => policiesApi.detail(policyId), refetchOnMount: 'always' });
  const leaveTypes = useQuery({ queryKey: ['hrm', 'leave-types-admin', companyId], queryFn: ({ signal }) => leaveTypesApi.list(companyId, signal), enabled: !!companyId });
  const [view, setView] = useState('');
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const policy: PolicyView | undefined = detail.data;
  const versions = policy ? newestFirst(policy.versions) : [];
  const draft = versions.find((v) => v.status === 'draft');
  const shown: PolicyVersion | undefined = versions.find((v) => v.id === view) ?? draft ?? versions.find((v) => v.inForce) ?? versions[0];
  const editable = canManage && !!policy?.isActive && shown?.status === 'draft';
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['hrm', 'policy', policyId] });
    await queryClient.invalidateQueries({ queryKey: ['hrm', 'policies'] });
  };
  const startDraft = async () => {
    setBusy(true);
    setFailure(null);
    try {
      const created = await policiesApi.newDraft(policyId);
      await refresh();
      setView(created.id);
      toast.success(t('lrl.draft.created'));
    } catch (e) {
      setFailure(refused(e).summary);
    } finally {
      setBusy(false);
    }
  };
  const options: Option[] = versions.map((v) => ({ id: v.id, label: t('pol.version.option', { n: v.version, status: t(`pol.status.${v.status}` as MessageKey) }) }));
  if (detail.isPending) return <Skeleton lines={8} />;
  if (detail.isError || !policy) return <ErrorPanel title={t('lrl.loadError')} message={isDenied(detail.error) ? t('lrl.denied') : t('lrl.loadError.body')} action={<Button variant="primary" onPress={() => { void detail.refetch(); }}>{t('common.retry')}</Button>} />;
  const link = `${POLICIES_PATH}/${policy.id}?company=${encodeURIComponent(policy.companyId)}`;
  return (
    <section className="erp-stack" aria-labelledby="lrl-rules">
      <div className="erp-lrule-head">
        <h2 id="lrl-rules" className="erp-lrule-title">{t('lrl.rules.title')}</h2>
        <Link href={link}>{t('lrl.activate')}</Link>
      </div>
      {!policy.isActive && <Banner tone="info">{t('pol.retired.note')}</Banner>}
      {failure && <Banner tone="danger">{failure}</Banner>}
      <div className="erp-ltype-filters">
        {options.length > 1 && <SelectField label={t('lrl.version')} options={options} value={shown?.id ?? ''} onChange={setView} />}
      </div>
      {shown && (
        <p className="erp-small erp-muted">
          {t('pol.version.n', { n: shown.version })} · {t(`pol.status.${shown.status}` as MessageKey)}
          {shown.inForce && <> <StatusChip status="active" label={t('pol.version.inForce')} /></>}
          {shown.validFrom ? ` · ${t('lrl.version.from', { date: day(shown.validFrom) })}` : ''}
        </p>
      )}
      {shown?.status === 'active' && <Banner tone="info">{t('lrl.readonly.active')}</Banner>}
      {canManage && policy.isActive && !draft && <div className="erp-row"><Button variant="primary" isPending={busy} onPress={() => { void startDraft(); }}>{t('lrl.draft.new')}</Button></div>}
      {shown?.status === 'draft' && <Banner tone="info">{t('lrl.draft.activate')}</Banner>}
      {shown
        ? <VersionRules key={`${shown.id}:${shown.rowVersion}`} version={shown} schema={schema} leaveTypes={leaveTypes.data ?? null} editable={editable} onSaved={refresh} />
        : <EmptyState title={t('lrl.versions.none')} />}
    </section>
  );
}

function VersionRules({ version, schema, leaveTypes, editable, onSaved }: {
  version: PolicyVersion; schema: ParamSchema; leaveTypes: LeaveTypeRow[] | null; editable: boolean; onSaved: () => Promise<void>;
}) {
  const { t } = useText();
  const toast = useToast();
  const refused = useRefused();
  const [parameters, setParameters] = useState<JsonObject>(version.parameters);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const changed = JSON.stringify(parameters) !== JSON.stringify(version.parameters);
  const save = async () => {
    setBusy(true);
    setSummary(null);
    try {
      await policiesApi.editDraft(version.id, parameters, version.rowVersion);
      setErrors({});
      await onSaved();
      toast.success(t('lrl.draft.saved'));
    } catch (e) {
      const r = refused(e);
      setErrors(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="erp-stack">
      {summary && <Banner tone="danger">{summary}</Banner>}
      <RuleBlocks schema={schema} parameters={parameters} leaveTypes={leaveTypes} errors={errors} {...(editable ? { onChange: setParameters } : {})} />
      {editable && (
        <div className="erp-row erp-wrap">
          <Button variant="primary" isPending={busy} isDisabled={!changed} onPress={() => { void save(); }}>{t('lrl.draft.save')}</Button>
          {changed && <span className="erp-small erp-muted">{t('lrl.draft.unsaved')}</span>}
        </div>
      )}
    </div>
  );
}

/** "Who does this apply to": the assignment set of the leave policies in words, and the current employees who would get no leave policy (the S1.23 check; managers of policies only). */
function AppliesTo({ companyId, policyId, canManage }: { companyId: string; policyId: string; canManage: boolean }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const sentence = useConditionText();
  const set = useQuery({ queryKey: ['hrm', 'policy-set', companyId, POLICY_TYPE, ''], queryFn: () => policiesApi.set(companyId, POLICY_TYPE), enabled: !!companyId, refetchOnMount: 'always' });
  const unmatched = useQuery({
    queryKey: ['hrm', 'leave-rules-unmatched', companyId, set.data?.latestFrom ?? '', set.data?.assignments.map((a) => a.id).join(',') ?? ''],
    queryFn: () => unmatchedOf(companyId, set.data as AssignmentSet),
    enabled: canManage && set.isSuccess,
  });
  return (
    <section className="erp-stack" aria-labelledby="lrl-applies">
      <h2 id="lrl-applies" className="erp-lrule-title">{t('lrl.applies.title')}</h2>
      <p className="erp-small erp-muted">{t('lrl.applies.help')}</p>
      {set.isPending && <Skeleton lines={3} />}
      {set.isError && <ErrorPanel message={isDenied(set.error) ? t('lrl.denied') : t('lrl.loadError.body')} action={<Button variant="primary" onPress={() => { void set.refetch(); }}>{t('common.retry')}</Button>} />}
      {set.isSuccess && set.data.assignments.length === 0 && <EmptyState title={t('pol.assign.empty')}>{t('pol.assign.empty.body')}</EmptyState>}
      {set.isSuccess && set.data.assignments.length > 0 && (
        <ol className="erp-config-list" aria-label={t('pol.assign.list')}>
          {set.data.assignments.map((a) => (
            <li key={a.id} className="erp-config-card">
              <span className="erp-pol-name">
                {t('pol.assign.order', { n: a.precedence })} · <bdi>{nameOf(a.policyName, locale) || a.policyCode}</bdi> <span className="erp-code erp-small"><bdi>{a.policyCode}</bdi></span>
                {a.policyId === policyId && <> <StatusChip status="active" label={t('lrl.applies.this')} /></>}
              </span>
              {(a.rule.conditions ?? []).length === 0
                ? <p className="erp-small">{t('pol.rule.everyone')}</p>
                : <ul className="erp-pol-conditions" aria-label={t('pol.rule.conditions')}>{(a.rule.conditions ?? []).map((c, i) => <li key={`${c.attribute}:${c.field ?? ''}:${i}`}>{i > 0 && <span className="erp-muted">{t('pol.rule.and')} </span>}{sentence(c, set.data.names)}</li>)}</ul>}
            </li>
          ))}
        </ol>
      )}
      {!canManage && set.isSuccess && <p className="erp-small erp-muted">{t('lrl.applies.needsManage')}</p>}
      {canManage && unmatched.isPending && set.isSuccess && <Skeleton lines={2} />}
      {canManage && unmatched.isError && <p className="erp-small erp-muted" role="status">{t('lrl.applies.unmatched.error')}</p>}
      {unmatched.data && unmatched.data.count > 0 && (
        <Banner tone="warning" title={t('pol.check.unmatched', { count: unmatched.data.count })}>
          <p>{t('lrl.applies.unmatched.note')}</p>
          <ul className="erp-config-list" aria-label={t('pol.check.unmatched.list')}>
            {unmatched.data.items.slice(0, SHOWN).map((u) => <li key={u.employmentId} className="erp-small"><bdi className="erp-code">{u.employeeNo}</bdi> <bdi>{u.name}</bdi> · {t(`pol.res.reason.${u.reason}` as MessageKey)}</li>)}
          </ul>
          {unmatched.data.count - (unmatched.data.notShown ?? 0) > Math.min(SHOWN, unmatched.data.items.length) && (
            <p className="erp-small">{t('pol.check.unmatched.more', { count: unmatched.data.count - (unmatched.data.notShown ?? 0) - Math.min(SHOWN, unmatched.data.items.length) })}</p>
          )}
          {(unmatched.data.notShown ?? 0) > 0 && <p className="erp-small">{t('pol.check.unmatched.notShown', { count: unmatched.data.notShown ?? 0 })}</p>}
        </Banner>
      )}
      {unmatched.data && unmatched.data.count === 0 && <p className="erp-small erp-muted">{t('pol.check.allCovered')}</p>}
      <p><Link href={`${RESOLUTION_PATH}?company=${encodeURIComponent(companyId)}`}>{t('lrl.applies.resolution')}</Link></p>
    </section>
  );
}

/** The S1.23 check of the set in force as it stands (nothing is changed or saved): its list of employees who would get no policy. */
async function unmatchedOf(companyId: string, set: AssignmentSet) {
  const answer = await policiesApi.check({ companyId, policyType: POLICY_TYPE, effectiveFrom: null, basedOn: set.latestFrom, assignments: toInputs(draftOf(set.assignments)) });
  return answer.unmatched ?? { count: 0, items: [] };
}
