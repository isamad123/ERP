// The texts of the attendance device screen (slice S1.27b), registered when the lazily loaded screen loads, so they are not part of the shell's scripts (R-213).
import en from '@erp/i18n/messages/devices.en.json';
import ur from '@erp/i18n/messages/devices.ur.json';
import { registerMessages } from '@erp/i18n';

registerMessages({ en, ur });
