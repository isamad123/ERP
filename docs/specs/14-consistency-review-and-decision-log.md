# Consistency Review and Decision Log (v0.2)

Document 14 of the planning set | Prepared for Ibrahim | 10 November 2026 | Review of documents 1 to 13: contradictions, missing links, duplicated ownership, kernel gaps and the decisions that settle them

## 1. Purpose, method and scope

### 1.1 Purpose

The thirteen planning documents were written one after another over a long session. Each is internally consistent, but a platform of this size needs the documents to agree with each other before code is generated from them. This review lists where they do not, proposes one resolution for each, consolidates the shared contracts (events, ownership, naming) into one canonical form, and records the decisions that need your confirmation.

### 1.2 Method and limits

- I compared the documents against each other on six dimensions: event names and directions, module ownership of documents and data, synchronous contracts, waves and dependencies, requirement and section references, and terminology.
- I reviewed from the drafted text. If you edited any document directly after it was drafted, tell me which, and I will re-check those sections. Rewriting statutory content, pricing and similar subject-matter judgments was out of scope.
- Every explicit cross-document reference I could trace (for example document 5 AR-26, document 8 VAL-04, document 7 RCV-09, document 9 section 14, document 12 section 8.5) points to a requirement or section that exists. No broken reference was found. The defects below are about content that disagrees, not about missing targets.
- Severity: High means code generated from the documents would be wrong or would silently lose data; Medium means a gap or ambiguity that would cause rework; Low means wording or housekeeping.

### 1.3 How to use this document

Section 3 is the corrections register. Sections 4 and 5 are the canonical event catalog and ownership and contract matrix; Claude Code should treat them as authoritative where the module documents differ. Section 6 is the decision log; each new decision (D-12 onward) needs your confirmation. Section 12 lists the exact patches per document, which I can apply when you approve.

## 2. Summary of findings

| Severity | Count | Examples |
| --- | --- | --- |
| High | 9 | Receipt flow ownership and event names (C-01); Finance not consuming procurement receipts or commitments events (C-02, C-03); events that consumers expect but nobody publishes (C-04, C-07); duplicated replenishment parameters (C-17); missing portal and offline sync frameworks in the kernel (C-22, C-23); build catalog missing waves B and C (C-30) |
| Medium | 17 | Event renames, publisher mismatches, supplier invoice capture point, quality inspection ownership, project master, search and collaboration services |
| Low | 9 | Naming variants, housekeeping, schema list updates |

Top issues to resolve before coding beyond Step 1:

1. Agree the receiving, issuing and return flows (D-15, D-17) so Procurement, Inventory, Sales and Finance publish and consume one coherent set of events.
2. Agree a single owner for replenishment parameters and classification (D-13).
3. Add five missing kernel capabilities (D-20): external portals, offline sync, global search, collaboration and print service. Offline sync is needed already in wave A for attendance and approvals on mobile.
4. Extend the Claude Code build catalog to cover waves B and C (section 9), so the manual covers the whole plan.
5. Adopt the event naming and versioning conventions of D-21 and the canonical catalog of section 4.

## 3. Corrections register

All items are resolved: you approved decisions D-12 to D-25 on 13 November 2026 and the patches of section 12 were applied (see the change log in section 14).

### 3.1 Events and contracts

| ID | Sev | Documents and sections | Issue | Resolution |
| --- | --- | --- | --- | --- |
| C-01 | High | Doc 7 RCV-02; doc 8 sections 26.2 and 26.3 | Procurement says it publishes GoodsReceiptPosted, Inventory says it publishes it; doc 8 consumes the Procurement receipt both as an event and as a synchronous command | Procurement posts its receipt, calls the Inventory receive-goods contract, Inventory posts the stock movement and publishes GoodsReceiptPosted; Procurement publishes ReceiptPosted afterwards for others (D-15) |
| C-02 | High | Doc 5 section 17.1; doc 7 section 16.1 | Finance does not consume ReceiptPosted or ServiceAccepted, so goods and services received but not invoiced for non-stock items and services would never accrue | Add both to Finance consumed events; stock items accrue from Inventory's GoodsReceiptPosted, non-stock and services from Procurement's events, never both |
| C-03 | High | Doc 5 sections 11 and 17.1; doc 7 section 16.1 | Commitments need PurchaseRequisitionApproved, Changed and Closed and PurchaseOrderRevised and Cancelled; Finance lists only PurchaseOrderIssued and Closed; GoodsReturnRecorded is also missing | Add all to Finance consumed events |
| C-04 | High | Doc 5 section 17.2; doc 11 section 16.2 | Sales consumes ReceiptAllocated and InvoicePaid; Finance publishes neither | Finance publishes ReceiptAllocated and CustomerInvoicePaid (replacing InvoicePaid) |
| C-05 | Med | Doc 5 section 17.1; doc 6 section 30 | Finance lists AdvanceIssued (HRMIS never publishes it; advances use LoanDisbursed) and omits LeaveLiabilityCalculated and GratuityAccrualCalculated | Remove AdvanceIssued; add the two liability events |
| C-06 | Med | Doc 5 section 17.1; doc 8 section 26.1 | Finance does not consume StockTransferShipped, StockTransferReceived or StockRevaluationPosted | Add to Finance consumed events |
| C-07 | High | Doc 5 sections 10 and 17.2; doc 8 sections 16 and 26.2 | Assets creates acquisitions only from Procurement's SupplierInvoiceApproved; invoices keyed directly in Finance AP with asset lines never reach Assets | Finance publishes VendorInvoicePosted (with asset flags); Assets consumes it; duplicate protection by source reference |
| C-08 | Med | Doc 7 section 16.2; doc 8 section 26.1 | Procurement consumes AssetCreated; Assets publishes AssetAcquired | Use AssetAcquired everywhere |
| C-09 | Med | Doc 8 section 26.1; doc 10 section 17.2 | Supply Chain consumes AssetStatusChanged; Assets does not publish it | Assets publishes AssetStatusChanged |
| C-10 | Med | Doc 8 section 26.1; doc 11 section 16.2 | Sales consumes StockAvailabilityChanged; Inventory does not publish it | Inventory publishes a throttled StockAvailabilityChanged for items with demand |
| C-11 | Med | Doc 8 section 26.2; doc 10 section 17.2; doc 11 sections 10 and 16 | CustomerReturnReceived is listed as published by Sales, but the physical receipt happens in Inventory; Inventory does not consume ReturnAuthorized | Inventory publishes CustomerReturnReceived after the receive-return contract; Inventory and Supply Chain consume ReturnAuthorized from Sales (D-17) |
| C-12 | Med | Doc 7 section 16.1; doc 11 section 16.2 | Sales consumes PurchaseOrderConfirmed for intercompany orders; Procurement does not publish it | Procurement publishes PurchaseOrderConfirmed when a supplier or counterpart confirms |
| C-13 | Low | Doc 7 section 16.2; doc 8 section 26.2; doc 10 section 17.1 | AsnReceived and SupplierForecastShared are published by Supply Chain but not listed as consumed by Procurement and Inventory | Add to consumed lists |
| C-14 | Med | Doc 1 section 4.4 | Early catalog uses GoodsReceived, StockMovementPosted and RequisitionApproved; later documents use GoodsReceiptPosted, GoodsIssuePosted, StockAdjustmentPosted and PurchaseRequisitionApproved | Replace by the canonical catalog (section 4); StockMovementPosted is retired |
| C-15 | Med | Doc 5 TAX-08; doc 8 section 26.2; doc 10 RLG-01; docs 10 and 11 DeliveryConfirmed | The four alignment items listed in document 11 section 22 | D-16 invoice issuing, D-15 deliveries and stock, D-17 return authorization ownership, D-18 DeliveryConfirmed publisher |
| C-16 | Med | Doc 5 AP-03 and section 2.2 | Finance AP describes matching against orders and receipts, which Procurement owns; section 2.2 says supplier invoices are issued in Procurement | Finance AP shows match status supplied by Procurement and handles non-order invoices; reword section 2.2 (D-12) |

### 3.2 Ownership and duplication

| ID | Sev | Documents and sections | Issue | Resolution |
| --- | --- | --- | --- | --- |
| C-17 | High | Doc 8 ITM-03, RPL-04; doc 10 PMD-01, OPT-01, OPT-06 | Reorder point, min-max, safety stock, lead time and ABC class live in both Inventory and Supply Chain | Inventory stores the values; Supply Chain, when licensed, proposes and writes approved values back through a contract; planning-only parameters stay in Supply Chain (D-13) |
| C-18 | Med | Doc 5 AP-01; doc 7 MAT-01 | Two capture points for supplier invoices when both modules are licensed | Order-related invoices are captured and matched in Procurement; non-order invoices are keyed in Finance AP; duplicate detection spans both (D-12) |
| C-19 | Med | Doc 7 section 22; doc 8 QLT | Procurement lists quality inspection ownership as open; Inventory specifies it | Inventory owns inspection; Procurement only reads outcomes (D-14) |
| C-20 | Low | Doc 2 PTY-07; doc 7 SUP-02; doc 11 CUS-08 | Partner onboarding defined three times | Kernel provides the onboarding workflow template; Procurement and Sales configure role-specific steps (D-23) |
| C-21 | Med | Doc 5 COA-12; doc 6 ATT-22; doc 7 REQ; doc 8 ACQ-05 | Projects are referenced in four modules but no document owns a project master | Projects are a Finance dimension for now; a Projects module is a later specification (D-19) |

