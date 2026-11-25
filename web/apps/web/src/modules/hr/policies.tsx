// HR policies (slice S1.23a Build items 2 and 6; FR-HRM-POL-02, FR-HRM-POL-06, ruling Q1): the policies of a company by type, each with its
// versions (draft, in force from a date, retired), the difference between any two versions, the draft's parameters as a form made from the
// type's schema, activation from a date (held for a second person when the customer asks for it), and retirement. Assignments and
// "Which policy applies?" are their own sections. The server checks every request again (permission, company scope, schema, dates).
import { useMemo, useState } from 'react';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { Plus } from 'lucide-react';
import { Banner, Button, Dialog, EmptyState, ErrorPanel, Link, PageHeader, SelectField, Skeleton, StatusChip, TextField, useToast, type Option, ScrollRegion } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { policiesApi, type JsonObject, type PolicyRow, type PolicyType, type PolicyVersion, type PolicyView } from '../../api/policies';
import { nameOf, type Names } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { NamePair, cleanNames, useRefused } from '../admin/config-parts';
import AssignmentsPage from './policy-assignments';
import { CompanyPicker, DateInput, POLICIES_PATH, Sections, useCompany, useDay, useParamsState, useTypeName, useTypes } from './policies-common';
import { defaultFor, diffParameters, newestFirst, showValue } from './policies-model';
import { ParameterForm } from './policy-form';
import './policies-messages';
import '../admin/users.css';
import '../admin/configure.css';
import './policies.css';

export default function PoliciesScreen() {
  return (
    <Routes>
      <Route index element={<PolicyList />} />
      <Route path="assignments" element={<AssignmentsPage />} />
      <Route path=":id" element={<PolicyPage />} />
    </Routes>
  );
}

function Problem({ error, retry }: { error: unknown; retry: () => void }) {
  const { t } = useText();
  const problem = error instanceof ApiProblem ? error : null;
  return <ErrorPanel message={problem?.status === 403 ? t('pol.denied') : t('pol.loadError')} reference={problem?.reference ?? null}
    action={<Button variant="primary" onPress={retry}>{t('common.retry')}</Button>} />;
}

