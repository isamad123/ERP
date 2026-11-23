// The texts of the policy screens (slice S1.23a), registered when the lazily loaded screens load, so they are not part of the shell's scripts (R-213).
import en from '@erp/i18n/messages/policies.en.json';
import ur from '@erp/i18n/messages/policies.ur.json';
import { registerMessages } from '@erp/i18n';

registerMessages({ en, ur });
