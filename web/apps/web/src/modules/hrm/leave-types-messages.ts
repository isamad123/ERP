// The texts of the leave types and leave rules screens (slice S1.33b), registered when a lazily loaded screen loads, so they are not part of the shell's scripts (R-213).
import en from '@erp/i18n/messages/leave-types.en.json';
import ur from '@erp/i18n/messages/leave-types.ur.json';
import { registerMessages } from '@erp/i18n';

registerMessages({ en, ur });
