# Procurement: Functional Specification (v0.2)

Document 7 of the planning set | Prepared for Ibrahim | 10 November 2026 | Companion to the Platform Kernel, Data Architecture, Admin Console, Finance and HRMIS specifications

## 1. Introduction

### 1.1 Purpose and scope

This document specifies the Procurement module: supplier management, purchase requisitions, catalogs and pricing, sourcing (RFQ and tenders), purchase orders, receiving and service acceptance, invoice matching, contracts, controls, the supplier portal and spend analytics. It covers the full procure-to-pay chain up to the hand-off of approved supplier invoices to accounts payable.

Procurement is built in wave B after Finance and HRMIS. It is specified now so that the kernel (parties, catalog, tax, workflow, rules, documents, entitlements) and Finance (budget control, commitments, posting rules) already contain the hooks it needs.

### 1.2 Dependencies (decision D-10)

| Module | Relationship |
| --- | --- |
| Kernel | Hard dependency: organization, identity, parties (suppliers), catalog (items and services), tax, localization, numbering, workflow, rules, documents, notifications, reporting |
| Finance | Optional integration: budget availability, commitments, supplier invoice posting to payables, payment status. Without Finance, budget checks are skipped and approved invoices go to the Accounting Export connector |
| Inventory | Optional integration: receipts of stock items, stock levels for reorder, landed cost. Without Inventory, only non-stock items and services are received (delivery confirmation or service acceptance) |
| Assets | Optional integration: purchases flagged as assets create asset acquisition events. Without Assets, the flag is informational |
| Supply Chain | Optional integration: replenishment proposals create requisitions; supplier collaboration extends the portal |
| HRMIS | Optional integration: approver resolution by organization and position; employee expense claims remain in HRMIS |

### 1.3 Conventions

- Requirement IDs: FR-PRC-\<area>-\<number>. Priority (M), (S), (C); unmarked is (M). Wave B unless stated.
- Each area lists Functional Requirements, Business Rules (BR) and Acceptance Criteria (AC).
- Every configuration is reachable through the admin console and the API (documents 2 and 4).
- Country-specific procurement and tax rules are pack content and are verified by local advisers before release.

### 1.4 Actors

| Actor | Role |
| --- | --- |
| Requester | Raises purchase requisitions |
| Department Approver | Approves requisitions within authority |
| Buyer | Sources, orders and manages suppliers |
| Procurement Manager | Sets policy, approves high-value orders, reviews performance |
| Receiver | Confirms goods or service delivery |
| Quality Inspector | Inspects incoming goods where used |
| AP Clerk | Processes supplier invoices (Finance) |
| Budget Owner | Owns budgets consumed by requisitions (Finance) |
| Supplier | External user of the portal |
| Evaluation Committee Member | Scores bids in tender processes |
| Auditor | Read-only access with drill-down |

### 1.5 Key terms

| Term | Meaning |
| --- | --- |
| Requisition (PR) | An internal request to buy something |
| RFQ | Request for quotation sent to suppliers |
| Purchase order (PO) | A binding order to a supplier |
| Goods receipt (GR) or service acceptance | Confirmation that goods or services were delivered |
| Three-way match | Comparison of PO, receipt and supplier invoice |
| Blanket or framework agreement | A contract with agreed prices and limits, with call-off orders |
| Commitment | Budget reserved by an approved requisition or PO |
| GRNI | Goods received not invoiced |
| Maverick spend | Purchases outside approved suppliers, contracts or catalogs |

## 2. Module overview

### 2.1 Capability map

| Area | Summary |
| --- | --- |
| Supplier management | Onboarding, qualification, categories, documents, performance, blocking |
| Requisitions | Request goods, services, assets; approvals; budget check; consolidation |
| Catalogs and pricing | Supplier catalogs, price lists, discounts, preferred suppliers, source determination |
| Sourcing | RFQ, quotation comparison, tenders, evaluation, award |
| Purchase orders | Standard, blanket call-off, service, contract-linked, import; changes and approvals |
| Receiving | Goods receipts, service acceptance, returns, tolerance, inspection hooks |
| Invoice matching | Two-way and three-way matching, tolerance, discrepancy handling, hand-off to AP |
| Contracts | Repository, terms, renewals, obligations, spend against contract |
| Controls | Approval matrix, segregation of duties, policy checks, anomaly detection |
| Supplier portal | Self-service for onboarding, quotes, confirmations, shipments, invoices, statements |
| Spend analytics | Spend, savings, cycle time, compliance, supplier scorecards |

### 2.2 Procure-to-pay flow and documents

```
Need -> Requisition -> Approval and budget check -> Sourcing (RFQ or catalog or contract)
     -> Purchase order -> Supplier confirmation -> Receipt or service acceptance
     -> Supplier invoice -> Matching -> Approval -> Hand-off to AP -> Payment (Finance)
```

| Document | Key statuses |
| --- | --- |
| Purchase requisition | draft, submitted, approved, rejected, partially ordered, ordered, closed, cancelled |
| RFQ | draft, issued, responses open, evaluated, awarded, cancelled |
| Purchase order | draft, submitted, approved, issued, confirmed, partially received, received, invoiced, closed, cancelled |
| Receipt or service acceptance | draft, posted, reversed |
| Supplier invoice (matching record) | received, matched, on hold, approved, handed to AP, rejected |
| Contract | draft, in approval, active, expiring, expired, terminated |

Status models are configurable within module-defined safe limits (kernel MET-30).

### 2.3 Document flow

Every document links to its predecessors and successors through the kernel document-link table, so users can trace requisition to payment, and reports can measure cycle time and conversion without cross-module joins.

## 3. Supplier management (SUP)

**Purpose:** know and control the supplier base. Supplier master data is the kernel business partner with the supplier role (kernel PTY). Procurement adds onboarding, qualification and performance. **Actors:** Buyer, Procurement Manager, Supplier.

