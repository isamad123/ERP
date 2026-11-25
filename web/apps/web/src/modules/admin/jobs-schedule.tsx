// The schedule of a job in words and the dialog that changes it (slice S1.16c Build item 9; FR-ADM-OPS-01). The five shapes are the
// server's (`JobSchedule`): every N minutes, hourly, daily, weekly, monthly (a day or the last day), optionally working days only.
// The server checks the schedule again and answers with a key when it is not valid.
import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Banner, Button, CheckboxField, Dialog, SelectField, SwitchField, TextField, useToast } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { opsApi, type JobDefinition, type Schedule } from '../../api/ops';
import { useFormats, useProblemText } from './ops-parts';

export const WEEKDAYS = ['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday'] as const;
export type Kind = 'every_minutes' | 'hourly' | 'daily' | 'weekly' | 'monthly' | 'none';
const KINDS: Kind[] = ['every_minutes', 'hourly', 'daily', 'weekly', 'monthly', 'none'];
const TIME = /^([01]\d|2[0-3]):[0-5]\d$/;

export interface ScheduleForm { kind: Kind; minutes: string; minute: string; at: string; days: string[]; day: string; lastDay: boolean; workingOnly: boolean; enabled: boolean }

export function kindOf(schedule: Schedule | null): Kind {
  if (!schedule) return 'none';
  return KINDS.find((k) => k !== 'none' && schedule[k] !== undefined) ?? 'none';
}

export function toForm(job: Pick<JobDefinition, 'schedule' | 'isEnabled'>): ScheduleForm {
  const s = job.schedule;
  const kind = kindOf(s);
  return {
    kind,
    minutes: String(s?.every_minutes ?? 60),
    minute: String(s?.hourly?.minute ?? 0),
    at: s?.daily?.at ?? s?.weekly?.at ?? s?.monthly?.at ?? '02:00',
    days: s?.weekly?.days ?? ['monday'],
    day: typeof s?.monthly?.day === 'number' ? String(s.monthly.day) : '1',
    lastDay: s?.monthly?.day === 'last',
    workingOnly: s?.working_days_only === true,
    enabled: job.isEnabled,
  };
}

const whole = (text: string, min: number, max: number): number | null => (/^\d{1,4}$/.test(text.trim()) && Number(text) >= min && Number(text) <= max ? Number(text) : null);

/** The schedule the form describes, `null` for "only on request", or `'invalid'` (nothing is sent then). */
export function toSchedule(f: ScheduleForm): Schedule | null | 'invalid' {
  const working = f.workingOnly ? { working_days_only: true } : {};
  switch (f.kind) {
    case 'none': return null;
    case 'every_minutes': { const n = whole(f.minutes, 1, 1440); return n === null ? 'invalid' : { every_minutes: n, ...working }; }
    case 'hourly': { const m = whole(f.minute, 0, 59); return m === null ? 'invalid' : { hourly: { minute: m }, ...working }; }
    case 'daily': return TIME.test(f.at) ? { daily: { at: f.at }, ...working } : 'invalid';
    case 'weekly': return TIME.test(f.at) && f.days.length > 0 ? { weekly: { days: WEEKDAYS.filter((d) => f.days.includes(d)), at: f.at }, ...working } : 'invalid';
    case 'monthly': {
      const d = f.lastDay ? 'last' : whole(f.day, 1, 31);
      return TIME.test(f.at) && d !== null ? { monthly: { day: d, at: f.at }, ...working } : 'invalid';
    }
  }
}

/** A schedule said in words (the same shapes as the editor). */
export function useScheduleText() {
  const { t } = useText();
  return (schedule: Schedule | null): string => {
    const f = toForm({ schedule, isEnabled: true });
    const tail = f.workingOnly ? `, ${t('jobs.schedule.workingDays')}` : '';
    switch (f.kind) {
      case 'none': return t('jobs.schedule.none');
      case 'every_minutes': return t('jobs.schedule.every', { n: f.minutes }) + tail;
      case 'hourly': return t('jobs.schedule.hourly', { m: f.minute }) + tail;
      case 'daily': return t('jobs.schedule.daily', { at: f.at }) + tail;
      case 'weekly': return t('jobs.schedule.weekly', { days: WEEKDAYS.filter((d) => f.days.includes(d)).map((d) => t(`jobs.day.${d}` as MessageKey)).join(', '), at: f.at }) + tail;
      case 'monthly': return (f.lastDay ? t('jobs.schedule.monthlyLast', { at: f.at }) : t('jobs.schedule.monthly', { day: f.day, at: f.at })) + tail;
    }
  };
}

