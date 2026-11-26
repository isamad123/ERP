// Receipts of an expense claim (slice S1.42; FR-HRM-EXP-02, FR-KRN-DOC-01): files belong to the saved claim (owner type `hrm.expense_claims`) and are added only while it is a draft;
// each line then picks one of them. Download goes through the signed-in client (no token in an address). A failed upload says which file and keeps the others.
import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Download, Paperclip } from 'lucide-react';
import { Button, ErrorPanel, IconButton, Skeleton, useToast } from '@erp/ui';
import { useText } from '@erp/i18n';
import { PROOF_ACCEPT, PROOF_OWNER, proofFilesApi, type ProofFile } from '../../api/attendance-requests';
import './expenses-messages';

export const receiptsKey = (claimId: string) => ['hrm', 'expenses', 'receipts', claimId];

export function useReceipts(claimId: string | null) {
  return useQuery({ queryKey: receiptsKey(claimId ?? ''), queryFn: ({ signal }) => proofFilesApi.list(PROOF_OWNER.expense, claimId as string, signal), enabled: !!claimId });
}

export function useReceiptDownload() {
  const { t } = useText();
  const toast = useToast();
  return async (file: Pick<ProofFile, 'id' | 'fileName'>) => {
    try {
      const url = URL.createObjectURL(await proofFilesApi.content(file.id));
      const link = document.createElement('a');
      link.href = url;
      link.download = file.fileName;
      document.body.append(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    } catch {
      toast.error(t('expc.receipts.downloadError'));
    }
  };
}

/** One receipt as a button that downloads it (used in the lines of a read-only claim and in the approvals card). */
export function ReceiptLink({ claimId, fileId }: { claimId: string; fileId: string | null }) {
  const { t } = useText();
  const files = useReceipts(claimId);
  const download = useReceiptDownload();
  if (!fileId) return <span className="erp-muted">{t('expc.receipt.none')}</span>;
  const file = files.data?.find((f) => f.id === fileId);
  if (!file) return <span className="erp-muted">{files.isPending ? t('expc.loading') : t('expc.receipt.attached')}</span>;
  return <Button variant="quiet" icon={<Download size={16} aria-hidden="true" />} onPress={() => { void download(file); }}><bdi>{file.fileName}</bdi></Button>;
}

/** The receipts of the claim: the list and, for a saved draft, the way to add more. */
export function ReceiptsPanel({ claimId, canAdd }: { claimId: string | null; canAdd: boolean }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const files = useReceipts(claimId);
  const download = useReceiptDownload();
  const [failed, setFailed] = useState<string[]>([]);
  const upload = useMutation({
    mutationFn: async (chosen: File[]) => {
      const lost: string[] = [];
      for (const file of chosen) {
        try { await proofFilesApi.upload(PROOF_OWNER.expense, claimId as string, file); } catch { lost.push(file.name); }
      }
      return lost;
    },
    onSuccess: async (lost) => {
      setFailed(lost);
      if (lost.length === 0) toast.success(t('expc.receipts.added'));
      await queryClient.invalidateQueries({ queryKey: receiptsKey(claimId ?? '') });
    },
  });
  return (
    <section aria-labelledby="erp-exp-receipts" className="erp-stack">
      <h2 id="erp-exp-receipts">{t('expc.receipts')}</h2>
      {!claimId && <p className="erp-muted">{t('expc.receipts.saveFirst')}</p>}
      {claimId && files.isPending && <Skeleton lines={1} />}
      {claimId && files.isError && <ErrorPanel message={t('expc.receipts.listError')} level={2} />}
      {claimId && files.data && (files.data.length === 0 ? <p className="erp-muted">{t('expc.receipts.empty')}</p> : (
        <ul className="erp-stack">
          {files.data.map((file) => (
            <li key={file.id} className="erp-row erp-wrap">
              <bdi>{file.fileName}</bdi>
              <IconButton label={t('expc.receipts.download', { name: file.fileName })} icon={<Download size={16} aria-hidden="true" />} onPress={() => { void download(file); }} />
            </li>))}
        </ul>))}
      {failed.length > 0 && <p role="alert" className="erp-field-error">{t('expc.receipts.failed', { names: failed.join(', ') })}</p>}
      {claimId && canAdd && (
        <div className="erp-stack">
          <input ref={input} type="file" multiple accept={PROOF_ACCEPT} hidden aria-hidden="true" tabIndex={-1} data-testid="receipt-input"
            onChange={(e) => { const chosen = Array.from(e.target.files ?? []); e.target.value = ''; if (chosen.length > 0) upload.mutate(chosen); }} />
          <div><Button variant="secondary" icon={<Paperclip size={16} aria-hidden="true" />} isPending={upload.isPending} onPress={() => input.current?.click()}>{t('expc.receipts.add')}</Button></div>
          <span className="erp-help erp-muted">{t('expc.receipts.help')}</span>
        </div>)}
    </section>
  );
}
