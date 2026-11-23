// Notification texts and rules (slice S1.15d Build item 11; FR-KRN-NTF-02, -03; S1.15 rulings Q1, Q10): per event, language and channel the subject and body
// with the placeholders the event offers, a preview with sample values, a test send to oneself, and "restore the product text"; and the rules that add people to an
// event's audience. The screen is a view over the S1.11b endpoints: the server checks every text (a salary field never, a personal field never in e-mail).
import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, Tabs, TextAreaField, TextField, useToast, type Option } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { CHANNELS, notifyApi, type CatalogueEvent, type Channel, type NotificationText, type TextPreview } from '../../api/templates';
import { useShell } from '../../shell/context';
import { useRefused } from './config-parts';
import { ConfirmDialog, PlaceholderList } from './tpl-parts';
import { RulesPanel } from './notify-rules';
import './notify-messages';
import './users.css';
import './configure.css';

const LANGUAGES = ['en', 'ur'] as const;

export default function NotifyScreen() {
  const { t } = useText();
  useDocumentTitle(t('ntfx.title'));
  const [params, setParams] = useSearchParams();
  const open = params.get('event');
  const [search, setSearch] = useState('');
  const patch = (event: string | null) => setParams((current) => { const next = new URLSearchParams(current); if (event) next.set('event', event); else next.delete('event'); return next; }, { replace: true });
  const catalogue = useQuery({ queryKey: ['ntf', 'catalogue'], queryFn: notifyApi.catalogue, refetchOnMount: 'always' });
  const problem = catalogue.error instanceof ApiProblem ? catalogue.error : null;
  const shown = useMemo(() => (catalogue.data ?? []).filter((e) => `${e.code} ${e.group}`.toLowerCase().includes(search.trim().toLowerCase())), [catalogue.data, search]);
  const selected = catalogue.data?.find((e) => e.code === open) ?? null;
  return (
    <div className="erp-page">
      <PageHeader title={t('ntfx.title')} description={t('ntfx.description')} />
      {catalogue.isPending && !problem && <Skeleton lines={6} />}
      {problem && (
        <ErrorPanel message={problem.status === 403 ? t('ntfx.denied') : t('ntfx.loadError')} reference={problem.reference}
          action={<Button variant="primary" onPress={() => { void catalogue.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {catalogue.isSuccess && catalogue.data.length === 0 && <EmptyState title={t('ntfx.empty')}>{t('ntfx.empty.body')}</EmptyState>}
      {catalogue.isSuccess && catalogue.data.length > 0 && (
        <div className="erp-config-split">
          <div className="erp-stack">
            <TextField label={t('ntfx.search')} value={search} onChange={setSearch} />
            {shown.length === 0 && <p className="erp-muted">{t('ntfx.search.none')}</p>}
            <ul className="erp-config-list erp-config-scroll" aria-label={t('ntfx.list.title')}>
              {shown.map((e) => (
                <li key={e.code}>
                  <div className="erp-config-card">
                    <span className="erp-stack">
                      <span className="erp-settings-name erp-code"><bdi>{e.code}</bdi></span>
                      <span className="erp-small erp-muted">{t(`ntfx.group.${e.group}` as MessageKey)}</span>
                    </span>
                    {e.mandatory && <StatusChip status="blocked" label={t('ntfx.mandatory')} />}
                    <Button variant={open === e.code ? 'primary' : 'secondary'} aria-pressed={open === e.code} aria-label={t('ntfx.open.aria', { code: e.code })} onPress={() => patch(e.code)}>{t('ntfx.open')}</Button>
                  </div>
                </li>
              ))}
            </ul>
          </div>
          {selected ? <EventDetail key={selected.code} event={selected} /> : <EmptyState title={t('ntfx.pick')}>{t('ntfx.pick.body')}</EmptyState>}
        </div>
      )}
    </div>
  );
}

function EventDetail({ event }: { event: CatalogueEvent }) {
  const { t } = useText();
  const { can } = useShell();
  const [tab, setTab] = useState('texts');
  return (
    <section className="erp-stack" aria-labelledby="ntfx-event">
      <h2 id="ntfx-event" className="erp-h3 erp-code"><bdi>{event.code}</bdi></h2>
      <Tabs label={t('ntfx.tabs')} selectedKey={tab} onSelectionChange={setTab} tabs={[
        { id: 'texts', title: t('ntfx.tab.texts'), content: <TextsPanel event={event} /> },
        ...(can('ntf.subscriptions.read') ? [{ id: 'rules', title: t('ntfx.tab.rules'), content: <RulesPanel event={event} /> }] : []),
      ]} />
    </section>
  );
}

function TextsPanel({ event }: { event: CatalogueEvent }) {
  const { t } = useText();
  const [language, setLanguage] = useState<string>('en');
  const [channel, setChannel] = useState<Channel>('in_app');
  const texts = useQuery({ queryKey: ['ntf', 'texts', event.code], queryFn: () => notifyApi.texts(event.code), refetchOnMount: 'always' });
  const problem = texts.error instanceof ApiProblem ? texts.error : null;
  const languageOptions: Option[] = LANGUAGES.map((l) => ({ id: l, label: t(`ntfx.language.${l}` as MessageKey) }));
  const channelOptions: Option[] = CHANNELS.map((c) => ({ id: c, label: t(`ntfx.channel.${c}` as MessageKey) }));
  if (texts.isPending && !problem) return <Skeleton lines={5} />;
  if (problem || !texts.data) {
    return <ErrorPanel message={problem?.status === 403 ? t('ntfx.denied') : t('ntfx.loadError')} reference={problem?.reference ?? null} action={<Button variant="primary" onPress={() => { void texts.refetch(); }}>{t('common.retry')}</Button>} />;
  }
  const active = texts.data.find((x) => x.channel === channel && x.language === language && x.status === 'active') ?? null;
  const history = texts.data.filter((x) => x.channel === channel && x.language === language);
  return (
    <div className="erp-stack">
      <div className="erp-row erp-wrap">
        <SelectField label={t('ntfx.language')} options={languageOptions} value={language} onChange={setLanguage} />
        <SelectField label={t('ntfx.channel')} options={channelOptions} value={channel} onChange={(c) => setChannel(c as Channel)} />
      </div>
      <TextEditor key={`${language}:${channel}:${active?.id ?? 'none'}`} event={event} language={language} channel={channel} active={active} versions={history.length} />
    </div>
  );
}

function TextEditor({ event, language, channel, active, versions }: { event: CatalogueEvent; language: string; channel: Channel; active: NotificationText | null; versions: number }) {
  const { t } = useText();
  const { can, writable } = useShell();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refused = useRefused();
  const canEdit = writable && can('ntf.templates.create');
  const [subject, setSubject] = useState(active?.subject ?? '');
  const [body, setBody] = useState(active?.body ?? '');
  const [sample, setSample] = useState<Record<string, string>>({});
  const [preview, setPreview] = useState<TextPreview | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [restoring, setRestoring] = useState(false);
  const dirty = subject !== (active?.subject ?? '') || body !== (active?.body ?? '');
  const dir = language === 'ur' ? 'rtl' : 'ltr';
  const general = event.fields.filter((f) => f.fieldClass === 'general');
  const allowed = (f: CatalogueEvent['fields'][number]) => f.fieldClass === 'general' || (f.fieldClass === 'personal' && channel === 'in_app');
  const request = { eventCode: event.code, channel, language, subject: subject.trim(), body: body.trim() };
  const given = Object.fromEntries(Object.entries(sample).filter(([, v]) => v.trim() !== ''));
  const act = async (name: string, run: () => Promise<void>) => {
    setBusy(name);
    setFailure(null);
    setFieldErrors({});
    try { await run(); } catch (error) { const r = refused(error); setFailure(r.summary); setFieldErrors(r.fields); } finally { setBusy(null); }
  };
  const save = () => act('save', async () => {
    await notifyApi.save(request);
    await queryClient.invalidateQueries({ queryKey: ['ntf', 'texts', event.code] });
    toast.success(t('ntfx.saved'));
  });
  const doPreview = () => act('preview', async () => { setPreview(await notifyApi.preview({ ...request, sample: given })); });
  const testSend = () => act('test', async () => {
    await notifyApi.testSend({ eventCode: event.code, language, subject: request.subject, body: request.body, sample: given });
    toast.success(t('ntfx.test.sent'));
  });
  return (
    <div className="erp-stack">
      {active && <p className="erp-small erp-muted">{t(active.source === 'product' ? 'ntfx.source.product' : 'ntfx.source.tenant', { n: active.version })}{versions > 1 ? ` ${t('ntfx.versions', { count: versions })}` : ''}</p>}
      {!active && <Banner tone="info">{t('ntfx.none')}</Banner>}
      {failure && <Banner tone="danger">{failure}</Banner>}
      <div dir={dir} className="erp-stack">
        <TextField label={t(channel === 'email' ? 'ntfx.subject' : 'ntfx.title.field')} value={subject} onChange={setSubject} isReadOnly={!canEdit} isRequired error={fieldErrors.subject ?? null} />
        <TextAreaField label={t('ntfx.body')} value={body} onChange={setBody} isReadOnly={!canEdit} isRequired error={fieldErrors.body ?? null} />
      </div>
      {canEdit && (
        <PlaceholderList label={t('ntfx.placeholders')} onPick={(name) => setBody(`${body}{${name}}`)}
          items={event.fields.map((f) => ({ name: f.name, ...(allowed(f) ? {} : { note: t(f.fieldClass === 'salary' ? 'ntfx.placeholder.salary' : 'ntfx.placeholder.personal') }) }))} />
      )}
      {canEdit && general.length > 0 && (
        <fieldset className="erp-stack">
          <legend className="erp-small erp-muted">{t('ntfx.sample')}</legend>
          {general.map((f) => <TextField key={f.name} label={f.name} value={sample[f.name] ?? ''} onChange={(v) => setSample({ ...sample, [f.name]: v })} />)}
        </fieldset>
      )}
      <div className="erp-route-actions">
        {canEdit && <Button variant="primary" isPending={busy === 'save'} isDisabled={!dirty || body.trim() === '' || subject.trim() === ''} onPress={() => { void save(); }}>{t('ntfx.save')}</Button>}
        <Button variant="secondary" isPending={busy === 'preview'} isDisabled={body.trim() === '' || subject.trim() === ''} onPress={() => { void doPreview(); }}>{t('ntfx.preview')}</Button>
        {can('ntf.templates.test_send') && writable && <Button variant="secondary" isPending={busy === 'test'} isDisabled={body.trim() === '' || subject.trim() === ''} onPress={() => { void testSend(); }}>{t('ntfx.test')}</Button>}
        {canEdit && active?.source === 'tenant' && <Button variant="quiet" onPress={() => setRestoring(true)}>{t('ntfx.restore')}</Button>}
      </div>
      {dirty && canEdit && <p className="erp-small erp-muted">{t('ntfx.unsaved')}</p>}
      {preview && (
        <section className="erp-stack" aria-label={t('ntfx.preview.title')}>
          <h3 className="erp-h3">{t('ntfx.preview.title')}</h3>
          <div dir={dir} className="erp-config-card" data-testid="ntfx-preview">
            <strong><bdi>{preview.subject}</bdi></strong>
            <p><bdi>{preview.body}</bdi></p>
          </div>
          <p className="erp-small erp-muted">{t('ntfx.preview.note')}</p>
        </section>
      )}
      {restoring && (
        <ConfirmDialog title={t('ntfx.restore.title')} effect={t('ntfx.restore.effect')} confirmLabel={t('ntfx.restore.confirm')} onClose={() => setRestoring(false)}
          onConfirm={async () => { await notifyApi.restore(event.code, channel, language); await queryClient.invalidateQueries({ queryKey: ['ntf', 'texts', event.code] }); toast.success(t('ntfx.restored')); }} />
      )}
    </div>
  );
}