export function ScheduleDialog({ job, onClose }: { job: JobDefinition; onClose: () => void }) {
  const { t } = useText();
  const f = useFormats();
  const toast = useToast();
  const queryClient = useQueryClient();
  const problemText = useProblemText();
  const [form, setForm] = useState<ScheduleForm>(() => toForm(job));
  const [error, setError] = useState<string | null>(null);
  const set = (patch: Partial<ScheduleForm>) => { setForm((x) => ({ ...x, ...patch })); setError(null); };
  const save = useMutation({
    mutationFn: (schedule: Schedule | null) => opsApi.changeSchedule(job.id, { schedule, isEnabled: form.enabled, rowVersion: job.rowVersion }),
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: ['jobs', 'definitions'] }); toast.success(t('jobs.edit.saved')); onClose(); },
    onError: (e) => setError(e instanceof ApiProblem && e.status === 409 ? t('jobs.edit.stale') : problemText(e, { 'errors.job.schedule_invalid': t('jobs.edit.invalid'), 'errors.job.schedule_not_editable': t('jobs.edit.locked') })),
  });
  const submit = () => {
    const schedule = toSchedule(form);
    if (schedule === 'invalid') { setError(t('jobs.edit.invalid')); return; }
    save.mutate(schedule);
  };
  const needsTime = form.kind === 'daily' || form.kind === 'weekly' || form.kind === 'monthly';
  return (
    <Dialog isOpen onOpenChange={(open) => { if (!open) onClose(); }} title={t('jobs.edit.title', { name: f.name(job.name, job.code) })}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('jobs.edit.cancel')}</Button>
          <Button variant="primary" isPending={save.isPending} onPress={submit}>{t('jobs.edit.save')}</Button>
        </>
      )}>
      {error && <Banner tone="danger">{error}</Banner>}
      <SelectField label={t('jobs.edit.kind')} value={form.kind} onChange={(v) => set({ kind: v as Kind })} options={KINDS.map((k) => ({ id: k, label: t(`jobs.edit.kind.${k}` as MessageKey) }))} />
      {form.kind === 'every_minutes' && <TextField label={t('jobs.edit.minutes')} inputMode="numeric" value={form.minutes} onChange={(v) => set({ minutes: v })} />}
      {form.kind === 'hourly' && <TextField label={t('jobs.edit.minute')} inputMode="numeric" value={form.minute} onChange={(v) => set({ minute: v })} />}
      {needsTime && <TextField label={t('jobs.edit.at')} value={form.at} onChange={(v) => set({ at: v })} />}
      {form.kind === 'weekly' && (
        <fieldset className="erp-stack">
          <legend>{t('jobs.edit.days')}</legend>
          {WEEKDAYS.map((d) => (
            <CheckboxField key={d} label={t(`jobs.day.${d}` as MessageKey)} isSelected={form.days.includes(d)}
              onChange={(on) => set({ days: on ? [...form.days, d] : form.days.filter((x) => x !== d) })} />
          ))}
        </fieldset>
      )}
      {form.kind === 'monthly' && (
        <>
          <CheckboxField label={t('jobs.edit.lastDay')} isSelected={form.lastDay} onChange={(on) => set({ lastDay: on })} />
          {!form.lastDay && <TextField label={t('jobs.edit.dayOfMonth')} inputMode="numeric" value={form.day} onChange={(v) => set({ day: v })} />}
        </>
      )}
      {form.kind !== 'none' && <CheckboxField label={t('jobs.edit.workingOnly')} isSelected={form.workingOnly} onChange={(on) => set({ workingOnly: on })} />}
      <SwitchField label={t('jobs.edit.enabled')} isSelected={form.enabled} onChange={(on) => set({ enabled: on })} />
    </Dialog>
  );
}
