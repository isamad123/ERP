// Licence banners above every screen (slice S1.12b Build item 13; FR-KRN-ENT-04, FR-KRN-TEN-09; ruling Q17): what is
// happening, what still works ("you can read and export") and whom to contact. Locked is a full page (pages/errors).
import { useState } from 'react';
import { Banner, Link } from '@erp/ui';
import { formatDate, formatInteger, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { systemClock } from '../session/clock';
import { useShell } from './context';
import { dayIn, licenceBanners, type BannerSpec } from './licence';
import { hideLicenceNotes, licenceNotesHiddenOn } from './memory';

const LIMIT_NAMES: Record<string, MessageKey> = {
  'users.named': 'licence.limit.usersNamed',
  'users.concurrent': 'licence.limit.usersConcurrent',
  companies: 'licence.limit.companies',
  'employees.active': 'licence.limit.employees',
  'storage.gb': 'licence.limit.storage',
};

export function LicenceBanners() {
  const { capabilities, can, me } = useShell();
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const today = dayIn(me.timeZone, systemClock.now());
  const [hiddenOn, setHiddenOn] = useState(licenceNotesHiddenOn);
  const banners = licenceBanners(capabilities, can('cfg.licence.read'), hiddenOn === today);
  if (banners.length === 0) return null;
  const language = locale === 'ur' ? 'ur' : 'en';
  // nextStateOn is a calendar date, so it is shown as that date wherever the person is.
  const date = capabilities.nextStateOn ? formatDate(capabilities.nextStateOn, language, 'UTC', numbers) : '';
  const hide = () => { hideLicenceNotes(today); setHiddenOn(today); };

  const text = (banner: BannerSpec) => {
    switch (banner.kind) {
      case 'warning':
        return { title: t('licence.warning.title'), body: t('licence.warning.body', { date }) };
      case 'grace':
        return { title: t('licence.grace.title'), body: t('licence.grace.body', { date }) };
      case 'read_only':
        return { title: t('licence.readOnly.title'), body: t('licence.readOnly.body', { date }) };
      case 'limit': {
        const limit = banner.limit ?? { key: '', max: null, used: null, percent: null };
        const name = LIMIT_NAMES[limit.key];
        return {
          title: t('licence.limit.title'),
          body: t('licence.limit.body', {
            name: name ? t(name) : t('licence.limit.other'),
            percent: formatInteger(limit.percent ?? 0, numbers),
            used: formatInteger(limit.used ?? 0, numbers),
            max: formatInteger(limit.max ?? 0, numbers),
          }),
        };
      }
    }
  };

  return (
    <div className="erp-shell-banners" role="region" aria-label={t('licence.region')}>
      {banners.map((banner) => {
        const { title, body } = text(banner);
        return (
          <Banner key={banner.kind + (banner.limit?.key ?? '')} tone={banner.tone} title={title}
            action={banner.kind !== 'limit' && can('cfg.licence.read') ? <Link href="/settings/licence">{t('licence.open')}</Link> : undefined}
            {...(banner.dismissible ? { onDismiss: hide } : {})}>
            {body}
          </Banner>
        );
      })}
    </div>
  );
}
