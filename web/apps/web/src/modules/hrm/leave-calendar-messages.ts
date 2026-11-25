// The texts of the team leave calendar (slice S1.33a), registered when the lazily loaded screen loads, so they are not part of the shell's scripts (R-213).
import en from '@erp/i18n/messages/leave-calendar.en.json';
import ur from '@erp/i18n/messages/leave-calendar.ur.json';
import { registerMessages } from '@erp/i18n';

registerMessages({ en, ur });
