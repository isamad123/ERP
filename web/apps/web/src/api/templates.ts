// Hand-written clients of the notification text and rule screens and of the document template editor (slice S1.15d Build items 11 and 12; contracts
// "Notification API" (S1.11b1, S1.11b2) and "Template API" (S1.11c2)). The server checks every text and every body again; the screens show its answers as
// confirmed, never assumed. Nothing here sends a real notification except the test send, which goes to the signed-in person only.
import { requestJson } from './client';
import type { Names } from './workflow';

const enc = encodeURIComponent;

export type Channel = 'in_app' | 'email';
export const CHANNELS: readonly Channel[] = ['in_app', 'email'];
export type FieldClass = 'general' | 'personal' | 'salary';

export interface CatalogueField { name: string; type: string; fieldClass: FieldClass }
export interface CatalogueEvent { code: string; module: string; group: string; mandatory: boolean; audited: boolean; fields: CatalogueField[]; link: string | null }
export interface NotificationText { id: string; eventCode: string; channel: Channel; language: string; subject: string | null; body: string; version: number; source: string; status: string; rowVersion: number }
export interface TextEdit { eventCode: string; channel: Channel; language: string; subject: string; body: string }
export interface TextPreview { subject: string | null; body: string }
export interface Audience { users: string[] | null; roles: string[] | null; groups: string[] | null; relationships: string[] | null }
export interface Rule { id: string; name: string | null; eventCode: string; companyId: string | null; condition: unknown; audience: Audience; channels: Channel[]; mandatory: boolean; rowVersion: number }
export interface RuleWrite { name: string | null; eventCode: string; companyId: string | null; condition: unknown; audience: Audience; channels: Channel[]; mandatory: boolean; rowVersion?: number }

export const notifyApi = {
  catalogue: () => requestJson<CatalogueEvent[]>('GET', '/api/ntf/catalogue'),
  texts: (eventCode: string) => requestJson<NotificationText[]>('GET', `/api/ntf/text-templates?eventCode=${enc(eventCode)}`),
  save: (body: TextEdit) => requestJson<NotificationText>('POST', '/api/ntf/text-templates', { body }),
  restore: (eventCode: string, channel: Channel, language: string) => requestJson<NotificationText>('POST', '/api/ntf/text-templates/restore', { body: { eventCode, channel, language } }),
  preview: (body: TextEdit & { sample?: Record<string, string> }) => requestJson<TextPreview>('POST', '/api/ntf/text-templates/preview', { body }),
  testSend: (body: { eventCode: string; language: string; subject: string; body: string; sample?: Record<string, string> }) => requestJson<unknown>('POST', '/api/ntf/text-templates/test-send', { body }),
  rules: () => requestJson<Rule[]>('GET', '/api/ntf/rules'),
  addRule: (body: RuleWrite) => requestJson<Rule>('POST', '/api/ntf/rules', { body }),
  changeRule: (id: string, body: RuleWrite) => requestJson<Rule>('PUT', `/api/ntf/rules/${enc(id)}`, { body }),
  deleteRule: (id: string, rowVersion: number) => requestJson<unknown>('DELETE', `/api/ntf/rules/${enc(id)}?rowVersion=${rowVersion}`),
};

export type Format = 'pdf' | 'html' | 'text' | 'csv' | 'fixed_width';
export const FORMATS: readonly Format[] = ['pdf', 'html', 'text', 'csv', 'fixed_width'];
export const KINDS = ['letter', 'payslip', 'certificate', 'form', 'email', 'bank_file', 'report', 'part'] as const;

export interface TemplateRow { id: string; code: string; nameI18n: Names; kind: string; entityType: string; companyId: string | null; language: string; source: string; activeVersionId: string | null; isActive: boolean; rowVersion: number }
export interface TemplateVersion { id: string; templateId: string; version: number; status: 'draft' | 'active' | 'retired'; outputFormat: Format; body: string; rowVersion: number }
export interface EntityFields { entityType: string; fields: { path: string; type: string; fieldClass: FieldClass }[] }
export interface DocPreview { html: string; pdf: string | null; usedFields: string[] }
export interface TemplateCreate { code: string; name: Names; kind: string; entityType: string; companyId: string | null; language: string; outputFormat: Format; body: string }

export const templatesApi = {
  list: async (): Promise<TemplateRow[]> => (await requestJson<{ items: TemplateRow[] }>('GET', '/api/doc/templates?limit=200&sort=code')).items,
  versions: (id: string) => requestJson<TemplateVersion[]>('GET', `/api/doc/templates/${enc(id)}/versions`),
  entityFields: () => requestJson<EntityFields[]>('GET', '/api/doc/templates/entity-fields'),
  create: (body: TemplateCreate) => requestJson<TemplateRow>('POST', '/api/doc/templates', { body }),
  newDraft: (id: string, body: { body: string; outputFormat?: Format }) => requestJson<TemplateVersion>('POST', `/api/doc/templates/${enc(id)}/versions`, { body }),
  saveDraft: (versionId: string, body: { body: string; outputFormat: Format; rowVersion: number }) => requestJson<TemplateVersion>('PUT', `/api/doc/templates/versions/${enc(versionId)}`, { body }),
  activate: (versionId: string, rowVersion: number) => requestJson<TemplateVersion>('POST', `/api/doc/templates/versions/${enc(versionId)}/activate`, { body: { rowVersion } }),
  retire: (id: string, rowVersion: number) => requestJson<TemplateRow>('POST', `/api/doc/templates/${enc(id)}/retire`, { body: { rowVersion } }),
  preview: (body: { body: string; outputFormat: Format; language: string; entityType: string; companyId: string | null }) => requestJson<DocPreview>('POST', '/api/doc/templates/preview', { body }),
};
