// "Which policy applies?" (slice S1.23a Build item 6; FR-HRM-POL-03, ruling Q5): pick an employee, a policy type and a date; the server's
// resolver answers the policy version in force and why: the rule that matched, every other rule with each condition met or not and the
// employee's own value, the layer (pack or customer), or the reason no policy applies. Nothing is decided on the screen.
import { useCallback, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, ComboBoxField, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, type Option, ScrollRegion } from '@erp/ui';
import { isolate, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { policiesApi, type AssignmentEvaluation, type PolicyType, type Resolution } from '../../api/policies';
import { nameOf } from '../../api/workflow';
import { CompanyPicker, DateInput, Sections, attributeKey, useActualText, useCompany, useConditionText, useDay, useParamsState, useTypes } from './policies-common';
import { todayIn } from './policies-model';
import { ParameterForm } from './policy-form';
import { useShell } from '../../shell/context';
import './policies-messages';
import '../admin/users.css';
import '../admin/configure.css';
import './policies.css';

export default function ResolutionScreen() {
  const { t } = useText();
  useDocumentTitle(t('pol.res.title'));
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  const companyId = useCompany();
  const { params, patch } = useParamsState();
  const types = useTypes();
  const [employee, setEmployee] = useState<Option | null>(null);
  const typeCode = params.get('type') ?? types.data?.items[0]?.code ?? '';
  const [date, setDate] = useState<string | null>(todayIn(me.timeZone));
  const [asked, setAsked] = useState<{ employmentId: string; type: string; date: string | null } | null>(null);
  const employments = useQuery({ queryKey: ['hrm', 'policy-employments', companyId], queryFn: () => policiesApi.employments(companyId), enabled: !!companyId, staleTime: 60_000 });
  const search = useCallback(async (text: string) => {
    const needle = text.trim().toLowerCase();
    return (employments.data ?? []).filter((e) => !needle || e.employeeNo.toLowerCase().includes(needle)).slice(0, 50)
      .map((e) => ({ id: e.id, label: e.employeeNo, detail: t(`pol.res.status.${e.status}` as MessageKey) }));
  }, [employments.data, t]);
  const answer = useQuery({ queryKey: ['hrm', 'policy-resolution', asked], queryFn: () => (asked ? policiesApi.resolve(asked.employmentId, asked.type, asked.date) : Promise.reject(new Error('nothing asked'))), enabled: asked !== null, retry: false });
  const typeOptions: Option[] = (types.data?.items ?? []).map((x) => ({ id: x.code, label: nameOf(x.name, locale) || x.code }));
  const type = types.data?.items.find((x) => x.code === (asked?.type ?? typeCode));
  return (
    <div className="erp-page">
      <PageHeader title={t('pol.res.title')} description={t('pol.res.description')} />
      <Sections />
      <div className="erp-pol-filters">
        <CompanyPicker onChange={() => { setEmployee(null); setAsked(null); }} />
        <ComboBoxField label={t('pol.res.employee')} help={t('pol.res.employee.help')} search={search} value={employee} onChange={setEmployee} />
        {typeOptions.length > 0 && <SelectField label={t('pol.type')} options={typeOptions} value={typeCode} onChange={(id) => patch({ type: id })} />}
        <DateInput label={t('pol.res.date')} value={date} onChange={setDate} />
        <Button variant="primary" isDisabled={!employee || !typeCode} onPress={() => { if (employee) setAsked({ employmentId: employee.id, type: typeCode, date }); }}>{t('pol.res.show')}</Button>
      </div>
      {types.isError && <ErrorPanel message={t('pol.loadError')} action={<Button variant="primary" onPress={() => { void types.refetch(); }}>{t('common.retry')}</Button>} />}
      {!asked && <EmptyState title={t('pol.res.pick')}>{t('pol.res.pick.body')}</EmptyState>}
      {asked && answer.isPending && <Skeleton lines={6} />}
      {asked && answer.isError && (
        <ErrorPanel message={answer.error instanceof ApiProblem && answer.error.status === 403 ? t('pol.denied') : answer.error instanceof ApiProblem && answer.error.status === 404 ? t('pol.res.notFound') : t('pol.loadError')}
          reference={answer.error instanceof ApiProblem ? answer.error.reference : null} action={<Button variant="primary" onPress={() => { void answer.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {answer.isSuccess && <Answer resolution={answer.data} type={type} />}
    </div>
  );
}

function Answer({ resolution, type }: { resolution: Resolution; type: PolicyType | undefined }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const day = useDay();
  const who = resolution.employee;
  const heading = t('pol.res.for', { employee: isolate(`${who.employeeNo} ${who.name}`.trim()), date: day(resolution.asOf) });
  return (
    <section className="erp-stack" aria-labelledby="pol-answer" aria-live="polite">
      <h2 id="pol-answer" className="erp-h3">{heading}</h2>
      {resolution.outcome === 'matched'
        ? resolution.matches.map((m) => (
          <div key={m.policyId} className="erp-config-card">
            <Banner tone="success" title={t('pol.res.applies', { policy: isolate(nameOf(m.policyName, locale) || m.policyCode), version: m.version })}>
              {t('pol.res.because', { order: m.precedence })} · {t(`pol.res.layer.${m.layer}` as MessageKey)} · {m.validTo ? t('pol.res.period', { from: day(m.validFrom), to: day(m.validTo) }) : t('pol.res.since', { from: day(m.validFrom) })}
            </Banner>
            {type ? <ParameterForm schema={type.schema} value={m.parameters} readOnly /> : <pre className="erp-code">{JSON.stringify(m.parameters, null, 2)}</pre>}
          </div>
        ))
        : <Banner tone="warning" title={t('pol.res.none')}>{t(`pol.res.reason.${resolution.reason ?? 'no_rule_matched'}` as MessageKey)}</Banner>}
      <Attributes resolution={resolution} />
      <h3 className="erp-h3">{t('pol.res.rules')}</h3>
      {resolution.evaluations.length === 0 ? <p className="erp-small erp-muted">{t('pol.res.rules.none')}</p> : (
        <ol className="erp-config-list" aria-label={t('pol.res.rules')}>
          {resolution.evaluations.map((e) => <Evaluation key={e.assignmentId} evaluation={e} resolution={resolution} />)}
        </ol>
      )}
    </section>
  );
}

const OUTCOME_STATUS = { matched: 'approved', not_matched: 'rejected', not_reached: 'draft', no_version: 'blocked' } as const;

function Evaluation({ evaluation: e, resolution }: { evaluation: AssignmentEvaluation; resolution: Resolution }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const sentence = useConditionText();
  const actual = useActualText();
  return (
    <li className="erp-config-card">
      <span className="erp-row erp-wrap">
        <span className="erp-pol-name">{t('pol.assign.order', { n: e.precedence })} · <bdi>{nameOf(e.policyName, locale) || e.policyCode}</bdi></span>
        <StatusChip status={OUTCOME_STATUS[e.outcome]} label={t(`pol.res.outcome.${e.outcome}` as MessageKey)} />
      </span>
      {e.conditions.length === 0 ? <p className="erp-small">{t('pol.rule.everyone')}</p> : (
        <ScrollRegion className="erp-table-scroll" label={t('pol.res.conditions', { order: e.precedence })}>
          <table className="erp-table" aria-label={t('pol.res.conditions', { order: e.precedence })}>
            <thead><tr><th scope="col">{t('pol.res.col.condition')}</th><th scope="col">{t('pol.res.col.employee')}</th><th scope="col">{t('pol.res.col.met')}</th></tr></thead>
            <tbody>
              {e.conditions.map((c, i) => (
                <tr key={`${c.attribute}:${c.field ?? ''}:${i}`}>
                  <th scope="row">{sentence(c, resolution.names)}</th>
                  <td><bdi>{actual(c, resolution.names)}</bdi></td>
                  <td className={c.met ? 'erp-pol-met' : 'erp-pol-not-met'}>{t(c.met ? 'pol.res.met' : 'pol.res.notMet')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </ScrollRegion>
      )}
    </li>
  );
}

function Attributes({ resolution }: { resolution: Resolution }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const a = resolution.attributes;
  const record = (id: string | null) => {
    if (!id) return t('pol.value.none');
    const r = resolution.names.records[id];
    return r ? `${nameOf(r.name, locale) || r.code} (${r.code})` : id;
  };
  const type = resolution.names.employeeTypes.find((e) => e.code === a.employeeType);
  const rows = useMemo(() => [
    ['employee_type', a.employeeType === null ? t('pol.value.hidden') : type ? nameOf(type.name, locale) || a.employeeType : a.employeeType],
    ['unit', record(a.orgUnitId)], ['branch', record(a.branchId)], ['location', record(a.locationId)], ['grade', record(a.gradeId)],
  ] as const, [a, type, locale]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <details className="erp-config-card">
      <summary>{t('pol.res.attributes')}</summary>
      {!a.hasAssignment && <p className="erp-small">{t('pol.res.reason.no_assignment_row')}</p>}
      <dl className="erp-num-facts">
        {rows.map(([attribute, value]) => <div key={attribute}><dt>{t(attributeKey(attribute))}</dt><dd><bdi>{value}</bdi></dd></div>)}
      </dl>
      {(resolution.currentValues?.length ?? 0) > 0 && <p className="erp-small erp-muted">{t('pol.res.currentValues')}</p>}
    </details>
  );
}