- **FR-PRC-SUP-01** Supplier categories shall be configurable with hierarchy (for example goods, services, works, consultancy) and drive approval, qualification and tax rules.
- **FR-PRC-SUP-02** Supplier onboarding shall be a configurable workflow: registration (by buyer or through the portal), required documents by category (registration, tax certificates, bank letter, quality certificates, references), verification, risk scoring, approval, and activation.
- **FR-PRC-SUP-03** Qualification shall support questionnaires with scoring, document checks, site audits and approval levels, with requalification due dates and reminders.
- **FR-PRC-SUP-04** Supplier documents with expiry (tax certificates, licenses, insurance) shall trigger reminders and can automatically block new orders on expiry according to policy.
- **FR-PRC-SUP-05** Supplier status (prospect, approved, conditionally approved, on hold, blocked, blacklisted) shall control selection on requisitions, RFQs and orders, with reasons, approvals, and effective dates; blacklisting shall be recorded with reference and notification.
- **FR-PRC-SUP-06** Preferred and approved supplier lists shall be maintained per category, item, location and company, with validity dates.
- **FR-PRC-SUP-07** Performance scorecards shall combine delivery timeliness, quantity accuracy, quality, price compliance, responsiveness and invoice accuracy using configurable weights, producing periodic ratings with trends and review workflows.
- **FR-PRC-SUP-08** Supplier risk indicators (financial, compliance, concentration, single-source, country) shall be maintained with manual entry and optional external data connectors. (S)
- **FR-PRC-SUP-09** Conflict-of-interest declarations by buyers and evaluators shall be captured and checked against supplier relationships. (S)
- **FR-PRC-SUP-10** Supplier diversity and local-content attributes (for example small business, women-owned, local supplier) shall be recordable and reportable. (S)
- **FR-PRC-SUP-11** Supplier merge and duplicate detection shall use the kernel duplicate manager (tax ID, bank account, name similarity) and carry procurement history to the surviving record.
- **FR-PRC-SUP-12** Supplier communication history (messages, notices, meetings) shall be recorded with attachments.
- **BR:** a blocked or blacklisted supplier cannot receive new RFQs or orders; open orders show a warning and require explicit action; bank detail changes follow kernel maker-checker.
- **AC:** a supplier registers through the portal, uploads required documents, is scored, approved by the procurement manager and becomes selectable; an expired tax certificate blocks new orders according to policy and notifies the buyer.

## 4. Purchase requisitions (REQ)

**Purpose:** let anyone request purchases through a controlled, simple process. **Actors:** Requester, Department Approver, Buyer, Budget Owner.

- **FR-PRC-REQ-01** Requisitions shall be created for stock items, non-stock items, services, assets, expense categories and free-text items, with quantity, unit, required date, delivery location, cost center, project, account (optional), justification and attachments.
- **FR-PRC-REQ-02** Requesters shall select from catalogs and favorites, copy previous requisitions, and use shopping-cart style entry with quick quantity edit.
- **FR-PRC-REQ-03** Requisition types shall be configurable (standard, urgent, emergency, capital, recurring, blanket) with different approval, budget and sourcing behavior.
- **FR-PRC-REQ-04** Approval routing shall use the kernel workflow engine with conditions by amount, category, cost center, project, asset flag, requester grade and risk, with delegation and escalation.
- **FR-PRC-REQ-05** When Finance is licensed, budget availability shall be checked on submission and on approval according to the budget policy (warn or block), and a commitment shall be recorded when approved; the requester sees available budget and the reason for any block.
- **FR-PRC-REQ-06** Approved requisition lines shall be routed to buyers by rules (category, location, value, supplier relationship) and appear in a buyer work queue.
- **FR-PRC-REQ-07** Buyers shall consolidate requisition lines into RFQs or purchase orders by item, supplier, delivery date and location, with traceability of each source line.
- **FR-PRC-REQ-08** Stock replenishment proposals from Inventory or Supply Chain shall create requisitions automatically or as drafts for review. (when those modules are licensed)
- **FR-PRC-REQ-09** Requesters shall see the status of each line (approved, sourced, ordered, received, invoiced) and expected delivery dates, and receive notifications at key steps.
- **FR-PRC-REQ-10** Changes to an approved requisition (quantity, date, supplier hint, cancellation) shall follow a change workflow and adjust commitments.
- **FR-PRC-REQ-11** Recurring and blanket requisitions shall define schedule, quantity, validity and release rules, creating requisitions or call-offs automatically with approvals.
- **FR-PRC-REQ-12** Mobile requisition entry and approval shall be supported, including barcode scan for catalog items.
- **FR-PRC-REQ-13** Capital requisitions shall require asset class, justification and approval levels defined for capital expenditure and shall generate asset acquisition events at invoice or capitalization. (when Assets is licensed)
- **FR-PRC-REQ-14** Split-purchase detection shall warn when multiple requisitions to the same supplier or category within a defined period avoid an approval threshold. (S)
- **BR:** a requisition cannot be ordered until approved; quantities ordered cannot exceed approved quantities beyond the configured tolerance; closing a requisition releases the unused commitment.
- **AC:** a requester submits a requisition for office equipment from the catalog; budget is checked, the department head and finance approve it above the threshold, the buyer sees it in the queue, and the requester follows its status through order and receipt.

## 5. Catalogs, price lists and source determination (CAT)

**Purpose:** speed up buying and steer spend to agreed suppliers and prices. **Actors:** Buyer, Procurement Manager, Supplier.