### 3.3 Kernel gaps found through module dependencies

| ID | Sev | Documents and sections | Issue | Resolution |
| --- | --- | --- | --- | --- |
| C-22 | High | Doc 4; doc 7 section 12; doc 10 section 11; doc 11 section 12.1 | Supplier, customer and carrier portals are specified per module but the kernel has no external-portal framework (separate authentication realm, invitations, branding, isolation, rate limits) | Add FR-KRN-PRT in the kernel, wave B (D-20) |
| C-23 | High | Doc 1 section 4.9; docs 6, 8, 10, 11, 13 | Mobile offline behavior is relied on everywhere but the kernel lacks a sync framework (device registry, sync protocol, outbox, conflict rules, remote wipe) | Add FR-KRN-SYN, needed in wave A for attendance and approvals (D-20) |
| C-24 | Med | Doc 13 IA-03 | Global search and command palette have no kernel service (indexing, permission-aware results, Arabic and Urdu normalization) | Add FR-KRN-SRC, thin in wave A (D-20) |
| C-25 | Med | Doc 2 WFL-07; docs 4, 6, 7, 11, 13 | Comments, mentions, tasks and activity feeds appear in many documents without a kernel specification | Add FR-KRN-COL, thin in wave A (D-20) |
| C-26 | Low | Doc 5 BNK-21; doc 8 labels; doc 10 TMS-20; doc 11 DLV-05 | Printing to label, cheque and document printers (especially on premise) has no service | Add print service requirements to kernel documents component (D-20) |
| C-27 | Med | Doc 2 INT; doc 3 section 3.7; doc 9 ADH-10 and ADH-11 | Analytics API and the external reporting database role are specified only in document 9 | Cross-reference in document 2 INT and add the role in document 3 (D-25) |
| C-28 | Low | Doc 3 sections 5 and 6 | rpt and ai table lists are a subset of document 9 section 25; no schemas for portal, sync, search or collaboration | Update document 3 with the extended tables and new schemas |
| C-29 | Low | Doc 4 section 13 | Analytics administration screens of document 9 ADM-02 are not referenced | Add reference |

### 3.4 Documents, structure and naming

| ID | Sev | Documents and sections | Issue | Resolution |
| --- | --- | --- | --- | --- |
| C-30 | High | Doc 12 sections 3.2, 6 and 7 | The build catalog stops at Step 6 (wave A); spec import covers documents 1 to 6; references use D1 to D6 only | Add Steps 7 to 12 for waves B and C and documents 7 to 13 (section 9) |
| C-31 | Med | Doc 1 sections 0.1 and 13 | Decision list stops at D-11; repository structure lacks the new kernel projects | Add D-12 onward; add Erp.Portal, Erp.Sync, Erp.Search, Erp.Collaboration |
| C-32 | Low | Docs 1 section 7, 2 ENT, 6, 9 PKG, 10 section 10 | Entitlement keys are described in several places but never listed | Draft key catalog in section 8 (D-24) |
| C-33 | Low | All | No registry of requirement ID prefixes | Registry in section 7.2 |
| C-34 | Low | All | Naming variants (Assets, Asset Management, HRMIS, HR MIS, HRMS) | Canonical names in section 7.1 (D-22) |
| C-35 | Low | Docs 1 and 6 | Wave labels A1 and A2 (HRMIS) versus A (platform) are not explained together | Add a legend to document 1 section 1.1 |

## 4. Canonical event catalog

Where a module document differs from this catalog, this catalog wins until the module document is patched (section 12).

### 4.1 Conventions (decision D-21)

- Names are PascalCase, past tense, and describe a business fact (PurchaseOrderIssued, not PurchaseOrderEvent).
- Every event has an envelope: event id, type, schema version, occurred-at time, company, correlation id, causation id, acting user or system, source module, and the reference of the aggregate that changed.
- Payloads are versioned (v1, v2). Within a version only additive changes are allowed; a breaking change creates a new version that is published in parallel for at least one release.
- Delivery is at least once. Consumers are idempotent through the inbox table (document 3, section 7.5). Ordering is guaranteed per aggregate only.
- Events carry identifiers and business totals, not unnecessary personal data. Employee-level payroll detail stays in HRMIS and is reached by drill-down.
- Events are published from the outbox after the business transaction commits. A failed consumer never blocks the publisher; failures go to the consumer's exception queue and can be replayed.
- Every event schema lives in docs/events with an example, producer, consumers and a contract test that fails the build on drift.
- Synchronous calls between modules are contracts (section 5.3), not events, and are used only where the user needs an immediate answer or confirmation.

### 4.2 Events published by HRMIS

| Event | Consumers | Status |
| --- | --- | --- |
| EmployeeHired, EmployeeTerminated | Kernel identity, Assets, Finance, notifications | Unchanged |
| EmployeeChanged | Kernel identity, Assets, Finance (cost center), Procurement, Sales and Supply Chain (approver, representative and driver assignments), notifications | Clarified |
| AttendanceLocked, AttendanceReopened | Payroll (internal), notifications | Unchanged |
| PayrollRunFinalized | Finance or Accounting Export | Unchanged |
| FinalSettlementApproved | Finance or Accounting Export | Unchanged |
| ExpenseClaimApproved | Finance or Accounting Export | Unchanged |
| LoanDisbursed (covers loans and advances) | Finance or Accounting Export | Clarified (C-05) |
| LeaveLiabilityCalculated, GratuityAccrualCalculated | Finance (accruals) | Clarified (C-05) |

### 4.3 Events published by Finance

| Event | Consumers | Status |
| --- | --- | --- |
| PeriodClosed, PeriodReopened | All modules | Unchanged |
| JournalPosted, PostingFailed | Reporting, notifications, audit | Unchanged |
| AccountingEntriesAvailable | The source module (accounting preview and drill-down) | Unchanged |
| VendorInvoicePosted (with asset-flagged lines) | Assets, Procurement (match references), reporting | New (C-07) |
| VendorInvoicePaid | Procurement, notifications | Unchanged |
| PaymentIssued, PaymentVoided | Procurement, HRMIS, notifications | Unchanged |
| CustomerInvoicePaid | Sales (status, collection-based commissions), customer portal | New, replaces InvoicePaid (C-04) |
| ReceiptAllocated | Sales (portal and statements) | New (C-04) |
| CustomerCreditHoldChanged, CustomerExposureChanged | Sales | Clarified |
| BudgetCommitmentChanged, BudgetThresholdReached | Procurement, notifications | Unchanged |

### 4.4 Events published by Procurement

| Event | Consumers | Status |
| --- | --- | --- |
| PurchaseRequisitionApproved, PurchaseRequisitionChanged, PurchaseRequisitionClosed | Finance (commitments), Supply Chain, notifications | Renamed from RequisitionApproved (C-14); Finance consumption added (C-03) |
| PurchaseOrderIssued, PurchaseOrderRevised, PurchaseOrderCancelled, PurchaseOrderClosed | Finance (commitments), Inventory (expected receipts), Supply Chain | Clarified (C-03) |
| PurchaseOrderConfirmed | Sales (intercompany), Supply Chain, notifications | New (C-12) |
| ReceiptPosted | Finance (accrual for non-stock items), Supply Chain (lead-time statistics), notifications | Clarified (C-01, C-02) |
| ServiceAccepted | Finance (accrual) | Clarified (C-02) |
| GoodsReturnRecorded | Inventory, Finance | Unchanged |
| SupplierInvoiceApproved | Finance (payable), Assets (asset-flagged lines), Inventory (cost correction), Accounting Export | Clarified |
| SupplierStatusChanged | Finance (payment holds), Supply Chain, notifications | Unchanged |
| ContractActivated, ContractExpiring, ContractExpired | Supply Chain, notifications | Unchanged |
| TenderAwarded | Procurement (internal), notifications | Unchanged |

### 4.5 Events published by Inventory

