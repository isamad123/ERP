# Sales and Distribution: Functional Specification (v0.2)

Document 11 of the planning set | Prepared for Ibrahim | 10 November 2026 | Companion to the Platform Kernel, Data Architecture, Finance, Inventory and Asset Management, and Supply Chain specifications

## 1. Introduction

### 1.1 Purpose and scope

This document specifies the Sales and Distribution module: customer management, pricing and promotions, quotations and a light opportunity pipeline, sales orders, contracts and recurring sales, delivery, billing, returns and credits, commissions, sales channels (customer portal, mobile field sales, van sales, e-commerce connectors), analytics and controls. It covers the order-to-cash chain up to the hand-off of issued invoices to accounts receivable.

Sales is built in wave C. It is specified now so that the kernel (parties, catalog, tax, numbering, workflow), Finance (credit exposure, receivables, e-invoicing), Inventory (availability, reservations, issues) and Supply Chain (promising, logistics) already contain the hooks it needs.

Out of scope: deep customer relationship management (marketing automation, campaign management, service desk), retail point-of-sale terminals (a later vertical module, section 12.5), and manufacturing configure-to-order. The light pipeline in section 5 covers the needs of sales teams that do not use a separate CRM.

### 1.2 Dependencies (decision D-10)

| Module | Relationship |
| --- | --- |
| Kernel | Hard dependency: parties (customers), catalog, tax, numbering, workflow, rules, documents, notifications, localization, reporting, AI Gateway |
| Finance | Optional integration: receivables, credit exposure, e-invoicing behavior, receipts, revenue and tax postings. Without Finance, issued invoices go to the Accounting Export connector and credit checks are skipped |
| Inventory | Optional integration: availability, reservations, stock issues, returns. Without Inventory, only non-stock items and services are sold and no stock effect occurs |
| Supply Chain | Optional integration: promising across locations, shipment planning, tracking, proof of delivery, demand history |
| Procurement | Optional integration: drop-ship and intercompany purchase orders |
| HRMIS | Optional integration: sales representatives as employees, commission payment through payroll |
| Assets | Optional integration: sale of assets through disposal |

Each integration degrades gracefully when the other module is not licensed (document 2, GR-12).

### 1.3 Conventions

- Requirement IDs: FR-SAL-\<area>-\<number>. Priority (M), (S), (C); unmarked is (M). Wave C unless stated.
- Each area lists Functional Requirements, Business Rules (BR) and Acceptance Criteria (AC).
- Every configuration is reachable through the admin console and the API (documents 2 and 4).
- Country-specific tax and invoicing rules are pack content and are verified by local advisers before release.

### 1.4 Actors

| Actor | Role |
| --- | --- |
| Sales Representative | Manages customers, quotations and orders |
| Sales Manager | Sets targets and pricing within authority, approves exceptions |
| Sales Administrator | Enters and maintains orders, prices and contracts |
| Customer Service Representative | Handles order changes, promising, returns |
| Credit Controller | Manages credit limits and holds (Finance) |
| Warehouse and Dispatch Staff | Pick, pack and dispatch deliveries |
| Billing Clerk | Issues invoices and credit notes |
| Finance Controller | Reviews revenue and tax postings |
| Customer (portal) | Places orders and views documents |
| Distributor and Field Sales User | Use the mobile and van sales tools |
| Auditor | Read-only access with drill-down |

### 1.5 Key terms

| Term | Meaning |
| --- | --- |
| Sold-to, ship-to, bill-to, payer | The customer roles on an order: who orders, who receives, who is invoiced, who pays |
| Price condition | A rule element of price determination (base price, discount, surcharge, freight, tax) |
| Quotation | A priced offer valid for a period |
| Sales order | A customer's confirmed demand |
| Delivery | The document that moves goods to the customer |
| Billing document | The invoice, credit note or debit note issued to the customer |
| Blanket order or framework contract | An agreement with agreed prices and volumes, with release orders |
| RMA | Return merchandise authorization |
| Van sales | Selling from a vehicle stock on a route with same-day settlement |

## 2. Module overview

### 2.1 Capability map

| Area | Summary |
| --- | --- |
| Customer management | Sales view of customers: roles, hierarchies, segments, sales areas, credit and shipping data |
| Pricing and promotions | Price lists, determination procedure, discounts, promotions, margin control |
| Quotations and pipeline | Leads, opportunities, quotations, versions, win and loss |
| Sales orders | Order types, availability and credit checks, approvals, changes, backorders |
| Contracts and recurring sales | Framework contracts, releases, subscriptions, milestone billing |
| Delivery | Deliveries, picking and dispatch with Inventory and Supply Chain, proof of delivery |
| Billing | Billing triggers, invoices, credit and debit notes, legal numbering, e-invoicing |
| Returns and credits | Authorizations, dispositions, credit notes, replacements |
| Commissions | Plans, accruals, statements, payment |
| Channels | Customer portal, mobile field sales, van sales, e-commerce connectors |
| Analytics | Sales, margin, pipeline, backlog, price realization, profitability |
| Controls | Segregation of duties, discount authority, screening, audit |

### 2.2 Order-to-cash flow and documents

```
Lead -> Opportunity -> Quotation -> Sales order -> Credit and availability check -> Approval
     -> Delivery (pick, pack, dispatch) -> Proof of delivery -> Billing document (invoice)
     -> Receivable (Finance) -> Receipt and allocation (Finance) -> Closed
Returns: RMA -> Return receipt -> Disposition -> Credit note -> Refund or replacement
```

| Document | Key statuses |
| --- | --- |
| Quotation | draft, submitted, approved, sent, accepted, rejected, expired, converted |
| Sales order | draft, submitted, approved, confirmed, on hold, partially delivered, delivered, invoiced, closed, cancelled |
| Delivery | draft, released, picking, packed, dispatched, delivered, cancelled |
| Billing document | draft, approved, issued, cancelled by credit note |
| Contract | draft, in approval, active, expiring, expired, terminated |
| Return authorization | requested, approved, received, dispositioned, credited, closed |

Status models are configurable within module-defined safe limits (kernel MET-30). Every document links to its predecessors and successors through the kernel document-link table, so users can trace quotation to payment without cross-module joins.

### 2.3 Principles

1. Sales owns the commercial documents (quotation, order, delivery, billing document); Inventory owns stock movements; Finance owns receivables, payments and the ledger.
2. Pricing and discounts are rules and data, never code; every price on a document can be explained by the conditions that produced it.
3. Checks that depend on other modules (availability, credit) are contracts that degrade gracefully, never hard dependencies.
4. Billing documents carry legal numbering and tax; issued invoices are immutable and corrected by credit and debit notes.
5. Channels (portal, mobile, van sales, connectors) use the same order engine, pricing and permissions as the web screens.

