// Extension point of the shared approvals inbox (slice S1.33a, FR-HRM-LVE-05): a module registers, per document type, a card the inbox shows under the generic fields of a
// task. Cards are lazy components, so registering one adds nothing to the shell; the card shows context only (the decision buttons and the comment dialog stay the inbox's own,
// FR-XM-32). Registered from the module's declaration file (`modules/hrm.ts`), never by importing the screen.
import type { ComponentType, LazyExoticComponent } from 'react';

export interface ApprovalCardProps {
  documentType: string;
  /** The id of the document the task is about (for a leave request or its cancellation: the request). */
  documentId: string;
  companyId: string;
  instanceId: string;
  requesterUserId: string;
}

export type ApprovalCard = LazyExoticComponent<ComponentType<ApprovalCardProps>>;

const cards = new Map<string, ApprovalCard>();

export function registerApprovalCard(documentType: string, card: ApprovalCard): void {
  cards.set(documentType, card);
}

export function approvalCardFor(documentType: string): ApprovalCard | undefined {
  return cards.get(documentType);
}
