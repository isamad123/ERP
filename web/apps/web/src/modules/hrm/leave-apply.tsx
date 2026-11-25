// Apply for leave (slice S1.33a; FR-HRM-LVE-04, -05, -16, GHR-09, FR-XM-32): the person picks a type and dates (or half days, or hours), sees the days counted by the
// server's rules, the balance before and after and every reason for a refusal as they type (a preview that saves nothing), attaches a document (required by some types;
// a camera photo can be taken on a phone) and sends the request. HR and managers with a wide scope can apply on behalf of an employee: the screen says who acts for whom.
// The same form changes approved leave (`?change=<id>`: a new request linked to the old one) and finishes a draft (`?draft=<id>`). Every result is the server's answer.
import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Camera, Paperclip, X } from 'lucide-react';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, DateField, EmptyState, ErrorPanel, IconButton, PageHeader, SelectField, Skeleton, TextAreaField, TextField, useToast } from '@erp/ui';
import { isolate, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { leaveApi, type LeaveBody, type LeavePart, type LeavePreview } from '../../api/leave';
import { PROOF_ACCEPT, PROOF_OWNER } from '../../api/attendance-requests';
import { policiesApi } from '../../api/policies';
import { useShell } from '../../shell/context';
import { UploadProgress, useProofUploads } from './proof-files';
import { DayList, TypeLabel, typeNameOf, useLeaveRefusal, useLeaveTypes, useQuantity, useReasons } from './leave-parts';
import './leave.css';

const SELF = '__self__';
const PARTS: LeavePart[] = ['full', 'first_half', 'second_half'];
const IMAGE_ACCEPT = 'image/*';

/** The files chosen for a request: choose from the device, or take a photo (the camera of a phone opens through the `capture` attribute of an image input). */
function FilePicker({ uploads, required, isDisabled }: { uploads: ReturnType<typeof useProofUploads>; required: boolean; isDisabled: boolean }) {
  const { t } = useText();
  const file = useRef<HTMLInputElement>(null);
  const photo = useRef<HTMLInputElement>(null);
  const add = (e: React.ChangeEvent<HTMLInputElement>) => { uploads.add(Array.from(e.target.files ?? [])); e.target.value = ''; };
  return (
    <div className="erp-field" role="group" aria-label={t('leave.apply.document')}>
      <span className="erp-label">{required ? t('leave.apply.document.required') : t('leave.apply.document')}</span>
      <input ref={file} type="file" multiple accept={PROOF_ACCEPT} hidden aria-hidden="true" tabIndex={-1} data-testid="leave-file-input" onChange={add} />
      <input ref={photo} type="file" accept={IMAGE_ACCEPT} capture="environment" hidden aria-hidden="true" tabIndex={-1} data-testid="leave-photo-input" onChange={add} />
      <div className="erp-row erp-wrap">
        <Button variant="secondary" icon={<Paperclip size={16} aria-hidden="true" />} isDisabled={isDisabled} onPress={() => file.current?.click()}>{t('leave.apply.document.choose')}</Button>
        <Button variant="secondary" icon={<Camera size={16} aria-hidden="true" />} isDisabled={isDisabled} onPress={() => photo.current?.click()}>{t('leave.apply.document.photo')}</Button>
      </div>
      <span className="erp-help">{t('leave.apply.document.help')}</span>
      {uploads.files.length === 0 ? <span className="erp-muted">{t('leave.apply.document.none')}</span> : (
        <ul className="erp-stack">
          {uploads.files.map((f, i) => (
            <li key={`${f.name}-${i}`} className="erp-row erp-wrap">
              <bdi>{f.name}</bdi>
              <IconButton label={t('leave.apply.document.remove', { name: f.name })} icon={<X size={16} aria-hidden="true" />} isDisabled={isDisabled} onPress={() => uploads.remove(i)} />
            </li>))}
        </ul>
      )}
    </div>
  );
}

export default function ApplyScreen() {
  const { t, server } = useText();
  const { locale } = useLocaleSettings();
  const { can, me, activeCompany, writable } = useShell();
  const navigate = useNavigate();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refusal = useLeaveRefusal();
  const reasons = useReasons();
  const qty = useQuantity();
  const [params] = useSearchParams();
  const changeId = params.get('change');
  const draftId = params.get('draft');
  const sourceId = changeId ?? draftId;
  useDocumentTitle(t(changeId ? 'leave.change.title' : 'leave.apply.title'));

  const source = useQuery({ queryKey: ['hrm', 'leave', 'details', sourceId], queryFn: ({ signal }) => leaveApi.details(sourceId as string, signal), enabled: !!sourceId });
  const [company, setCompany] = useState<string>(activeCompany?.id ?? me.defaultCompanyId ?? me.companies[0]?.id ?? '');
  const [employment, setEmployment] = useState<string>(SELF);
  const [typeId, setTypeId] = useState('');
  const [start, setStart] = useState<CalendarDate | null>(null);
  const [end, setEnd] = useState<CalendarDate | null>(null);
  const [startPart, setStartPart] = useState<LeavePart>('full');
  const [endPart, setEndPart] = useState<LeavePart>('full');
  const [hours, setHours] = useState('');
  const [reason, setReason] = useState('');
  const [problem, setProblem] = useState<string | null>(null);
  const [fieldProblems, setFieldProblems] = useState<Record<string, string>>({});
  const [draft, setDraft] = useState<{ id: string; rowVersion: number } | null>(null);
  const uploads = useProofUploads(PROOF_OWNER.leave);
  const filled = useRef(false);

  // Prefill from the request being changed or finished.
  useEffect(() => {
    const d = source.data;
    if (!d || filled.current) return;
    filled.current = true;
    setCompany(d.companyId);
    setTypeId(d.leaveTypeId);
    setStart(parseDate(d.startDate));
    setEnd(d.endDate !== d.startDate ? parseDate(d.endDate) : null);
    setStartPart(d.startPart);
    setEndPart(d.endPart);
    setHours(d.hours === null ? '' : String(d.hours));
    setReason(d.reason ?? '');
    if (!d.onBehalf) setEmployment(SELF); else setEmployment(d.employmentId);
  }, [source.data]);

  const onBehalfAllowed = can('hrm.employments.read');
  const types = useLeaveTypes(company, true);
  const type = types.data?.find((x) => x.id === typeId);
  const people = useQuery({ queryKey: ['hrm', 'policy-employments', company], queryFn: () => policiesApi.employments(company), enabled: !!company && onBehalfAllowed, staleTime: 60_000 });
  const chosenPerson = people.data?.find((p) => p.id === employment);
  const behalf = employment !== SELF;

  const hourly = type?.unit === 'hour';
  const halfDays = type?.unit === 'half_day';
  const single = end === null || (start !== null && end.compare(start) === 0);
  const hoursNumber = hours.trim() === '' ? NaN : Number(hours);
  const hoursOk = !hourly || (Number.isFinite(hoursNumber) && hoursNumber > 0);
  const effectiveEnd = hourly ? null : end;

  const body = useMemo((): LeaveBody | null => {
    if (!type || !start || !hoursOk) return null;
    const lastIsSingle = effectiveEnd === null || effectiveEnd.compare(start) === 0;
    return {
      ...(behalf ? { employmentId: employment } : {}),
      leaveTypeId: type.id,
      startDate: start.toString(),
      endDate: (effectiveEnd ?? start).toString(),
      ...(halfDays ? { startPart, endPart: lastIsSingle ? startPart : endPart } : {}),
      ...(hourly ? { hours: hoursNumber } : {}),
    };
  }, [type, start, effectiveEnd, startPart, endPart, hoursNumber, hoursOk, halfDays, hourly, behalf, employment]);

  // The preview waits for a short pause in typing, then asks the server (it saves nothing).
  const [asked, setAsked] = useState<LeaveBody | null>(null);
  useEffect(() => {
    const timer = window.setTimeout(() => setAsked(body), 300);
    return () => window.clearTimeout(timer);
  }, [body]);
  const preview = useQuery({
    queryKey: ['hrm', 'leave', 'preview', asked],
    queryFn: ({ signal }) => leaveApi.preview(asked as LeaveBody, signal),
    enabled: asked !== null && body !== null,
    retry: false,
    staleTime: 0,
  });
  const result: LeavePreview | undefined = body && asked ? preview.data : undefined;
  const previewProblem = preview.isError && preview.error instanceof ApiProblem ? preview.error : null;

  const documentMissing = !!type?.requiresDocument && uploads.files.length === 0 && !draft;
  const send = useMutation({
    mutationFn: async () => {
      if (!body) throw new ApiProblem(0, 'leave.error.generic', [], {}, null);
      const full: LeaveBody = { ...body, reason };
      const withFiles = uploads.files.length > 0 || (type?.requiresDocument ?? false);
      let saved: { id: string; rowVersion: number };
      if (draft) saved = draft;
      else if (draftId) saved = await leaveApi.updateDraft(draftId, { ...full, submit: false, rowVersion: source.data?.rowVersion ?? 0 });
      else if (changeId) saved = await leaveApi.change(changeId, { ...full, submit: !withFiles });
      else saved = await leaveApi.create({ ...full, submit: !withFiles });
      if (!withFiles && !draftId) return saved;
      // A draft is attached to, then sent (a document of a new request can only be attached once the request exists).
      setDraft(saved);
      const ok = uploads.files.length === 0 || (uploads.states.length > 0 ? await uploads.retry() : await uploads.start(saved.id));
      if (!ok) throw new ApiProblem(0, 'leave.upload.failed', [], {}, null);
      const fresh = await leaveApi.details(saved.id);
      return await leaveApi.submit(saved.id, fresh.rowVersion);
    },
    onSuccess: async (saved) => {
      setProblem(null);
      toast.success(t('leave.apply.sent'));
      await queryClient.invalidateQueries({ queryKey: ['hrm', 'leave'] });
      await queryClient.invalidateQueries({ queryKey: ['wf'] });
      void navigate(`/time/leave/requests/${saved.id}`);
    },
    onError: (e) => {
      if (e instanceof ApiProblem && e.key === 'leave.upload.failed') { setProblem(t('leave.apply.uploadFailed')); return; }
      if (e instanceof ApiProblem && Object.keys(e.fieldErrors).length > 0) {
        setFieldProblems(Object.fromEntries(Object.entries(e.fieldErrors).map(([k, v]) => [k, server(v[0] ?? '') ?? t('leave.error.generic')])));
        setProblem(refusal(e));
        return;
      }
      setProblem(refusal(e));
    },
  });

  const press = () => {
    setFieldProblems({});
    if (documentMissing) { setProblem(t('leave.apply.documentMissing')); return; }
    setProblem(null);
    send.mutate();
  };

  if (!can('hrm.leave_requests.create')) return <div className="erp-page"><PageHeader title={t('leave.apply.title')} /><EmptyState title={t('leave.denied')}>{t('leave.apply.denied.body')}</EmptyState></div>;
  if (sourceId && source.isPending) return <div className="erp-page"><PageHeader title={t('leave.apply.title')} /><Skeleton lines={6} /></div>;
  if (sourceId && source.isError) return <div className="erp-page"><PageHeader title={t('leave.apply.title')} /><ErrorPanel title={t('leave.loadError')} message={refusal(source.error)} action={<Button onPress={() => { void source.refetch(); }}>{t('leave.retry')}</Button>} /></div>;

  const companyOptions = me.companies.map((c) => ({ id: c.id, label: c.name[locale] ?? c.name.en ?? c.code }));
  const typeOptions = (types.data ?? []).map((x) => ({ id: x.id, label: typeNameOf(x, locale), detail: t(`leave.unit.label.${x.unit}` as MessageKey) }));
  const personOptions = [{ id: SELF, label: t('leave.apply.forMyself') }, ...(people.data ?? []).map((p) => ({ id: p.id, label: p.employeeNo }))];
  const partOptions = PARTS.map((p) => ({ id: p, label: t(`leave.part.${p}` as MessageKey) }));
  const field = (name: string) => fieldProblems[name] ?? null;
  const reasonList = result && !result.allowed ? reasons(result.error, result.errors, result.details) : [];
  const problemList = previewProblem ? reasons(null, previewProblem.fieldErrors as Record<string, string[]>, null).concat(Object.keys(previewProblem.fieldErrors).length === 0 ? [refusal(previewProblem)] : []) : [];
  const plan = result?.plan;
  const sendable = !!result?.allowed && writable && !send.isPending && !(draft && uploads.states.some((s) => s === 'uploading'));
  const lockedForm = !!draft;

  return (
    <div className="erp-page">
      <PageHeader title={changeId ? t('leave.change.title') : t('leave.apply.title')} description={changeId ? t('leave.change.description') : t('leave.apply.description')} />
      <div className="erp-leave-split">
        <form className="erp-leave-form" onSubmit={(e) => { e.preventDefault(); press(); }} aria-label={t('leave.apply.form')}>
          {problem && <Banner tone="danger" title={problem}>{null}</Banner>}
          {companyOptions.length > 1 && !changeId && !draftId && <SelectField label={t('leave.apply.company')} options={companyOptions} value={company} isDisabled={lockedForm}
            onChange={(id) => { setCompany(id); setTypeId(''); setEmployment(SELF); }} />}
          {onBehalfAllowed && !changeId && (
            <SelectField label={t('leave.apply.employee')} help={t('leave.apply.employee.help')} options={personOptions} value={employment} isDisabled={lockedForm || draftId !== null} onChange={setEmployment} />)}
          {(behalf || source.data?.onBehalf) && (
            <Banner tone="info" title={t('leave.apply.actingFor', { you: isolate(me.displayName), employee: isolate(chosenPerson?.employeeNo ?? source.data?.employeeNo ?? '') })}>{t('leave.apply.actingFor.body')}</Banner>)}
          <SelectField label={t('leave.apply.type')} isRequired options={typeOptions} value={typeId || null} error={field('leaveTypeId')}
            isDisabled={lockedForm || types.isPending} onChange={(id) => { setTypeId(id); setStartPart('full'); setEndPart('full'); }}
            help={types.isError ? t('leave.apply.type.loadError') : undefined} />
          {type && <div><TypeLabel name={typeNameOf(type, locale)} token={type.colorToken} /></div>}
          <DateField label={hourly ? t('leave.apply.day') : t('leave.apply.from')} isRequired value={start} isDisabled={lockedForm} error={field('startDate')} onChange={(v) => setStart(v ? parseDate(v.toString()) : null)} />
          {!hourly && <DateField label={t('leave.apply.to')} help={t('leave.apply.to.help')} value={end} isDisabled={lockedForm} error={field('endDate')} onChange={(v) => setEnd(v ? parseDate(v.toString()) : null)} />}
          {halfDays && (
            <>
              <SelectField label={single ? t('leave.apply.part.day') : t('leave.apply.part.first')} options={partOptions} value={startPart} isDisabled={lockedForm} error={field('startPart')} onChange={(id) => setStartPart(id as LeavePart)} />
              {!single && <SelectField label={t('leave.apply.part.last')} options={partOptions} value={endPart} isDisabled={lockedForm} error={field('endPart')} onChange={(id) => setEndPart(id as LeavePart)} />}
            </>)}
          {hourly && <TextField label={t('leave.apply.hours')} help={t('leave.apply.hours.help')} isRequired value={hours} isDisabled={lockedForm} inputMode="numeric" error={field('hours')} onChange={setHours} isCode />}
          <TextAreaField label={t('leave.apply.reason')} value={reason} maxLength={500} isDisabled={lockedForm} error={field('reason')} onChange={setReason} />
          {type && <FilePicker uploads={uploads} required={type.requiresDocument} isDisabled={send.isPending || !!draft && !uploads.states.some((s) => s === 'failed')} />}
          {documentMissing && <p role="status" className="erp-help">{t('leave.apply.documentMissing')}</p>}
          {field('fileIds') && <p role="alert" className="erp-field-error">{field('fileIds')}</p>}
          {draft && <UploadProgress uploads={uploads} onRetry={() => press()} onClose={() => { void navigate(`/time/leave/requests/${draft.id}`); }} />}
          <div className="erp-row">
            <Button variant="primary" type="submit" isPending={send.isPending} isDisabled={!sendable}>{t(changeId ? 'leave.change.send' : 'leave.apply.send')}</Button>
            <Button variant="quiet" onPress={() => { void navigate('/time/leave'); }}>{t('leave.apply.cancel')}</Button>
          </div>
          {!writable && <p className="erp-muted">{t('leave.readOnly')}</p>}
        </form>

        <section aria-labelledby="erp-leave-preview" className="erp-stack" aria-live="polite">
          <h2 id="erp-leave-preview">{t('leave.preview.title')}</h2>
          {body === null && <p className="erp-muted">{t('leave.preview.waiting')}</p>}
          {body !== null && (preview.isFetching || asked !== body) && !result && !previewProblem && <Skeleton lines={4} />}
          {previewProblem && (
            <Banner tone="danger" title={t('leave.preview.refused')}>
              <ul>{problemList.map((line) => <li key={line}>{line}</li>)}</ul>
            </Banner>)}
          {preview.isError && !previewProblem && <ErrorPanel message={t('leave.preview.error')} />}
          {result && !result.allowed && (
            <Banner tone="danger" title={t('leave.preview.refused')}>
              <ul>{reasonList.map((line) => <li key={line}>{line}</li>)}</ul>
            </Banner>)}
          {result && result.allowed && <Banner tone="success" title={t('leave.preview.ok')}>{null}</Banner>}
          {plan?.days && plan.days.length > 0 && (
            <>
              <DayList unit={plan.unit} days={plan.days.map((d) => ({ date: d.date, quantity: d.quantity, kind: d.kind, text: server(d.reason) }))} />
              <dl className="erp-leave-sum">
                <dt>{t('leave.preview.total')}</dt><dd data-strong>{qty(plan.total, plan.unit)}</dd>
                {plan.balance !== null && <><dt>{t('leave.preview.before')}</dt><dd>{qty(plan.balance, plan.unit)}</dd></>}
                {plan.pending !== null && plan.pending !== 0 && <><dt>{t('leave.preview.pending')}</dt><dd>{qty(plan.pending, plan.unit)}</dd></>}
                {plan.after !== null && <><dt>{t('leave.preview.after')}</dt><dd data-strong>{qty(plan.after, plan.unit)}</dd></>}
                {plan.lowest !== null && plan.after !== null && plan.lowest !== plan.after && <><dt>{t('leave.preview.lowest')}</dt><dd>{qty(plan.lowest, plan.unit)}</dd></>}
                {plan.leaveYear !== null && <><dt>{t('leave.preview.year')}</dt><dd><bdi>{plan.leaveYear}</bdi></dd></>}
              </dl>
            </>)}
          {!result?.allowed && plan?.days && <p className="erp-muted">{t('leave.preview.notSent')}</p>}
        </section>
      </div>
    </div>
  );
}
