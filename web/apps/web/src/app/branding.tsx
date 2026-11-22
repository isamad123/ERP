// The customer's public branding (slice S1.15a, FR-ADM-SEC-08): product name, logo address, login banner and terms notice, read
// without a sign-in from `GET /api/kernel/branding` for the sign-in screens and the top bar. When the server cannot be reached the
// standard product name is shown, so the sign-in page never fails because of its branding.
import { useQuery } from '@tanstack/react-query';
import { useLocaleSettings, useText } from '@erp/i18n';
import { readBranding } from '../api/branding';

export interface ShownBranding { productName: string; logoUrl: string | null; banner: string; terms: string }

const pickText = (texts: { en: string; ur: string } | undefined, language: string): string => (language === 'ur' && texts?.ur ? texts.ur : texts?.en ?? '');

export function useBranding(): ShownBranding {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const query = useQuery({ queryKey: ['kernel', 'branding'], queryFn: ({ signal }) => readBranding(signal), staleTime: 60_000, retry: false });
  const data = query.data;
  return {
    // The standard name is the translatable web text; a customer's own name is shown as written.
    productName: data?.productName?.trim() && data.productName !== 'ERP' ? data.productName : t('product.name'),
    logoUrl: data?.logoUrl ?? null,
    banner: pickText(data?.banner, locale),
    terms: pickText(data?.terms, locale),
  };
}
