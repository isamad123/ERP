// The texts of the settings centre, feature flags and sign-in page screens (slice S1.15a), registered when the first of these lazily
// loaded screens loads, so they are not part of the shell's scripts (R-213).
import en from '@erp/i18n/messages/config.en.json';
import ur from '@erp/i18n/messages/config.ur.json';
import { registerMessages } from '@erp/i18n';

registerMessages({ en, ur });
