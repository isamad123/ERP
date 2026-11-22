// The company logo (slice S1.14a Build item 1, FR-ADM-ORG-01): chosen from a file of the person's computer, stored in the
// file store as a file of the company (the server scans and checks it), and shown as a small picture. The company record
// points to it when the form is saved.
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Upload, X } from 'lucide-react';
import type { FieldEditorProps } from '@erp/engine';
import { Banner, Button } from '@erp/ui';
import { useText } from '@erp/i18n';
import { org } from '../../api/org';
import { useProblemText } from '../admin/users-parts';

const ACCEPT = 'image/png,image/jpeg,image/svg+xml,image/webp';

function LogoField({ companyId, label, value, onChange, readOnly, help }: FieldEditorProps & { companyId: string | null }) {
  const { t } = useText();
  const problemText = useProblemText();
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fileId = typeof value === 'string' && value ? value : null;
  const image = useQuery({ queryKey: ['org', 'logo', fileId], queryFn: ({ signal }) => org.logoImage(fileId ?? '', signal), enabled: fileId !== null, staleTime: 300_000, retry: false });
  useEffect(() => { setError(null); }, [fileId]);
  const choose = async (file: File | undefined) => {
    if (!file || !companyId) return;
    setBusy(true);
    setError(null);
    try {
      const info = await org.uploadLogo(companyId, file);
      onChange(info.id);
    } catch (e) {
      setError(problemText(e));
    } finally {
      setBusy(false);
      if (input.current) input.current.value = '';
    }
  };
  return (
    <div className="erp-field" role="group" aria-label={label}>
      <span className="erp-label">{label}</span>
      <div className="erp-row">
        {image.data ? <img className="erp-logo-preview" src={image.data} alt={t('org.logo.alt')} width={64} height={64} /> : <span className="erp-muted">{fileId ? t('org.logo.set') : t('org.logo.none')}</span>}
        {!readOnly && companyId && (
          <>
            <input ref={input} type="file" accept={ACCEPT} hidden aria-hidden="true" tabIndex={-1} onChange={(e) => { void choose(e.target.files?.[0]); }} />
            <Button variant="secondary" icon={<Upload size={16} aria-hidden="true" />} isPending={busy} onPress={() => input.current?.click()}>{t(fileId ? 'org.logo.replace' : 'org.logo.choose')}</Button>
            {fileId && <Button variant="quiet" icon={<X size={16} aria-hidden="true" />} onPress={() => onChange(null)}>{t('org.logo.remove')}</Button>}
          </>
        )}
      </div>
      {!companyId && !readOnly && <span className="erp-help">{t('org.logo.saveFirst')}</span>}
      {help && <span className="erp-help">{help}</span>}
      {error && <Banner tone="danger" title={error}>{null}</Banner>}
    </div>
  );
}

/** The editor of the `logoFileId` field of a company; a new company has no logo yet (it needs a record to own the file). */
export const logoEditor = (companyId: string | null) => (props: FieldEditorProps): ReactNode => <LogoField {...props} companyId={companyId} />;