## 3. Customer management (CUS)

**Purpose:** the sales view of customers. Customer master data is the kernel business partner with the customer role (kernel PTY); Sales adds commercial attributes. **Actors:** Sales Representative, Sales Administrator, Credit Controller.

- **FR-SAL-CUS-01** Customer accounts shall carry sales data by sales area (company, channel, division, region): customer group, price group, discount group, currency, payment terms, incoterms, shipping conditions, delivery priority, preferred warehouse, and tax classification.
- **FR-SAL-CUS-02** Customer hierarchies shall model sold-to, ship-to, bill-to and payer roles and parent-child structures (head office, branches, dealers) with inheritance of defaults and consolidated reporting.
- **FR-SAL-CUS-03** Multiple ship-to addresses and contacts shall be maintained with delivery instructions, receiving hours, geo-coordinates and contact roles (buyer, receiving, accounts payable).
- **FR-SAL-CUS-04** Customer segmentation shall support configurable segments, tags and tiers (key account, wholesale, retail, government), used by pricing, promotions, credit and reporting.
- **FR-SAL-CUS-05** Territories shall be defined by geography, segment, product line or named accounts, with assignment of sales representatives and managers, effective dates, and reassignment tools with history.
- **FR-SAL-CUS-06** Credit data (limit, terms, risk class, hold status, insurance) shall be maintained in Finance when licensed and shown read-only in Sales; without Finance, a basic credit limit and hold flag shall be maintained in Sales.
- **FR-SAL-CUS-07** Customer-specific catalogs (allowed items, customer part numbers, minimum order quantities, agreed lead times) and customer-specific documents (labels, packing instructions) shall be supported.
- **FR-SAL-CUS-08** Customer onboarding shall be a configurable workflow: application, required documents (registration, tax certificates, identity), verification, credit assessment, approval, activation; screening against restricted-party lists through a connector where configured.
- **FR-SAL-CUS-09** A customer 360 view shall show orders, deliveries, invoices, balance and ageing (when Finance is licensed), returns, quotations, opportunities, contacts, notes, tasks and documents.
- **FR-SAL-CUS-10** Customer status (prospect, active, on hold, blocked, closed) shall control quotation, order, delivery and billing separately, with reasons and effective dates.
- **FR-SAL-CUS-11** Duplicate detection and merge shall use the kernel duplicate manager (tax ID, name, phone, address) and re-point sales history.
- **FR-SAL-CUS-12** Customer contact consent and communication preferences (marketing, order notifications) shall be recorded for privacy compliance.
- **BR:** a blocked customer cannot receive new orders or deliveries according to the block type; customers with open documents cannot be deleted, only closed.
- **AC:** a dealer with three branches orders for each branch, ships to branch addresses, is invoiced and pays centrally; the 360 view shows consolidated orders and balance across the hierarchy.

## 4. Pricing and promotions (PRC)

**Purpose:** correct, explainable and controllable prices. **Actors:** Sales Manager, Pricing Analyst, Sales Administrator.

### 4.1 Price determination

- **FR-SAL-PRC-01** Price lists shall hold prices by item or group, unit, currency, quantity breaks, validity dates, customer or customer group, sales area and channel, with versioning and mass import.
- **FR-SAL-PRC-02** A configurable price determination procedure shall evaluate conditions in order (base price, customer price, contract price, quantity discount, customer discount, promotion, surcharge, freight, tax) using rules-engine conditions, and shall show a trace of each condition applied to each line.
- **FR-SAL-PRC-03** Prices shall be effective-dated: orders keep the price at order date unless policy specifies price at delivery or invoice, with rules for price change between order and delivery.
- **FR-SAL-PRC-04** Multi-currency pricing shall support prices in several currencies or conversion at set rates with rounding rules and rate types.
- **FR-SAL-PRC-05** Tax-inclusive and tax-exclusive pricing shall be supported by customer type and channel, with tax computed by the kernel tax engine.
- **FR-SAL-PRC-06** Unit-of-measure pricing shall support pricing in a different unit from the order or stock unit, using kernel conversions.
- **FR-SAL-PRC-07** Manual price overrides shall be allowed by role and limit, with reason codes and approval beyond limits, and shall be reported.

### 4.2 Discounts, promotions and margins

- **FR-SAL-PRC-10** Discount structures shall include line and header discounts by amount or percentage, quantity-based and value-based tiers, customer-group discounts, and cumulative or exclusive discount rules.
- **FR-SAL-PRC-11** Promotions shall be defined with period, channels, customers, items, mechanics (percentage off, amount off, buy X get Y, bundle price, free item, coupon code), budgets and usage limits, stacking rules, and approval, applied automatically in price determination.
- **FR-SAL-PRC-12** Rebate agreements (volume, growth, loyalty) shall accrue on invoices by agreement terms, settle by credit note or payment, and publish accrual events to Finance.
- **FR-SAL-PRC-13** Margin control shall compare price to cost (standard or average from Inventory, with cost visibility permission), enforce floor prices and minimum margin by category or customer, and route violations to approval.
- **FR-SAL-PRC-14** Price simulation shall let users test a price for a customer, item and quantity with the full condition trace before quoting, and test the effect of a proposed price change on a sample of historical orders.
- **FR-SAL-PRC-15** Price maintenance shall support mass updates (percentage or amount) with preview, approval and scheduled activation, and a complete price history with who changed what.
- **FR-SAL-PRC-16** Price lists and agreed prices shall be exportable to customers and distributors in templates and through the portal.
- **BR:** a document price that differs from the determined price carries an override reason; price lists with overlapping validity for the same scope are rejected; promotion budgets cannot be exceeded unless an authorized override is recorded.
- **AC:** an order line for a key-account customer applies the customer price list, a quantity break, a promotion and a freight surcharge; the trace shows each step, and a manual discount above the user's limit requires manager approval.

## 5. Quotations and light pipeline (QUO)

**Purpose:** win business with correct, timely offers. **Actors:** Sales Representative, Sales Manager.

### 5.1 Leads and opportunities

