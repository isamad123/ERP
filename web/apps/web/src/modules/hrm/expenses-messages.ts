// The texts of the expense screens (slice S1.42), registered when a lazily loaded screen loads, so they are not part of the shell's scripts (R-213).
import en from '@erp/i18n/messages/expenses.en.json';
import ur from '@erp/i18n/messages/expenses.ur.json';
import { registerMessages } from '@erp/i18n';

registerMessages({ en, ur });