- **FR-PRC-CAT-01** Internal catalogs shall expose kernel catalog items and services to requesters with category browsing, search in any language, images, specifications, and approved supplier information.
- **FR-PRC-CAT-02** Supplier price lists shall hold prices by supplier, item or service, unit, currency, quantity breaks, minimum order quantity, lead time, and validity dates, with versioning and import from spreadsheets.
- **FR-PRC-CAT-03** Supplier-maintained catalogs shall be uploaded through the portal or imported with validation, approved by buyers before activation, and expire automatically.
- **FR-PRC-CAT-04** Discount structures shall support line discounts, header discounts, volume rebates, early-payment terms and surcharges with rules-engine conditions.
- **FR-PRC-CAT-05** Source determination shall propose the supplier and price for each requisition line from, in order of configuration: contract, price list, preferred supplier, last order, quota arrangement; the rule order shall be configurable with a trace of how each result was determined.
- **FR-PRC-CAT-06** Quota arrangements shall split demand across suppliers by percentage with tracking against targets. (S)
- **FR-PRC-CAT-07** Item-supplier relations shall store supplier part numbers, lead times, order multiples, and country of origin.
- **FR-PRC-CAT-08** Price change history shall be retained, with alerts when an order price exceeds the contract or price list by a tolerance.
- **FR-PRC-CAT-09** Substitute and alternative items shall be configurable, offered when an item is unavailable or discontinued.
- **FR-PRC-CAT-10** External punch-out catalogs shall be supported through the connector framework. (C)
- **AC:** a requisition line for a catalog item automatically shows the contract supplier and contract price; an order at a higher price triggers a price-variance warning and approval.

## 6. Sourcing: RFQ, bids and tenders (SRC)

**Purpose:** obtain competitive offers with transparency and auditability. **Actors:** Buyer, Procurement Manager, Evaluation Committee Member, Supplier.

### 6.1 Request for quotation

- **FR-PRC-SRC-01** RFQs shall be created from requisition lines or manually, with items or scope, quantities, delivery terms, validity, evaluation criteria and attachments, sent to selected suppliers by email, portal or both.
- **FR-PRC-SRC-02** Suppliers shall respond through the portal or the buyer shall enter responses manually, including prices, lead times, alternates, taxes and terms, with attachments.
- **FR-PRC-SRC-03** Responses shall be locked and hidden from other suppliers and, if configured, sealed from internal users until the response deadline.
- **FR-PRC-SRC-04** Comparison shall present prices, total cost (including freight, taxes, discounts and payment terms), lead time, supplier rating and compliance side by side, with weighting.
- **FR-PRC-SRC-05** Awards shall be recorded by line or whole RFQ, with split awards, reasons for selection (especially when not the lowest price), and approval workflows by value.
- **FR-PRC-SRC-06** Awarded RFQs shall create purchase orders or contracts and notify unsuccessful suppliers using templates.
- **FR-PRC-SRC-07** Clarification questions and answers shall be managed centrally and shared with all invited suppliers without revealing identities.
- **FR-PRC-SRC-08** Reverse auction events with rules, rounds and live ranking shall be supported in a later wave. (wave C)

### 6.2 Tenders and formal procurement

- **FR-PRC-SRC-10** Tender processes shall be configurable (open, restricted, two-stage, single-source with justification) with stages, deadlines, publication, bid security and document fees.
- **FR-PRC-SRC-11** Two-envelope evaluation shall separate technical and financial proposals, opening financial bids only for technically qualified bidders.
- **FR-PRC-SRC-12** Evaluation committees shall be defined with members, roles and conflict declarations; members score against weighted criteria independently, and the system consolidates scores with a full audit trail.
- **FR-PRC-SRC-13** Public-sector procurement rules (method thresholds, minimum bidding periods, publication requirements, standard documents, grievance windows) shall be pack-configurable per jurisdiction. (S)
- **FR-PRC-SRC-14** A bid opening record, evaluation report and award notice shall be generated from templates and stored with the tender.
- **FR-PRC-SRC-15** Single-source and emergency purchases shall require justification and elevated approvals and appear in a dedicated exception report.
- **BR:** responses cannot be viewed before the opening time for sealed events; changing evaluation criteria after bids are received requires an approved amendment and notification; the audit trail records every access.
- **AC:** a tender with technical and financial envelopes is issued to five suppliers; three qualify technically; the financial bids open only for those three; the committee scores independently; the award report is generated and the PO created from the winning bid.

## 7. Purchase orders (POR)

**Purpose:** place clear, controlled, traceable orders with suppliers. **Actors:** Buyer, Procurement Manager, Approver, Supplier.

### 7.1 Creation and content

- **FR-PRC-POR-01** Purchase orders shall be created from approved requisition lines, awarded RFQs or tenders, contracts, price lists, or manually, with traceable links to every source.
- **FR-PRC-POR-02** Order types shall be configurable: standard, blanket or framework (with call-offs), service, contract-linked, import, consignment (wave C), and drop-ship (wave C), each with its own status model, approvals and numbering.
- **FR-PRC-POR-03** The header shall hold supplier, company, buyer, order date, currency and rate, payment terms, delivery terms (including incoterms), delivery address, tax determination, reference fields, terms and conditions text, attachments and custom fields.
- **FR-PRC-POR-04** Lines shall hold item or service, description, quantity and unit, price, discounts, tax codes, delivery date or schedule, receiving location, account and dimension coding, project, asset flag, inspection flag and custom fields.
- **FR-PRC-POR-05** Delivery schedules shall allow multiple delivery lines per order line with dates and quantities.
- **FR-PRC-POR-06** Price, tax, discount and total calculation shall use the kernel tax engine and catalog units of measure, with unit conversions between purchase and stock units.
- **FR-PRC-POR-07** Orders in foreign currency shall record rate, rate type and date; optional rate protection or fixed rate by contract shall be supported.
- **FR-PRC-POR-08** Advance payments or deposits may be requested on the order, creating a payment request to Finance (when licensed) and tracked against later invoices.
- **FR-PRC-POR-09** Order documents shall be generated from templates in the supplier's language (English, Urdu, Arabic) with company letterhead, terms, and QR or barcode, and sent by email or portal.

### 7.2 Approval, issue and confirmation

