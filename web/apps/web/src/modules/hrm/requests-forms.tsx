// The two request forms of slice S1.30a (FR-HRM-ATT-11, ATT-14, GHR-09), shared by the request screen and the exception queue. A person asks for
// themselves; HR and managers type the employee number of someone in their scope (the server decides what is in scope and records who acted).
// Times are clock times of the company's day; the server turns them into instants in the company's time zone.
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Banner, Button, CheckboxField, DateField, Dialog, SelectField, TextAreaField, TextField, useToast } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { attendanceRequestsApi } from '../../api/attendance-requests';
import { policiesApi } from '../../api/policies';
import { useShell } from '../../shell/context';
import './requests-messages';

export const DUTY_KINDS = ['on_duty', 'official_visit', 'work_from_home', 'field_duty'] as const;
const TIME = /^([01]\d|2[0-3]):[0-5]\d$/;
const withSeconds = (value: string) => (value.trim() === '' ? undefined : `${value.trim()}:00`);
const optional = <K extends string>(key: K, value: string | undefined): Partial<Record<K, string>> => (value === undefined ? {} : ({ [key]: value } as Record<K, string>));

export function useRefusal(): (error: unknown) => string {
  const { t, server } = useText();
  return (error) => {
    if (error instanceof ApiProblem) return error.status === 0 ? t('req.error.network') : server(error.key) ?? t('req.error.generic');
    return t('req.error.generic');
  };
}

function useEmployeeLookup(company: string, employeeNo: string) {
  const { can } = useShell();
  const people = useQuery({ queryKey: ['hrm', 'policy-employments', company], queryFn: () => policiesApi.employments(company), enabled: !!company && employeeNo.trim() !== '' && can('hrm.employments.read'), staleTime: 60_000 });
  const wanted = employeeNo.trim().toLowerCase();
  return wanted === '' ? undefined : (people.data ?? []).find((e) => e.employeeNo.toLowerCase() === wanted)?.id ?? null;
}

interface FormProps { company: string; isOpen: boolean; onOpenChange: (open: boolean) => void }

export function CorrectionDialog({ company, isOpen, onOpenChange, exceptionId, workDate, employmentId: fixedEmployment }: FormProps & { exceptionId?: string; workDate?: string; employmentId?: string }) {
  const { t, server } = useText();
  const { writable } = useShell();
  const toast = useToast();
  const refusal = useRefusal();
  const queryClient = useQueryClient();
  const [employeeNo, setEmployeeNo] = useState('');
  const [day, setDay] = useState<CalendarDate | null>(workDate ? parseDate(workDate) : null);
  const [timeIn, setTimeIn] = useState('');
  const [timeOut, setTimeOut] = useState('');
  const [nextDay, setNextDay] = useState(false);
  const [reason, setReason] = useState('');
  const [problem, setProblem] = useState<ApiProblem | null>(null);
  const looked = useEmployeeLookup(company, fixedEmployment ? '' : employeeNo);
  const found = fixedEmployment ?? looked;
  const field = (name: string) => { const e = problem?.fieldErrors[name]?.[0]; return e ? server(e) ?? e : null; };
  const badTime = (v: string) => v.trim() !== '' && !TIME.test(v.trim());
  const ready = reason.trim() !== '' && (exceptionId !== undefined || day !== null) && (timeIn.trim() !== '' || timeOut.trim() !== '') && !badTime(timeIn) && !badTime(timeOut) && found !== null;
  const send = useMutation({
    mutationFn: () => attendanceRequestsApi.regularize({
      ...(found ? { employmentId: found } : {}),
      ...(exceptionId ? { exceptionId } : { workDate: day?.toString() ?? '' }),
      ...optional('requestedInTime', withSeconds(timeIn)),
      ...(withSeconds(timeOut) ? { requestedOutTime: withSeconds(timeOut) ?? '', requestedOutDayOffset: nextDay ? 1 : 0 } : {}),
      reason,
    }),
    onSuccess: async () => {
      setProblem(null);
      onOpenChange(false);
      toast.success(t('req.sent'));
      await queryClient.invalidateQueries({ queryKey: ['hrm', 'attendance-requests'] });
      await queryClient.invalidateQueries({ queryKey: ['hrm', 'attendance-exceptions'] });
    },
    onError: (e) => setProblem(e instanceof ApiProblem ? e : null),
  });
  const general = problem && !Object.keys(problem.fieldErrors).length ? refusal(problem) : null;
  return (
    <Dialog title={t('req.form.correction.title')} isOpen={isOpen} onOpenChange={onOpenChange}
      actions={(close) => (<>
        <Button variant="quiet" onPress={close}>{t('req.cancel')}</Button>
        <Button variant="primary" isPending={send.isPending} isDisabled={!writable || !ready} onPress={() => send.mutate()}>{t('req.send')}</Button>
      </>)}>
      {general && <Banner tone="danger" title={general}>{null}</Banner>}
      {fixedEmployment === undefined && <TextField label={t('req.form.employee')} help={t('req.form.employee.help')} value={employeeNo} onChange={setEmployeeNo} error={found === null ? t('req.error.generic') : field('employmentId')} isCode />}
      {!exceptionId && <DateField label={t('req.form.day')} isRequired value={day} onChange={(v) => setDay(v ? parseDate(v.toString()) : null)} error={field('workDate')} />}
      <TextField label={t('req.form.in')} value={timeIn} onChange={setTimeIn} error={badTime(timeIn) ? t('req.error.generic') : field('requestedIn')} isCode inputMode="numeric" maxLength={5} />
      <TextField label={t('req.form.out')} value={timeOut} onChange={setTimeOut} error={badTime(timeOut) ? t('req.error.generic') : field('requestedOut')} isCode inputMode="numeric" maxLength={5} />
      <CheckboxField label={t('req.form.out.next')} isSelected={nextDay} onChange={setNextDay} />
      <TextAreaField label={t('req.form.reason')} isRequired value={reason} onChange={setReason} maxLength={500} error={field('reason')} />
      <p>{t('req.proof')}</p>
    </Dialog>
  );
}

