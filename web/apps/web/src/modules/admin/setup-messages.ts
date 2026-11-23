// The texts of the set-up guide and readiness checklist (slice S1.18b), registered when the lazily loaded screen loads, so they are not part of the shell's scripts (R-213).
import en from '@erp/i18n/messages/setup.en.json';
import ur from '@erp/i18n/messages/setup.ur.json';
import { registerMessages } from '@erp/i18n';

registerMessages({ en, ur });