- **FR-PRC-POR-10** Approval routing shall use the workflow engine with conditions by value, category, supplier, order type, price variance, deviation from contract and risk, with delegation and escalation.
- **FR-PRC-POR-11** When Finance is licensed, the order shall check budget availability, convert the requisition commitment into an order commitment on approval, and release unused commitment on close or cancel.
- **FR-PRC-POR-12** Approved orders shall be issued to the supplier by portal, email or file; issue shall be recorded with timestamp and version.
- **FR-PRC-POR-13** Supplier acknowledgement and confirmation shall be captured through the portal or entered by the buyer, including confirmed dates, quantities and prices; deviations shall create tasks for the buyer.
- **FR-PRC-POR-14** Order tracking shall show expected versus confirmed dates, advance shipping notices, and overdue deliveries, with automated reminders and escalation to suppliers and buyers.

### 7.3 Changes and closure

- **FR-PRC-POR-20** Changes to an issued order (quantity, price, dates, terms, cancellation) shall create a numbered revision with change reason, approval by rules (for example price increase above tolerance), supplier notification and acknowledgement, and full version history with comparison.
- **FR-PRC-POR-21** Partial and full cancellations shall release commitments and update requisition status, with controls for lines already received or invoiced.
- **FR-PRC-POR-22** Orders shall close automatically or manually when fully received and invoiced, or when remaining quantities fall within tolerance; closing releases remaining commitments and records the reason.
- **FR-PRC-POR-23** Blanket orders shall track total value and quantity against limits and validity, with call-off orders consuming the blanket and alerts at thresholds.
- **FR-PRC-POR-24** A change log shall report orders changed after issue by reason and requester, supporting audit and supplier performance review.
- **BR:** an issued order cannot be edited except through a revision; a supplier on hold or blocked cannot receive a new order without a recorded override; the sum of delivery schedule quantities equals the line quantity.
- **AC:** a buyer creates an order from an awarded RFQ, it is approved by value, budget commitment is converted, the supplier confirms dates through the portal, a price revision above tolerance routes for approval, and the history shows both versions side by side.

## 8. Receiving and service acceptance (RCV)

**Purpose:** confirm what was actually delivered, and trigger the accounting and stock consequences. **Actors:** Receiver, Quality Inspector, Buyer.

- **FR-PRC-RCV-01** Goods receipts shall be created against purchase order lines (full or partial), with quantity received, unit, date, receiving location, delivery note number, condition notes, photos and attachments.
- **FR-PRC-RCV-02** When Inventory is licensed, receipts of stock items shall call the Inventory ReceiveGoods contract with warehouse, location, batch and serial numbers; Inventory posts the stock movement and publishes GoodsReceiptPosted for valuation, and Procurement publishes ReceiptPosted after success (decision D-15); when Inventory is not licensed, items are received as non-stock deliveries and confirm quantities only.
- **FR-PRC-RCV-03** Over-delivery and under-delivery tolerances shall be configurable by item category, supplier and order type, with block, warn or allow behavior and approvals for exceptions.
- **FR-PRC-RCV-04** Service orders shall support service entry sheets or acceptance records against milestones, time and materials, or percentage of completion, with approvals by the requester or project owner.
- **FR-PRC-RCV-05** Receipts requiring inspection shall move to a quarantine or inspection status and link to quality inspection results, with accept, reject, partial accept and return outcomes. (inspection steps use Inventory quality functions when licensed)
- **FR-PRC-RCV-06** Returns to supplier shall reference the receipt, record reason and quantity, create credit expectations, and publish events so Finance and Inventory adjust.
- **FR-PRC-RCV-07** Receipts without a purchase order shall be allowed only for permitted roles and categories, generating an unmatched receipt that must be regularized by an order within a set time.
- **FR-PRC-RCV-08** Barcode and QR scanning on web and mobile shall support quick receipt against orders, with offline capture and later sync. (S)
- **FR-PRC-RCV-09** Landed cost components (freight, duty, insurance, handling) shall be recorded against receipts or shipments and allocated by value, quantity or weight, handing allocations to Inventory valuation and Assets capitalization where licensed.
- **FR-PRC-RCV-10** Reversal or correction of a receipt shall create linked reversing documents and keep the audit trail; corrections in closed periods follow Finance period rules.
- **FR-PRC-RCV-11** Receipt status shall update the order, requisition and requester views, and notify the requester when goods arrive.
- **FR-PRC-RCV-12** A goods-received-not-invoiced report shall list receipts without matching invoices, with value and age, reconciling to the Finance accrual account when licensed.
- **BR:** receipt quantity cannot exceed the order quantity plus tolerance without approval; posted receipts are immutable and corrected by reversal; services are accepted only by authorized acceptors.
- **AC:** a partial delivery of ten pallets is received with batch numbers, five are accepted and five returned after inspection, stock and the order status update correctly, and the GRNI report shows the accepted quantity at order price.

## 9. Invoice matching and hand-off to payables (MAT)

**Purpose:** pay only what was ordered and received, at the agreed price. **Actors:** AP Clerk, Buyer, Approver. Procurement owns matching; Finance owns the payable.