| Event | Consumers | Status |
| --- | --- | --- |
| GoodsReceiptPosted | Finance (stock value and goods-received-not-invoiced for stock items), Procurement (status), Supply Chain | Clarified (C-01); replaces GoodsReceived |
| GoodsIssuePosted | Finance (consumption or cost of goods sold), Sales (delivery confirmation), Assets (spares), Supply Chain | Unchanged |
| StockAdjustmentPosted, StockRevaluationPosted | Finance | Clarified (C-06); StockMovementPosted retired |
| StockTransferShipped, StockTransferReceived | Finance (inter-company), Supply Chain | Clarified (C-06) |
| StockAvailabilityChanged (throttled, only for items with open demand or reservations) | Sales, Supply Chain | New (C-10) |
| StockBelowReorder | Procurement, Supply Chain, notifications | Unchanged |
| ReplenishmentProposed | Procurement (draft requisitions), Supply Chain | Unchanged |
| BatchBlocked, RecallRaised | Sales, Procurement, notifications | Unchanged |
| CustomerReturnReceived | Sales, Supply Chain | Moved from Sales (C-11) |

### 4.6 Events published by Asset Management

| Event | Consumers | Status |
| --- | --- | --- |
| AssetAcquired | Finance, Procurement (link from purchase line), reporting | Renamed from AssetCreated where used (C-08) |
| DepreciationCalculated | Finance (journals per ledger) | Unchanged |
| AssetTransferred, AssetDisposed, AssetRevalued, AssetImpaired | Finance, HRMIS (custody), notifications | Unchanged |
| AssetIssued, AssetReturned | HRMIS (custody and offboarding), notifications | Unchanged |
| AssetStatusChanged | Supply Chain (fleet availability), notifications | New (C-09) |
| MaintenanceWorkOrderCompleted | Finance (internal cost allocation), Procurement (service acceptance) | Unchanged |

### 4.7 Events published by Sales

| Event | Consumers | Status |
| --- | --- | --- |
| OrderCreated, OrderChanged, OrderCancelled | Supply Chain, Inventory (reservations), Finance (credit exposure), reporting | Unchanged |
| DeliveryPosted | Supply Chain (history), Finance (revenue triggers), reporting | Clarified (C-15): stock effect comes from the issue contract, not from this event |
| SalesInvoiceIssued, SalesCreditNoteIssued | Finance or Accounting Export | Unchanged |
| SalesReceiptRecorded (van, counter and field collections) | Finance | Unchanged |
| RebateAccrued, BillingPeriodEvent | Finance | Unchanged |
| ReturnAuthorized | Inventory, Supply Chain, notifications | Clarified (C-11) |
| CommissionApproved | HRMIS (variable pay), Finance (external agents) | Unchanged |
| CustomerStatusChanged | Finance, Supply Chain, customer portal | Unchanged |

### 4.8 Events published by Supply Chain

| Event | Consumers | Status |
| --- | --- | --- |
| ForecastApproved, SopPlanApproved | Planning engine, Finance (budget and forecast views), notifications | Unchanged |
| PlannedOrdersReleased | Procurement, Inventory | Unchanged |
| SupplyExceptionRaised, SupplyExceptionResolved | Workflow, control tower, notifications | Unchanged |
| ShipmentPlanned, ShipmentDispatched, ShipmentMilestoneReached | Sales, Inventory (in-transit), notifications | Unchanged |
| DeliveryConfirmed | Sales (billing triggers), Inventory, Finance | Published by Supply Chain when licensed, otherwise by Sales (D-18) |
| FreightAccrued | Finance | Unchanged |
| CarrierInvoiceApproved | Finance or Accounting Export | Unchanged |
| ReturnDispositioned | Sales (credit), Inventory (when Supply Chain is not licensed this event is published by Inventory), Finance | Clarified (C-11) |
| SupplierForecastShared, AsnReceived | Procurement, Inventory | Clarified (C-13) |

### 4.9 Kernel and platform events

| Event | Consumers |
| --- | --- |
| UserDisabled | HRMIS (employee link), reporting ownership transfer |
| PeriodCloseRequested | Finance |
| SemanticModelChanged, ReportPublished, ReportCertified, ExportRequested, ExportApproved | Analytics catalog, approvers, audit (document 9, section 23) |
| AiProposalCreated, AiProposalConfirmed, AiProposalRejected, AiQuotaWarning | Audit, notifications, entitlements |

### 4.10 Retired or renamed names

| Old name | Replacement |
| --- | --- |
| GoodsReceived | GoodsReceiptPosted |
| StockMovementPosted | GoodsReceiptPosted, GoodsIssuePosted, StockAdjustmentPosted |
| RequisitionApproved | PurchaseRequisitionApproved |
| AssetCreated | AssetAcquired |
| InvoicePaid | CustomerInvoicePaid (customers) or VendorInvoicePaid (suppliers) |
| AdvanceIssued | LoanDisbursed |

## 5. Ownership and contract matrix

### 5.1 Documents and who owns them

| Document or record | Owner | Notes |
| --- | --- | --- |
| Journal, ledger, account balances, budgets, commitments | Finance | Commitments are driven by Procurement events |
| Supplier invoice (order-related) with matching record | Procurement | Approved match goes to Finance AP (D-12) |
| Supplier invoice (non-order), vendor payment, payment run | Finance (AP) |  |
| Customer invoice, credit note, debit note (billing documents) | Sales when licensed, otherwise Finance (AR) | Issuer calls the e-invoicing connector (D-16) |
| Receipt of money, allocation, dunning, credit exposure | Finance (AR) |  |
| Purchase requisition, RFQ, tender, purchase order, order revision | Procurement |  |
| Receipt document (goods or service acceptance) | Procurement | Stock effect through the Inventory contract |
| Stock receipt, issue, transfer, adjustment, count, reservation | Inventory |  |
| Inspection and non-conformance | Inventory | Procurement reads outcomes (D-14) |
| Quotation, sales order, sales contract, delivery, return authorization | Sales | Reverse logistics extends the return authorization (D-17) |
| Pick list and putaway task (basic) | Inventory | Advanced waves in Supply Chain WMS |
| Shipment, load, tender, proof of delivery, freight settlement | Supply Chain | Proof of delivery in Sales when Supply Chain is absent |
| Forecast, plan, planned order, S&OP cycle | Supply Chain | Release creates documents elsewhere |
| Asset, acquisition, depreciation run, disposal, work order | Asset Management |  |
| Payroll run, payslip, loan, expense claim, leave ledger | HRMIS |  |
| Configuration, workflow, rules, settings, audit | Kernel |  |

### 5.2 Master data and who owns it

The table of document 1, section 1.3 stays valid, with these additions and clarifications:

| Master data | Owner | Consumers and notes |
| --- | --- | --- |
| Replenishment parameters (reorder point, min and max, safety stock, lead time) | Inventory stores the values | Supply Chain proposes and writes approved values back by contract (D-13) |
| ABC and XYZ class | Inventory for basic classification; Supply Chain segmentation when licensed | Written back through the same contract |
| Planning-only parameters (time fences, lot-sizing rules, sourcing rules, network lanes) | Supply Chain |  |
| Carrier | Kernel party with role carrier | Rates and performance in Supply Chain |
| Vehicle | Asset Management | Fleet use in Supply Chain |
| Driver | HRMIS employee | Assignments in Supply Chain |
| Project | Finance dimension | A Projects module is future work (D-19) |
| Supplier price lists and contracts | Procurement |  |
| Sales price lists, promotions, customer contracts | Sales |  |
| Customer credit limit and hold | Finance | Basic fallback in Sales when Finance is absent |
| Stock attributes of items | Inventory |  |
| Planning items | Supply Chain | Extend items per location |

### 5.3 Synchronous contract catalog

Commands carry idempotency keys; failures are shown to the user and never leave half-completed documents; every contract is versioned and degrades gracefully when its provider is not licensed (document 2, GR-12).

| Contract | Type | Provider | Consumers | Without provider |
| --- | --- | --- | --- | --- |
| CheckBudgetAvailability | Query | Finance | Procurement, HRMIS (expense claims) | Always allowed |
| GetCustomerCreditExposure | Query | Finance | Sales | Always allowed; basic limit in Sales if configured |
| GetExchangeRate | Query | Kernel | All | Not applicable |
| PreviewAccountingEntries | Query | Finance | Any module | Not shown |
| GetSupplierPaymentStatus | Query | Finance | Procurement, portal | Not shown |
| ReceiveGoods | Command | Inventory | Procurement | Receipt confirms quantities only |
| IssueGoods | Command | Inventory | Sales, Assets (spares), HRMIS (issues) | Document proceeds without stock effect for non-stock items |
| ReceiveReturn | Command | Inventory | Sales, Procurement | No stock effect |
| GetAvailabilityAndPromise | Query | Inventory, Supply Chain | Sales, portal | No promise date; always allowed |
| GetStockPosition | Query | Inventory | Procurement, Supply Chain | Not shown; imported snapshot for planning |
| GetDemandHistoryAndOpenOrders | Query | Sales | Supply Chain | Imported history |
| GetSupplierLeadTimeStatistics | Query | Procurement | Supply Chain | Manual or imported parameters |
| CreateRequisitionDraft | Command | Procurement | Supply Chain, Inventory | Planned orders exported |
| CreateTransferDraft | Command | Inventory | Supply Chain | Planned orders exported |
| UpdatePlanningParameters | Command | Inventory | Supply Chain | Parameters stay in Supply Chain only |
| CreateAcquisitionDraft | Command | Assets | Procurement, Finance | Asset flag informational |
| IssueAsset, ReturnAsset, GetCustodyList | Command, command, query | Assets | HRMIS | Lightweight HR register |
| SubmitEInvoice | Command | Pack connector | Sales, Finance | Feature unavailable |
| ScreenParty | Query | Pack connector | Procurement, Sales, Supply Chain | No screening |
| SemanticQuery | Query | Analytics | Reports, dashboards, NLQ, APIs | Always available |
| AI completion, retrieval, extraction, forecast, anomaly | Query and command | AI Gateway and services | Skills and modules | AI features hidden |

