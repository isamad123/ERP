// The texts of the attendance request screens (slice S1.30a), registered when a lazily loaded screen loads, so they are not part of the shell's scripts (R-213).
import en from '@erp/i18n/messages/requests.en.json';
import ur from '@erp/i18n/messages/requests.ur.json';
import { registerMessages } from '@erp/i18n';

registerMessages({ en, ur });