- **FR-PRC-MAT-01** Supplier invoices shall enter through the portal, email capture, upload or manual entry (order-related invoices are captured and matched here even when Finance is licensed; invoices without orders are keyed in Finance AP, and duplicate detection spans both, decision D-12), and be linked to orders and receipts.
- **FR-PRC-MAT-02** Matching modes shall be configurable per supplier, category or order type: two-way (order and invoice), three-way (order, receipt and invoice), or no match for approved categories.
- **FR-PRC-MAT-03** Tolerances shall be configurable for price, quantity, tax and total variance by percentage and absolute value, with different limits by supplier group and category.
- **FR-PRC-MAT-04** The matching engine shall compare each invoice line to order and receipt lines, show differences (price, quantity, tax, missing receipt, duplicate, over-billing), and automatically approve lines within tolerance.
- **FR-PRC-MAT-05** Exceptions shall create tasks for the buyer or receiver, with reasons, required actions, aging and escalations, and shall hold the invoice until resolved, a credit note arrives, or an approver authorizes the variance.
- **FR-PRC-MAT-06** One invoice may match multiple orders and receipts, and one order may have multiple invoices, including partial and advance invoices, with remaining quantity and value tracked.
- **FR-PRC-MAT-07** Duplicate invoice detection shall check supplier, invoice number, date and amount across Procurement and Finance.
- **FR-PRC-MAT-08** Freight, tax and other header charges on invoices shall be allocated to lines or accounts by configuration, and price variances shall be accounted through posting rules (purchase price variance) when Finance is licensed.
- **FR-PRC-MAT-09** Approved matches shall publish SupplierInvoiceApproved with references to orders and receipts so Finance creates or releases the payable; without Finance, the Accounting Export connector receives it.
- **FR-PRC-MAT-10** Advance payments and deposits shall be matched to later invoices, reducing the amount payable.
- **FR-PRC-MAT-11** Credit notes and debit notes from suppliers shall be matched to returns and original invoices.
- **FR-PRC-MAT-12** Invoice processing metrics (touchless rate, exception rate, cycle time, discount capture) shall be available in dashboards.
- **FR-PRC-MAT-13** AI-assisted invoice extraction shall propose header and line values and likely order and receipt matches for review. (S)
- **BR:** an invoice cannot be approved for payment while it has an unresolved blocking exception; a line paid cannot be re-matched; matching is auditable at line level with the rule and tolerance applied.
- **AC:** an invoice with a 2 percent price difference within tolerance auto-approves; one with a 9 percent difference and a missing receipt holds, creates two tasks, and approves after the buyer corrects the order and the receiver posts the receipt; Finance receives exactly one payable.

## 10. Contracts (CTR)

**Purpose:** manage supplier agreements, track obligations, and make orders contract-compliant. **Actors:** Buyer, Procurement Manager, Legal Reviewer, Contract Owner.

- **FR-PRC-CTR-01** The contract repository shall hold supplier agreements with type (framework, blanket, service, maintenance, lease, non-disclosure), parties, validity, value and quantity limits, currency, payment terms, pricing schedule, documents and custom fields.
- **FR-PRC-CTR-02** Contract creation shall use templates and a clause library with approved wording, variable fields, and fallback clauses, with version comparison for negotiated changes.
- **FR-PRC-CTR-03** Contract approval and signature shall use workflow by value and type, with e-signature connectors and a signed copy stored with the record.
- **FR-PRC-CTR-04** Contract pricing shall feed source determination and price checks; orders referencing a contract shall consume its limits and show remaining value and quantity.
- **FR-PRC-CTR-05** Milestones, deliverables, SLAs, service credits and penalties shall be defined and tracked, with owners and due dates, linking to service acceptance and invoice matching.
- **FR-PRC-CTR-06** Obligations (insurance certificates, reporting, audits, price reviews) shall have owners and reminders.
- **FR-PRC-CTR-07** Renewal, expiry and notice-period alerts shall be configurable, with renewal workflows that create a new version or successor contract.
- **FR-PRC-CTR-08** Amendments shall create versions with reason, approval and effective date; the contract history shall be reconstructable at any date.
- **FR-PRC-CTR-09** Contract compliance reports shall show spend under contract versus off-contract, price variances, leakage and expiring contracts.
- **FR-PRC-CTR-10** Contract performance reviews shall record ratings and findings at set intervals.
- **FR-PRC-CTR-11** Retention money, performance guarantees and advance payment guarantees shall be tracked with amounts, validity and release events. (S)
- **FR-PRC-CTR-12** Confidential contracts shall be restricted by role, with access logging and masked pricing for users without commercial permission.
- **BR:** a contract cannot be activated without an approved version and validity dates; an order against an expired contract triggers a warning or block per policy; amendments never alter the history of prior versions.
- **AC:** a framework agreement with price tiers is approved and signed electronically; orders consume its limit; at 80 percent of value an alert goes to the owner; 90 days before expiry the renewal workflow starts.

## 11. Controls, compliance and fraud prevention (CTL)

**Purpose:** make the right way the easy way, and make exceptions visible. **Actors:** Procurement Manager, Auditor, Finance Controller.

- **FR-PRC-CTL-01** An approval matrix shall define authority limits by role, position, organization unit, category and document type; the workflow engine shall use it to resolve approvers, with delegation and escalation.
- **FR-PRC-CTL-02** Segregation of duties shall be enforced and configurable: the requester shall not approve their own requisition; the creator of an order shall not approve it above a threshold; the approver of an order shall not post its receipt; the receiver shall not approve the invoice for payment; payment authorization stays in Finance. Violations are blocked or flagged and reportable.
- **FR-PRC-CTL-03** Purchasing policy checks shall flag purchases from non-preferred suppliers, off-catalog or off-contract buying, orders created after the invoice (retrospective orders), orders just under approval thresholds, repeated single-source buys, and unusual vendors.
- **FR-PRC-CTL-04** Emergency purchases shall be allowed with a streamlined approval, justification and time-limited regularization, appearing in an exception report with owner and aging.
- **FR-PRC-CTL-05** Anomaly detection shall highlight unusual prices, quantities, frequency, supplier changes, and similarities such as supplier bank account matching an employee account (when HRMIS is licensed and policy allows). (S)
- **FR-PRC-CTL-06** Conflict-of-interest checks shall compare buyers, approvers and evaluators with supplier ownership, relationships and declared interests. (S)
- **FR-PRC-CTL-07** Gifts and hospitality offered by suppliers shall be declared and approved, with a register and reporting. (S)
- **FR-PRC-CTL-08** Procurement policy shall be stored as configuration (thresholds, required quotes by value band, mandatory tendering limits) and published to users with acknowledgement tracking.
- **FR-PRC-CTL-09** A complete audit trail shall record who created, changed, approved, issued, received and matched each document, with reasons for overrides and links to workflow decisions.
- **FR-PRC-CTL-10** Required-quotation rules shall block ordering above set values unless the configured number of quotations or a documented exemption exists.
- **BR:** every override of a control requires a reason and an authorized role, is flagged in the exception report, and cannot be deleted.
- **AC:** an order created by a user who also posts its receipt is blocked by the segregation rule; an order just below a threshold repeated with the same supplier appears in the split-purchase report.

