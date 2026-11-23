// The texts of the attendance capture screens (slice S1.27a), registered when a lazily loaded screen loads, so they are not part of the shell's scripts (R-213).
import en from '@erp/i18n/messages/attendance.en.json';
import ur from '@erp/i18n/messages/attendance.ur.json';
import { registerMessages } from '@erp/i18n';

registerMessages({ en, ur });