export function DutyDialog({ company, isOpen, onOpenChange }: FormProps) {
  const { t, server } = useText();
  const { writable } = useShell();
  const toast = useToast();
  const refusal = useRefusal();
  const queryClient = useQueryClient();
  const [employeeNo, setEmployeeNo] = useState('');
  const [kind, setKind] = useState('');
  const [first, setFirst] = useState<CalendarDate | null>(null);
  const [last, setLast] = useState<CalendarDate | null>(null);
  const [from, setFrom] = useState('');
  const [until, setUntil] = useState('');
  const [purpose, setPurpose] = useState('');
  const [problem, setProblem] = useState<ApiProblem | null>(null);
  const found = useEmployeeLookup(company, employeeNo);
  const field = (name: string) => { const e = problem?.fieldErrors[name]?.[0]; return e ? server(e) ?? e : null; };
  const badTime = (v: string) => v.trim() !== '' && !TIME.test(v.trim());
  const ready = kind !== '' && first !== null && last !== null && purpose.trim() !== '' && !badTime(from) && !badTime(until) && found !== null;
  const send = useMutation({
    mutationFn: () => attendanceRequestsApi.duty({
      ...(found ? { employmentId: found } : {}), kind, startDate: first?.toString() ?? '', endDate: last?.toString() ?? '',
      ...optional('startTime', withSeconds(from)), ...optional('endTime', withSeconds(until)), purpose,
    }),
    onSuccess: async () => {
      setProblem(null);
      onOpenChange(false);
      toast.success(t('req.sent'));
      await queryClient.invalidateQueries({ queryKey: ['hrm', 'attendance-requests'] });
    },
    onError: (e) => setProblem(e instanceof ApiProblem ? e : null),
  });
  const general = problem && !Object.keys(problem.fieldErrors).length ? refusal(problem) : null;
  return (
    <Dialog title={t('req.form.duty.title')} isOpen={isOpen} onOpenChange={onOpenChange}
      actions={(close) => (<>
        <Button variant="quiet" onPress={close}>{t('req.cancel')}</Button>
        <Button variant="primary" isPending={send.isPending} isDisabled={!writable || !ready} onPress={() => send.mutate()}>{t('req.send')}</Button>
      </>)}>
      {general && <Banner tone="danger" title={general}>{null}</Banner>}
      <TextField label={t('req.form.employee')} help={t('req.form.employee.help')} value={employeeNo} onChange={setEmployeeNo} error={found === null ? t('req.error.generic') : field('employmentId')} isCode />
      <SelectField label={t('req.form.kind')} isRequired options={DUTY_KINDS.map((k) => ({ id: k, label: t(`req.kind.${k}` as MessageKey) }))} value={kind} onChange={setKind} error={field('kind')} />
      <DateField label={t('req.form.first')} isRequired value={first} onChange={(v) => setFirst(v ? parseDate(v.toString()) : null)} error={field('startDate')} />
      <DateField label={t('req.form.last')} isRequired value={last} onChange={(v) => setLast(v ? parseDate(v.toString()) : null)} error={field('endDate')} />
      <TextField label={t('req.form.from')} help={t('req.form.time.help')} value={from} onChange={setFrom} error={badTime(from) ? t('req.error.generic') : field('startTime')} isCode inputMode="numeric" maxLength={5} />
      <TextField label={t('req.form.to')} value={until} onChange={setUntil} error={badTime(until) ? t('req.error.generic') : field('endTime')} isCode inputMode="numeric" maxLength={5} />
      <TextAreaField label={t('req.form.purpose')} isRequired value={purpose} onChange={setPurpose} maxLength={500} error={field('purpose')} />
    </Dialog>
  );
}