- **FR-SAL-QUO-01** Leads shall be captured from forms, imports, portal registrations and manual entry, with source, owner, status and conversion to customer and opportunity, with duplicate checks.
- **FR-SAL-QUO-02** Opportunities shall record customer, products or services of interest, expected value and close date, stage (configurable pipeline), probability, competitors, next step, and owner, with stage history.
- **FR-SAL-QUO-03** Activities (calls, meetings, tasks, emails) shall be logged against customers and opportunities with reminders and calendar synchronization through connectors. (S)
- **FR-SAL-QUO-04** Pipeline views shall show value and count by stage, owner and period with weighted forecasts, feeding the sales forecast and the Supply Chain demand plan.

### 5.2 Quotations

- **FR-SAL-QUO-10** Quotations shall be created from opportunities, customers, previous quotations or orders, with lines (items, services, bundles, free text), pricing from the determination procedure, discounts, taxes, delivery terms, validity date, and terms and conditions.
- **FR-SAL-QUO-11** Versions shall be kept with comparison between versions; only one version is the active offer; changes after sending create a new version.
- **FR-SAL-QUO-12** Approval routing shall follow the workflow engine by value, discount, margin and customer, with delegation and escalation.
- **FR-SAL-QUO-13** Quotation documents shall be generated from templates in the customer's language (English, Urdu, Arabic) with company letterhead, images and specifications, and sent by email or portal, with open tracking where enabled.
- **FR-SAL-QUO-14** Customers shall accept, reject or request changes through the portal or by reply, with acceptance recorded and e-signature supported through connectors.
- **FR-SAL-QUO-15** Accepted quotations shall convert to sales orders in one step, carrying prices, terms and references, with partial conversion allowed.
- **FR-SAL-QUO-16** Win and loss reasons, competitor information and price feedback shall be captured and reported.
- **FR-SAL-QUO-17** Expiry handling shall remind owners before validity ends, support extension with approval, and mark expired quotations automatically.
- **FR-SAL-QUO-18** Responses to customer tenders and requests for quotation shall be managed as bids with deadlines, documents, pricing approvals and outcomes. (S)
- **FR-SAL-QUO-19** Quotations shall check availability and expected delivery dates through the promising contract so offers carry realistic lead times.
- **BR:** a quotation cannot be sent without required approvals; an expired quotation cannot convert without extension; converted quotations retain links to the resulting orders.
- **AC:** a representative converts an opportunity into a three-line quotation with a 15 percent discount that requires manager approval, sends it in Arabic, the customer accepts in the portal, and a sales order is created with the same prices and a link back to the quotation.

## 6. Sales orders (SOR)

**Purpose:** capture confirmed demand quickly and correctly, and carry it through to delivery. **Actors:** Sales Representative, Sales Administrator, Customer Service Representative, Customer (portal).

### 6.1 Creation and content

- **FR-SAL-SOR-01** Orders shall be created from quotations, contracts, previous orders, portal and mobile entry, imports, EDI messages, e-commerce connectors and the API, with traceable links to sources.
- **FR-SAL-SOR-02** Order types shall be configurable: standard, rush, blanket or framework release, service, free of charge or sample, intercompany, consignment (later), drop-ship (later), and subscription or recurring, each with its own status model, approvals, numbering, delivery and billing behavior.
- **FR-SAL-SOR-03** The header shall hold sold-to, ship-to, bill-to and payer, company and sales area, order date, requested delivery date, customer purchase order reference and date, currency, payment terms, incoterms, shipping conditions, sales representative, discounts, tax determination, notes, attachments and custom fields.
- **FR-SAL-SOR-04** Lines shall hold item or service, description, quantity and unit, price and its determination trace, discounts, tax, requested and confirmed dates, delivery schedule, warehouse, batch or serial preferences, reservation, delivery and billing blocks, and custom fields.
- **FR-SAL-SOR-05** Duplicate detection shall flag orders with the same customer purchase order number or the same customer, items and date within a configured window.
- **FR-SAL-SOR-06** Bundles and kits shall be exploded into components for delivery and stock with pricing at bundle or component level as configured.
- **FR-SAL-SOR-07** Quick order entry shall support barcode and item search in any language, customer favorites and last-ordered lists, copy of previous orders, and keyboard-first line entry.

### 6.2 Checks and approvals

- **FR-SAL-SOR-10** Availability checks shall query Inventory and Supply Chain (when licensed) for available stock and promise dates by line, with options to reserve, split across warehouses or dates, back-order, or substitute.
- **FR-SAL-SOR-11** Credit checks shall query Finance for exposure (open invoices, unapplied cash, open orders and deliveries) against limits and terms at save, at release and at delivery, returning allow, warn, or block with the reason; blocked orders go on credit hold and notify the credit controller.
- **FR-SAL-SOR-12** Approval routing shall use the workflow engine with conditions by value, discount, margin, customer, product, payment terms and risk, with delegation and escalation.
- **FR-SAL-SOR-13** Order holds (credit, manual, compliance, price, stock) shall be recorded with reason, owner and release, and shall block delivery and billing independently.
- **FR-SAL-SOR-14** Customer-specific rules (minimum order value, order multiples, restricted items, maximum discounts) shall be enforced at entry with clear messages.
- **FR-SAL-SOR-15** Screening of customers and destinations against restricted-party lists through a connector shall be possible before confirmation. (S)

### 6.3 Changes, tracking and closure

- **FR-SAL-SOR-20** Order changes (quantity, price, date, address, cancellation) shall create numbered versions with reason, approval by rules, impact on reservations and planned supply, and customer notification, with full history and comparison.
- **FR-SAL-SOR-21** Partial delivery rules (ship complete, allow partial, minimum fill) shall be configurable per customer and order; backorders shall be tracked with expected dates and reallocation.
- **FR-SAL-SOR-22** Order confirmations shall be generated from templates in the customer's language and sent by email or portal, with the promised dates and terms.
- **FR-SAL-SOR-23** Order status tracking shall show each line's progress (confirmed, picked, delivered, invoiced) with expected dates, visible to representatives and customers (portal).
- **FR-SAL-SOR-24** Closing shall occur automatically when fully delivered and invoiced, or manually with reason, releasing reservations; cancelled lines shall release stock, credit exposure and planned supply.
- **FR-SAL-SOR-25** Intercompany orders shall create the matching purchase order in the counterpart company when Procurement is licensed and the intercompany accounting events for Finance.
- **BR:** an order cannot be delivered while a delivery block or hold applies; quantities delivered cannot exceed ordered plus tolerance without approval; price changes after delivery require credit and rebill, not order edits.
- **AC:** a 20-line order is entered by keyboard, availability splits two lines across warehouses, a credit check returns a warning that routes to the credit controller, a manager approves a discount above limit, and the customer receives a confirmation in Urdu with the promised dates.

## 7. Contracts and recurring sales (CTR)

