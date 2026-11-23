// The scheme form of the numbering screen (slice S1.15c Build item 9; FR-KRN-NUM-01, -02; S1.15 ruling Q11): set a scheme, change one that
// has issued no number, or "retire and replace" one that has. A used scheme is never edited in place (S1.08). The example is worked out here
// from the same rules as the server's; the server checks the pattern again on every save and its refusals are shown beside the fields.
import { useMemo, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Banner, Button, Dialog, NumberField, SelectField, SwitchField, TextField, type Option } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import { numberingApi, RESET_RULES, type NumberedType, type ResetRule, type SchemeRow, type SchemeWrite } from '../../api/numbering';
import { useShell } from '../../shell/context';
import { useRefused } from './config-parts';
import { checkPattern, renderExample, TOKENS, tooLong, todayIn, usesCodes } from './numbering-model';
import './numbering-messages';

export type FormMode = 'new' | 'edit' | 'replace';
export interface BranchOption { id: string; code: string; name: string }

interface Props {
  mode: FormMode;
  type: NumberedType;
  typeName: string;
  companyId: string;
  /** The scheme being changed or replaced. */
  scheme?: SchemeRow;
  branches: BranchOption[];
  fiscalStartMonth: number;
  onClose: () => void;
  onDone: (mode: FormMode) => void;
}

const writeOf = (s: SchemeRow): SchemeWrite => ({ companyId: s.companyId, branchId: s.branchId, documentType: s.documentType, pattern: s.pattern, resetRule: s.resetRule, gapFree: s.gapFree, startValue: s.startValue });

