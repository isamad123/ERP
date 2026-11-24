// The texts of the access reports and the audit search (slices S1.13b and S1.16a), registered when a lazily loaded screen loads, so they are not part of the shell's scripts (R-213, R-255).
import en from '@erp/i18n/messages/reports.en.json';
import ur from '@erp/i18n/messages/reports.ur.json';
import { registerMessages } from '@erp/i18n';

registerMessages({ en, ur });