**Purpose:** agreed prices and volumes over time, and recurring billing. **Actors:** Sales Manager, Sales Administrator, Billing Clerk.

- **FR-SAL-CTR-01** Sales contracts shall hold customer, validity, currency, price schedule, volume or value commitments, payment terms, service levels, renewal terms, documents and custom fields, with workflow approval and e-signature.
- **FR-SAL-CTR-02** Framework (blanket) contracts shall support release orders that consume the committed quantity or value, with alerts at thresholds, shortfall tracking and optional penalties or rebates.
- **FR-SAL-CTR-03** Contract prices shall feed price determination with priority over standard lists, with expiry and renewal workflows and price escalation rules (fixed percentage, index, schedule).
- **FR-SAL-CTR-04** Subscriptions and recurring services shall be defined with plans, billing frequency, start and end dates, quantities or seats, price, proration rules, trial periods, and renewal behavior (auto-renew, notice, cancellation).
- **FR-SAL-CTR-05** Usage-based billing shall import usage records, rate them by price conditions, validate and produce billing documents, with adjustments and disputes.
- **FR-SAL-CTR-06** Milestone and progress billing shall link billing events to project or service milestones with approvals and customer acceptance, issuing billing documents on completion.
- **FR-SAL-CTR-07** Upgrades, downgrades, add-ons and cancellations shall prorate charges and credits according to rules and generate the corresponding billing documents.
- **FR-SAL-CTR-08** Deferred revenue handling shall publish billing and period events with performance obligation data so Finance can defer and recognize revenue; full revenue-recognition accounting is planned in a later Finance wave. (S)
- **FR-SAL-CTR-09** Contract renewal and expiry alerts, renewal opportunities in the pipeline, and contract performance reports (commitment versus actual, margin, renewal rate) shall be available.
- **AC:** a yearly framework contract with price tiers is signed electronically, release orders consume it, an alert fires at 80 percent of committed value, a subscription is upgraded mid-month with proration, and renewal opportunities appear 90 days before expiry.

## 8. Delivery and fulfillment (DLV)

**Purpose:** move goods or perform services against orders, with controlled stock and documents. **Actors:** Warehouse and Dispatch Staff, Customer Service Representative, Logistics Planner, Billing Clerk.

- **FR-SAL-DLV-01** Deliveries shall be created from released order lines by rule (immediately, scheduled, consolidated by customer and date) or manually, with shipping point, warehouse, quantities, batches, serials, and delivery date and time windows.
- **FR-SAL-DLV-02** Delivery creation shall call Inventory to reserve and pick stock (pick lists, putaway, packing, wave handling by Inventory and Supply Chain), and shall support direct delivery of services and non-stock items without stock effects.
- **FR-SAL-DLV-03** Goods issue confirmation shall call the Inventory issue contract, post the stock movement (publishing GoodsIssuePosted for Finance cost of goods sold), and publish DeliveryPosted for planning, billing triggers and analytics; failures shall be shown to the user and not leave a half-posted delivery.
- **FR-SAL-DLV-04** Partial deliveries, split deliveries (several ship-to addresses or dates) and consolidated deliveries (several orders for one customer) shall be supported with clear status on each order line.
- **FR-SAL-DLV-05** Delivery documents (delivery note, packing list, gate pass, customs and compliance documents, labels) shall be generated from templates in the right languages with barcodes and QR codes.
- **FR-SAL-DLV-06** Transport arrangement shall use own fleet, carrier, customer pickup or courier; with Supply Chain licensed, shipments are planned, tendered, tracked and settled there (document 10, section 9).
- **FR-SAL-DLV-07** Proof of delivery shall be captured by Supply Chain or by a Sales mobile screen (signature, photo, received quantities, time and location), offline-capable; discrepancies (short, damaged, refused) create adjustment tasks and can trigger returns or credit notes.
- **FR-SAL-DLV-08** Delivery confirmation shall publish DeliveryConfirmed (published by Supply Chain when licensed, otherwise by Sales when proof of delivery is captured, decision D-18) and, where configured, trigger billing documents (billing on proof of delivery).
- **FR-SAL-DLV-09** Delivery blocks (credit, compliance, customer request) shall prevent goods issue with reason and release workflow; stock reservations shall remain or release by policy.
- **FR-SAL-DLV-10** Shipping charges, packaging and handling shall be added to deliveries or orders by rules and flow to billing.
- **FR-SAL-DLV-11** Delivery cancellation or reversal shall reverse stock movements through Inventory with linked documents and customer notification.
- **FR-SAL-DLV-12** Delivery scheduling shall consider customer receiving hours, route and dock appointments, with reminders and rescheduling.
- **BR:** a delivery posts only for approved, unblocked order lines; delivered quantity cannot exceed open quantity plus tolerance; posted deliveries are immutable and corrected by reversal or return.
- **AC:** a delivery is consolidated for two orders, picked on mobile, posted with batch numbers, proof of delivery is captured offline with a short delivery on one line, and the order shows the delivered and remaining quantities while billing is triggered for the confirmed quantity.

## 9. Billing and invoicing (BIL)

**Purpose:** issue correct, legal invoices and hand receivables to Finance. **Actors:** Billing Clerk, Sales Administrator, Finance Controller.

