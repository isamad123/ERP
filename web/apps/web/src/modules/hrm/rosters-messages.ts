// The texts of the roster, shift, optional-holiday and effective-shift screens (slice S1.28b), registered when a lazily loaded screen loads, so they are not part of the shell's scripts (R-213).
import en from '@erp/i18n/messages/rosters.en.json';
import ur from '@erp/i18n/messages/rosters.ur.json';
import { registerMessages } from '@erp/i18n';

registerMessages({ en, ur });