## 12. Supplier portal and collaboration (PRT)

**Purpose:** let suppliers do their part online, with strong isolation. **Actors:** Supplier users, Buyer.

- **FR-PRC-PRT-01** Suppliers shall access a branded portal with their own authentication realm (invited users, multi-factor authentication, optional single sign-on), separate from internal users, and see only their own data.
- **FR-PRC-PRT-02** Self-registration and onboarding shall use the supplier onboarding workflow (SUP-02), with document upload and status tracking.
- **FR-PRC-PRT-03** Suppliers shall respond to RFQs and tenders, ask clarification questions, and view results and award notices within their own data.
- **FR-PRC-PRT-04** Suppliers shall view and confirm purchase orders, propose changes, view revisions, and send advance shipping notices with quantities, packaging and tracking.
- **FR-PRC-PRT-05** Suppliers shall submit invoices and credit notes against orders and receipts, with validation of tax numbers and required fields before submission, and see approval and payment status when Finance is licensed.
- **FR-PRC-PRT-06** Suppliers shall maintain catalogs and price lists (with buyer approval), contact details, bank details (through maker-checker), and certificates with expiry.
- **FR-PRC-PRT-07** Suppliers shall view statements, open items and remittance advices when Finance is licensed, and download documents.
- **FR-PRC-PRT-08** Suppliers shall see their performance scorecards and improvement tasks where the buyer allows.
- **FR-PRC-PRT-09** The portal shall support English, Urdu and Arabic with right-to-left layout and mobile browsers.
- **FR-PRC-PRT-10** Portal files shall be scanned and size-limited; actions shall be rate-limited and audited; portal accounts shall have separate permissions and no access to internal data.
- **AC:** a supplier user logs in, confirms two order lines with a revised date, uploads an invoice that the system validates and matches, and checks payment status; no other supplier's data is reachable by any means in security tests.

## 13. Spend analytics and reporting (ANL)

Procurement reporting uses the kernel semantic layer and builders (document 9); this section defines standard content.

- **FR-PRC-ANL-01** Spend analysis shall report by supplier, category, item, cost center, project, company, period and currency, with drill-down to documents and comparison to prior periods and budgets.
- **FR-PRC-ANL-02** Compliance dashboards shall show contract coverage, catalog usage, preferred supplier share, maverick spend, retrospective orders and approval overrides.
- **FR-PRC-ANL-03** Savings tracking shall record negotiated savings versus baseline (previous price, budget, or quotation average) with owner, validation and realization.
- **FR-PRC-ANL-04** Process metrics shall include requisition-to-order cycle time, order-to-receipt lead time, supplier on-time-in-full rate, invoice match rate, exception ageing, and approval turnaround by approver.
- **FR-PRC-ANL-05** Supplier concentration and dependency analysis shall show single-source items and top-supplier share of spend.
- **FR-PRC-ANL-06** Open commitments, GRNI and upcoming deliveries shall be available for finance and warehouse planning.
- **FR-PRC-ANL-07** Reports shall respect data scopes and hide pricing and supplier-confidential fields from users without commercial permission.
- **FR-PRC-ANL-08** All standard reports shall be cloneable through the report builder, scheduled and exported.

## 14. Special procurement scenarios (SPC)

- **FR-PRC-SPC-01** Subcontracting orders (components supplied to a supplier and finished goods returned) shall be supported with the Inventory module. (wave C)
- **FR-PRC-SPC-02** Import procurement shall track pro forma invoices, letters of credit or other payment instruments, shipment documents, customs duties, clearing agent charges and landed cost, with shipment status milestones. (wave C)
- **FR-PRC-SPC-03** Consignment stock shall record supplier-owned stock held at the buyer, with liability only on consumption. (wave C, with Inventory)
- **FR-PRC-SPC-04** Capital projects shall link requisitions, orders and invoices to project budgets and asset classes, with cost tracking until capitalization. (with Assets and Finance)
- **FR-PRC-SPC-05** Employee expense purchases and corporate-card spend remain in HRMIS and Finance; Procurement receives summary data for spend analytics only.
- **FR-PRC-SPC-06** Framework agreements shared across companies in a tenant shall be supported with per-company limits and approvals.
- **FR-PRC-SPC-07** Intercompany purchasing shall create linked sales orders in the counterpart company when Sales is licensed. (wave C)

## 15. Country notes for Procurement

These are planning inputs to be verified by qualified local advisers and procurement specialists before design is frozen.

| Topic | Pakistan | Saudi Arabia | United States |
| --- | --- | --- | --- |
| Public-sector procurement | Federal and provincial procurement rules and regulatory authorities, with their e-procurement systems; method thresholds, bidding periods, publication and grievance steps | Government tendering through the national platform, with local-content and national preference requirements | State and local public procurement rules by jurisdiction; federal acquisition regulation out of scope |
| Tax at purchase | Sales tax invoices, withholding on supplies and services that depends on supplier registration status | VAT invoices and withholding on certain payments to non-residents | Sales and use tax with exemption certificates; vendor reporting |
| Supplier data | Tax registration numbers and registration status checks; business registration numbers | Commercial registration, VAT number, national address | EIN and W-9 collection; small and diverse supplier attributes |
| Documents | Urdu and English purchase orders and notices | Arabic and English with right-to-left layout | English, optional Spanish |
| Payments and invoices | Cheques and bank transfers through Finance; e-invoicing by sector as scoped | Supplier e-invoices received under the national e-invoicing phases | ACH and check through Finance |

