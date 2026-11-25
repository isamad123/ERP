// The texts of the operations screens (slice S1.16c: job centre, notification log, system health, licence page), registered when a
// lazily loaded screen loads, so they are not part of the shell's scripts (R-213).
import en from '@erp/i18n/messages/ops.en.json';
import ur from '@erp/i18n/messages/ops.ur.json';
import { registerMessages } from '@erp/i18n';

registerMessages({ en, ur });