function PolicyList() {
  const { t } = useText();
  useDocumentTitle(t('pol.title'));
  const { locale } = useLocaleSettings();
  const { can, writable } = useShell();
  const navigate = useNavigate();
  const companyId = useCompany();
  const { params, patch } = useParamsState();
  const typeFilter = params.get('type') ?? '';
  const types = useTypes();
  const typeName = useTypeName();
  const list = useQuery({ queryKey: ['hrm', 'policies', companyId], queryFn: () => policiesApi.list(companyId), enabled: !!companyId, refetchOnMount: 'always' });
  const [creating, setCreating] = useState(false);
  const rows = (list.data ?? []).filter((p) => !typeFilter || p.policyType === typeFilter);
  const typeOptions: Option[] = [{ id: '', label: t('pol.type.all') }, ...(types.data?.items ?? []).map((x) => ({ id: x.code, label: nameOf(x.name, locale) || x.code }))];
  const canManage = writable && can('hrm.policies.manage', companyId);
  return (
    <div className="erp-page">
      <PageHeader title={t('pol.title')} description={t('pol.description')}
        actions={canManage && types.isSuccess ? <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={() => setCreating(true)}>{t('pol.new')}</Button> : undefined} />
      <Sections />
      <div className="erp-pol-filters">
        <CompanyPicker />
        <SelectField label={t('pol.type')} options={typeOptions} value={typeFilter} onChange={(id) => patch({ type: id || null })} />
      </div>
      {(list.isPending || types.isPending) && !list.isError && !types.isError && <Skeleton lines={6} />}
      {(list.isError || types.isError) && <Problem error={list.error ?? types.error} retry={() => { void list.refetch(); void types.refetch(); }} />}
      {list.isSuccess && types.isSuccess && rows.length === 0 && <EmptyState title={t('pol.empty')}>{t('pol.empty.body')}</EmptyState>}
      {list.isSuccess && types.isSuccess && rows.length > 0 && (
        <ScrollRegion className="erp-table-scroll" label={t('pol.list')}>
          <table className="erp-table" aria-label={t('pol.list')}>
            <thead><tr>
              <th scope="col">{t('pol.col.code')}</th><th scope="col">{t('pol.col.name')}</th><th scope="col">{t('pol.col.type')}</th>
              <th scope="col">{t('pol.col.source')}</th><th scope="col">{t('pol.col.state')}</th><th scope="col"><span className="erp-visually-hidden">{t('pol.col.actions')}</span></th>
            </tr></thead>
            <tbody>
              {rows.map((p: PolicyRow) => (
                <tr key={p.id}>
                  <th scope="row"><span className="erp-code"><bdi>{p.code}</bdi></span></th>
                  <td>{nameOf(p.nameI18n, locale)}</td>
                  <td>{typeName(p.policyType)}</td>
                  <td>{t(`pol.layer.${p.source}` as MessageKey)}</td>
                  <td><StatusChip status={p.isActive ? 'active' : 'inactive'} label={t(p.isActive ? 'pol.state.active' : 'pol.state.retired')} /></td>
                  <td><Button variant="secondary" aria-label={t('pol.open.aria', { code: p.code })} onPress={() => navigate(`${POLICIES_PATH}/${p.id}?company=${encodeURIComponent(companyId)}`)}>{t('pol.open')}</Button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </ScrollRegion>
      )}
      {creating && types.data && <CreateDialog companyId={companyId} types={types.data.items} initialType={typeFilter} onClose={() => setCreating(false)}
        onCreated={(id) => { setCreating(false); navigate(`${POLICIES_PATH}/${id}?company=${encodeURIComponent(companyId)}`); }} />}
    </div>
  );
}

function CreateDialog({ companyId, types, initialType, onClose, onCreated }: { companyId: string; types: PolicyType[]; initialType: string; onClose: () => void; onCreated: (id: string) => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const refused = useRefused();
  const queryClient = useQueryClient();
  const [typeCode, setTypeCode] = useState(initialType || types[0]?.code || '');
  const type = types.find((x) => x.code === typeCode);
  const [code, setCode] = useState('');
  const [name, setName] = useState<Names>({ en: '', ur: '' });
  const [parameters, setParameters] = useState<JsonObject>(() => (type ? defaultFor(type.schema) as JsonObject : {}));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    setBusy(true);
    setSummary(null);
    try {
      const created = await policiesApi.create({ companyId, policyType: typeCode, code: code.trim(), name: cleanNames(name), parameters });
      await queryClient.invalidateQueries({ queryKey: ['hrm', 'policies'] });
      onCreated(created.id);
    } catch (error) {
      const r = refused(error);
      setErrors(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog title={t('pol.create.title')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (<><Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button><Button variant="primary" isPending={busy} onPress={() => { void submit(); }}>{t('pol.create.save')}</Button></>)}>
      <div className="erp-stack">
        {summary && <Banner tone="danger">{summary}</Banner>}
        <SelectField label={t('pol.type')} isRequired options={types.map((x) => ({ id: x.code, label: nameOf(x.name, locale) || x.code }))} value={typeCode}
          onChange={(id) => { setTypeCode(id); const next = types.find((x) => x.code === id); setParameters(next ? defaultFor(next.schema) as JsonObject : {}); }} error={errors.policyType ?? null} />
        {type && <p className="erp-small erp-muted">{nameOf(type.description, locale)}</p>}
        <TextField label={t('pol.col.code')} isCode isRequired value={code} onChange={setCode} maxLength={40} error={errors.code ?? null} help={t('pol.create.code.help')} />
        <NamePair label={t('pol.col.name')} value={name} onChange={setName} errors={{ en: errors.name ?? errors['name.en'] }} />
        <h3 className="erp-h3">{t('pol.version.parameters')}</h3>
        <p className="erp-small erp-muted">{t('pol.create.draft')}</p>
        {type && <ParameterForm schema={type.schema} value={parameters} onChange={setParameters} errors={errors} />}
      </div>
    </Dialog>
  );
}

function PolicyPage() {
  const { t } = useText();
  const { id = '' } = useParams();
  const { locale } = useLocaleSettings();
  const { can, writable } = useShell();
  const typeName = useTypeName();
  const types = useTypes();
  const detail = useQuery({ queryKey: ['hrm', 'policy', id], queryFn: () => policiesApi.detail(id), refetchOnMount: 'always' });
  const policy = detail.data;
  useDocumentTitle(policy ? `${policy.code} ${nameOf(policy.name, locale)}` : t('pol.title'));
  const type = types.data?.items.find((x) => x.code === policy?.policyType);
  const canManage = !!policy && writable && policy.isActive && can('hrm.policies.manage', policy.companyId);
  return (
    <div className="erp-page">
      <PageHeader title={policy ? nameOf(policy.name, locale) || policy.code : t('pol.title')}
        breadcrumbs={[{ label: t('pol.title'), href: `${POLICIES_PATH}${policy ? `?company=${encodeURIComponent(policy.companyId)}` : ''}` }, { label: policy?.code ?? '…' }]} />
      {detail.isPending && <Skeleton lines={8} />}
      {detail.isError && (detail.error instanceof ApiProblem && detail.error.status === 404
        ? <EmptyState title={t('pol.notFound')}><Link href={POLICIES_PATH}>{t('pol.back')}</Link></EmptyState>
        : <Problem error={detail.error} retry={() => { void detail.refetch(); }} />)}
      {policy && (
        <div className="erp-stack">
          <p className="erp-small erp-muted">
            <span className="erp-code"><bdi>{policy.code}</bdi></span> · {typeName(policy.policyType)} · {t(`pol.layer.${policy.source}` as MessageKey)} · {t(policy.isActive ? 'pol.state.active' : 'pol.state.retired')}
            {' · '}{t('pol.assignedCount', { count: policy.assignments })}
          </p>
          {!policy.isActive && <Banner tone="info">{t('pol.retired.note')}</Banner>}
          {canManage && <PolicyActions policy={policy} />}
          <Versions policy={policy} type={type} canManage={canManage} />
        </div>
      )}
    </div>
  );
}

function useRefresh(policyId: string) {
  const queryClient = useQueryClient();
  return async () => {
    await queryClient.invalidateQueries({ queryKey: ['hrm', 'policy', policyId] });
    await queryClient.invalidateQueries({ queryKey: ['hrm', 'policies'] });
  };
}

function PolicyActions({ policy }: { policy: PolicyView }) {
  const { t } = useText();
  const toast = useToast();
  const refused = useRefused();
  const refresh = useRefresh(policy.id);
  const [dialog, setDialog] = useState<'rename' | 'retire' | null>(null);
  const [name, setName] = useState<Names>(policy.name);
  const [date, setDate] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [fields, setFields] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const hasDraft = policy.versions.some((v) => v.status === 'draft');
  const run = async (work: () => Promise<unknown>, done: string) => {
    setBusy(true);
    setFailure(null);
    try {
      await work();
      await refresh();
      setDialog(null);
      toast.success(done);
    } catch (error) {
      const r = refused(error);
      setFields(r.fields);
      setFailure(r.summary);
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="erp-row erp-wrap">
      {!hasDraft && <Button variant="primary" onPress={() => { void run(() => policiesApi.newDraft(policy.id), t('pol.draft.created')); }}>{t('pol.draft.new')}</Button>}
      <Button variant="secondary" onPress={() => { setName(policy.name); setFields({}); setFailure(null); setDialog('rename'); }}>{t('pol.rename')}</Button>
      <Button variant="quiet" onPress={() => { setDate(null); setFields({}); setFailure(null); setDialog('retire'); }}>{t('pol.retire')}</Button>
      {failure && dialog === null && <Banner tone="danger">{failure}</Banner>}
      {dialog === 'rename' && (
        <Dialog title={t('pol.rename.title')} isOpen onOpenChange={(open) => { if (!open) setDialog(null); }}
          actions={() => (<><Button variant="secondary" onPress={() => setDialog(null)}>{t('common.cancel')}</Button>
            <Button variant="primary" isPending={busy} onPress={() => { void run(() => policiesApi.rename(policy.id, cleanNames(name), policy.rowVersion), t('pol.renamed')); }}>{t('pol.save')}</Button></>)}>
          <div className="erp-stack">
            {failure && <Banner tone="danger">{failure}</Banner>}
            <NamePair label={t('pol.col.name')} value={name} onChange={setName} errors={{ en: fields.name }} />
          </div>
        </Dialog>
      )}
      {dialog === 'retire' && (
        <Dialog title={t('pol.retire.title')} role="alertdialog" isOpen onOpenChange={(open) => { if (!open) setDialog(null); }}
          actions={() => (<><Button variant="secondary" onPress={() => setDialog(null)}>{t('common.cancel')}</Button>
            <Button variant="danger" isPending={busy} isDisabled={!date} onPress={() => { if (date) void run(() => policiesApi.retire(policy.id, date, policy.rowVersion), t('pol.retired')); }}>{t('pol.retire.confirm')}</Button></>)}>
          <div className="erp-stack">
            <p>{t('pol.retire.body')}</p>
            {failure && <Banner tone="danger">{failure}</Banner>}
            <DateInput label={t('pol.retire.date')} value={date} onChange={setDate} isRequired error={fields.effectiveFrom ?? null} />
          </div>
        </Dialog>
      )}
    </div>
  );
}

function Versions({ policy, type, canManage }: { policy: PolicyView; type: PolicyType | undefined; canManage: boolean }) {
  const { t } = useText();
  const day = useDay();
  const versions = newestFirst(policy.versions);
  const draft = versions.find((v) => v.status === 'draft');
  const done = versions.filter((v) => v.status !== 'draft');
  const date = day;
  return (
    <section className="erp-stack" aria-labelledby="pol-versions">
      <h2 id="pol-versions" className="erp-h3">{t('pol.versions')}</h2>
      {draft && type && <DraftCard policy={policy} version={draft} type={type} canManage={canManage} />}
      {done.length === 0 ? <p className="erp-small erp-muted">{t('pol.versions.none')}</p> : (
        <ScrollRegion className="erp-table-scroll" label={t('pol.versions')}>
          <table className="erp-table" aria-label={t('pol.versions')}>
            <thead><tr>
              <th scope="col">{t('pol.version.col.version')}</th><th scope="col">{t('pol.version.col.status')}</th><th scope="col">{t('pol.version.col.layer')}</th>
              <th scope="col">{t('pol.version.col.from')}</th><th scope="col">{t('pol.version.col.to')}</th>
            </tr></thead>
            <tbody>
              {done.map((v) => (
                <tr key={v.id}>
                  <th scope="row">{t('pol.version.n', { n: v.version })}{v.inForce && <> <StatusChip status="active" label={t('pol.version.inForce')} /></>}</th>
                  <td>{t(`pol.status.${v.status}` as MessageKey)}</td>
                  <td>{t(`pol.layer.${v.layer}` as MessageKey)}{v.isOverridden && <div className="erp-small erp-muted">{t('pol.version.overridden')}</div>}</td>
                  <td>{date(v.validFrom)}</td>
                  <td>{v.validTo ? t('pol.version.until', { date: date(v.validTo) }) : t('pol.version.open')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </ScrollRegion>
      )}
      {done[0] && type && (
        <details className="erp-config-card">
          <summary>{t('pol.version.show', { n: done[0].version })}</summary>
          <ParameterForm schema={type.schema} value={done[0].parameters} readOnly />
        </details>
      )}
      {versions.length >= 2 && <Compare versions={versions} />}
    </section>
  );
}

function DraftCard({ policy, version, type, canManage }: { policy: PolicyView; version: PolicyVersion; type: PolicyType; canManage: boolean }) {
  const { t } = useText();
  const toast = useToast();
  const refused = useRefused();
  const refresh = useRefresh(policy.id);
  const [parameters, setParameters] = useState<JsonObject>(version.parameters);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [activating, setActivating] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [from, setFrom] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [pending, setPending] = useState(false);
  const changed = JSON.stringify(parameters) !== JSON.stringify(version.parameters);
  const attempt = async (work: () => Promise<void>) => {
    setBusy(true);
    setFailure(null);
    try {
      await work();
    } catch (error) {
      const r = refused(error);
      setErrors(r.fields);
      setFailure(r.summary);
    } finally {
      setBusy(false);
    }
  };
  const save = () => attempt(async () => {
    await policiesApi.editDraft(version.id, parameters, version.rowVersion);
    setErrors({});
    await refresh();
    toast.success(t('pol.draft.saved'));
  });
  const activate = () => attempt(async () => {
    if (!from) return;
    const answer = await policiesApi.activate(version.id, from, version.rowVersion, reason.trim() || undefined);
    setActivating(false);
    await refresh();
    if (answer.status === 'pending') { setPending(true); toast.success(t('pol.activate.pending')); } else toast.success(t('pol.activate.done'));
  });
  const remove = () => attempt(async () => {
    await policiesApi.deleteDraft(version.id, version.rowVersion);
    setDeleting(false);
    await refresh();
    toast.success(t('pol.draft.deleted'));
  });
  return (
    <section className="erp-config-card" aria-labelledby="pol-draft">
      <h3 id="pol-draft" className="erp-h3">{t('pol.draft.title', { n: version.version })}</h3>
      {version.isOverridden && <Banner tone="info">{t('pol.draft.override')}</Banner>}
      {pending && <Banner tone="info">{t('pol.activate.pending')}</Banner>}
      {failure && !activating && !deleting && <Banner tone="danger">{failure}</Banner>}
      <ParameterForm schema={type.schema} value={parameters} errors={errors} {...(canManage ? { onChange: setParameters } : {})} readOnly={!canManage} />
      {canManage && (
        <div className="erp-config-actions">
          <Button variant="secondary" isPending={busy} isDisabled={!changed} onPress={() => { void save(); }}>{t('pol.draft.save')}</Button>
          <Button variant="primary" isDisabled={changed} onPress={() => { setFrom(null); setFailure(null); setActivating(true); }}>{t('pol.activate')}</Button>
          <Button variant="quiet" onPress={() => { setFailure(null); setDeleting(true); }}>{t('pol.draft.delete')}</Button>
        </div>
      )}
      {canManage && changed && <p className="erp-small erp-muted">{t('pol.draft.saveFirst')}</p>}
      {activating && (
        <Dialog title={t('pol.activate.title', { n: version.version })} isOpen onOpenChange={(open) => { if (!open) setActivating(false); }}
          actions={() => (<><Button variant="secondary" onPress={() => setActivating(false)}>{t('common.cancel')}</Button>
            <Button variant="primary" isPending={busy} isDisabled={!from} onPress={() => { void activate(); }}>{t('pol.activate.confirm')}</Button></>)}>
          <div className="erp-stack">
            <p>{t('pol.activate.body')}</p>
            {failure && <Banner tone="danger">{failure}</Banner>}
            <DateInput label={t('pol.activate.date')} value={from} onChange={setFrom} isRequired error={errors.effectiveFrom ?? null} />
            <TextField label={t('pol.reason')} help={t('pol.reason.help')} value={reason} onChange={setReason} maxLength={500} />
          </div>
        </Dialog>
      )}
      {deleting && (
        <Dialog title={t('pol.draft.delete.title')} role="alertdialog" isOpen onOpenChange={(open) => { if (!open) setDeleting(false); }}
          actions={() => (<><Button variant="secondary" onPress={() => setDeleting(false)}>{t('common.cancel')}</Button>
            <Button variant="danger" isPending={busy} onPress={() => { void remove(); }}>{t('pol.draft.delete')}</Button></>)}>
          <p>{t('pol.draft.delete.body')}</p>
          {failure && <Banner tone="danger">{failure}</Banner>}
        </Dialog>
      )}
    </section>
  );
}

/** The difference between two versions, key by key (FR-HRM-POL-06 diff view of versions). */
function Compare({ versions }: { versions: PolicyVersion[] }) {
  const { t } = useText();
  const [later, setLater] = useState(versions[0]?.id ?? '');
  const [earlier, setEarlier] = useState(versions[1]?.id ?? '');
  const a = versions.find((v) => v.id === earlier);
  const b = versions.find((v) => v.id === later);
  const lines = useMemo(() => (a && b ? diffParameters(a.parameters, b.parameters) : []), [a, b]);
  const options: Option[] = versions.map((v) => ({ id: v.id, label: t('pol.version.option', { n: v.version, status: t(`pol.status.${v.status}` as MessageKey) }) }));
  return (
    <section className="erp-stack" aria-labelledby="pol-compare">
      <h2 id="pol-compare" className="erp-h3">{t('pol.compare')}</h2>
      <div className="erp-pol-filters">
        <SelectField label={t('pol.compare.earlier')} options={options} value={earlier} onChange={setEarlier} />
        <SelectField label={t('pol.compare.later')} options={options} value={later} onChange={setLater} />
      </div>
      {lines.length === 0 ? <p className="erp-small erp-muted">{t('pol.compare.same')}</p> : (
        <ScrollRegion className="erp-table-scroll" label={t('pol.compare')}>
          <table className="erp-table" aria-label={t('pol.compare')}>
            <thead><tr><th scope="col">{t('pol.compare.value')}</th><th scope="col">{t('pol.compare.before')}</th><th scope="col">{t('pol.compare.after')}</th></tr></thead>
            <tbody>
              {lines.map((l) => (
                <tr key={l.path}>
                  <th scope="row"><span className="erp-code"><bdi>{l.path}</bdi></span> <span className="erp-small erp-muted">{t(`pol.diff.${l.kind}` as MessageKey)}</span></th>
                  <td className={l.kind === 'removed' ? 'erp-pol-diff-removed' : undefined}><bdi>{showValue(l.before)}</bdi></td>
                  <td className={l.kind === 'added' ? 'erp-pol-diff-added' : undefined}><bdi>{showValue(l.after)}</bdi></td>
                </tr>
              ))}
            </tbody>
          </table>
        </ScrollRegion>
      )}
    </section>
  );
}
