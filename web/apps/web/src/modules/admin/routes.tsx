// Approval routes (slice S1.15c Build item 10; FR-KRN-WFL-01 to -03, -10; S1.15 rulings Q9 and the S1.10 ruling Q2): per document type, the versions of its route (draft,
// active, retired) and a step-list editor: approver kind, a condition built from the document's declared fields, a time limit and an escalation. "Check" shows the
// engine's problems (including the refusal of an approver who could be the requester, FR-XM-32); activation waits for a second administrator when the route needs one.
// Requests already running stay on the version they started with, and the screen says how many.
import { useEffect, useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { useDocumentTitle } from '@erp/engine';
import { Plus } from 'lucide-react';
import { Banner, Button, EmptyState, ErrorPanel, PageHeader, Skeleton, StatusChip, useToast } from '@erp/ui';
import { formatDateTime, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { routesApi, type RouteDefinition, type RouteDocumentType, type RouteJson, type RouteProblem, type RouteVersion } from '../../api/routes';
import { users } from '../../api/users';
import { nameOf } from '../../api/workflow';
import { companyName, useShell } from '../../shell/context';
import { StepCard } from './routes-step';
import { canonical, MAX_STEPS, move, newStep, stepOfProblem, stepTitle, tidy, type SentenceContext } from './routes-model';
import { ReasonDialog, useProblemText } from './users-parts';
import './routes-messages';
import './users.css';
import './configure.css';

export default function RoutesScreen() {
  const { t } = useText();
  useDocumentTitle(t('routes.title'));
  const { locale } = useLocaleSettings();
  const [params, setParams] = useSearchParams();
  const open = params.get('route');
  const patch = (changes: Record<string, string | null>) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [k, v] of Object.entries(changes)) { if (v) next.set(k, v); else next.delete(k); }
    return next;
  }, { replace: true });
  const types = useQuery({ queryKey: ['wf', 'types'], queryFn: routesApi.types, refetchOnMount: 'always' });
  const defs = useQuery({ queryKey: ['wf', 'defs'], queryFn: routesApi.list, refetchOnMount: 'always' });
  const problem = [types.error, defs.error].find((e) => e instanceof ApiProblem) as ApiProblem | undefined;
  return (
    <div className="erp-page">
      <PageHeader title={t('routes.title')} description={t('routes.description')} />
      {(types.isPending || defs.isPending) && !problem && <Skeleton lines={6} />}
      {problem && (
        <ErrorPanel message={problem.status === 403 ? t('routes.denied') : t('routes.loadError')} reference={problem.reference}
          action={<Button variant="primary" onPress={() => { void types.refetch(); void defs.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {types.isSuccess && defs.isSuccess && defs.data.length === 0 && <EmptyState title={t('routes.empty')}>{t('routes.empty.body')}</EmptyState>}
      {types.isSuccess && defs.isSuccess && defs.data.length > 0 && (
        <div className="erp-config-split">
          <ul className="erp-config-list" aria-label={t('routes.list.title')}>
            {defs.data.map((d) => {
              const name = nameOf(d.name, locale);
              return (
                <li key={d.id}>
                  <div className="erp-config-card">
                    <span className="erp-stack">
                      <span className="erp-settings-name"><bdi>{name}</bdi></span>
                      <span className="erp-small erp-muted erp-code"><bdi>{d.documentType}</bdi></span>
                    </span>
                    <span className="erp-row erp-wrap">
                      <StatusChip status={d.status === 'active' ? 'active' : 'inactive'} label={t(d.status === 'active' ? 'routes.status.active' : 'routes.status.inactive')} />
                      <span className="erp-small">{d.activeVersion ? t('routes.activeVersion', { n: d.activeVersion }) : t('routes.noActiveVersion')}</span>
                      {d.problems.length > 0 && <StatusChip status="blocked" label={t('routes.problemCount', { count: d.problems.length })} />}
                    </span>
                    <Button variant={open === d.id ? 'primary' : 'secondary'} aria-pressed={open === d.id} aria-label={t('routes.open.aria', { name })} onPress={() => patch({ route: d.id, version: null })}>{t('routes.open')}</Button>
                  </div>
                </li>
              );
            })}
          </ul>
          {open
            ? <RouteDetail key={open} id={open} type={types.data.find((x) => x.code === defs.data.find((d) => d.id === open)?.documentType) ?? null} versionId={params.get('version')} onVersion={(v) => patch({ version: v })} />
            : <EmptyState title={t('routes.pick')}>{t('routes.pick.body')}</EmptyState>}
        </div>
      )}
    </div>
  );
}

function RouteDetail({ id, type, versionId, onVersion }: { id: string; type: RouteDocumentType | null; versionId: string | null; onVersion: (id: string) => void }) {
  const { t } = useText();
  const detail = useQuery({ queryKey: ['wf', 'def', id], queryFn: () => routesApi.get(id), refetchOnMount: 'always' });
  const problem = detail.error instanceof ApiProblem ? detail.error : null;
  if (detail.isPending) return <Skeleton lines={5} />;
  if (detail.isError || !type) {
    return (
      <ErrorPanel message={problem?.status === 403 ? t('routes.denied') : t('routes.loadError')} reference={problem?.reference ?? null}
        action={<Button variant="primary" onPress={() => { void detail.refetch(); }}>{t('common.retry')}</Button>} />
    );
  }
  const { definition, versions } = detail.data;
  const draft = versions.find((v) => v.status === 'draft');
  const shown = versions.find((v) => v.id === versionId) ?? draft ?? versions.find((v) => v.id === definition.activeVersionId) ?? versions.at(-1);
  return <VersionView key={shown?.id ?? 'none'} definition={definition} versions={versions} shown={shown ?? null} type={type} onVersion={onVersion} />;
}

const statusChip = (status: RouteVersion['status']) => (status === 'active' ? 'active' : status === 'draft' ? 'draft' : 'inactive');

function VersionView({ definition, versions, shown, type, onVersion }: { definition: RouteDefinition; versions: RouteVersion[]; shown: RouteVersion | null; type: RouteDocumentType; onVersion: (id: string) => void }) {
  const { t, server } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const { can, writable, me } = useShell();
  const toast = useToast();
  const queryClient = useQueryClient();
  const problemText = useProblemText();
  const canEdit = writable && can('wf.workflow_definitions.update');
  const draft = versions.find((v) => v.status === 'draft');
  const editable = canEdit && shown?.status === 'draft';
  const [route, setRoute] = useState<RouteJson>(shown?.route ?? { steps: [] });
  const [problems, setProblems] = useState<RouteProblem[] | null>(null);
  const [checked, setChecked] = useState<boolean | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [pending, setPending] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [activating, setActivating] = useState(false);
  const [deactivating, setDeactivating] = useState(false);
  const [people, setPeople] = useState<Record<string, string>>({});
  const roles = useQuery({ queryKey: ['wf', 'roles'], queryFn: users.roles, retry: false });
  // What the server holds, as last read or saved: the draft is "changed" when the editor differs from it.
  const [baseline, setBaseline] = useState(() => (shown ? canonical(tidy(shown.route)) : ''));
  const dirty = shown ? canonical(tidy(route)) !== baseline : false;

  // Names of the named people in the route (the stored route holds only their ids).
  const ids = useMemo(() => [...new Set(route.steps.flatMap((s) => [s.approver, s.escalation, s.fallback].flatMap((a) => (a?.kind === 'user' && a.user_id ? [a.user_id] : []))))], [route]);
  useEffect(() => {
    for (const userId of ids.filter((x) => !(x in people))) {
      users.get(userId).then((u) => setPeople((p) => ({ ...p, [userId]: u.displayName }))).catch(() => setPeople((p) => ({ ...p, [userId]: '' })));
    }
  }, [ids, people]);

  const sentence: SentenceContext = { fields: type.fields, locale, say: (key, values) => t(key as MessageKey, values), companyName: (cid) => companyName(me.companies.find((c) => c.id === cid) ?? null, locale) };
  const reload = async () => { await queryClient.invalidateQueries({ queryKey: ['wf'] }); };
  const words = (p: RouteProblem): string => {
    const n = stepOfProblem(p.path);
    const step = n === null ? null : route.steps[n];
    const where = n === null || !step ? t('routes.problem.whole') : t('routes.problem.step', { n: n + 1, name: stepTitle(step, locale) });
    return t('routes.problem.line', { where, text: server(p.error) ?? p.error });
  };
  const fromProblem = (error: unknown): RouteProblem[] => (error instanceof ApiProblem ? error.details.map((d) => { const at = d.indexOf(': '); return at < 0 ? { path: 'route', error: d } : { path: d.slice(0, at), error: d.slice(at + 2) }; }) : []);
  const refuse = (error: unknown) => {
    const found = fromProblem(error);
    setProblems(found.length > 0 ? found : null);
    setFailure(problemText(error));
  };
  const act = async (run: () => Promise<void>) => {
    setBusy(true);
    setFailure(null);
    try { await run(); } catch (error) { refuse(error); } finally { setBusy(false); }
  };

  const newDraft = () => act(async () => {
    const source = shown ?? versions.find((v) => v.id === definition.activeVersionId);
    const created = await routesApi.newDraft(definition.id, source ? { copyOf: source.id } : { route: { steps: [newStep([])] } });
    await reload();
    onVersion(created.id);
    toast.success(t('routes.draft.created'));
  });
  const save = () => act(async () => {
    if (!shown) return;
    const written = tidy(route);
    await routesApi.save(shown.id, written, shown.rowVersion);
    setBaseline(canonical(written));
    setProblems(null);
    setChecked(null);
    await reload();
    toast.success(t('routes.saved'));
  });
  const check = () => act(async () => {
    if (!shown) return;
    const result = await routesApi.check(shown.id);
    setProblems(result.problems);
    setChecked(result.ready);
  });
  const patchStep = (index: number, step: RouteJson['steps'][number]) => { setRoute({ steps: route.steps.map((s, i) => (i === index ? step : s)) }); setChecked(null); };
  const hasPlainStep = route.steps.some((s) => !s.condition);

  return (
    <section className="erp-stack" aria-labelledby="route-title">
      <div className="erp-row erp-wrap">
        <h2 id="route-title" className="erp-h3"><bdi>{nameOf(definition.name, locale)}</bdi></h2>
        <StatusChip status={definition.status === 'active' ? 'active' : 'inactive'} label={t(definition.status === 'active' ? 'routes.status.active' : 'routes.status.inactive')} />
        {canEdit && definition.status === 'active' && <Button variant="quiet" onPress={() => setDeactivating(true)}>{t('routes.deactivate')}</Button>}
      </div>
      <h3 className="erp-h3">{t('routes.versions.title')}</h3>
      <ul className="erp-route-versions" aria-label={t('routes.versions.title')}>
        {versions.map((v) => (
          <li key={v.id} className="erp-config-card">
            <span className="erp-row erp-wrap">
              <strong>{t('routes.version', { n: v.version })}</strong>
              <StatusChip status={statusChip(v.status)} label={t(`routes.version.status.${v.status}` as MessageKey)} />
            </span>
            {v.activatedAt && <span className="erp-small erp-muted">{t('routes.version.activatedAt', { when: formatDateTime(v.activatedAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) })}</span>}
            <span className="erp-small">{t('routes.running', { count: v.runningInstances })}</span>
            <Button variant={shown?.id === v.id ? 'primary' : 'secondary'} aria-pressed={shown?.id === v.id} aria-label={t('routes.version.open', { n: v.version })} onPress={() => onVersion(v.id)}>{t('routes.open')}</Button>
          </li>
        ))}
      </ul>
      <p className="erp-small erp-muted">{t('routes.running.note')}</p>
      {canEdit && !draft && <div><Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} isPending={busy} onPress={() => { void newDraft(); }}>{shown ? t('routes.draft.newFrom', { n: shown.version }) : t('routes.draft.new')}</Button></div>}
      {pending && <Banner tone="info">{pending}</Banner>}
      {failure && <Banner tone="danger">{failure}</Banner>}
      {shown && shown.status !== 'draft' && <Banner tone="info">{t('routes.readonly', { status: t(`routes.version.status.${shown.status}` as MessageKey).toLowerCase() })}</Banner>}
      {!shown && <EmptyState title={t('routes.steps.none')} />}
      {shown && (
        <>
          <h3 className="erp-h3">{t('routes.steps.title')}</h3>
          {route.steps.length === 0 && <p className="erp-muted">{t('routes.steps.none')}</p>}
          {route.steps.map((step, index) => (
            <StepCard key={`${index}:${step.key}`} step={step} index={index} count={route.steps.length} type={type} roles={roles.data ?? []} readOnly={!editable} people={people} sentence={sentence}
              problems={(problems ?? []).filter((p) => stepOfProblem(p.path) === index).map(words)}
              onChange={(s) => patchStep(index, s)} onMove={(by) => { setRoute({ steps: move(route.steps, index, by) }); setChecked(null); }}
              onRemove={() => { setRoute({ steps: route.steps.filter((_, i) => i !== index) }); setChecked(null); toast.success(t('routes.step.removed')); }}
              onPerson={(pid, name) => setPeople((p) => ({ ...p, [pid]: name }))} />
          ))}
          {editable && route.steps.length < MAX_STEPS && <div><Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} onPress={() => setRoute({ steps: [...route.steps, newStep(route.steps)] })}>{t('routes.step.add')}</Button></div>}
          {editable && route.steps.length > 0 && !hasPlainStep && <Banner tone="warning">{t('routes.when.needStep')}</Banner>}
          {(problems ?? []).some((p) => stepOfProblem(p.path) === null) && <Banner tone="danger"><ul className="erp-route-problems">{(problems ?? []).filter((p) => stepOfProblem(p.path) === null).map((p) => <li key={p.path + p.error}>{words(p)}</li>)}</ul></Banner>}
          {checked === true && <Banner tone="success">{t('routes.check.ok')}</Banner>}
          {checked === false && <p>{t('routes.check.problems')}</p>}
          {dirty && editable && <p className="erp-small erp-muted">{t('routes.unsaved')}</p>}
          {shown.status === 'draft' && (
            <div className="erp-route-actions">
              {editable && <Button variant="primary" isPending={busy} isDisabled={!dirty} onPress={() => { void save(); }}>{t('routes.save')}</Button>}
              <Button variant="secondary" isPending={busy} isDisabled={dirty} onPress={() => { void check(); }}>{t('routes.check')}</Button>
              {editable && <Button variant="secondary" isDisabled={dirty || busy} onPress={() => setActivating(true)}>{t('routes.activate')}</Button>}
            </div>
          )}
          {shown.status === 'draft' && dirty && <p className="erp-small erp-muted">{t('routes.check.saveFirst')}</p>}
        </>
      )}
      {shown && activating && (
        <ReasonDialog title={t('routes.activate.title', { n: shown.version })} confirmLabel={t('routes.activate.confirm', { n: shown.version })} isOpen reasonOptional onClose={() => setActivating(false)}
          effect={t('routes.activate.effect', { count: versions.filter((v) => v.status === 'active').reduce((sum, v) => sum + v.runningInstances, 0) })}
          onConfirm={async (reason) => {
            const answer = await routesApi.activate(shown.id, shown.rowVersion, reason);
            await reload();
            if (answer.outcome === 'pending') setPending(t('routes.activate.pending', { n: shown.version }));
            else toast.success(t('routes.activate.done', { n: shown.version }));
          }} />
      )}
      {deactivating && (
        <ReasonDialog title={t('routes.deactivate.title')} confirmLabel={t('routes.deactivate.confirm')} danger isOpen onClose={() => setDeactivating(false)} effect={t('routes.deactivate.effect')}
          onConfirm={async (reason) => { await routesApi.deactivate(definition.id, reason, definition.rowVersion); await reload(); toast.success(t('routes.deactivate.done')); }} />
      )}
    </section>
  );
}
