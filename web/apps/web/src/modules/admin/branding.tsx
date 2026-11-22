// The sign-in page screen (slice S1.15a, Build item 4; FR-ADM-SEC-08, S1.15 ruling Q3): the customer's product name, logo, login
// banner and terms notice in English and Urdu, with a preview of the page in either language. Each value is a setting changed through
// the settings service (so it is audited and reversible); the logo is a file of the branding owner. Colours, custom domains and the
// e-mail sender identity are Release 1.1 (FR-KRN-TEN-08).
import { useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Upload, X } from 'lucide-react';
import { EngineProvider, useDocumentTitle, type EngineApi } from '@erp/engine';
import { Banner, Button, ErrorPanel, PageHeader, RadioGroupField, Skeleton, TextAreaField, TextField, useToast } from '@erp/ui';
import { useText } from '@erp/i18n';
import { ApiProblem, sendText } from '../../api/client';
import { BRANDING_KEYS, brandingApi, settingsApi, type Branding } from '../../api/settings';
import { org } from '../../api/org';
import { useShell } from '../../shell/context';
import { useChangeText } from './settings-parts';
import './config-messages';
import './settings.css';

const api: EngineApi = { send: sendText };
const ACCEPT = 'image/png,image/jpeg';

export default function BrandingScreen() {
  return <EngineProvider api={api}><BrandingEditor /></EngineProvider>;
}

interface Form { name: string; bannerEn: string; bannerUr: string; termsEn: string; termsUr: string; logoFileId: string | null }

