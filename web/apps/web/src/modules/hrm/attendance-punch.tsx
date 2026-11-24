// My attendance (slice S1.27a Build items 3 and 4; FR-HRM-ATT-01): check in and out from the portal, and the landing of a scanned gate QR code
// (`/time/punch#code=...`). The employee is the signed-in user's own employment, chosen by the server; the time recorded is the server's.
// A retried press punches once (the key is made when the screen opens a punch, not per request).
import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, TextField } from '@erp/ui';
import { formatDate, formatDateTime, useLocaleSettings, useText } from '@erp/i18n';
import { attendanceApi, newKey, type PunchResult } from '../../api/attendance';
import { ApiProblem } from '../../api/client';
import { companyName, useShell } from '../../shell/context';
import { directionKey, doneKey, sourceKey, useRefusal } from './attendance-parts';
import './attendance-messages';

const dayKey = (company: string | null) => ['hrm', 'attendance', 'my-day', company] as const;

/** The code of a scanned QR address: `#code=...` (kept out of the server's logs), or a typed one. */
function scannedCode(): string {
  const hash = window.location.hash.replace(/^#/, '');
  return new URLSearchParams(hash).get('code') ?? '';
}

export default function PunchScreen() {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const { can, writable, me } = useShell();
  const refusal = useRefusal();
  const client = useQueryClient();
  useDocumentTitle(t('att.title'));
  const [company, setCompany] = useState<string | null>(null);
  const [code, setCode] = useState(scannedCode);
  const [message, setMessage] = useState<{ tone: 'success' | 'danger' | 'warning'; text: string } | null>(null);
  const key = useRef<string | null>(null); // one key per attempt: pressing again after a lost answer repeats the same punch
  const lang = locale === 'ur' ? 'ur' : 'en';
  const day = useQuery({ queryKey: dayKey(company), queryFn: ({ signal }) => attendanceApi.myDay(company, signal), refetchOnMount: 'always' });
  const punch = useMutation({
    mutationFn: async (direction: 'in' | 'out') => {
      key.current ??= newKey();
      return code.trim()
        ? attendanceApi.qrPunch(code.trim(), direction, key.current)
        : attendanceApi.punch(direction, key.current, day.data?.companyId ?? company);
    },
    onSuccess: async (result: PunchResult) => {
      key.current = null;
      setCode('');
      window.history.replaceState(null, '', window.location.pathname);
      const time = formatDateTime(result.eventAt, lang, timeZone, numbers);
      setMessage({ tone: result.isDuplicate ? 'warning' : 'success', text: t(doneKey(result), { time }) });
      await client.invalidateQueries({ queryKey: ['hrm', 'attendance', 'my-day'] });
    },
    onError: (error) => {
      if (!(error instanceof ApiProblem) || error.status !== 0) key.current = null; // a lost answer keeps the key; a refusal starts afresh
      setMessage({ tone: 'danger', text: refusal(error) });
    },
  });

  if (!can('hrm.attendance.punch')) return <div className="erp-page"><PageHeader title={t('att.title')} /><EmptyState title={t('att.denied')} /></div>;
  const problem = day.error instanceof ApiProblem ? day.error : null;
  const data = day.data;
  const companies = data?.employments ?? [];
  const options = companies.map((e) => ({ id: e.companyId, label: companyName(me.companies.find((c) => c.id === e.companyId) ?? null, lang) || e.employeeNo, detail: e.employeeNo }));
  return (
    <div className="erp-page">
      <PageHeader title={t('att.title')} description={t('att.description')} />
      {day.isPending && <Skeleton />}
      {problem && <ErrorPanel title={t('att.loadError')} message={refusal(problem)} />}
      {data && data.employments.length === 0 && <EmptyState title={t('att.noEmployment')}>{t('att.noEmployment.body')}</EmptyState>}
      {data && data.employments.length > 0 && (
        <div className="erp-stack">
          {message && <Banner tone={message.tone} title={message.text} onDismiss={() => setMessage(null)}>{null}</Banner>}
          {data.needsCompany && <SelectField label={t('att.company')} help={t('att.company.pick')} options={options} value={company} onChange={setCompany} />}
          {!data.needsCompany && (
            <>
              <p role="status">{data.next === 'out' ? t('att.next.out') : t('att.next.in')}</p>
              {(code || scannedCode()) && <TextField label={t('att.scan.code')} value={code} onChange={setCode} isCode />}
              <div className="erp-row erp-wrap">
                <Button variant={data.next === 'in' ? 'primary' : 'secondary'} isPending={punch.isPending} isDisabled={!writable} onPress={() => punch.mutate('in')}>{t('att.checkIn')}</Button>
                <Button variant={data.next === 'out' ? 'primary' : 'secondary'} isPending={punch.isPending} isDisabled={!writable} onPress={() => punch.mutate('out')}>{t('att.checkOut')}</Button>
              </div>
              <ScanBox code={code} setCode={setCode} />
              <h2>{t('att.today', { date: data.today ? formatDate(data.today, lang, 'UTC', numbers) : '' })}</h2>
              {data.events.length === 0 ? <p className="erp-muted">{t('att.noPunches')}</p> : (
                <table className="erp-table">
                  <thead><tr><th scope="col">{t('att.col.time')}</th><th scope="col">{t('att.col.direction')}</th><th scope="col">{t('att.col.source')}</th><th scope="col">{t('att.col.counted')}</th></tr></thead>
                  <tbody>
                    {data.events.map((e) => (
                      <tr key={e.id}>
                        <td>{formatDateTime(e.eventAt, lang, timeZone, numbers)}</td><td>{t(directionKey(e.direction))}</td><td>{t(sourceKey(e.source))}</td>
                        <td><StatusChip status={e.isDuplicate ? 'inactive' : 'active'} label={t(e.isDuplicate ? 'att.counted.no' : 'att.counted.yes')} /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

/** The typed-code fallback beside the camera scan: an employee without a camera app types the code printed under the gate's QR. */
function ScanBox({ code, setCode }: { code: string; setCode: (v: string) => void }) {
  const { t } = useText();
  const [open, setOpen] = useState(false);
  if (code && !open) return null;
  return open
    ? <div className="erp-stack"><p className="erp-muted">{t('att.scan.body')}</p><TextField label={t('att.scan.code')} value={code} onChange={setCode} isCode /></div>
    : <Button variant="quiet" onPress={() => setOpen(true)}>{t('att.scan.title')}</Button>;
}