Pack responsibilities for Procurement: tender rules and templates, tax and withholding determination data, supplier registration validation rules, document templates, approval-matrix starter sets, and local-content or diversity reports.

## 16. Events and contracts

### 16.1 Events published

| Event | Consumers |
| --- | --- |
| PurchaseRequisitionApproved, Changed, Closed | Finance (commitments), Supply Chain, notifications |
| PurchaseOrderIssued, Revised, Cancelled, Closed, Confirmed | Finance (commitments), Supply Chain (inbound expectations), Inventory (expected receipts) |
| ReceiptPosted, ServiceAccepted | Inventory (stock movement), Finance (accruals for goods and services received not invoiced) |
| GoodsReturnRecorded | Inventory, Finance |
| SupplierInvoiceApproved | Finance (payable creation) or Accounting Export |
| SupplierStatusChanged | Finance (payment holds), notifications |
| ContractActivated, ContractExpiring, ContractExpired | Notifications, reporting |
| TenderAwarded | Contracts and purchase orders (internal), notifications |

### 16.2 Events consumed

| Event | Source | Effect |
| --- | --- | --- |
| GoodsReceiptPosted | Inventory | Receipt status and valuation reference updated |
| VendorInvoicePaid, PaymentIssued, PaymentVoided | Finance | Payment status shown on invoice, order and portal |
| PeriodClosed, PeriodReopened | Finance | Receipt and invoice posting dates follow lock rules |
| ReplenishmentProposed | Inventory, Supply Chain | Draft requisitions created (ReplenishmentProposed); expected receipts and supplier forecast views updated (AsnReceived and SupplierForecastShared, from Supply Chain) |
| AssetAcquired | Assets | Link from purchase line to asset |
| EmployeeChanged | HRMIS | Approver and requester assignments refreshed |

### 16.3 Synchronous contracts

| Contract | Provider | Behavior without the provider |
| --- | --- | --- |
| Check budget availability | Finance | Always allowed |
| Get stock position and reorder data | Inventory | Not shown |
| Get supplier payment status | Finance | Not shown |
| Get exchange rate | Kernel | Kernel service answers |

## 17. Roles, permissions and default workflows

### 17.1 Baseline roles

| Role | Main permissions |
| --- | --- |
| Requester | Create and track own requisitions, receive notifications, confirm receipt where assigned |
| Department Approver | Approve requisitions and service acceptances within authority |
| Buyer | Manage suppliers, sourcing, orders, price lists, matching exceptions |
| Procurement Manager | Approve high-value orders and awards, manage policy, supplier status, performance reviews |
| Receiver | Post receipts and returns for assigned locations |
| Quality Inspector | Record inspection outcomes |
| Contract Manager | Manage contracts, obligations and renewals |
| Evaluation Committee Member | Score bids assigned to them; no access to other members' scores before submission |
| Auditor | Read-only access, exception reports, exports by approval |
| Supplier user (portal) | Own data only, as in section 12 |

### 17.2 Default workflows (all editable)

| Workflow | Default route |
| --- | --- |
| Requisition | Department approver, then finance or project owner above thresholds |
| Purchase order | Buyer submits; procurement manager above threshold; executive above higher threshold |
| Order revision | Same as order when price or quantity increases beyond tolerance |
| Supplier onboarding | Buyer, procurement manager, finance verification of bank details |
| Invoice exception | Buyer or receiver resolves; procurement manager for variances above limits |
| Tender award | Evaluation committee, procurement manager, executive by value |
| Contract | Contract manager, legal review, approver by value, signature |
| Emergency purchase | Fast single approver, retrospective review within a set time |

## 18. Data model overview

Tables live in the prc schema (kernel references as in document 3). Commitments are owned by Finance and driven by events from this module.

| Area | Main tables |
| --- | --- |
| Suppliers | supplier\_profiles, supplier\_categories, supplier\_documents, supplier\_qualifications, supplier\_scores, preferred\_lists, supplier\_risk\_flags, conflict\_declarations |
| Requisitions | requisitions, requisition\_lines, requisition\_changes, recurring\_requisitions |
| Catalogs and pricing | catalogs, catalog\_items, price\_lists, price\_list\_lines, discount\_rules, source\_rules, quota\_arrangements |
| Sourcing | rfqs, rfq\_lines, rfq\_suppliers, rfq\_responses, rfq\_response\_lines, tenders, tender\_stages, evaluation\_committees, evaluation\_scores, awards |
| Orders | purchase\_orders, purchase\_order\_lines, delivery\_schedules, order\_revisions, order\_confirmations, blanket\_consumptions |
| Receiving | receipts, receipt\_lines, service\_acceptances, returns\_to\_supplier, inspection\_links |
| Matching | invoice\_matches, invoice\_match\_lines, match\_exceptions, tolerance\_rules |
| Contracts | contracts, contract\_versions, contract\_clauses, contract\_milestones, contract\_obligations, contract\_guarantees |
| Portal | portal\_users, portal\_messages, asn\_notices |
| Controls and savings | control\_exceptions, savings\_records, gifts\_register |
| Outbox and inbox | outbox\_messages, inbox\_messages |

Key design points: document headers and lines follow the pattern in document 3, section 7.1; receipts and returns are append-only after posting; order revisions are immutable versions; document links join requisition, order, receipt, invoice and payment without cross-module foreign keys.

## 19. AI skills for Procurement

All skills run through the AI Gateway with the user's permissions, propose and never decide, and are switchable per tenant (kernel document 2, section 22).

| Skill | Description | Wave |
| --- | --- | --- |
| Requisition assistant | Turn a plain-language request into catalog lines and suggested suppliers | B |
| Spend classification | Categorize uncategorized lines and suggest catalog items | B |
| RFQ drafting | Draft scope, criteria and supplier questions from past tenders | B |
| Bid comparison summary | Summarize responses side by side and highlight risks, always advisory | B |
| Contract assistant | Extract key terms, obligations and renewal dates; flag unusual clauses | B to C |
| Price anomaly detection | Flag prices or volumes out of line with history and contracts | B |
| Invoice extraction and match suggestions | Read invoices and propose order and receipt matches | B |
| Delivery risk | Predict late deliveries from supplier history and confirmations | C |
| Negotiation preparation | Summarize supplier history, benchmarks and options | C |