export function SchemeForm({ mode, type, typeName, companyId, scheme, branches, fiscalStartMonth, onClose, onDone }: Props) {
  const { t, server } = useText();
  const { me } = useShell();
  const queryClient = useQueryClient();
  const refused = useRefused();
  const company = me.companies.find((c) => c.id === companyId) ?? null;
  const [branchId, setBranchId] = useState<string | null>(scheme?.branchId ?? null);
  const [pattern, setPattern] = useState(scheme?.pattern ?? type.defaultPattern);
  const [reset, setReset] = useState<ResetRule>(scheme?.resetRule ?? type.defaultReset);
  const [gapFree, setGapFree] = useState(scheme?.gapFree ?? false);
  const [start, setStart] = useState(scheme?.startValue ?? 1);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const branch = branches.find((b) => b.id === branchId) ?? null;
  const facts = { hasCompany: !type.tenantWide, hasBranch: type.hasBranch, branchScheme: branchId !== null };
  const patternError = useMemo(() => checkPattern(pattern, reset, facts), [pattern, reset, facts.hasCompany, facts.hasBranch, facts.branchScheme]); // eslint-disable-line react-hooks/exhaustive-deps
  const example = useMemo(() => renderExample(pattern, { date: todayIn(me.timeZone), fiscalStartMonth, company: company?.code ?? '', branch: branch?.code ?? '', value: start }), [pattern, me.timeZone, fiscalStartMonth, company, branch, start]);
  const codes = usesCodes(pattern);
  const resetOptions: Option[] = RESET_RULES.map((r) => ({ id: r, label: t(`num.reset.${r}` as MessageKey) }));
  const title = t(`num.form.title.${mode}` as MessageKey, { name: typeName });
  const branchOptions: Option[] = [{ id: '', label: t('num.form.branch.company') }, ...branches.map((b) => ({ id: b.id, label: b.name, detail: b.code }))];

  const submit = async () => {
    const found: Record<string, string> = {};
    if (patternError) found.pattern = server(patternError) ?? t('num.form.error.fix');
    if (!Number.isInteger(start) || start < 1) found.startValue = t('num.form.error.start');
    setErrors(found);
    if (Object.keys(found).length > 0) { setSummary(t('num.form.error.fix')); return; }
    const body: SchemeWrite = { pattern, resetRule: reset, gapFree, startValue: start };
    setBusy(true);
    setSummary(null);
    try {
      if (mode === 'edit' && scheme) {
        await numberingApi.edit(scheme.id, { ...body, branchId: scheme.branchId, rowVersion: scheme.rowVersion });
      } else if (mode === 'replace' && scheme) {
        if (!await replace(scheme, { ...body, companyId: scheme.companyId, branchId: scheme.branchId, documentType: scheme.documentType })) return;
      } else {
        await numberingApi.create({ ...body, companyId: type.tenantWide ? null : companyId, branchId, documentType: type.code });
      }
      await queryClient.invalidateQueries({ queryKey: ['cfg', 'numbering'] });
      onDone(mode);
    } catch (error) {
      const r = refused(error);
      setErrors(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };

  /** Retire the old scheme, then set the new one; when the new one is refused the old pattern is set again (it continues its own series), so numbering never stops by accident. */
  const replace = async (old: SchemeRow, next: SchemeWrite): Promise<boolean> => {
    await numberingApi.retire(old.id, old.rowVersion);
    try {
      await numberingApi.create(next);
      return true;
    } catch (error) {
      let restored = true;
      try {
        await numberingApi.create(writeOf(old));
      } catch {
        restored = false;
      }
      await queryClient.invalidateQueries({ queryKey: ['cfg', 'numbering'] });
      const r = refused(error);
      setErrors(r.fields);
      setSummary(restored ? t('num.form.replace.failed', { reason: Object.values(r.fields)[0] ?? r.summary }) : t('num.form.replace.stranded'));
      return false;
    }
  };

  return (
    <Dialog title={title} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" isPending={busy} onPress={() => { void submit(); }}>{mode === 'replace' ? t('num.form.replace') : t('num.form.save')}</Button>
        </>
      )}>
      <div className="erp-stack">
        {summary && <Banner tone="danger">{summary}</Banner>}
        {mode === 'replace' && <Banner tone="info">{t('num.explain.used')}</Banner>}
        {mode === 'new' && type.hasBranch && branches.length > 0 && (
          <SelectField label={t('num.form.branch')} help={t('num.form.branch.help')} options={branchOptions} value={branchId ?? ''} onChange={(id) => setBranchId(id || null)} />
        )}
        <TextField label={t('num.form.pattern')} help={t('num.form.pattern.help')} isCode isRequired value={pattern} onChange={setPattern} maxLength={80} error={errors.pattern ?? (pattern && patternError ? (server(patternError) ?? null) : null)} />
        <ul className="erp-num-tokens" aria-label={t('num.form.tokens')}>
          {TOKENS.map((token) => (
            <li key={token}><code className="erp-code">{token === 'seq' ? '{seq:N}' : `{${token}}`}</code> <span className="erp-small erp-muted">{t(`num.token.${token}` as MessageKey)}</span></li>
          ))}
        </ul>
        <p className="erp-num-example" aria-live="polite">
          {example === null ? t('num.form.example.none') : (
            <>{t('num.form.example', { example: `\u2066${example}\u2069` })}{tooLong(example) && <> {t('num.form.example.long')}</>}</>
          )}
        </p>
        <SelectField label={t('num.form.reset')} options={resetOptions} value={reset} onChange={(id) => setReset(id as ResetRule)} />
        <SwitchField label={t('num.form.gapFree')} isSelected={gapFree} onChange={setGapFree} isDisabled={!type.gapFreeAllowed} />
        {type.gapFreeAllowed && <p className="erp-small erp-muted">{t('num.form.gapFree.help')}</p>}
        <NumberField label={t('num.form.start')} value={start} onChange={setStart} minValue={1} isRequired error={errors.startValue ?? null} />
        {mode !== 'edit' && scheme && <p className="erp-small erp-muted">{t('num.explain.fixed')}</p>}
        {codes.company && <Banner tone="warning">{t('num.form.warn.company', { code: company ? company.code : '' })}</Banner>}
        {codes.branch && <Banner tone="warning">{t('num.form.warn.branch')}</Banner>}
      </div>
    </Dialog>
  );
}