- **FR-SAL-BIL-01** Billing triggers shall be configurable: on order, on delivery, on proof of delivery, on milestone or progress, periodic (subscription), advance or down payment, and manual.
- **FR-SAL-BIL-02** Billing documents shall be invoice, simplified invoice, pro forma, down-payment request, credit note and debit note, each with its own numbering (through kernel numbering, gap-free where law requires), templates, tax treatment and approval.
- **FR-SAL-BIL-03** Invoices may be created individually, collectively (several deliveries or orders for one payer), or split (by tax, by ship-to or by payer), with a worklist of items due for billing by customer, date and trigger.
- **FR-SAL-BIL-04** Tax shall be determined by the kernel tax engine from item, customer, jurisdiction and date, with exemption certificates, withholding by customers recorded for Finance (document 5, AR-26), reverse charge where applicable, and rounding policy.
- **FR-SAL-BIL-05** Invoice templates shall be configurable in English, Urdu and Arabic with required legal elements from country packs (tax IDs, sequence numbers, QR codes, wording) and company letterhead.
- **FR-SAL-BIL-06** E-invoicing shall use the kernel connector framework and pack-provided adapters to submit, clear or report invoices to authorities, store response identifiers and QR data, show rejections in a work queue, and block issuing when the pack rules require a successful submission (see document 5, TAX-08).
- **FR-SAL-BIL-07** Approval before issue shall be configurable by value, customer, and non-standard terms; issued invoices are immutable and corrected by credit and debit notes or cancel-and-reissue rules where the law permits.
- **FR-SAL-BIL-08** Down-payments and advances shall be invoiced and later deducted on final invoices, with tax handled per pack rules.
- **FR-SAL-BIL-09** Rebates, volume discounts and commission-related adjustments shall be settled by credit notes from the agreements (FR-SAL-PRC-12).
- **FR-SAL-BIL-10** Issuing a billing document shall publish SalesInvoiceIssued or SalesCreditNoteIssued with lines, taxes, dimensions, payer, terms and due dates, so Finance creates the receivable and ledger entries through posting rules (or the Accounting Export connector receives them).
- **FR-SAL-BIL-11** Invoice delivery shall support email, customer portal, print, and EDI or e-invoice network, with delivery tracking and resend.
- **FR-SAL-BIL-12** Payment status, due dates and ageing shall be shown on invoices and customer views when Finance is licensed, with payment links through connectors in a later wave.
- **FR-SAL-BIL-13** Intercompany billing shall create corresponding payable documents in the counterpart company through events. (S)
- **FR-SAL-BIL-14** Billing blocks and disputes shall be recorded with reason, owner and resolution, excluding disputed items from dunning in Finance.
- **BR:** an issued invoice cannot be edited or deleted; legal numbers are never reused; billing quantity cannot exceed delivered quantity unless the trigger is advance or milestone; each invoice event is published once.
- **AC:** a collective invoice is created for three deliveries, tax and withholding are computed, the e-invoice is cleared with the authority and the QR code printed, SalesInvoiceIssued reaches Finance, and the receivable and ledger entries appear in Finance exactly once.

## 10. Returns and credits (RET)

**Purpose:** handle returns and price corrections fairly and traceably. **Actors:** Customer Service Representative, Warehouse Staff, Credit Controller, Billing Clerk.

- **FR-SAL-RET-01** Return authorizations shall be created from orders, deliveries or invoices with reason codes, eligibility rules (time window, condition, product type), and approval workflows, and shall be visible to customers in the portal.
- **FR-SAL-RET-02** Returned goods shall be received through Inventory (and the reverse logistics flows of Supply Chain), with condition and disposition (restock, quarantine for inspection, refurbish, scrap, return to supplier).
- **FR-SAL-RET-03** Credit notes shall be issued from approved returns, price corrections and rebates, referencing the original invoice, adjusting tax, and publishing SalesCreditNoteIssued for Finance.
- **FR-SAL-RET-04** Restocking fees, handling charges and non-returnable categories shall be configurable.
- **FR-SAL-RET-05** Replacement orders and exchanges shall be created from returns with linked documents and pricing rules.
- **FR-SAL-RET-06** Refunds shall be requested from Finance as customer refunds when credits are paid out, with approval.
- **FR-SAL-RET-07** Return analytics shall show rates, reasons, cost, recovery value and trends by customer, item and period.
- **AC:** a customer returns damaged goods within the window, inspection grades them as scrap, a credit note is issued with restocking fee waived by approval, a replacement order is created, and Finance receives the credit event.

## 11. Commissions and incentives (COM)

**Purpose:** reward sales performance transparently and accurately. **Actors:** Sales Manager, Sales Representative, Finance Controller, HR Payroll Officer.

- **FR-SAL-COM-01** Commission plans shall be configurable by role, representative, team or territory with bases (revenue, margin, quantity, collected cash), rates (flat, tiered, accelerators), product or customer category differences, caps, floors, split between representatives, and validity dates.
- **FR-SAL-COM-02** Commission shall accrue on a configurable event: invoice issue, delivery, or collection of payment (using receipt events from Finance when licensed), with clawback on credit notes, returns and write-offs.
- **FR-SAL-COM-03** Targets and quotas shall be set by representative, team, product and period with phasing, and progress shall be visible to representatives and managers.
- **FR-SAL-COM-04** Commission statements shall be generated per period and representative with transaction details, adjustments, disputes, approval workflow and publication in self-service.
- **FR-SAL-COM-05** Approved commissions shall be paid through HRMIS payroll as variable pay (event to HRMIS) or through Finance payables for external agents, with accrual events to Finance.
- **FR-SAL-COM-06** Dispute handling shall let representatives query statement lines, managers adjust with approval, and all changes be audited.
- **FR-SAL-COM-07** Simulation shall estimate commission cost for a plan or a sample of historical transactions before activation.
- **BR:** commission rules in effect on the accrual date apply; adjustments after payment create next-period corrections; plans cannot be changed retroactively without approval.
- **AC:** a tiered plan accrues on collected cash, a credit note reduces the accrual in the next statement, the manager approves the statement, and payroll receives the amounts as variable pay.

## 12. Sales channels (CHN)

### 12.1 Customer portal

- **FR-SAL-CHN-01** Customers shall access a branded portal with their own authentication realm (invited users, multi-factor authentication, optional single sign-on) and see only their own data and the sales areas they are assigned.
- **FR-SAL-CHN-02** Customers shall browse their catalog with their prices and availability, place orders (including reorder and bulk upload), request quotations, accept quotations, track orders and deliveries, view and download invoices and statements, and raise return requests.
- **FR-SAL-CHN-03** Customers shall see account balance, open invoices and payment status (Finance licensed), and may pay through payment connectors in a later wave.
- **FR-SAL-CHN-04** Portal approvals shall let customers' own approvers confirm orders above their company's thresholds.
- **FR-SAL-CHN-05** Portal content, languages (English, Urdu, Arabic with right-to-left layout), branding and messages shall be configurable, with security controls as for the supplier portal (rate limits, file scanning, audit).

### 12.2 Mobile field sales

- **FR-SAL-CHN-10** A mobile application shall support customer lists and visit plans, check-in with location, catalog browsing with customer prices and stock, order and quotation entry, collection of payments and cheques (posted to Finance), photos and notes, and competitor and shelf surveys.
- **FR-SAL-CHN-11** The mobile application shall work offline with a local database for catalogs, prices, customer data and drafts, and synchronize with conflict rules and validation on reconnection.
- **FR-SAL-CHN-12** Visit planning shall assign customers to routes and days with targets, distance and time tracking, and manager review of coverage and missed visits.
- **FR-SAL-CHN-13** Orders entered in the field shall use the same pricing, credit and availability engines as the web application, with offline pricing validated on sync.

### 12.3 Van sales and route distribution