### 5.4 Interaction rules

1. A module that needs an immediate answer or a user-visible confirmation uses a contract; everything else uses an event.
2. A command that changes another module's data returns that module's result to the user (success or the real error); the calling module records its own document state only after success.
3. A module never writes another module's tables, and never reads them except through reporting views in the analytics layer.
4. Both directions of every flow are tested with and without the optional module (document 2, GR-12).

## 6. Decision log

Status values: Confirmed (you agreed in this conversation), Partly confirmed, Proposed (needs your confirmation). Decisions D-12 to D-25 were approved on 13 November 2026 and applied to the documents; their Proposed status in the table below is superseded by that approval. D-05, D-07, D-09, D-10 and D-11 in section 6.1 are still awaiting your explicit confirmation, although the documents already follow them.

### 6.1 Decisions from document 1

| ID | Decision | Status |
| --- | --- | --- |
| D-01 | Modular monolith on .NET with PostgreSQL and React; Clean Architecture inside modules | Partly confirmed (.NET chosen earlier; PostgreSQL and React proposed) |
| D-02 | Database per tenant by default behind a tenant-resolution layer | Confirmed |
| D-03 | Industry-neutral core; industries through packs | Confirmed |
| D-04 | Country packs for Pakistan, United States and Saudi Arabia | Confirmed (markets named by you); pack scope proposed |
| D-05 | One entitlement service for license files and subscriptions | Proposed |
| D-06 | Built-in analytics; Superset optional later | Confirmed |
| D-07 | AI as a kernel service (AI Gateway) | Proposed |
| D-08 | First build is the platform foundation, Finance and HRMIS; other modules planned now | Confirmed |
| D-09 | Permissive open-source licenses only, enforced by a CI gate | Proposed (consistent with your open-source requirement) |
| D-10 | Soft dependencies between modules; hard dependency on the kernel only | Proposed |
| D-11 | Items, partners and units live in the kernel so any module can be sold alone | Proposed |

### 6.2 New decisions from this review

| ID | Decision | Resolves | Status |
| --- | --- | --- | --- |
| D-12 | Supplier invoices related to orders are captured and matched in Procurement and handed to Finance AP when approved; invoices without orders are keyed in Finance AP; duplicate detection spans both | C-16, C-18 | Proposed |
| D-13 | Inventory stores replenishment parameters and basic ABC class; when Supply Chain is licensed it proposes changes and writes approved values back through the UpdatePlanningParameters contract; planning-only parameters stay in Supply Chain | C-17 | Proposed |
| D-14 | Quality inspection is owned by Inventory; Procurement reads the outcomes | C-19 | Proposed |
| D-15 | Receiving, issuing and returns use synchronous commands (ReceiveGoods, IssueGoods, ReceiveReturn); the owning module publishes its event after success; Finance accrues stock items from Inventory events and non-stock items and services from Procurement events, never both | C-01, C-02, C-06, C-15 | Proposed |
| D-16 | When Sales is licensed it issues customer invoices and calls the e-invoicing connector; otherwise invoices are keyed in Finance AR | C-15 | Proposed |
| D-17 | Return authorizations are owned by Sales; Supply Chain reverse logistics extends them; Inventory receives goods and publishes CustomerReturnReceived; dispositions are published by Supply Chain or, without it, Inventory | C-11, C-15 | Proposed |
| D-18 | DeliveryConfirmed is published by Supply Chain when licensed, otherwise by Sales when proof of delivery is captured | C-15 | Proposed |
| D-19 | Projects are a Finance dimension until a Projects module is specified | C-21 | Proposed |
| D-20 | The kernel adds five capabilities: external portal framework (PRT, wave B), offline sync framework (SYN, wave A), global search (SRC, wave A thin), collaboration (COL, wave A thin) and print service (under DOC) | C-22 to C-26 | Proposed |
| D-21 | Event conventions and the canonical catalog of section 4 are adopted; each event has a versioned schema and a contract test | C-14 | Proposed |
| D-22 | Canonical module names, codes and requirement prefixes of section 7 are adopted | C-33, C-34 | Proposed |
| D-23 | The kernel provides a partner onboarding workflow template; Procurement and Sales configure role-specific steps | C-20 | Proposed |
| D-24 | The entitlement key catalog of section 8 is the starting point for packaging; commercial editions are defined by you later | C-32 | Proposed |
| D-25 | The analytics API and an external reporting database role are specified in the kernel and data documents | C-27 | Proposed |

## 7. Naming and registries

### 7.1 Canonical names (decision D-22)

| Concept | Canonical name | Code | Avoid |
| --- | --- | --- | --- |
| Human resources module | HRMIS | hrm | HR MIS, HRMS (except as an informal alias) |
| Finance module | Finance | fin | Accounting module |
| Procurement module | Procurement | prc | Purchasing (user-facing menu label may say Purchasing) |
| Inventory module | Inventory | inv | Stock module |
| Asset module | Asset Management, short form Assets | ast | Fixed Assets (reserved for the accounting term) |
| Sales module | Sales and Distribution, short form Sales | sal | Order management |
| Supply chain module | Supply Chain | scm | SCM in prose |
| Analytics | Analytics and reporting | rpt | BI (reserved for external tools) |
| AI | AI Gateway and skills; user-facing label AI assistant | ai | Copilot |
| Platform base | Platform kernel | various | Core, platform layer |
| Customer account | Tenant; legal entity inside it is a Company | cp, org | Client, organization (for a legal entity) |
| Installable configuration | Pack (country, industry, template, connector, language) | cfg | Plugin, template (alone) |
| Exported configuration | Configuration package | cfg | Pack (for exports) |
| External accounting route | Accounting Export connector | itg | Journal export |
| Counterparties | Business partner (roles: supplier, customer, carrier and others) | ref | Vendor in the data model (UI may say vendor) |
| Cross-module analytics rules | Semantic layer and reporting views | rpt | Data warehouse |

### 7.2 Requirement and identifier registry

| Prefix or form | Document | Used for |
| --- | --- | --- |
| D-nn | Documents 1 and 14 | Architecture and planning decisions |
| C-nn | Document 14 | Corrections in this review |
| S-nn.m (for example S3.2) | Document 12 | Build slices |
| GR-nn, GHR-nn | Documents 2 and 6 | Global requirements of kernel and HRMIS |
| FR-KRN-area-nn | Document 2 | Kernel (TEN, ORG, IAM, SET, AUD, NUM, PTY, CAT, TAX, LOC, MET, WFL, RUL, NTF, DOC, INT, JOB, ENT, CFG, AIG; PRT, SYN, SRC, COL to be added) |
| FR-ADM-area-nn | Document 4 | Admin console |
| FR-FIN-area-nn | Document 5 | Finance |
| FR-HRM-area-nn | Document 6 | HRMIS |
| FR-PRC-area-nn | Document 7 | Procurement |
| FR-INV-area-nn, FR-AST-area-nn | Document 8 | Inventory and Asset Management |
| FR-ANL-area-nn, FR-AIX-area-nn | Document 9 | Analytics and AI |
| FR-SCM-area-nn | Document 10 | Supply Chain |
| FR-SAL-area-nn | Document 11 | Sales and Distribution |
| FR-UX-area-nn | Document 13 | UX and design system |
| BR, AC | Module documents | Business rules and acceptance criteria, unnumbered per area |

## 8. Entitlement key catalog (draft, decision D-24)

Keys are technical building blocks of packaging; editions and prices are your commercial decision. Every key requires platform.base; a sub-key requires its parent.

### 8.1 Module and capability keys

