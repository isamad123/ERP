// Attendance kiosk (slice S1.27a Build item 4; FR-HRM-ATT-01): a shared screen signed in as a station user. The employee types a badge or
// PIN number and chooses in or out; the server finds the person from the mapping (R-155: the number alone identifies the person).
// The screen forgets the number and the name a few seconds after the answer, so the next person sees nothing of the last.
import { useEffect, useRef, useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, EmptyState, PageHeader, SelectField, TextField } from '@erp/ui';
import { formatDateTime, useLocaleSettings, useText } from '@erp/i18n';
import { attendanceApi, newKey } from '../../api/attendance';
import { ApiProblem } from '../../api/client';
import { companyName, useShell } from '../../shell/context';
import { directionKey, useRefusal } from './attendance-parts';
import './attendance-messages';

const FORGET_AFTER_MS = 8000;

export default function KioskScreen() {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const { can, writable, me, activeCompany } = useShell();
  const refusal = useRefusal();
  useDocumentTitle(t('att.kiosk.title'));
  const lang = locale === 'ur' ? 'ur' : 'en';
  const [company, setCompany] = useState<string | null>(activeCompany?.id ?? me.companies[0]?.id ?? null);
  const [badge, setBadge] = useState('');
  const [message, setMessage] = useState<{ tone: 'success' | 'danger'; text: string } | null>(null);
  const key = useRef<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const clear = () => { setBadge(''); setMessage(null); key.current = null; };
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);
  const punch = useMutation({
    mutationFn: (direction: 'in' | 'out') => { key.current ??= newKey(); return attendanceApi.kioskPunch(company ?? '', badge.trim(), direction, key.current); },
    onSuccess: (result) => {
      const time = formatDateTime(result.punch.eventAt, lang, timeZone, numbers);
      setMessage({ tone: 'success', text: t('att.kiosk.done', { name: result.firstName, direction: t(directionKey(result.punch.direction)), time }) });
      key.current = null;
      setBadge('');
      timer.current = setTimeout(clear, FORGET_AFTER_MS);
    },
    onError: (error) => {
      if (!(error instanceof ApiProblem) || error.status !== 0) key.current = null;
      setMessage({ tone: 'danger', text: refusal(error) });
    },
  });
  if (!can('hrm.attendance.kiosk')) return <div className="erp-page"><PageHeader title={t('att.kiosk.title')} /><EmptyState title={t('att.denied')} /></div>;
  const options = me.companies.map((c) => ({ id: c.id, label: companyName(c, lang) }));
  const ready = !!company && badge.trim().length > 0 && writable;
  return (
    <div className="erp-page">
      <PageHeader title={t('att.kiosk.title')} description={t('att.kiosk.description')} />
      <div className="erp-stack">
        {message && <Banner tone={message.tone} title={message.text}>{null}</Banner>}
        {options.length > 1 && <SelectField label={t('att.company')} options={options} value={company} onChange={setCompany} />}
        <TextField label={t('att.kiosk.badge')} value={badge} onChange={(v) => { if (timer.current) { clearTimeout(timer.current); timer.current = null; } setBadge(v); }} isCode inputMode="numeric" autoComplete="off" maxLength={40} />
        <div className="erp-row erp-wrap">
          <Button variant="primary" isPending={punch.isPending} isDisabled={!ready} onPress={() => punch.mutate('in')}>{t('att.checkIn')}</Button>
          <Button variant="secondary" isPending={punch.isPending} isDisabled={!ready} onPress={() => punch.mutate('out')}>{t('att.checkOut')}</Button>
          <Button variant="quiet" onPress={clear}>{t('att.kiosk.clear')}</Button>
        </div>
      </div>
    </div>
  );
}