- **FR-SAL-CHN-20** Vehicle stock shall be managed as a vehicle warehouse in Inventory with daily load-out, sales, returns and unload reconciliation, and variance approval.
- **FR-SAL-CHN-21** Route sales shall create invoices and receipts on the spot from the vehicle stock, print or send documents, and settle cash, cheque and credit sales at day end.
- **FR-SAL-CHN-22** Day-end settlement shall reconcile stock, cash and documents for each salesperson and vehicle, publish SalesReceiptRecorded for Finance, and flag shortages and surplus.
- **FR-SAL-CHN-23** Route and van analytics shall show sales per route and visit, strike rate, returns, stock variance and collections. (S)

### 12.4 E-commerce, marketplace and EDI connectors

- **FR-SAL-CHN-30** Connectors shall import orders from e-commerce platforms and marketplaces, export stock and price updates, and update order, delivery and tracking status, with mapping of customers and items, error queues and retries.
- **FR-SAL-CHN-31** EDI and e-document connectors shall receive purchase orders and send acknowledgements, advance shipping notices and invoices in the formats used by large customers, with mapping and validation. (S)
- **FR-SAL-CHN-32** Channel-specific pricing, stock allocation and fulfillment rules shall be configurable.

### 12.5 Point of sale (hook only)

Retail point-of-sale terminals are a later vertical module. Sales shall provide the hooks it will need: counter-sale order types with immediate delivery and billing, cash-drawer and shift documents through Finance, barcode and loyalty identifiers on customers, and offline selling with sync. Detailed requirements are specified with the retail vertical pack.

## 13. Sales analytics (SAN)

Sales reporting uses the kernel semantic layer and builders (document 9); this section defines standard content.

- **FR-SAL-SAN-01** Sales analysis shall report revenue, quantity, discount and margin by customer, customer group, item, category, region, territory, representative, channel, period and currency, with drill-down to documents and comparison to targets and prior periods.
- **FR-SAL-SAN-02** Margin and profitability analysis shall combine prices and costs (with cost permission), freight and rebate allocations, and returns, by customer, item and order.
- **FR-SAL-SAN-03** Pipeline and quotation analytics shall report pipeline value by stage, conversion rates, win and loss reasons, quotation ageing and cycle times.
- **FR-SAL-SAN-04** Backlog and fulfillment reports shall show open orders by age and promised date, backorders, delivery performance, on-time-in-full, and order-to-delivery cycle time.
- **FR-SAL-SAN-05** Price realization and discount leakage reports shall compare list, determined and actual prices, overrides by user, and promotion effectiveness.
- **FR-SAL-SAN-06** Customer analytics shall show top and bottom customers, concentration, growth, churn risk (declining orders), credit exposure, and lifetime value.
- **FR-SAL-SAN-07** Commission, target attainment and representative performance dashboards shall be available to managers and representatives.
- **FR-SAL-SAN-08** Sales forecasts from pipeline and history shall be available to the Supply Chain demand plan through published views.
- **FR-SAL-SAN-09** All standard reports shall be cloneable through the report builder, scheduled and exported, and respect data scopes (territory, team) and cost and margin visibility permissions.

## 14. Controls and compliance (CTL)

- **FR-SAL-CTL-01** Segregation of duties shall be configurable: order entry, credit release, price override approval, delivery posting, and invoice issue by different roles above thresholds; violations are blocked or flagged and reportable.
- **FR-SAL-CTL-02** A discount and pricing authority matrix shall define limits by role, product, customer and margin, applied by the workflow engine.
- **FR-SAL-CTL-03** Customer and order screening against restricted-party and sanctions lists shall be available through connectors with holds and review workflows. (S)
- **FR-SAL-CTL-04** Anomaly detection shall flag unusual order patterns, sudden volume spikes, repeated overrides, orders just under approval limits, address or bank changes before orders, and returns abuse. (S)
- **FR-SAL-CTL-05** Customer data privacy shall follow consent and retention rules, with data access, correction and erasure workflows within legal retention (kernel SEC-06).
- **FR-SAL-CTL-06** Audit shall record price changes, overrides, holds and releases, order versions, deliveries, invoices and approvals with user, time and reason, and be searchable by document and customer.
- **FR-SAL-CTL-07** Sensitive pricing and margin data shall be hidden from roles without permission, in screens, reports, exports and AI answers.
- **AC:** a representative cannot release the credit hold on their own order; an override above the limit is blocked without a manager; the exception report lists all overrides by user and reason.

## 15. Country notes

These are planning inputs to be verified by qualified local tax and legal advisers before design is frozen.

| Topic | Pakistan | Saudi Arabia | United States |
| --- | --- | --- | --- |
| Invoicing | Sales tax invoices with registration numbers; further tax rules for unregistered buyers; authority integration for certain sectors | VAT tax invoices and simplified invoices with national e-invoicing phases, QR codes, and bilingual Arabic and English content | No national e-invoicing mandate; customer portals and EDI formats through connectors |
| Indirect tax | Federal and provincial sales taxes with different rates and registrations | 15 percent VAT with zero rating and exemptions | State and local sales tax by destination, nexus rules, rate services, customer exemption certificates |
| Customer withholding | Customers may withhold income tax at payment and issue certificates | Limited withholding cases | Not typical |
| Regulated sectors | Track-and-trace and stamp requirements for certain products to be scoped by sector | Product registration and labeling rules to be scoped by sector | Product traceability and labeling rules by sector |
| Payment practices | Cheques, post-dated cheques, cash on delivery and credit terms common; van sales and distributors widespread | Bank transfer and card; credit terms by sector | ACH, card and check; net terms |
| Language and documents | Urdu and English quotations, invoices and delivery notes | Arabic and English with right-to-left layout | English, optional Spanish |
| Channels | Distributors, van sales, field sales with offline mobile; growing e-commerce | Distributors, marketplaces and e-commerce platforms | EDI with retailers, marketplaces and portals |

Pack responsibilities: tax and withholding determination, invoice templates and legal elements, e-invoicing adapters, regulated-sector tracking connectors, document templates, price and discount starter rules, and channel connectors.

## 16. Events and contracts

### 16.1 Events published