| Key | Unlocks | Wave |
| --- | --- | --- |
| platform.base | Kernel, admin console, audit, workflow, rules, notifications, documents, core analytics, integration basics | A |
| fin | General ledger, AP, AR, banking, tax, close, budgets basic, financial reporting | A |
| fin.consolidation, fin.cost | Consolidation and intercompany; cost accounting | B |
| hrm.core | Employee records, positions, documents, attendance, leave, onboarding, self-service | A |
| hrm.payroll | Payroll, loans, expenses, compensation, statutory compliance | A |
| hrm.recruitment, hrm.performance, hrm.learning, hrm.relations | Talent and employee relations areas | B |
| hrm.us | United States readiness (partner payroll, benefits, compliance) | C |
| prc | Suppliers, requisitions, catalogs, purchase orders, receiving, matching | B |
| prc.sourcing, prc.contracts | RFQ and tenders; contract management | B |
| inv | Stock attributes, warehouses, stock transactions, valuation, counts, replenishment | B |
| inv.tracking, inv.quality, inv.operations | Batch, serial and expiry; inspection; warehouse tasks and mobile | B |
| ast | Register, acquisition, depreciation, transfers, disposal, verification | B |
| ast.maintenance | Maintenance management | B |
| sal | Customers, pricing, quotations, orders, deliveries, billing, returns | C |
| sal.contracts, sal.commissions, sal.channels | Contracts and recurring; commissions; portal, mobile and van sales | C |
| scm | Demand and supply planning, S&OP, optimization, order promising | C |
| scm.tms, scm.wms, scm.collab, scm.tower | Transportation; advanced warehouse management; supplier collaboration; control tower | C |

### 8.2 Add-on, portal and pack keys

| Key | Unlocks |
| --- | --- |
| ai.gateway | AI controls, metering and the gateway; required by every other ai key |
| ai.assistant, ai.nlq, ai.docint, ai.predictive, ai.config, ai.migration | Assistants; natural-language analytics; document intelligence; predictive services; configuration assistant; migration assistant |
| analytics.pro | Pivot analysis, external recipients, analytics API and external BI access |
| analytics.extract, analytics.superset | Analytical extract store; Superset adapter |
| portal.supplier, portal.customer, portal.carrier | External portals |
| mobile.apps | Native-feel mobile applications |
| integration.premium | EDI and advanced connectors |
| pack.country.pk, pack.country.sa, pack.country.us | Country packs |
| pack.industry.\<name> | Industry packs (retail, services, education, healthcare, distribution) |

### 8.3 Limit keys

users.named, users.concurrent, companies, employees.active, storage.gb, api.calls, ai.tokens, portal.external\_users. Limits and keys together form the entitlement of document 1, section 7.1.

## 9. Consolidated wave plan and the extended build catalog

### 9.1 Waves at a glance

| Wave | Scope | Specifications | Build steps | Pilot exit |
| --- | --- | --- | --- | --- |
| A | Thin kernel, Finance, HRMIS core and payroll (Pakistan), analytics and AI foundations | Documents 1 to 6, 9 (foundations), 12, 13 | Steps 0 to 6 | Finance month closed in parallel; two payroll cycles reconciled |
| B | Procurement, Inventory, Asset Management, HRMIS talent, Saudi pack, document intelligence | Documents 7, 8, 6 | Steps 7 to 9 and 13 | Procure-to-pay and stock flows run end to end; depreciation cycle reconciled to Finance |
| C | Sales, Supply Chain, US readiness, Superset adapter | Documents 10, 11, 6 | Steps 10, 11, 12 and 14 | Order-to-cash and planning cycles run end to end |
| D | Industry verticals, point of sale, marketplace, predictive HR | Future documents | To be planned | Demand driven |

### 9.2 Additions to document 12, section 6 (decision C-30)

**Step 2 additions (kernel gaps, decision D-20)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S2.20 | Global search and command palette | D13 IA-03, D2 SRC (to be added) | Opus |
| S2.21 | Collaboration: comments, mentions, tasks, activity feed | D2 COL (to be added) | Opus |
| S2.22 | Offline sync framework: device registry, sync protocol, outbox, conflicts, remote wipe | D2 SYN (to be added), D13 MOB | Fable |
| S2.23 | Print service for documents, labels and cheques | D2 DOC (extended) | Opus |

**Step 7: Procurement (wave B)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S7.0 | External portal framework: separate authentication realm, invitations, isolation, branding | D2 PRT (to be added) | Fable |
| S7.1 | Supplier management and onboarding | D7 SUP | Opus |
| S7.2 | Requisitions, catalogs and price lists | D7 REQ, CAT | Opus |
| S7.3 | RFQ and tenders including bid secrecy | D7 SRC | Opus with Fable review |
| S7.4 | Purchase orders, revisions, commitment events | D7 POR | Opus |
| S7.5 | Receiving and service acceptance with the ReceiveGoods contract | D7 RCV, D14 D-15 | Opus |
| S7.6 | Invoice matching and hand-off to Finance | D7 MAT, D14 D-12 | Fable |
| S7.7 | Contracts | D7 CTR | Opus |
| S7.8 | Supplier portal | D7 PRT | Opus with Fable security review |
| S7.9 | Controls and analytics content | D7 CTL, ANL | Opus |

**Step 8: Inventory (wave B)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S8.1 | Stock attributes, warehouses, locations | D8 ITM, WHS | Opus |
| S8.2 | Stock ledger and movements | D8 MOV | Fable |
| S8.3 | Valuation and landed cost | D8 VAL | Fable |
| S8.4 | Batch, serial, expiry and recall | D8 TRK | Opus |
| S8.5 | Reservations and availability | D8 RSV | Fable |
| S8.6 | Counts | D8 CNT | Opus |
| S8.7 | Replenishment and classification, UpdatePlanningParameters contract | D8 RPL, D14 D-13 | Opus |
| S8.8 | Quality inspection | D8 QLT | Opus |
| S8.9 | Warehouse operations and mobile | D8 OPS | Opus with Fable review of offline behavior |
| S8.10 | Inventory reports | D8 IRP | Opus |

**Step 9: Asset Management (wave B)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S9.1 | Register and classes | D8 AMS | Opus |
| S9.2 | Acquisition and construction in progress | D8 ACQ | Opus |
| S9.3 | Depreciation engine | D8 DEP | Fable |
| S9.4 | Transfers, adjustments, revaluation, impairment | D8 TRF | Opus |
| S9.5 | Disposal | D8 DSP | Opus |
| S9.6 | Physical verification on mobile | D8 VER | Opus |
| S9.7 | Maintenance | D8 MNT | Opus |
| S9.8 | Custody, insurance, reports and Finance reconciliation | D8 CST, INS, ARP | Opus |

**Step 10: Sales and Distribution (wave C)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S10.1 | Customer sales data, hierarchies, territories | D11 CUS | Opus |
| S10.2 | Pricing determination engine, discounts, promotions | D11 PRC | Fable |
| S10.3 | Quotations and pipeline | D11 QUO | Opus |
| S10.4 | Orders with availability and credit checks | D11 SOR | Opus |
| S10.5 | Contracts and recurring billing | D11 CTR | Opus |
| S10.6 | Delivery with the IssueGoods contract | D11 DLV, D14 D-15 | Opus |
| S10.7 | Billing documents, legal numbering, e-invoicing interface | D11 BIL, D14 D-16 | Fable |
| S10.8 | Returns and credits | D11 RET, D14 D-17 | Opus |
| S10.9 | Commissions | D11 COM | Opus |
| S10.10 | Channels: customer portal, mobile field sales, van sales | D11 CHN | Opus with Fable review of offline behavior |
| S10.11 | Sales reports | D11 SAN | Opus |

**Step 11: Supply Chain (wave C)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S11.1 | Planning master data and data quality | D10 PMD | Opus |
| S11.2 | Demand planning and forecasting | D10 DEM | Opus, Fable for the forecasting engine |
| S11.3 | Supply planning engine | D10 SUP | Fable |
| S11.4 | Sales and operations planning | D10 SOP | Opus |
| S11.5 | Inventory optimization and rebalancing | D10 OPT | Fable |
| S11.6 | Order promising and fulfillment | D10 OFM | Opus |
| S11.7 | Transportation, tracking, proof of delivery, freight settlement | D10 TMS | Opus |
| S11.8 | Advanced warehouse management | D10 WMS | Opus |
| S11.9 | Supplier collaboration and returns | D10 SCO, RLG | Opus |
| S11.10 | Control tower and analytics | D10 CTW, SCA | Opus |

**Steps 12 to 14: intelligence, talent and United States readiness**

| Slice group | Task | Spec | Model |
| --- | --- | --- | --- |
| Step 12 | Analytics API and external BI; explorer and pivot; document intelligence; migration assistant; anomaly and forecasting services; Superset adapter and analytical extract; evaluation and red-team expansion | D9 | Opus, Fable for prediction services and safety suites |
| Step 13 | HRMIS recruitment, performance, learning, employee relations; Saudi pack; asset custody integration | D6, D8 | Opus, Fable for the Saudi payroll and end-of-service logic |
| Step 14 | United States readiness: partner payroll integration, benefits, compliance | D6 | Opus with practitioner review |

## 10. Gaps register

Topics that matter for selling and running the product but are not covered by any of documents 1 to 13. Priority reflects when the gap starts to hurt, not its importance in the long run.