AI shall not issue orders, award tenders, approve invoices or change supplier status.

## 20. Non-functional requirements

| Area | Requirement |
| --- | --- |
| Volume | At least 1 million order lines per year and 500 concurrent users per tenant |
| Performance | Standard screens under 2 seconds at the 95th percentile; matching of 10,000 invoices per hour in batch; catalog search under 1 second on 1 million items |
| Portal security | Separate authentication realm, strict data isolation by supplier, file scanning, rate limits, penetration tested |
| Confidentiality | Sealed bids unreadable until opening; supplier pricing masked from users without commercial permission |
| Auditability | Complete trail of documents, approvals, overrides and bid access |
| Localization | English, Urdu and Arabic documents and screens; right-to-left layouts |
| Availability | As in the Architecture document; portal outages must not lose submitted data |
| Configurability | Status models, approval matrix, tolerances, source rules, templates and policies configurable without code |

## 21. Acceptance and testing

- **End-to-end scenarios:** standard procure-to-pay from requisition to payment; blanket order with call-offs; service order with milestone acceptance; two-envelope tender to award and order; partial receipt, inspection and return with credit; price revision above tolerance; duplicate and over-billed invoices; emergency purchase and regularization.
- **Configuration variants:** run the flows with Finance only, with Inventory only, with both, and with neither (Accounting Export and non-stock receipts) to prove graceful degradation (GR-12 of document 2).
- **Controls tests:** segregation of duties matrix, approval-matrix resolution, split-purchase and retrospective-order detection, bid secrecy and access logging.
- **Portal security tests:** tenant and supplier isolation tests, abuse and rate-limit tests, file upload tests.
- **Integration invariants:** every receipt and approved invoice produces exactly one set of events; commitments equal approved open requisition and order values; GRNI equals receipts minus matched invoices.
- **Performance:** volume and concurrency tests at 1.5 times expected load.
- **Pilot exit criteria:** one full month of purchasing completed by the pilot customer without workarounds, zero open severity-1 and severity-2 defects, and Finance reconciliation of commitments, GRNI and payables.

## 22. Assumptions and open items

**Assumptions:** Procurement owns the receiving document and the matching record; Inventory owns stock movements and valuation; Finance owns commitments, the payable and payment; public-sector rules arrive as packs; the supplier portal shares the product's web platform but a separate authentication realm.

**Open decisions:** (resolved: quality inspection is owned by Inventory, decision D-14); depth of import and customs handling for wave C; punch-out catalog standards to support; external supplier risk data providers; e-procurement platform integrations for public-sector customers per country; whether Sales-to-Procurement intercompany flows are in scope for wave B.

**Next documents:** 8 Inventory and Asset Management Functional Specification, then 10 Supply Chain, 11 Sales and Distribution, 9 Analytics, Reporting and AI, and 13 UX and Design System Principles.

## 23. Baseline 1.0 addendum (this document is now version 0.2)

*Added 14 November 2026 by the comprehensive review (Doc 16). Sections 1 to 22 are unchanged. The previous state (version 0.1) is kept in this document's version history and in the frozen originals archive. Items tied to a proposed decision take effect on its approval.*

### 23.1 Binding cross-module rules

Doc 16 section 6 applies to this module. Where a requirement in sections 3 to 14 is looser than an FR-XM rule, the FR-XM rule applies.

| Rule | Effect on Procurement | Sections touched |
| --- | --- | --- |
| FR-XM-01, 03 | Service acceptance and non-stock receipts post their accrual atomically through the Finance posting contract (proposed D-31); deferred mode by policy | 8, 16 |
| FR-XM-06, 08 | A purchase order with receipts or invoices cannot be cancelled, only short-closed; reversal order is invoice match, then receipt | 7, 8, 9 |
| FR-XM-09, 10, 11 | Receipt-before-invoice and invoice-before-receipt accruals; variance within tolerance posts by costing method, outside tolerance blocks the invoice | 9 |
| FR-XM-12, 15 | A receipt already matched to an invoice is corrected by a supplier return and expected credit note, never by reversing the receipt; return after payment creates a supplier debit balance | 8, 9, 14 |
| FR-XM-13 | Every line tracks open quantity and open amount; short-close with reason releases budget commitment | 4, 7 |
| FR-XM-14 | Amending an approved order creates a new version, re-approves the difference, and cannot go below received or invoiced quantity | 7 |
| FR-XM-16 | Landed cost arriving after receipt is allocated to the receipt; Inventory applies the split between stock on hand and stock issued | 9, 14 |
| FR-XM-24 | Capital purchases are received to a clearing account; capitalization is a separate step in Asset Management | 14 |
| FR-XM-31, 32 | Budget commitment life cycle from requisition to invoice; approvals never auto-approve; overrides appear on the audit exception report | 4, 7, 11 |

### 23.2 Phase and size profile

Procurement is built in Phase 3 together with Inventory, Asset Management and Sales core, after one shared data design gate for the four modules (proposed D-32, D-35). Phase 3 scope: SUP, REQ, CAT basic, POR, RCV, MAT, CTL, basic ANL. Phase 4 scope: SRC tendering, CTR framework agreements, PRT supplier portal depth, SPC.

The Micro size profile shows a direct purchase order with no requisition and no approval; the requisition and approval records exist with defaults and can be switched on without conversion (Doc 16 rules SC-1 to SC-3).

### 23.3 Data notes

Purchase documents carry `tenant_id` (proposed D-30) and `branch_id` (correction C-39). Item default cost in the kernel is informational only; order price comes from price lists, contracts or entry (correction C-41).