function BrandingEditor() {
  const { t } = useText();
  useDocumentTitle(t('branding.title'));
  const query = useQuery({ queryKey: ['kernel', 'branding', 'admin'], queryFn: ({ signal }) => brandingApi.get(signal), refetchOnMount: 'always' });
  const logoId = useQuery({
    queryKey: ['cfg', 'branding-logo-id'],
    queryFn: async () => (await settingsApi.detail(BRANDING_KEYS.logo)).setting.value as string,
    refetchOnMount: 'always', retry: false,
  });
  const problem = query.error instanceof ApiProblem ? query.error : null;
  return (
    <div className="erp-page">
      <PageHeader title={t('branding.title')} description={t('branding.description')} />
      {(query.isPending || logoId.isPending) && <Skeleton lines={6} />}
      {query.isError && (
        <ErrorPanel message={problem?.status === 403 ? t('branding.denied') : t('branding.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void query.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {query.data && !logoId.isPending && <BrandingForm initial={query.data} initialLogo={typeof logoId.data === 'string' && logoId.data !== '' ? logoId.data : null} />}
    </div>
  );
}

const fromBranding = (b: Branding, logo: string | null): Form => ({ name: b.productName, bannerEn: b.banner.en, bannerUr: b.banner.ur, termsEn: b.terms.en, termsUr: b.terms.ur, logoFileId: logo });

function BrandingForm({ initial, initialLogo }: { initial: Branding; initialLogo: string | null }) {
  const { t } = useText();
  const { can, writable } = useShell();
  const canWrite = writable && can('cfg.setting_values.update');
  const toast = useToast();
  const queryClient = useQueryClient();
  const changeText = useChangeText();
  const saved = fromBranding(initial, initialLogo);
  const [form, setForm] = useState<Form>(saved);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [previewLanguage, setPreviewLanguage] = useState<'en' | 'ur'>('en');
  const set = (patch: Partial<Form>) => setForm((f) => ({ ...f, ...patch }));
  const changed = (['name', 'bannerEn', 'bannerUr', 'termsEn', 'termsUr', 'logoFileId'] as const).filter((k) => form[k] !== saved[k]);
  const nameProblem = form.name.trim() === '' ? t('settings.problem.required') : form.name.length > 60 ? t('settings.problem.length', { limit: 60 }) : null;

  const save = async () => {
    setError(null);
    if (nameProblem) { setError(nameProblem); return; }
    if (reason.trim() === '') { setError(t('settings.problem.reason')); return; }
    const keyOf = { name: BRANDING_KEYS.productName, bannerEn: BRANDING_KEYS.bannerEn, bannerUr: BRANDING_KEYS.bannerUr, termsEn: BRANDING_KEYS.termsEn, termsUr: BRANDING_KEYS.termsUr, logoFileId: BRANDING_KEYS.logo } as const;
    setBusy(true);
    try {
      let waiting = false;
      for (const field of changed) {
        const value = field === 'logoFileId' ? form.logoFileId ?? '' : field === 'name' ? form.name.trim() : form[field];
        const answer = await settingsApi.set(keyOf[field], { layer: 'tenant', scopeId: null }, value, reason.trim());
        waiting ||= answer?.outcome === 'pending';
      }
      setReason('');
      await queryClient.invalidateQueries({ queryKey: ['kernel', 'branding'] });
      await queryClient.invalidateQueries({ queryKey: ['cfg'] });
      toast.success(waiting ? t('settings.saved.pending') : t('branding.saved'));
    } catch (e) {
      setError(changeText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="erp-stack">
      <form className="erp-stack" onSubmit={(e) => { e.preventDefault(); void save(); }} aria-label={t('branding.title')}>
        <TextField label={t('branding.name')} help={t('branding.name.help')} value={form.name} maxLength={60} isRequired isDisabled={!canWrite} error={nameProblem && form.name !== saved.name ? nameProblem : null} onChange={(name) => set({ name })} />
        <LogoField fileId={form.logoFileId} publicUrl={initial.logoUrl} savedId={saved.logoFileId} canWrite={canWrite} onChange={(logoFileId) => set({ logoFileId })} />
        <TextAreaField label={t('branding.banner.en')} help={t('branding.banner.help')} value={form.bannerEn} maxLength={300} isDisabled={!canWrite} onChange={(bannerEn) => set({ bannerEn })} />
        <div dir="rtl" lang="ur"><TextAreaField label={t('branding.banner.ur')} value={form.bannerUr} maxLength={300} isDisabled={!canWrite} onChange={(bannerUr) => set({ bannerUr })} /></div>
        <TextAreaField label={t('branding.terms.en')} help={t('branding.terms.help')} value={form.termsEn} maxLength={2000} isDisabled={!canWrite} onChange={(termsEn) => set({ termsEn })} />
        <div dir="rtl" lang="ur"><TextAreaField label={t('branding.terms.ur')} value={form.termsUr} maxLength={2000} isDisabled={!canWrite} onChange={(termsUr) => set({ termsUr })} /></div>
        {canWrite && (
          <>
            <TextAreaField label={t('settings.editor.reason')} help={t('settings.editor.reason.help')} value={reason} onChange={setReason} isRequired />
            {error && <p className="erp-field-error" role="alert">{error}</p>}
            <div className="erp-row"><Button type="submit" variant="primary" isPending={busy} isDisabled={busy || changed.length === 0}>{t('branding.save')}</Button></div>
          </>
        )}
        {!canWrite && <p className="erp-small erp-muted">{t('settings.readOnly')}</p>}
      </form>
      <section className="erp-stack" aria-labelledby="branding-preview">
        <h2 id="branding-preview" className="erp-h3">{t('branding.preview.title')}</h2>
        <RadioGroupField label={t('branding.preview.language')} value={previewLanguage} onChange={(id) => setPreviewLanguage(id === 'ur' ? 'ur' : 'en')}
          options={[{ id: 'en', label: t('language.en') }, { id: 'ur', label: t('language.ur') }]} />
        <Preview form={form} language={previewLanguage} publicUrl={initial.logoUrl} savedId={saved.logoFileId} />
      </section>
    </div>
  );
}

function Preview({ form, language, publicUrl, savedId }: { form: Form; language: 'en' | 'ur'; publicUrl: string | null; savedId: string | null }) {
  const { t } = useText();
  const logo = useLogoSource(form.logoFileId, publicUrl, savedId);
  const banner = language === 'ur' && form.bannerUr ? form.bannerUr : form.bannerEn;
  const terms = language === 'ur' && form.termsUr ? form.termsUr : form.termsEn;
  return (
    <div className="erp-auth-card erp-stack" dir={language === 'ur' ? 'rtl' : 'ltr'} lang={language} role="group" aria-label={t('branding.preview.title')}>
      <span className="erp-auth-brand">
        {logo && <img className="erp-auth-logo" src={logo} alt="" />}
        <strong>{form.name.trim() || t('product.name')}</strong>
      </span>
      {banner && <Banner tone="info">{banner}</Banner>}
      <p className="erp-muted">{t('branding.preview.form')}</p>
      {terms && <p className="erp-auth-terms erp-small">{terms}</p>}
    </div>
  );
}

/** The picture to show: the live public logo when nothing changed, else the newly chosen file read from the file store. */
function useLogoSource(fileId: string | null, publicUrl: string | null, savedId: string | null): string | null {
  const fresh = fileId !== null && fileId !== savedId;
  const image = useQuery({ queryKey: ['cfg', 'branding-logo', fileId], queryFn: ({ signal }) => org.logoImage(fileId ?? '', signal), enabled: fresh, staleTime: 300_000, retry: false });
  if (fileId === null) return null;
  return fresh ? image.data ?? null : publicUrl;
}

function LogoField({ fileId, publicUrl, savedId, canWrite, onChange }: { fileId: string | null; publicUrl: string | null; savedId: string | null; canWrite: boolean; onChange: (id: string | null) => void }) {
  const { t } = useText();
  const changeText = useChangeText();
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const source = useLogoSource(fileId, publicUrl, savedId);
  useEffect(() => { setProblem(null); }, [fileId]);
  const choose = async (file: File | undefined) => {
    if (!file) return;
    setBusy(true);
    setProblem(null);
    try {
      onChange((await brandingApi.upload(file)).id);
    } catch (e) {
      setProblem(changeText(e));
    } finally {
      setBusy(false);
      if (input.current) input.current.value = '';
    }
  };
  return (
    <div className="erp-field" role="group" aria-label={t('branding.logo')}>
      <span className="erp-label">{t('branding.logo')}</span>
      <div className="erp-row erp-wrap">
        {source ? <img className="erp-settings-logo" src={source} alt={t('branding.logo.alt')} /> : <span className="erp-muted">{fileId ? t('branding.logo.new') : t('branding.logo.none')}</span>}
        {canWrite && (
          <>
            <input ref={input} type="file" accept={ACCEPT} hidden aria-hidden="true" tabIndex={-1} onChange={(e) => { void choose(e.target.files?.[0]); }} />
            <Button variant="secondary" icon={<Upload size={16} aria-hidden="true" />} isPending={busy} onPress={() => input.current?.click()}>{t(fileId ? 'branding.logo.replace' : 'branding.logo.choose')}</Button>
            {fileId && <Button variant="quiet" icon={<X size={16} aria-hidden="true" />} onPress={() => onChange(null)}>{t('branding.logo.remove')}</Button>}
          </>
        )}
      </div>
      <span className="erp-help">{t('branding.logo.help')}</span>
      {problem && <Banner tone="danger" title={problem}>{null}</Banner>}
    </div>
  );
}