| ID | Gap | Why it matters | Priority | Needed by |
| --- | --- | --- | --- | --- |
| G-01 | Security architecture and threat model: key management design, secure development lifecycle, incident response, compliance roadmap (SOC 2, ISO 27001, Saudi national cybersecurity controls, data protection law) | Architecture section 10 is a summary only; enterprise buyers ask for evidence | High | Before milestone M2 |
| G-02 | Legal and commercial documents: end-user license (binary and source editions), SaaS terms, data processing agreement, service levels, source license and escrow, partner agreement, AI terms | The licensing and source-edition model depends on contract, not code (document 1, section 7.5) | High | Before the first customer |
| G-03 | Pricing, packaging and vendor back office: editions, price metrics, partner margins, tenant invoicing, payment collection, usage statements | Entitlements exist, but nobody bills the customer yet | High | Before SaaS launch |
| G-04 | Operations and release engineering: environments, CI and CD, observability, runbooks, disaster recovery, offline update bundles, support tooling | Only parts are in documents 1 and 12 | High | Step 0 to Step 1 and Step 6 |
| G-05 | Pakistan country pack specification: charts of accounts, taxes and withholding, payroll slabs and statutory contributions, bank file formats, document templates | Wave A cannot be validated without it; documents 5 and 6 only describe what packs contain | High | Before Step 3 |
| G-06 | Test strategy and test data: master test plan, synthetic data generator, demo tenants, golden dataset library, performance environment | Every document requires tests; none defines the shared tooling | Medium | Step 0 |
| G-07 | Data migration playbook and implementation methodology: project templates, cutover plans, partner enablement and certification | Implementations decide customer satisfaction | Medium | Before the pilot |
| G-08 | Capacity and sizing guide for on-premise and SaaS | Customers ask for hardware sizing; targets exist, sizing does not | Medium | Before the pilot |
| G-09 | Manufacturing and production: bills of materials, routings, material requirements planning, work orders, production costing | Many medium and large enterprises in the target markets manufacture; kits are the only support today | Strategic | Roadmap decision before wave C |
| G-10 | Projects and services: project accounting, billing, resource planning, timesheet billing | Services customers need it; only a dimension and timesheets exist | Medium | Roadmap decision before wave C |
| G-11 | Customer relationship depth and service desk, field service | The light pipeline covers little beyond sales quotes | Low | Wave D |
| G-12 | Retail point of sale vertical | Hooks only; retail packs need it | Medium | With the retail pack |
| G-13 | Treasury and trade finance: letters of credit, guarantees, loans, cash pooling | Referenced in several documents, never specified | Low | Wave D |
| G-14 | Revenue recognition and lease accounting | Required by international and US reporting standards for many mid-size and large entities | Medium | Wave C |
| G-15 | Industry pack specifications: retail, services, education, healthcare, distribution | Starter packs are promised as part of the industry-neutral strategy | Medium | Wave D, with starter content earlier |
| G-16 | Vendor support operations: support desk, status page, escalation, customer success | Needed from the first customer | Medium | Before the first customer |
| G-17 | Documentation and developer portal: help center authoring, API portal, partner portal | The product ships help offline; the production process is undefined | Medium | Before the pilot |
| G-18 | Partner ecosystem and extension SDK, marketplace | Rung 5 of the extension ladder (document 1, section 4.7) is undefined | Low | Wave D |

## 11. Open decisions register

Every open decision stated in documents 1 to 13, grouped by owner and the point at which it must be settled. Items resolved by decisions D-12 to D-25 are omitted.

### 11.1 Yours to decide

| Decision | Source | Needed by |
| --- | --- | --- |
| Product name and code namespace | Documents 1, 12 | Before slice S0.1 |
| Pilot customer for Finance and HRMIS; beachhead segment | Documents 1, 6 | Before Step 1 ends |
| Code hosting and CI provider | Document 12 | S0.1 |
| When to engage the technical adviser | Document 12 | Before Step 1 |
| Accountant and payroll practitioner reviewers for Pakistan | Documents 5, 6, 12 | Before Step 3 |
| Hosting provider and regions for SaaS | Document 1 | Before Step 6 |
| Pricing and metering definitions; support model; SaaS billing system | Documents 1, 2, 4, 6 | Before the first contract |
| Whether Sales moves from wave C to wave B | Documents 1, 11 | Before Step 7 planning |
| Run the Fable 5.2 versus Opus 5.5 comparison on the first two high-risk slices | Document 12 | S1.3 and S3.2 |
| Roadmap position of manufacturing, projects, revenue recognition and lease accounting | Gaps G-09, G-10, G-14 | Before wave C |

### 11.2 To settle by technical decision record or spike

| Decision | Source | Needed by |
| --- | --- | --- |
| Expression engine (Dynamic Expresso or NCalc) | Documents 1, 2 | S2.10 |
| Obfuscation and license protection tooling | Documents 1, 12 | S6.3 |
| UUIDv7 library, partition maintenance approach, encryption library and key management product per deployment type, jsonb versus promoted columns for hot entities | Document 3 | Step 1 and Step 2 |
| Data grid, accessible primitives, forms and state libraries, icon set, Hijri calendar library, map data, component workshop tool | Document 13 | S1.8 and Step 2 |
| Workflow canvas library; form designer technology; SCIM in wave A or B; partner-authored packs | Document 4 | S2.9 and S2.19 |
| Separate orchestration engine beyond native approvals; messaging gateways; e-signature provider; default retention periods; scope of the configuration assistant in wave A | Document 2 | Step 2 |
| Build or adopt a semantic layer; OCR engine or vision model for Urdu and Arabic; local model candidates and hardware sizing; vector index limits; analytical store; AI regulatory classification per country | Document 9 | Step 2 and Step 12 |

### 11.3 Module-specific open items

| Module | Open items | Source |
| --- | --- | --- |
| Finance | Chart of accounts template sources; first banks and file formats; e-invoicing scope per country; report writer build or adopt; cheque printing scope; expected-credit-loss in wave A | Document 5 |
| HRMIS | First biometric devices and banks; scripting and extension scope for partners; corporate card integration; employee data export approvals | Document 6 |
| Procurement | Punch-out catalog standards; supplier risk data providers; public e-procurement integrations; import and customs depth | Document 7 |
| Inventory and Assets | Manufacturing scope; warehouse management depth; RFID and sensors; lease accounting; component accounting defaults; depth of maintenance management | Document 8 |
| Supply Chain | Build or integrate constrained planning and route optimization; carrier and telematics connectors; EDI scope; control tower data providers; advanced warehouse management as a separate product | Document 10 |
| Sales | Depth of pipeline versus a separate CRM; EDI; payment connectors; point of sale timing; revenue recognition coupling; regulated-sector tracking connectors | Document 11 |
| UX | Urdu typeface default; digits and number grouping defaults per market; chart direction in right-to-left; density defaults; usability test recruitment; accessibility conformance report timing | Document 13 |

## 12. Patch list by document

These are the exact edits to apply once the decisions of section 6 are confirmed. I can apply them in one pass and give you a change log.

