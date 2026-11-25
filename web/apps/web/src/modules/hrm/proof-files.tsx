// Proof files of an attendance request (slice S1.30b, R-161; FR-KRN-DOC-01, FR-HRM-ATT-11): the person picks files in the form, the request is made first, then each
// file is uploaded to the new request. A failed upload never loses the request: the person is told which file failed and can retry into the same request.
// Files are listed on a request, downloaded through the signed-in client (no token in an address) and deleted while the request waits.
import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Download, Paperclip, Trash2, X } from 'lucide-react';
import { Banner, Button, ErrorPanel, IconButton, Skeleton, useToast } from '@erp/ui';
import { useText } from '@erp/i18n';
import { PROOF_ACCEPT, proofFilesApi, type ProofFile, type ProofOwner } from '../../api/attendance-requests';
import { useRefusal } from './requests-forms';
import './requests-messages';

export type UploadState = 'queued' | 'uploading' | 'done' | 'failed';

/** The files chosen in a form and, after the request exists, the state of each upload. */
export function useProofUploads(owner: ProofOwner) {
  const [files, setFiles] = useState<File[]>([]);
  const [states, setStates] = useState<UploadState[]>([]);
  const [target, setTarget] = useState<string | null>(null);
  const run = async (ownerId: string, list: File[], current: UploadState[]): Promise<boolean> => {
    const next = [...current];
    for (let i = 0; i < list.length; i += 1) {
      if (next[i] === 'done') continue;
      next[i] = 'uploading';
      setStates([...next]);
      try {
        await proofFilesApi.upload(owner, ownerId, list[i] as File);
        next[i] = 'done';
      } catch {
        next[i] = 'failed';
      }
      setStates([...next]);
    }
    return next.every((s) => s === 'done');
  };
  return {
    files,
    states,
    target,
    add: (added: File[]) => setFiles((current) => [...current, ...added]),
    remove: (index: number) => setFiles((current) => current.filter((_, i) => i !== index)),
    /** Uploads every file to the request; resolves true when all arrived. */
    start: async (ownerId: string): Promise<boolean> => { setTarget(ownerId); const queued = files.map(() => 'queued' as UploadState); setStates(queued); return run(ownerId, files, queued); },
    /** Uploads the failed files again, into the same request. */
    retry: async (): Promise<boolean> => (target ? run(target, files, states.map((s) => (s === 'failed' ? 'queued' : s))) : false),
  };
}

export type ProofUploads = ReturnType<typeof useProofUploads>;

export function ProofPicker({ uploads, isDisabled }: { uploads: ProofUploads; isDisabled?: boolean }) {
  const { t } = useText();
  const input = useRef<HTMLInputElement>(null);
  return (
    <div className="erp-field" role="group" aria-label={t('req.proof')}>
      <span className="erp-label">{t('req.proof')}</span>
      <input ref={input} type="file" multiple accept={PROOF_ACCEPT} hidden aria-hidden="true" tabIndex={-1} data-testid="proof-input"
        onChange={(e) => { uploads.add(Array.from(e.target.files ?? [])); e.target.value = ''; }} />
      <div><Button variant="secondary" icon={<Paperclip size={16} aria-hidden="true" />} isDisabled={isDisabled} onPress={() => input.current?.click()}>{t('req.proof.choose')}</Button></div>
      <span className="erp-help">{t('req.proof.help')}</span>
      {uploads.files.length === 0 ? <span className="erp-muted">{t('req.proof.none')}</span> : (
        <ul className="erp-stack">
          {uploads.files.map((file, i) => (
            <li key={`${file.name}-${i}`} className="erp-row erp-wrap">
              <bdi>{file.name}</bdi>
              <IconButton label={t('req.proof.remove', { name: file.name })} icon={<X size={16} aria-hidden="true" />} isDisabled={isDisabled ?? false} onPress={() => uploads.remove(i)} />
            </li>))}
        </ul>
      )}
    </div>
  );
}

/** After the request was made: one line per file with its state, and the retry for the failed ones. */
export function UploadProgress({ uploads, onRetry, onClose }: { uploads: ProofUploads; onRetry: () => void; onClose: () => void }) {
  const { t } = useText();
  const failed = uploads.states.some((s) => s === 'failed');
  const busy = uploads.states.some((s) => s === 'uploading' || s === 'queued');
  return (
    <div className="erp-stack">
      {failed && !busy && <Banner tone="warning" title={t('req.proof.sentTitle')}>{t('req.proof.sentBody')}</Banner>}
      <ul className="erp-stack" aria-live="polite">
        {uploads.files.map((file, i) => (
          <li key={`${file.name}-${i}`}><bdi>{file.name}</bdi>{' · '}{t(`req.proof.${uploads.states[i] ?? 'queued'}` as 'req.proof.queued')}</li>))}
      </ul>
      <div className="erp-row">
        {failed && !busy && <Button variant="primary" onPress={onRetry}>{t('req.proof.retry')}</Button>}
        <Button variant="quiet" isDisabled={busy} onPress={onClose}>{t('req.proof.close')}</Button>
      </div>
    </div>
  );
}

/** The files of one request, with download and (while it waits) delete. */
export function ProofList({ owner, ownerId, canDelete }: { owner: ProofOwner; ownerId: string; canDelete: boolean }) {
  const { t } = useText();
  const toast = useToast();
  const refusal = useRefusal();
  const queryClient = useQueryClient();
  const key = ['hrm', 'attendance-requests', 'proof', owner, ownerId];
  const files = useQuery({ queryKey: key, queryFn: ({ signal }) => proofFilesApi.list(owner, ownerId, signal) });
  const remove = useMutation({
    mutationFn: (id: string) => proofFilesApi.remove(id),
    onSuccess: async () => { toast.success(t('req.proof.deleted')); await queryClient.invalidateQueries({ queryKey: key }); },
    onError: (e) => toast.error(refusal(e)),
  });
  const download = async (file: ProofFile) => {
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
      toast.error(t('req.proof.downloadError'));
    }
  };
  if (files.isPending) return <Skeleton lines={1} />;
  if (files.isError) return <ErrorPanel message={t('req.proof.listError')} level={2} />;
  if (files.data.length === 0) return <p className="erp-muted">{t('req.proof.listEmpty')}</p>;
  return (
    <ul className="erp-stack">
      {files.data.map((file) => (
        <li key={file.id} className="erp-row erp-wrap">
          <bdi>{file.fileName}</bdi>
          <IconButton label={t('req.proof.download', { name: file.fileName })} icon={<Download size={16} aria-hidden="true" />} onPress={() => { void download(file); }} />
          {canDelete && <IconButton label={t('req.proof.delete', { name: file.fileName })} icon={<Trash2 size={16} aria-hidden="true" />} isDisabled={remove.isPending} onPress={() => remove.mutate(file.id)} />}
        </li>))}
    </ul>
  );
}
