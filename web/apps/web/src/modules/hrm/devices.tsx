// Attendance devices (slice S1.27b Build items 6 to 9; FR-HRM-ATT-02, ATT-03, ATT-20): the device list with status, last read and last error, "read now";
// the device users mapped to employees with their consent and the removal tasks of people who left; the punches that belong to nobody yet; and the punch log.
// The secret of a device is typed once and never shown; no fingerprint or face template exists in this system.
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Banner, Button, CheckboxField, DateField, Dialog, EmptyState, ErrorPanel, PageHeader, PasswordField, SelectField, Skeleton, StatusChip, Tabs, TextField } from '@erp/ui';
import { formatDate, formatDateTime, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { attendanceApi } from '../../api/attendance';
import { devicesApi, type DeviceRow, type LogFilter, type LogRow, type MapRow, type UnmappedRow } from '../../api/devices';
import { useShell } from '../../shell/context';
import './devices-messages';

const SOURCES = ['web', 'qr', 'kiosk', 'biometric', 'hr_entry', 'import'];
const STATES = ['all', 'active', 'ended', 'removal_pending'];

function useRefusal(): (error: unknown) => string {
  const { t, server } = useText();
  return (error) => {
    if (error instanceof ApiProblem) return error.status === 0 ? t('dev.error.network') : server(error.key) ?? t('dev.error.generic');
    return t('dev.error.generic');
  };
}

type Note = { tone: 'success' | 'danger'; text: string } | null;

export default function DevicesScreen() {
  const { t } = useText();
  const { can, me, activeCompany } = useShell();
  useDocumentTitle(t('dev.title'));
  const [company, setCompany] = useState<string>(activeCompany?.id ?? me.companies[0]?.id ?? '');
  const [tab, setTab] = useState('devices');
  const [note, setNote] = useState<Note>(null);
  if (!can('hrm.attendance_devices.read')) return <div className="erp-page"><PageHeader title={t('dev.title')} /><EmptyState title={t('dev.denied')} /></div>;
  const options = me.companies.map((c) => ({ id: c.id, label: c.name.en ?? c.code }));
  return (
    <div className="erp-page">
      <PageHeader title={t('dev.title')} description={t('dev.description')} />
      <div className="erp-stack">
        {note && <Banner tone={note.tone} title={note.text} onDismiss={() => setNote(null)}>{null}</Banner>}
        {options.length > 1 && <SelectField label={t('dev.company')} options={options} value={company} onChange={setCompany} />}
        <Tabs label={t('dev.tab.label')} selectedKey={tab} onSelectionChange={setTab} tabs={[
          { id: 'devices', title: t('dev.tab.devices'), content: <DevicesTab company={company} note={setNote} /> },
          { id: 'maps', title: t('dev.tab.maps'), content: <MapsTab company={company} note={setNote} /> },
          { id: 'unmapped', title: t('dev.tab.unmapped'), content: <UnmappedTab company={company} note={setNote} /> },
          { id: 'log', title: t('dev.tab.log'), content: <LogTab company={company} /> },
        ]} />
      </div>
    </div>
  );
}

function Loading({ error, pending, children }: { error: unknown; pending: boolean; children: React.ReactNode }) {
  const { t } = useText();
  const refusal = useRefusal();
  if (pending) return <Skeleton />;
  if (error) return <ErrorPanel title={t('dev.loadError')} message={refusal(error)} />;
  return <>{children}</>;
}

// ------------------------------------------------------------------ devices

function DevicesTab({ company, note }: { company: string; note: (n: Note) => void }) {
  const { t, server } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const { can, writable } = useShell();
  const refusal = useRefusal();
  const client = useQueryClient();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const list = useQuery({ queryKey: ['hrm', 'devices', company], queryFn: ({ signal }) => devicesApi.list(company, signal) });
  const refresh = () => client.invalidateQueries({ queryKey: ['hrm', 'devices'] });
  const sync = useMutation({
    mutationFn: (id: string) => devicesApi.syncNow(id),
    onSuccess: async () => { note({ tone: 'success', text: t('dev.syncNow.done') }); await refresh(); },
    onError: (e) => note({ tone: 'danger', text: refusal(e) }),
  });
  const retire = useMutation({
    mutationFn: (d: DeviceRow) => devicesApi.update(d.id, { status: 'retired', rowVersion: d.rowVersion }),
    onSuccess: async () => { note({ tone: 'success', text: t('dev.retire.done') }); await refresh(); },
    onError: (e) => note({ tone: 'danger', text: refusal(e) }),
  });
  /** A device's last error is "key" or "key: reason"; the key is shown in words. */
  const errorText = (error: string) => { const [key, ...rest] = error.split(': '); return [server(key ?? '') ?? key, ...rest].join(': '); };
  const when = (v: string | null) => (v ? formatDateTime(v, lang, timeZone, numbers) : t('dev.never'));
  return (
    <Loading error={list.error} pending={list.isPending}>
      <div className="erp-stack">
        {can('hrm.attendance_devices.create') && <div className="erp-row"><AddDevice company={company} done={async () => { note({ tone: 'success', text: t('dev.added') }); await refresh(); }} /></div>}
        {list.data?.devices.length === 0 ? <EmptyState title={t('dev.empty.devices')}>{t('dev.empty.devices.body')}</EmptyState> : (
          <table className="erp-table">
            <thead><tr>
              <th scope="col">{t('dev.col.serial')}</th><th scope="col">{t('dev.col.model')}</th><th scope="col">{t('dev.col.driver')}</th><th scope="col">{t('dev.col.status')}</th>
              <th scope="col">{t('dev.col.lastSeen')}</th><th scope="col">{t('dev.col.lastSync')}</th><th scope="col">{t('dev.col.lastError')}</th><th scope="col">{t('dev.col.unmapped')}</th><th scope="col">{t('dev.col.actions')}</th>
            </tr></thead>
            <tbody>
              {list.data?.devices.map((d) => (
                <tr key={d.id}>
                  <td>{d.serialNo}</td><td>{d.model ?? ''}</td><td>{d.provider ? t(`dev.driver.${d.provider}` as MessageKey) : ''}</td>
                  <td><StatusChip status={d.status === 'retired' ? 'inactive' : d.online ? 'active' : 'blocked'} label={t(d.status === 'retired' ? 'dev.status.retired' : d.online ? 'dev.status.online' : 'dev.status.offline')} /></td>
                  <td>{when(d.lastSeenAt)}</td><td>{when(d.lastSyncAt)}</td><td>{d.lastError ? errorText(d.lastError) : ''}</td><td>{d.unmappedOpen}</td>
                  <td>
                    <div className="erp-row erp-wrap">
                      {d.status !== 'retired' && d.provider === 'zkteco' && can('hrm.attendance_devices.sync') && (
                        <Button variant="secondary" isPending={sync.isPending} isDisabled={!writable} onPress={() => sync.mutate(d.id)}>{t('dev.syncNow')}</Button>
                      )}
                      {d.status !== 'retired' && can('hrm.attendance_devices.update') && <Button variant="quiet" isDisabled={!writable} onPress={() => retire.mutate(d)}>{t('dev.retire')}</Button>}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </Loading>
  );
}

function AddDevice({ company, done }: { company: string; done: () => Promise<unknown> }) {
  const { t, server } = useText();
  const { writable } = useShell();
  const refusal = useRefusal();
  const [open, setOpen] = useState(false);
  const [location, setLocation] = useState('');
  const [serial, setSerial] = useState('');
  const [model, setModel] = useState('');
  const [provider, setProvider] = useState('generic_push');
  const [host, setHost] = useState('');
  const [port, setPort] = useState('4370');
  const [credentials, setCredentials] = useState('');
  const [problem, setProblem] = useState<ApiProblem | null>(null);
  const locations = useQuery({ queryKey: ['hrm', 'devices', 'locations', company], queryFn: ({ signal }) => attendanceApi.locations(company, signal), enabled: open && company !== '' });
  const create = useMutation({
    mutationFn: () => devicesApi.create({ companyId: company, locationId: location, serialNo: serial.trim(), provider, ...(model.trim() ? { model: model.trim() } : {}), ...(provider === 'zkteco' ? { host: host.trim(), port: Number(port), protocol: 'tcp' } : {}), ...(credentials ? { credentials } : {}) }),
    onSuccess: async () => { setOpen(false); setCredentials(''); setSerial(''); setProblem(null); await done(); },
    onError: (e) => setProblem(e instanceof ApiProblem ? e : null),
  });
  const field = (name: string) => { const key = problem?.fieldErrors[name]?.[0]; return key ? server(key) ?? t('dev.error.generic') : null; };
  const pull = provider === 'zkteco';
  return (
    <>
    <Button variant="primary" isDisabled={!writable} onPress={() => setOpen(true)}>{t('dev.add')}</Button>
    <Dialog title={t('dev.add.title')} isOpen={open} onOpenChange={setOpen}
      actions={(close) => (<>
        <Button variant="quiet" onPress={close}>{t('dev.cancel')}</Button>
        <Button variant="primary" isPending={create.isPending} isDisabled={!location || !serial.trim()} onPress={() => create.mutate()}>{t('dev.save')}</Button>
      </>)}>
      {problem && !problem.fieldErrors['serialNo'] && Object.keys(problem.fieldErrors).length === 0 && <Banner tone="danger" title={refusal(problem)}>{null}</Banner>}
      <SelectField label={t('dev.field.location')} isRequired options={(locations.data?.items ?? []).map((l) => ({ id: l.id, label: l.nameI18n['en'] ?? l.code, detail: l.code }))} value={location} onChange={setLocation} error={field('locationId')} />
      <TextField label={t('dev.field.serial')} isRequired value={serial} onChange={setSerial} error={field('serialNo')} />
      <TextField label={t('dev.field.model')} value={model} onChange={setModel} />
      <SelectField label={t('dev.field.driver')} options={['generic_push', 'zkteco'].map((p) => ({ id: p, label: t(`dev.driver.${p}` as MessageKey) }))} value={provider} onChange={setProvider} error={field('provider')} />
      {pull && <TextField label={t('dev.field.host')} value={host} onChange={setHost} error={field('host')} isCode />}
      {pull && <TextField label={t('dev.field.port')} value={port} onChange={setPort} inputMode="numeric" error={field('port')} isCode />}
      <PasswordField label={t('dev.field.credentials')} autoComplete="new-password" help={t(pull ? 'dev.field.credentials.pull' : 'dev.field.credentials.push')} value={credentials} onChange={setCredentials} error={field('credentials')} isRequired={!pull} />
    </Dialog>
    </>
  );
}


// ------------------------------------------------------------------ device users

function MapsTab({ company, note }: { company: string; note: (n: Note) => void }) {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const { can, writable } = useShell();
  const refusal = useRefusal();
  const client = useQueryClient();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const [state, setState] = useState('active');
  const maps = useQuery({ queryKey: ['hrm', 'device-maps', company, state], queryFn: ({ signal }) => devicesApi.maps(company, state === 'all' ? undefined : state, signal) });
  const devices = useQuery({ queryKey: ['hrm', 'devices', company], queryFn: ({ signal }) => devicesApi.list(company, signal) });
  const serial = (id: string | null) => (id ? devices.data?.devices.find((d) => d.id === id)?.serialNo ?? '' : t('dev.map.allDevices'));
  const refresh = () => client.invalidateQueries({ queryKey: ['hrm', 'device-maps'] });
  const end = useMutation({
    mutationFn: (m: MapRow) => devicesApi.endMap(m.id, m.rowVersion),
    onSuccess: async () => { note({ tone: 'success', text: t('dev.map.ended') }); await refresh(); },
    onError: (e) => note({ tone: 'danger', text: refusal(e) }),
  });
  const removed = useMutation({
    mutationFn: (m: MapRow) => devicesApi.removalDone(m.id, m.rowVersion),
    onSuccess: async () => { note({ tone: 'success', text: t('dev.map.removal.confirmed') }); await refresh(); },
    onError: (e) => note({ tone: 'danger', text: refusal(e) }),
  });
  const day = (v: string | null) => (v ? formatDate(v, lang, 'UTC', numbers) : '');
  return (
    <Loading error={maps.error} pending={maps.isPending}>
      <div className="erp-stack">
        <div className="erp-row erp-wrap">
          {can('hrm.device_user_maps.create') && <MapDialog company={company} devices={devices.data?.devices ?? []} done={async (replayed) => { note({ tone: 'success', text: replayed > 0 ? t('dev.map.done.replayed', { count: replayed }) : t('dev.map.done') }); await refresh(); await client.invalidateQueries({ queryKey: ['hrm', 'unmapped'] }); }} />}
          <SelectField label={t('dev.map.filter.state')} options={STATES.map((s) => ({ id: s, label: t(`dev.map.state.${s}` as MessageKey) }))} value={state} onChange={setState} />
        </div>
        {maps.data?.maps.length === 0 ? <EmptyState title={t('dev.empty.maps')} /> : (
          <table className="erp-table">
            <thead><tr>
              <th scope="col">{t('dev.map.col.userNo')}</th><th scope="col">{t('dev.map.col.employee')}</th><th scope="col">{t('dev.map.col.device')}</th><th scope="col">{t('dev.map.col.from')}</th>
              <th scope="col">{t('dev.map.col.to')}</th><th scope="col">{t('dev.map.col.consent')}</th><th scope="col">{t('dev.map.col.removal')}</th><th scope="col">{t('dev.col.actions')}</th>
            </tr></thead>
            <tbody>
              {maps.data?.maps.map((m) => (
                <tr key={m.id}>
                  <td>{m.deviceUserNo}</td><td>{m.employeeNo ?? ''}</td><td>{serial(m.deviceId)}</td><td>{day(m.validFrom)}</td><td>{day(m.validTo)}</td><td>{day(m.consentAt)}</td>
                  <td>{m.removalDueAt ? (m.removalDoneAt ? t('dev.map.removal.complete') : t('dev.map.removal.pending')) : t('dev.map.removal.none')}</td>
                  <td>
                    <div className="erp-row erp-wrap">
                      {!m.validTo && can('hrm.device_user_maps.end') && <Button variant="quiet" isDisabled={!writable} onPress={() => end.mutate(m)}>{t('dev.map.end')}</Button>}
                      {m.removalDueAt && !m.removalDoneAt && can('hrm.device_user_maps.removal') && <Button variant="secondary" isDisabled={!writable} onPress={() => removed.mutate(m)}>{t('dev.map.removal.done')}</Button>}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </Loading>
  );
}

function MapDialog({ company, devices, done }: { company: string; devices: DeviceRow[]; done: (replayed: number) => Promise<unknown> }) {
  const { t, server } = useText();
  const { writable } = useShell();
  const [open, setOpen] = useState(false);
  const [device, setDevice] = useState('all');
  const [userNo, setUserNo] = useState('');
  const [employeeNo, setEmployeeNo] = useState('');
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [consent, setConsent] = useState<CalendarDate | null>(null);
  const [problem, setProblem] = useState<ApiProblem | null>(null);
  const save = useMutation({
    mutationFn: () => devicesApi.map({ companyId: company, deviceUserNo: userNo.trim(), employeeNo: employeeNo.trim(), ...(device !== 'all' ? { deviceId: device, ...(consent ? { consentDate: consent.toString() } : {}) } : {}), ...(from ? { validFrom: from.toString() } : {}) }),
    onSuccess: async (r) => { setOpen(false); setUserNo(''); setEmployeeNo(''); setProblem(null); await done(r.replayedPunches); },
    onError: (e) => setProblem(e instanceof ApiProblem ? e : null),
  });
  const field = (name: string) => { const key = problem?.fieldErrors[name]?.[0]; return key ? server(key) ?? t('dev.error.generic') : null; };
  // An error on a field the form does not show (the consent date of an all-devices mapping) is still said in words, at the top.
  const shown = new Set(['deviceId', 'deviceUserNo', 'employmentId', ...(device !== 'all' ? ['consentDate'] : [])]);
  const hidden = problem ? Object.entries(problem.fieldErrors).find(([name]) => !shown.has(name))?.[1][0] : undefined;
  const general = problem && (Object.keys(problem.fieldErrors).length === 0 || hidden) ? server(hidden ?? problem.key) ?? t('dev.error.generic') : null;
  return (
    <>
    <Button variant="primary" isDisabled={!writable} onPress={() => setOpen(true)}>{t('dev.map.add')}</Button>
    <Dialog title={t('dev.map.title')} isOpen={open} onOpenChange={setOpen}
      actions={(close) => (<>
        <Button variant="quiet" onPress={close}>{t('dev.cancel')}</Button>
        <Button variant="primary" isPending={save.isPending} isDisabled={!userNo.trim() || !employeeNo.trim()} onPress={() => save.mutate()}>{t('dev.save')}</Button>
      </>)}>
      {general && <Banner tone="danger" title={general}>{null}</Banner>}
      <SelectField label={t('dev.map.device')} options={[{ id: 'all', label: t('dev.map.allDevices') }, ...devices.filter((d) => d.status !== 'retired').map((d) => ({ id: d.id, label: d.serialNo }))]} value={device} onChange={setDevice} error={field('deviceId')} />
      <TextField label={t('dev.map.userNo')} isRequired value={userNo} onChange={setUserNo} error={field('deviceUserNo')} isCode />
      <TextField label={t('dev.map.employee')} help={t('dev.map.employee.find')} isRequired value={employeeNo} onChange={setEmployeeNo} error={field('employmentId')} isCode />
      <DateField label={t('dev.map.from')} value={from} onChange={(v) => setFrom(v ? parseDate(v.toString()) : null)} />
      {device !== 'all' && <DateField label={t('dev.map.consent')} help={t('dev.map.consent.help')} isRequired value={consent} onChange={(v) => setConsent(v ? parseDate(v.toString()) : null)} error={field('consentDate')} />}
    </Dialog>
    </>
  );
}

// ------------------------------------------------------------------ unmapped punches

function UnmappedTab({ company, note }: { company: string; note: (n: Note) => void }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const { can, writable } = useShell();
  const refusal = useRefusal();
  const client = useQueryClient();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const list = useQuery({ queryKey: ['hrm', 'unmapped', company], queryFn: ({ signal }) => devicesApi.unmapped(company, signal) });
  const devices = useQuery({ queryKey: ['hrm', 'devices', company], queryFn: ({ signal }) => devicesApi.list(company, signal) });
  const discard = useMutation({
    mutationFn: (p: UnmappedRow) => devicesApi.discard(p.id, p.rowVersion),
    onSuccess: async () => { note({ tone: 'success', text: t('dev.unmapped.discarded') }); await client.invalidateQueries({ queryKey: ['hrm', 'unmapped'] }); },
    onError: (e) => note({ tone: 'danger', text: refusal(e) }),
  });
  return (
    <Loading error={list.error} pending={list.isPending}>
      {list.data?.punches.length === 0 ? <EmptyState title={t('dev.empty.unmapped')}>{t('dev.empty.unmapped.body')}</EmptyState> : (
        <table className="erp-table">
          <thead><tr><th scope="col">{t('dev.unmapped.col.userNo')}</th><th scope="col">{t('dev.unmapped.col.device')}</th><th scope="col">{t('dev.unmapped.col.time')}</th><th scope="col">{t('dev.log.col.direction')}</th><th scope="col">{t('dev.col.actions')}</th></tr></thead>
          <tbody>
            {list.data?.punches.map((p) => (
              <tr key={p.id}>
                <td>{p.deviceUserNo}</td><td>{devices.data?.devices.find((d) => d.id === p.deviceId)?.serialNo ?? ''}</td><td>{formatDateTime(p.punchedAt, lang, timeZone, numbers)}</td>
                <td>{t(`dev.direction.${p.direction}` as MessageKey)}</td>
                <td>{can('hrm.unmapped_punches.discard') && <Button variant="quiet" isDisabled={!writable} onPress={() => discard.mutate(p)}>{t('dev.unmapped.discard')}</Button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Loading>
  );
}

// ------------------------------------------------------------------ punch log

function LogTab({ company }: { company: string }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  const [source, setSource] = useState('');
  const [employeeNo, setEmployeeNo] = useState('');
  const [duplicates, setDuplicates] = useState(false);
  const [pages, setPages] = useState<string[]>([]);
  const filter: LogFilter = { companyId: company, ...(from ? { from: from.toString() } : {}), ...(to ? { to: to.toString() } : {}), ...(source ? { source } : {}), ...(employeeNo.trim() ? { employeeNo: employeeNo.trim() } : {}), includeDuplicates: duplicates };
  const reset = <T,>(set: (v: T) => void) => (v: T) => { set(v); setPages([]); };
  const first = useQuery({ queryKey: ['hrm', 'punch-log', filter], queryFn: ({ signal }) => devicesApi.log(filter, null, signal) });
  const extra = useQuery({ queryKey: ['hrm', 'punch-log', filter, pages], queryFn: async ({ signal }) => { const rows: LogRow[] = []; let next: string | null = null; for (const after of pages) { const r = await devicesApi.log(filter, after, signal); rows.push(...r.events); next = r.next; } return { rows, next }; }, enabled: pages.length > 0 });
  const rows = [...(first.data?.events ?? []), ...(extra.data?.rows ?? [])];
  const next = pages.length > 0 ? extra.data?.next ?? null : first.data?.next ?? null;
  return (
    <div className="erp-stack">
      <div className="erp-row erp-wrap">
        <DateField label={t('dev.log.from')} value={from} onChange={(v) => reset(setFrom)(v ? parseDate(v.toString()) : null)} />
        <DateField label={t('dev.log.to')} value={to} onChange={(v) => reset(setTo)(v ? parseDate(v.toString()) : null)} />
        <SelectField label={t('dev.log.source')} options={[{ id: '', label: t('dev.log.source.all') }, ...SOURCES.map((s) => ({ id: s, label: t(`dev.source.${s}` as MessageKey) }))]} value={source} onChange={reset(setSource)} />
        <TextField label={t('dev.log.employee')} value={employeeNo} onChange={reset(setEmployeeNo)} isCode />
        <CheckboxField label={t('dev.log.duplicates')} isSelected={duplicates} onChange={reset(setDuplicates)} />
      </div>
      <Loading error={first.error} pending={first.isPending}>
        {rows.length === 0 ? <EmptyState title={t('dev.empty.log')} /> : (
          <table className="erp-table">
            <thead><tr><th scope="col">{t('dev.log.col.time')}</th><th scope="col">{t('dev.log.col.received')}</th><th scope="col">{t('dev.log.col.employee')}</th><th scope="col">{t('dev.log.col.direction')}</th><th scope="col">{t('dev.log.col.source')}</th><th scope="col">{t('dev.log.col.device')}</th></tr></thead>
            <tbody>
              {rows.map((e) => (
                <tr key={e.id}>
                  <td>{formatDateTime(e.eventAt, lang, timeZone, numbers)}</td><td>{formatDateTime(e.receivedAt, lang, timeZone, numbers)}</td><td>{e.employeeNo ?? ''}</td>
                  <td>{t(`dev.direction.${e.direction}` as MessageKey)}{e.isDuplicate ? ` (${t('dev.counted.no')})` : ''}</td><td>{t(`dev.source.${e.source}` as MessageKey)}</td><td>{e.deviceSerial ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {next && <Button variant="secondary" isPending={extra.isFetching} onPress={() => setPages([...pages, next])}>{t('dev.log.more')}</Button>}
      </Loading>
    </div>
  );
}