| Document | Section | Patch | Corrections |
| --- | --- | --- | --- |
| 1 Architecture | 0.1 | Add decisions D-12 to D-25 | C-31 |
| 1 | 1.1 | Add a legend explaining waves A, B, C, D and the A1 and A2 split used by HRMIS | C-35 |
| 1 | 4.4 | Replace the early event catalog by the canonical catalog of this document (section 4) and the retired names list | C-14 |
| 1 | 4.6, 13 | Add kernel components Portal, Sync, Search and Collaboration; add projects Erp.Portal, Erp.Sync, Erp.Search, Erp.Collaboration; print service under Documents | C-22 to C-26, C-31 |
| 2 Kernel | New areas | Add requirement areas PRT (external portals), SYN (offline sync), SRC (global search), COL (collaboration) and print service requirements under DOC | C-22 to C-26 |
| 2 | INT, 24 | Reference the analytics API of document 9 (ADH-10); extend the key entities table | C-27 |
| 3 Data | 3.7 | Add a reporting\_ext database role limited to the reporting schema | C-27 |
| 3 | 5, 6.4 | Add schemas for portal, sync, search and collaboration; point rpt and ai tables to document 9, section 25 | C-28 |
| 4 Admin | 13, 16 | Reference analytics and AI platform administration (document 9, sections 21 and ADM); add portal administration for external users | C-29, C-22 |
| 5 Finance | 2.2 | Reword the sentence about invoices issued in Sales and Procurement | C-16 |
| 5 | AP-01, AP-03 | Capture order-related invoices in Procurement; show match status; keep non-order invoices in Finance | C-16, C-18 |
| 5 | 17.1 | Consumed events: add PurchaseRequisition events, PurchaseOrderRevised and Cancelled, ReceiptPosted, ServiceAccepted, GoodsReturnRecorded, LeaveLiabilityCalculated, GratuityAccrualCalculated, StockTransferShipped and Received, StockRevaluationPosted; remove AdvanceIssued | C-02, C-03, C-05, C-06 |
| 5 | 17.2 | Published events: add VendorInvoicePosted, CustomerInvoicePaid, ReceiptAllocated | C-04, C-07 |
| 6 HRMIS | 30 | State that LoanDisbursed covers advances and that Finance consumes the liability events | C-05 |
| 7 Procurement | RCV-02 | Reword: Procurement calls ReceiveGoods; Inventory publishes GoodsReceiptPosted | C-01 |
| 7 | MAT-01, 22 | Reword capture point per D-12; close the quality ownership item per D-14 | C-18, C-19 |
| 7 | 16.1, 16.2 | Add PurchaseOrderConfirmed to published events; replace AssetCreated by AssetAcquired; add AsnReceived and SupplierForecastShared to consumed events | C-08, C-12, C-13 |
| 8 Inventory and Assets | 26.1 | Add StockAvailabilityChanged, CustomerReturnReceived, AssetStatusChanged | C-09, C-10, C-11 |
| 8 | 26.2, 26.3 | Receipts through the ReceiveGoods contract; remove DeliveryPosted and CustomerReturnReceived as consumed from Sales; add ReturnAuthorized, VendorInvoicePosted, AsnReceived, SupplierForecastShared; add UpdatePlanningParameters | C-01, C-07, C-11, C-13, C-17 |
| 8 | ITM-03, RPL-04 | Add note on D-13 (Supply Chain write-back) | C-17 |
| 10 Supply Chain | 17.1, 17.2 | Remove ReturnAuthorized from published events; Inventory publishes CustomerReturnReceived; clarify ReturnDispositioned and DeliveryConfirmed | C-11, C-15 |
| 10 | RLG-01, PMD-01, OPT-06 | Reference the Sales return authorization; reference D-13 and the UpdatePlanningParameters contract | C-15, C-17 |
| 11 Sales | 22, DLV-08 | Mark the four alignment items as resolved by D-15 to D-18 | C-15 |
| 12 Operating manual | 3.2, 6, 7, 8 | Import file names for documents 7 to 14; add slices S2.20 to S2.23 and Steps 7 to 14 (section 9.2); add a review prompt that checks published and consumed events against the registry | C-30 |
| 9 Analytics and AI, 13 UX | None | No change required; re-check after the patches above | None |

One addition to document 12 is worth stating in full. It is a ninth review prompt: *Event and contract conformance review: compare the events and contracts implemented in this slice with docs/events and the canonical catalog; list events published without consumers, consumed without publishers, name or version mismatches, missing idempotency keys, and any module that reads another module's tables.*

## 13. Next steps

1. Review sections 3 and 6 and tell me which decisions to approve, change or drop; a reply such as approve all, or approve all except D-13, is enough.
2. I apply the patches of section 12 to the documents, as plain edits with a change log or as suggestions you can accept one by one; tell me which you prefer.
3. After that the planning set is version 1.0. Export the documents as Markdown into docs/specs, create the decision log and event registry in the repository, and start Step 0 with the prompts of document 12.
4. Close the High gaps of section 10 in parallel with the early build: security architecture (G-01), operations and release engineering (G-04), the Pakistan pack specification (G-05), then legal and pricing documents (G-02, G-03) before the first customer conversation.
5. Decide the roadmap position of manufacturing, projects and revenue recognition (G-09, G-10, G-14) before planning wave C in detail.

## 14. Change log: patches applied on 13 November 2026

All patches were applied as plain edits to documents made in this conversation. Edits used exact-text replacement and targeted insertions; I did not re-read each document in full afterwards, so skim the changed places listed below.

| Document | What changed |
| --- | --- |
| 1 Architecture | Added decisions D-12 to D-25 (table before section 0.2); wave legend and corrected document list in section 1.1; event names updated in the section 4.4 catalog (GoodsReceived, StockMovementPosted, RequisitionApproved replaced); note on new kernel components after the section 4.6 table; four new project names in the repository tree; document status paragraph in section 17 |
| 2 Kernel | New sections 27 (portals), 28 (offline sync), 29 (global search), 30 (collaboration) and 31 (print service requirements FR-KRN-DOC-09 to 11 and analytics API FR-KRN-INT-10), appended at the end |
| 3 Data | reporting\_ext role sentence in section 3.7; new section 13 with schemas prt, syn, src, col, print tables, pointer to document 9 section 25 |
| 4 Admin | New section 21 (portal, device, search, collaboration and printer administration; analytics and AI administration pointer) |
| 5 Finance | Section 2.2 and AP-03 reworded for D-12 and D-16; FXA-03 reworded; consumed and published event tables updated; new paragraph before section 17.2 listing the additional consumed events (ReceiptPosted, ServiceAccepted, GoodsReturnRecorded, CommissionApproved, FreightAccrued, CarrierInvoiceApproved) |
| 6 HRMIS | Section 3.2 sentence clarifies LoanDisbursed covers advances and lists the liability events |
| 7 Procurement | RCV-02 and MAT-01 reworded for D-15 and D-12; quality ownership question closed (D-14); PurchaseOrderConfirmed added; AssetCreated replaced by AssetAcquired; AsnReceived and SupplierForecastShared noted in the consumed events |
| 8 Inventory and Assets | Event tables updated (StockAvailabilityChanged, CustomerReturnReceived, AssetStatusChanged, ReturnAuthorized, VendorInvoicePosted, AsnReceived, SupplierForecastShared); receipts via ReceiveGoods; ITM-03 and RPL-04 note the Supply Chain write-back; new paragraph before section 27 describing UpdatePlanningParameters and the receive, issue and return contracts |
| 9 Analytics and AI | No change required |
| 10 Supply Chain | Return authorization ownership, event publisher fixes, PMD-01 and OPT-06 notes for D-13, DeliveryConfirmed publisher note |
| 11 Sales | Alignment items marked resolved; DLV-08 states the DeliveryConfirmed publisher rule |
| 12 Operating manual | New section 6.1 (slices S2.20 to S2.23 and Steps 7 to 14); review prompt 8.7; spec import file names for documents 7 to 14; ADR and event registry wording updated |
| 13 UX | No change required |

### 14.1 Additional correction found while patching

| ID | Sev | Issue | Resolution |
| --- | --- | --- | --- |
| C-36 | Med | Finance did not list several events that Sales and Supply Chain publish for it (RebateAccrued, BillingPeriodEvent, CommissionApproved, FreightAccrued, CarrierInvoiceApproved) | Added to document 5 section 17.1 and the paragraph before section 17.2; present in the canonical catalog of section 4 |

### 14.2 Known limits of the patching

- A table row cannot be added with a text edit, so some new events appear in existing cells or in a paragraph beside the table instead of as new rows; the canonical catalog in section 4 is the authoritative list.
- Document 1 section 17 still shows the old status cells in its table; the new status paragraph below it is correct.
- In document 6 the event table cell for LoanDisbursed is unchanged; the clarification is in section 3.2.
- The retired event names list exists only in section 4.10 of this document.
- Entries in documents 7 and 8 that mention AsnReceived and SupplierForecastShared are in the effect column, not in the event column.

### 14.3 Recommended follow-up

1. Skim the changed places in documents 1, 5, 7 and 8, where the edits were densest.
2. Confirm D-05, D-07, D-09, D-10 and D-11 so the decision log is fully closed.
3. Export the documents as Markdown into docs/specs and create docs/events from section 4, then start Step 0 with the prompts of document 12.

## 15. Addendum: document 15 (Editions and Starter Edition)

Document 15, drafted on 13 November 2026 after the review, adds the edition model and the small-business Starter edition. It partly closes gap G-03 (pricing and packaging) by defining structure, not prices.

| Item | Addition |
| --- | --- |
| Decisions | D-26 three editions as entitlement templates; D-27 small tenants stay on database per tenant until review triggers justify a pooled micro tier by ADR; D-28 Trading Starter launches without the Sales module, with a Service Starter possible after wave A; D-29 simple mode is a configuration and presentation layer, never a code branch. All four are Proposed and need your confirmation |
| Requirement prefix | FR-STR-area-nn (areas SIM, ONB, TRD, LIM, OPS, SUP, BIL, MOB) joins the registry of section 7.2 |
| Build catalog | Step 15 slices S15.1 to S15.11 added to document 12, section 6.1 |
| Entitlements | Editions are templates over the key catalog of section 8; no new keys, plus an optional people.lite key for the People Lite add-on |
| Waves | A Service Starter can follow wave A, the Trading Starter wave B |

## 16. Baseline 1.0 addendum (this document is now version 0.2)

*Added 14 November 2026 by the comprehensive review (Doc 16). Sections 1 to 15 are unchanged. The previous state (version 0.1) is kept in this document's version history and in the frozen originals archive. This section extends the decision log (section 6), the corrections register (section 3), the gaps register (section 10) and the wave plan (section 9).*

### 16.1 Decision log additions

