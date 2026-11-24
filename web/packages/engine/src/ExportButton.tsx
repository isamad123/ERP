// Export of the list as it is now (slice S1.16b, FR-ADM-DAT-04, FR-ADM-NAV-02): a button that opens a small dialog, asks the server what the
// export would be (sensitive field classes, a reason, a second person's approval) and asks for it. The file is made in the background as the
// person, with the list's own search, filters, sort, data scope and field security; it is downloaded from the export centre. Its texts are
// registered when the module loads, with the list screens (not part of the shell's scripts, R-213).
import { useEffect, useMemo, useState } from 'react';
import { Download } from 'lucide-react';
import { Banner, Button, Dialog, Link, SelectField, TextField, useToast } from '@erp/ui';
import { registerMessages, useLocaleSettings, useText } from '@erp/i18n';
import en from '@erp/i18n/messages/exports.en.json';
import ur from '@erp/i18n/messages/exports.ur.json';
import { isProblem, useEngineApi } from './data';

registerMessages({ en, ur });

export interface ExportColumnChoice { field: string; label: string }

export interface ExportButtonProps {
  /** The list's code on the server: its table (`org.branches`) or a screen's own list (`iam.user_overview`). */
  source: string;
  /** The list's name for the dialog's title. */
  title: string;
  /** The columns as the person sees them now, in order. */
  columns: ExportColumnChoice[];
  /** The list's own filters (its query string values). */
  query: Record<string, string | number | boolean | null | undefined>;
  search?: string | null;
  sort?: string | null;
  /** The company in hand: the export is filed under it. */
  companyId: string | null;
  centreHref?: string;
}

interface Check {
  sensitiveClasses: string[];
  reasonRequired: boolean;
  needsApproval: boolean;
  approvalOn: boolean;
  rows: number;
  rowsCapped: boolean;
  approvalRows: number;
  hidden: string[];
}

export const EXPORT_CENTRE = '/admin/exports';

export default function ExportButton(props: ExportButtonProps) {
  const { source, title, columns, query, search, sort, companyId, centreHref = EXPORT_CENTRE } = props;
  const { t, server } = useText();
  const { locale } = useLocaleSettings();
  const api = useEngineApi();
  const toast = useToast();
  const [open, setOpen] = useState(false);
  const [format, setFormat] = useState<'csv' | 'xlsx'>('csv');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const clean = Object.fromEntries(Object.entries(query).filter(([, v]) => v !== undefined && v !== null && v !== ''));
  const body = (extra: Record<string, unknown>) => JSON.stringify({ source, query: clean, search: search || null, sort: sort || null, columns, companyId, language: locale, ...extra });
  // What the export would be, asked of the server each time the dialog opens or the view changes (nothing is written).
  const [checked, setChecked] = useState<{ data: Check | null; error: unknown; pending: boolean }>({ data: null, error: null, pending: false });
  const request = useMemo(() => body({ format: 'csv' }), [source, clean, search, sort, columns, companyId, locale]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!open) return undefined;
    const abort = new AbortController();
    setChecked({ data: null, error: null, pending: true });
    api.send('POST', '/api/itg/exports/check', request, abort.signal)
      .then((text) => setChecked({ data: JSON.parse(text) as Check, error: null, pending: false }))
      .catch((e: unknown) => { if (!abort.signal.aborted) setChecked({ data: null, error: e, pending: false }); });
    return () => abort.abort();
  }, [open, request, api]);
  const check = { isPending: checked.pending, isError: checked.error !== null, error: checked.error, data: checked.data };
  useEffect(() => { if (open) { setError(null); setReason(''); } }, [open]);
  const failure = (e: unknown): string => (isProblem(e) ? (server(e.key) ?? t('exp.error.generic')) : t('exp.error.generic'));
  const data = check.data ?? null;
  const classes = (data?.sensitiveClasses ?? []).map((c) => t(`exp.class.${c}` as never)).join(', ');
  const start = async (close: () => void) => {
    setBusy(true);
    setError(null);
    try {
      const view = JSON.parse(await api.send('POST', '/api/itg/exports', body({ format, reason: reason.trim() || null }))) as { status: string };
      toast.success(view.status === 'awaiting_approval' ? t('exp.started.waiting') : t('exp.started'));
      close();
    } catch (e) {
      setError(failure(e));
    } finally {
      setBusy(false);
    }
  };
  const needReason = !!data?.reasonRequired;
  return (
    <>
      <Button variant="secondary" icon={<Download size={18} aria-hidden="true" />} isDisabled={!companyId || columns.length === 0} onPress={() => setOpen(true)}>{t('exp.button')}</Button>
      <Dialog title={t('exp.dialog.title', { list: title })} isOpen={open} onOpenChange={setOpen}
        actions={(close) => (
          <>
            <Button variant="secondary" onPress={close}>{t('exp.cancel')}</Button>
            <Button variant="primary" isPending={busy} isDisabled={!data || (needReason && reason.trim().length < 3)} onPress={() => { void start(close); }}>{t('exp.start')}</Button>
          </>
        )}>
        <div className="erp-stack">
          <p>{t('exp.dialog.intro')}</p>
          <SelectField label={t('exp.format')} value={format} onChange={(v) => setFormat(v === 'xlsx' ? 'xlsx' : 'csv')}
            options={[{ id: 'csv', label: t('exp.format.csv') }, { id: 'xlsx', label: t('exp.format.xlsx') }]} />
          <p className="erp-small erp-muted">{t('exp.columns.count', { count: columns.length })}</p>
          {check.isPending && <p role="status" className="erp-muted">{t('exp.error.checking')}</p>}
          {check.isError && <Banner tone="danger" title={t('exp.error.check')}>{isProblem(check.error) ? server(check.error.key) ?? '' : ''}</Banner>}
          {data && data.hidden.length > 0 && <p className="erp-small">{t('exp.hidden.body', { columns: data.hidden.join(', ') })}</p>}
          {data && needReason && (
            <>
              <Banner tone="warning" title={t('exp.sensitive.title')}>{t('exp.sensitive.body', { classes })}</Banner>
              <TextField label={t('exp.reason')} help={t('exp.reason.help')} isRequired value={reason} onChange={setReason} maxLength={500} />
            </>
          )}
          {data?.needsApproval && (
            <Banner tone="info" title={t('exp.approval.title')}>
              {t('exp.approval.body')}{data.rowsCapped ? ' ' + t('exp.rows.large', { rows: data.approvalRows }) : ''}
            </Banner>
          )}
          {error && <p role="alert" className="erp-field-error">{error}</p>}
          <p className="erp-small"><Link href={centreHref}>{t('exp.open')}</Link></p>
        </div>
      </Dialog>
    </>
  );
}