| Event | Consumers |
| --- | --- |
| OrderCreated, OrderChanged, OrderCancelled | Supply Chain (demand), Inventory (reservations), Finance (credit exposure), reporting |
| DeliveryPosted | Supply Chain (history), Finance (revenue triggers where configured), reporting |
| DeliveryConfirmed | Billing triggers (this module), Inventory, Finance. Published by Supply Chain when licensed, otherwise by Sales when proof of delivery is captured |
| SalesInvoiceIssued, SalesCreditNoteIssued | Finance (receivable and ledger entries) or Accounting Export |
| SalesReceiptRecorded | Finance (receipts for van, counter and field collections) |
| RebateAccrued | Finance (accruals) |
| BillingPeriodEvent (subscriptions and milestones) | Finance (deferred revenue and recognition) |
| ReturnAuthorized | Inventory, Supply Chain (reverse logistics), notifications |
| CommissionApproved | HRMIS (variable pay) or Finance (payables) |
| CustomerStatusChanged | Finance, Supply Chain, portal |

### 16.2 Events consumed

| Event | Source | Effect |
| --- | --- | --- |
| GoodsIssuePosted | Inventory | Delivery line confirmed with stock and cost reference |
| StockAvailabilityChanged, BatchBlocked, RecallRaised | Inventory | Promising refreshed; affected orders and customers listed |
| CustomerCreditHoldChanged, CustomerExposureChanged | Finance | Holds applied or released on orders and deliveries |
| ReceiptAllocated, InvoicePaid | Finance | Payment status shown; collection-based commissions accrued |
| ShipmentMilestoneReached, DeliveryConfirmed | Supply Chain | Order tracking and billing triggers updated |
| ReturnDispositioned | Supply Chain or Inventory | Credit note and restocking steps proceed |
| PurchaseOrderConfirmed | Procurement | Intercompany order confirmation |
| PeriodClosed, PeriodReopened | Finance | Billing and posting date locks |
| EmployeeChanged | HRMIS | Representative and territory assignments refreshed |

### 16.3 Synchronous contracts

| Contract | Provider | Behavior without provider |
| --- | --- | --- |
| Availability and promise date | Inventory or Supply Chain | Always allowed, no promise date |
| Credit exposure and check | Finance | Always allowed; basic limit in Sales if configured |
| Issue goods and receive return commands | Inventory | Documents proceed without stock effect for non-stock items |
| Tax determination | Kernel tax engine | Not optional; kernel service |
| Exchange rate | Kernel | Kernel service answers |
| E-invoice submission, restricted-party screening, payment links | Connectors from packs | Feature unavailable |

## 17. Roles, permissions and default workflows

### 17.1 Baseline roles

| Role | Main permissions |
| --- | --- |
| Sales Representative | Manage own customers, opportunities, quotations and orders within discount authority |
| Sales Administrator | Enter and maintain orders, contracts, price lists with approval where required |
| Sales Manager | Approve discounts, quotations and orders above limits; manage targets, territories and commissions |
| Pricing Analyst | Maintain price lists, conditions and promotions |
| Customer Service Representative | Promise, change and expedite orders, manage returns |
| Billing Clerk | Prepare and issue billing documents |
| Dispatch Staff | Create, pick and post deliveries |
| Credit Controller | Release holds and manage limits (Finance) |
| Customer user (portal) | Own account data only, as in section 12.1 |
| Auditor | Read-only with drill-down; margin and cost fields by approval |

### 17.2 Default workflows (all editable)

| Workflow | Default route |
| --- | --- |
| Quotation | Representative; sales manager above discount or value limits; finance above higher limits |
| Sales order | Sales administrator; manager for discount, margin or terms exceptions; credit controller for holds |
| Price override | Sales manager by percentage and margin; pricing analyst for permanent changes |
| Price list change | Pricing analyst, sales manager, scheduled activation |
| Contract | Sales manager, legal review, finance for terms, signature |
| Billing document issue | Billing clerk; manager above value or non-standard terms |
| Return authorization | Customer service, warehouse inspection, finance for credit |
| Commission statement | Sales manager, finance, payroll |
| Customer onboarding | Representative, credit assessment, sales manager |

## 18. Data model overview

Tables live in the sal schema (kernel references as in document 3). Issued billing documents, deliveries and order versions are immutable; pricing results keep their determination traces.

| Area | Main tables |
| --- | --- |
| Customers | customer\_sales\_areas, customer\_hierarchies, customer\_segments, territories, territory\_assignments, customer\_item\_lists, onboarding\_cases |
| Pricing | price\_lists, price\_list\_lines, pricing\_procedures, price\_conditions, condition\_records, discount\_structures, promotions, promotion\_usage, rebate\_agreements, rebate\_accruals, price\_determination\_traces |
| Pipeline and quotations | leads, opportunities, activities, quotations, quotation\_versions, quotation\_lines, bids |
| Orders | sales\_orders, sales\_order\_lines, order\_versions, delivery\_schedules, order\_holds, backorders, order\_links |
| Contracts | sales\_contracts, contract\_prices, contract\_releases, subscription\_plans, subscriptions, subscription\_periods, usage\_records, billing\_schedules |
| Delivery | deliveries, delivery\_lines, delivery\_blocks, proofs\_of\_delivery (when captured in Sales) |
| Billing | billing\_documents, billing\_lines, billing\_runs, billing\_blocks, e\_invoice\_submissions |
| Returns | return\_authorizations, return\_lines, return\_dispositions\_link |
| Commissions | commission\_plans, plan\_assignments, commission\_accruals, commission\_statements, commission\_adjustments, targets |
| Channels | portal\_users, visit\_plans, visits, mobile\_sync\_log, van\_loads, day\_settlements, connector\_orders, edi\_messages |
| Outbox and inbox | outbox\_messages, inbox\_messages |

Key design points: document headers and lines follow the pattern of document 3, section 7.1; order lines carry price traces; billing documents carry legal numbers and tax lines; document links join quotation, order, delivery, invoice and payment without cross-module foreign keys.

## 19. AI skills

All skills run through the AI Gateway with the user's permissions, propose and never decide, and are switchable per tenant (kernel document 2, section 22).

| Skill | Description | Wave |
| --- | --- | --- |
| Quotation drafting | Draft quotations from customer requests (email, message, document) for review | C |
| Order capture from documents | Extract orders from emails, PDFs and messages and prefill order lines | C |
| Pricing and discount guidance | Suggest discounts and price points from history, margin and win rates, always advisory | C |
| Next-best-action | Suggest follow-ups for opportunities, quotations and customers | C |
| Customer 360 summary | Summarize account status, risks and opportunities | C |
| Churn and upsell signals | Detect declining orders and suggest products | C |
| Natural-language sales questions | Query sales, margin and pipeline within scope | C |
| Commission query assistant | Explain statement lines to representatives | C |
| Return reason classification | Group return reasons and suggest root causes | C |

AI shall not change prices beyond the user's authority, release holds, issue billing documents, or send messages to customers without human confirmation.