| ID | Decision | Status | Detail |
| --- | --- | --- | --- |
| D-30 | Hybrid-ready tenancy: `tenant_id` on every tenant-scoped table; dedicated database by default; pooled mode with row-level security for SaaS Starter | Proposed | Doc 1 section 18.1; Doc 2 section 32.3; Doc 3 section 14.1 |
| D-31 | Atomic ledger posting by default; deferred posting by policy | Proposed | Doc 1 section 18.2; Doc 5 section 24.1; Doc 16 FR-XM-01 to 03 |
| D-32 | Four phases replace waves as the planning unit; Sales core in Phase 3 | Proposed | Doc 1 section 18.4; Doc 11 section 23.1; Doc 16 section 8 |
| D-33 | Minimum Viable Kernel; other kernel services scheduled by phase | Proposed | Doc 2 section 32.1 |
| D-34 | Release scope tagging (R, L, X) at each phase entry | Proposed | Doc 12 section 16.3 and 16.4 |
| D-35 | Module data design gate before any module build | Proposed | Doc 3 section 14.5 |
| D-36 | Geospatial in the application; no database geospatial extension | Proposed | Doc 3 section 14.4; Doc 2 FR-KRN-GEO-01 |
| D-37 | HRMIS v0.3 is the HRMIS build baseline; kernel accepts KER-01 to KER-08 | Proposed | Doc 2 section 32.2; HRMIS v0.3 section 41 |
| D-38 | Three-model allocation by risk class, with escalation rules | Proposed | Doc 12 section 16.2; Doc 16 section 9 |
| D-39 | Change control for baseline items; repository registers | Proposed | Doc 16 section 11 |
| D-40 | Four size profiles as configuration presets over three editions | Proposed | Doc 15 section 17; Doc 16 section 5 |

Still proposed from earlier: D-05, D-07, D-09, D-10, D-11 (Doc 1) and D-26 to D-29 (Doc 15). D-27 is absorbed by D-30 if D-30 is approved: the pooled micro tier no longer waits for review triggers to be designed, only to be sold. D-28 is changed by D-32: the Trading Starter launches with Sales core in Phase 3 instead of without Sales.

### 16.2 Corrections register additions

| ID | Documents | Problem | Resolution | Applied in |
| --- | --- | --- | --- | --- |
| C-37 | 3, 5 | Journal idempotency key omits the ledger although one source can post to several ledgers | Ledger added to the key | Doc 3 section 14.2; Doc 5 PST-06 and section 24.2 |
| C-38 | 1, 3, 5 | Item and location dimensions promised but absent from the journal line model | Explicit dimension columns listed | Doc 3 section 14.2 |
| C-39 | 1, 3, modules | Branch is a data-scope dimension but no transactional table carries it | `branch_id` standard column on transactional tables | Doc 3 section 14.2; Docs 7, 8, 10, 11 addenda |
| C-40 | 3, 5, 8 | Posting and stock movements update single balance rows, which serializes month-end load | Append deltas and roll up | Doc 3 section 14.2; Doc 5 section 24.2; Doc 8 section 33.2 |
| C-41 | 3, 7, 8, 11 | Kernel item holds one cost and one price for all companies | Renamed to defaults, informational only | Doc 3 section 14.2; Docs 7, 8, 11 addenda |
| C-42 | 3 | Association tables without `id`; server-generated ids versus offline creation | Composite keys allowed; server assigns ids on sync | Doc 3 section 14.2 |
| C-43 | 2, 6a | HRMIS v0.3 kernel requests absent from the kernel specification | Accepted with phases and requirement IDs | Doc 2 section 32.2 |
| C-44 | 12 | Model table named a model outside the three in use and had Fable build high-risk slices | Replaced by risk-class allocation | Doc 12 section 16.2 |
| C-45 | 1, 9 section, 12, 6a | Three planning vocabularies (waves, steps, mobile releases) | Four phases; mapping tables | Doc 1 section 18.4; Doc 12 section 16.1; HRMIS v0.3 section 41 |
| C-46 | 5 | Exception queue was a pre-close check, not a block | Period cannot close with queue items dated in it | Doc 5 FR-FIN-PST-15 |

### 16.3 Phase plan (supersedes section 9 as the planning unit on approval of D-32)

Wave A1 kernel work maps to Phase 1; Finance, HRMIS core and payroll (waves A1 and A2) to Phase 2; wave B plus Sales core from wave C, the Saudi pack and the HRMIS v0.3 geo-attendance and task extensions to Phase 3; the rest of waves C and D to Phase 4. The extended build catalog of section 9 remains valid as a list of slices; Doc 12 section 16.1 assigns them to phases.

### 16.4 Gaps register status

| Gap | Status after this review |
| --- | --- |
| G-01 Security architecture and threat model | Open. Completion condition of Phase 1 (Doc 16 section 12). Personal-data erasure rule added (Doc 3 section 14.3) |
| G-02 Legal and commercial documents | Open. Needed before the first sale, not before development |
| G-03 Pricing and packaging | Open. Needed before Release 1.0 |
| G-04 Operations and release engineering | Open. Written during Phase 1 with the human adviser |
| G-05 Pakistan country pack specification | Open. Entry condition of Phase 2 payroll and tax work |
| G-06 Test strategy and test data | Partly closed: quality gates and integrity invariants defined (Doc 16 section 11.6; Doc 3 section 14.6). Master test plan still to write in Phase 1 |
| G-08 Capacity and sizing guide | Open. Produced from Phase 2 and 3 load tests |
| G-09 Manufacturing | Out of scope by your module list; remains a later option |
| New: module column-level data designs | Closed by process (D-35), delivered per module at phase entry |
| Others (G-07, G-10 to G-18) | Unchanged |

### 16.5 Canonical registries

The event catalog (section 4), ownership matrix (section 5), naming registry (section 7) and entitlement key catalog (section 8) are unchanged and remain authoritative. FR-XM is added to the requirement prefix registry (cross-module rules, owned by Doc 16). FR-KRN-GEO is added for the kernel geospatial service.

### 16.6 Baseline 1.1 decisions (15 November 2026)

| ID | Decision | Status | Replaces or changes | Detail |
| --- | --- | --- | --- | --- |
| D-41 | Phase order: HRMIS, then Inventory and Assets, then Finance, then Procurement and Supply Chain; Sales core with Procurement by default | Proposed | D-08, D-32 | Doc 16 section 14.1 |
| D-42 | Phase 1 = minimum kernel for HRMIS + HRMIS core and Pakistan payroll + Android M1 | Proposed | D-33 (narrower) | Doc 16 section 14.2 |
| D-43 | Until Finance exists, financial effects go through the kernel stub as journal exports; Finance starts at a cutover with opening balances | Proposed | D-31 deferred to Phase 3 | Doc 16 section 14.3 |
| D-44 | Database per tenant only; `tenant_id` kept on tables; pooled mode not built | Proposed | D-30 | Doc 16 section 14.5 |
| D-45 | Models: Opus 5.5 for design and integrity-critical work with fresh-session review; Sonnet 5.5 for routine work; Fable not used | Proposed | D-38 | Doc 16 section 14.6 |
| D-46 | Conditions to start Phase 1 | Proposed | Doc 16 section 12.3 | Doc 16 section 14.7 |

**Decision record, 15 November 2026:** D-41, D-42, D-43, D-45 and D-46 are **approved** by the product owner. D-44 is **rejected**, and the tenancy model for Phase 1 is open until the replacement is decided.

| ID | Decision | Status | Replaces |
| --- | --- | --- | --- |
| D-47 | Every customer has its own separate database, in SaaS and on-premises alike. Business tables carry no `tenant_id` and no tenant row-level security, because isolation is by database. Pooled or shared-database hosting is not offered. Reversing this would need a new decision. | Approved 15 November 2026 (product owner: "separate database for every customer") | D-30, D-44; confirms D-02 and Doc 1 section 5.1 as originally written |
| — | Core Sales is built in Phase 4 with Procurement and Supply Chain | Working assumption, not answered | Doc 16 section 14.1 default |
| — | D-05, D-07, D-09, D-10, D-11, D-26, D-29, D-33 to D-37, D-39, D-40 and assumptions AS-1, AS-2, AS-4, AS-6 | Working assumptions as recommended, not yet confirmed | — |

Consequence of D-47: Doc 3 rules R-16 to R-20 and Doc 2 FR-KRN-TEN-13 to TEN-17 are withdrawn. Doc 15 FR-STR-OPS-10 to OPS-12 are withdrawn, and section 10.1 option A applies to Starter tenants. Small customers run as separate databases on shared database servers, with connection pooling and idle-tenant hibernation (FR-STR-OPS-02).

**Decision record, 15 November 2026 (second entry):** D-05, D-07, D-09, D-10, D-11, D-26, D-29, D-33, D-34, D-35, D-36, D-37, D-39 and D-40 are **approved** by the product owner. Core Sales in Phase 4 and the assumptions AS-1, AS-2, AS-4 and AS-6 remain working assumptions. The code repository is github.com/isamad123/ERP (private).