## 20. Non-functional requirements

| Area | Requirement |
| --- | --- |
| Volume | At least 5 million order lines per year and 500 concurrent users per tenant; portal for 5,000 concurrent customers |
| Performance | Price determination under 200 milliseconds per line at the 95th percentile; saving a 100-line order under 2 seconds; billing run of 100,000 documents under 30 minutes as a resumable job |
| Mobile | Offline catalogs of 100,000 items and 50,000 customers on device; sync with conflict rules |
| Integrity | Order, delivery and billing quantities reconcile; invoices equal receivable postings in Finance; each event published once |
| Security | Separate portal authentication realm, customer data isolation, price and margin confidentiality, rate limits, penetration tested |
| Auditability | Complete history of prices, overrides, holds, versions, deliveries and invoices |
| Localization | English, Urdu and Arabic screens and documents; right-to-left layouts; amounts in words where required |
| Configurability | Order types, status models, pricing procedures, workflows, templates and rules configurable without code |

## 21. Acceptance and testing

- **Golden pricing datasets:** independent calculations of price determination, discounts, promotions and rebates for representative customers, reviewed by a sales operations practitioner.
- **End-to-end scenarios:** lead to quotation to order to delivery to proof of delivery to invoice to receipt; framework contract with releases; subscription with proration and renewal; return with credit and replacement; van sales day with settlement.
- **Tax and e-invoicing tests:** tax scenarios per country pack verified by a practitioner; e-invoicing adapters tested against authority sandboxes where available.
- **Configuration variants:** run flows with Finance and Inventory, Inventory only, Finance only, and neither (Accounting Export and non-stock sales) to prove graceful degradation (document 2, GR-12).
- **Controls tests:** segregation of duties matrix, discount authority, credit holds, and override reporting.
- **Portal and mobile tests:** customer isolation, abuse and rate-limit tests, offline order entry and sync.
- **Reconciliation invariants:** invoice totals equal Finance receivable postings; delivered quantities equal Inventory issues; order status equals delivery and billing state.
- **Performance:** volume and concurrency tests at 1.5 times expected load.
- **Pilot exit criteria:** one full month of order-to-cash by a pilot customer without workarounds, zero open severity-1 and severity-2 defects, and Finance reconciliation of invoices, receivables and revenue.

## 22. Assumptions, alignment items and open items

**Assumptions:** Sales owns commercial documents up to the issued billing document; Inventory owns stock movements; Finance owns receivables, receipts and the ledger; Supply Chain adds planning and logistics on top; country rules arrive as packs; the portal shares the web platform with its own authentication realm.

**Cross-document alignment items** (resolved by decisions D-15 to D-18 of document 14 and applied to documents 5, 8 and 10):

1. Invoice issuing: in tenants with Sales, Sales issues invoices and calls the e-invoicing connector; Finance TAX-08 (document 5) applies to invoices entered directly in Finance and to the behavior of the pack adapters.
2. Deliveries and stock: Sales confirms goods issue through the Inventory issue contract (section 8); document 8, section 26.2 describes the same flow as consuming DeliveryPosted and should read as issue-contract driven.
3. Returns: the return authorization is owned by Sales (section 10); document 10, RLG-01 extends it with reverse shipments and dispositions and should reference it.
4. DeliveryConfirmed publisher: Supply Chain when licensed, otherwise Sales (section 16).

**Open decisions:** whether Sales moves from wave C to wave B for pilot customers who need order-to-cash early (the architecture supports this because Finance, Inventory and kernel hooks are already planned); depth of the light pipeline versus a separate CRM product or connector; EDI scope; payment connector providers per country; timing of the retail point-of-sale vertical; depth of subscription, usage and revenue-recognition coupling; regulated-sector tracking connectors per country.

**Next documents:** 9 Analytics, Reporting and AI Functional Specification, and 13 UX and Design System Principles. After these, I recommend a consistency review across all documents before coding steps beyond Step 1.

## 23. Baseline 1.0 addendum (this document is now version 0.2)

*Added 14 November 2026 by the comprehensive review (Doc 16). Sections 1 to 22 are unchanged. The previous state (version 0.1) is kept in this document's version history and in the frozen originals archive. Items tied to a proposed decision take effect on its approval.*

### 23.1 Sales core moves to Phase 3 (proposed D-32)

Sales was planned after Procurement and Inventory (wave C). A trading business cannot run without customer orders and invoices, so Sales core is now built in Phase 3 with Procurement, Inventory and Asset Management, after one shared data design gate. This settles the alignment item in section 22 on promoting Sales.

| Scope | Areas | Phase |
| --- | --- | --- |
| Sales core | CUS, PRC basic price lists and discounts, QUO quotations, SOR, DLV, BIL, RET, CTL credit control, basic SAN | 3 |
| Sales extended | PRC promotions and rebates, CTR contracts and recurring billing, COM commissions, CHN channels, advanced SAN | 4 |

### 23.2 Binding cross-module rules

Doc 16 section 6 applies. Where a requirement in sections 3 to 14 is looser than an FR-XM rule, the FR-XM rule applies.

| Rule | Effect on Sales | Sections touched |
| --- | --- | --- |
| FR-XM-01, 03 | Customer invoices and credit notes post atomically through the Finance posting contract (proposed D-31); high-volume channels (point of sale batches) may use deferred mode by policy | 9, 12 |
| FR-XM-05, 06 | A posted invoice is corrected only by credit note; legal invoice numbers are never reused; an unposted order may be cancelled and keeps its number | 6, 9 |
| FR-XM-13 | Order lines track open quantity; short-close with reason releases reservations | 6 |
| FR-XM-20 | Reserved stock is not issued to another order without explicit release | 6, 8 |
| FR-XM-22 | Partial delivery and partial invoicing per line; credit hold checked at confirmation and again at delivery; release needs a role and a reason | 6, 8, 14 |
| FR-XM-23 | A return references the original invoice where known, brings stock back at original issue cost and raises a credit note; after payment the credit is refunded or offset | 10 |
| FR-XM-19 | A sale between legal entities of one tenant is an intercompany sale and purchase at transfer price | 6, 9 |
| FR-XM-32 | Credit release, price override and discount beyond authority appear on the audit exception report | 14 |

### 23.3 Size profile and data notes

The Micro size profile shows a quick invoice that creates order, delivery and invoice in one step; the three documents exist and can be separated later without conversion. Sales documents carry `tenant_id` (proposed D-30) and `branch_id` (correction C-39). Prices come from price lists; the kernel item `default_price` is informational only (correction C-41).
