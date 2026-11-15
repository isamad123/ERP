# Inventory and Asset Management: Functional Specification (v0.2)

Document 8 of the planning set | Prepared for Ibrahim | 10 November 2026 | Companion to the Platform Kernel, Data Architecture, Finance, HRMIS and Procurement specifications

## 1. Introduction

### 1.1 Purpose and scope

This document specifies two modules that are sold separately or together:

- **Inventory** (schema inv): stock attributes, warehouses and locations, stock transactions, valuation and costing, batch and serial tracking, reservations, counts, replenishment, quality inspection and warehouse operations.
- **Asset Management** (schema ast): the fixed-asset register, acquisition and capitalization, multi-book depreciation, transfers, revaluation, disposal, physical verification, maintenance, insurance and custody.

Both are built in wave B, after Finance and HRMIS, and are specified now so that the kernel catalog, Finance posting rules and the Procurement module already contain the hooks they need.

### 1.2 Dependencies (decision D-10)

| Module | Inventory | Asset Management |
| --- | --- | --- |
| Kernel | Hard: catalog, UoM, organization, parties, tax, numbering, workflow, documents, rules, localization, reporting | Hard: same kernel services |
| Finance | Optional: valuation, goods-received-not-invoiced, cost of goods sold, adjustment journals through posting rules; without Finance, the Accounting Export connector receives events | Optional: control accounts, acquisition and depreciation journals, disposal gain or loss; without Finance, depreciation is calculated and exported |
| Procurement | Optional: expected receipts, receipts against orders, returns to supplier, landed cost | Optional: asset-flagged purchase lines create acquisitions |
| Sales (wave C) | Optional: deliveries, customer returns, reservations, availability | Optional: disposal by sale |
| Supply Chain (wave C) | Optional: planning, replenishment, logistics | None |
| HRMIS | Optional: uniform and consumable issues to employees | Optional: custody, offboarding clearance |
| Inventory and Assets | Spare parts for maintenance and asset-related issues | Uses Inventory for spares when licensed |

The catalog of items, units of measure and categories belongs to the kernel (document 2, CAT), so an asset-only or procurement-only tenant works without Inventory.

### 1.3 Conventions

- Requirement IDs: FR-INV-\<area>-\<number> and FR-AST-\<area>-\<number>. Priority (M), (S), (C); unmarked is (M). Wave B unless stated.
- Each area lists Functional Requirements, Business Rules (BR) and Acceptance Criteria (AC).
- Every configuration is reachable through the admin console and the API (documents 2 and 4).
- Country-specific accounting and tax rules for depreciation and stock are pack content, verified by local practitioners.

### 1.4 Actors

| Actor | Role |
| --- | --- |
| Warehouse Manager | Runs warehouses, approves adjustments, reviews counts |
| Storekeeper | Receives, issues, transfers and counts stock |
| Inventory Controller | Owns valuation, costing methods, reconciliation to Finance |
| Quality Inspector | Records inspection outcomes |
| Planner | Manages reorder parameters and replenishment |
| Asset Manager | Owns the asset register, depreciation and disposals |
| Maintenance Planner and Technician | Plan and perform maintenance |
| Custodian | Employee or unit responsible for an asset |
| Financial Controller | Approves valuation changes, depreciation postings, disposals |
| Auditor | Read-only access with drill-down |

### 1.5 Key terms

| Term | Meaning |
| --- | --- |
| Stock item | A catalog item whose quantities and values are tracked |
| Stock ledger | The append-only record of all stock movements |
| On hand, reserved, available | Physical quantity; quantity promised to documents; on hand less reserved |
| Valuation method | The rule that gives each movement a cost: FIFO, weighted average, standard cost or specific identification |
| Landed cost | Freight, duty and other costs added to the value of received goods |
| Batch and serial | Lot-level and unit-level identities used for traceability |
| Depreciation book | A parallel set of depreciation rules and results, mapped to a Finance ledger |
| CWIP | Construction in progress: asset costs accumulated before capitalization |
| Capitalization | Moving accumulated cost into an in-service asset |

## 2. Inventory overview

### 2.1 Capability map

| Area | Summary |
| --- | --- |
| Item stock attributes | Valuation, tracking, shelf life, reorder and storage parameters added to kernel items |
| Warehouses and locations | Warehouse types, location hierarchy, putaway and picking rules |
| Stock transactions | Receipts, issues, transfers, adjustments, returns with an append-only ledger |
| Valuation and costing | FIFO, weighted average, standard cost, specific identification, landed cost, revaluation |
| Batch, serial and expiry | Traceability, expiry control, recalls, warranty |
| Reservations and availability | Reservations, allocation, availability to promise |
| Counts | Cycle counts and stock takes with variance control |
| Replenishment | Reorder point, min-max, suggested requisitions and transfers |
| Quality inspection | Inspection plans, results, quarantine, non-conformance |
| Warehouse operations | Receiving, putaway, picking, packing, dispatch with mobile scanning |
| Reporting | Stock, valuation, ageing, expiry, turnover, variance, reconciliation to Finance |

### 2.2 Documents and statuses

| Document | Key statuses |
| --- | --- |
| Stock receipt | draft, posted, reversed |
| Stock issue | draft, approved, posted, reversed |
| Stock transfer | draft, approved, in transit, received, cancelled |
| Stock adjustment | draft, submitted, approved, posted, rejected |
| Stock count | planned, frozen, counting, review, approved, posted, cancelled |
| Reservation | active, partially consumed, consumed, released |
| Inspection | pending, in progress, accepted, rejected, partially accepted |
| Pick list and putaway task | open, in progress, completed, cancelled |

Status models are configurable within module-defined safe limits (kernel MET-30). All stock documents link to their sources through the kernel document-link table.

### 2.3 Principles

1. The stock ledger is the single source of truth; on-hand balances derive from it and are maintained transactionally.
2. Posted stock documents are immutable; corrections are reversals and new documents.
3. Stock can be used without Finance; accounting is an event consumer, not a prerequisite.
4. Units of measure, batches, serials and locations are optional layers: a simple tenant uses item and warehouse only, a complex one uses all.

## 3. Item stock attributes (ITM)

**Purpose:** extend kernel catalog items with stock-specific data without altering the kernel table. **Actors:** Inventory Controller, Planner.

- **FR-INV-ITM-01** Each stock item shall have stock attributes: stock unit of measure, valuation method, costing group, tracking type (none, batch, serial, batch and serial), shelf-life days, minimum remaining shelf life for receipt and issue, default warehouse and location, storage conditions, hazardous flag, weight and volume, ABC class, and inspection required flag.
- **FR-INV-ITM-02** Stock attributes shall appear only when Inventory is licensed and shall be editable by permission with effective-dated changes for valuation method and tracking type; changes after movements exist shall follow controlled procedures (section 6).
- **FR-INV-ITM-03** Item-warehouse parameters shall override defaults per warehouse: reorder point, min and max, safety stock, lead time, order multiple, putaway location, picking rules, and active status. When Supply Chain is licensed it proposes changes and writes approved values back through the UpdatePlanningParameters contract (decision D-13).
- **FR-INV-ITM-04** Item groups and costing groups shall carry default accounts through posting rules, valuation method, tracking and reorder defaults, inherited by items.
- **FR-INV-ITM-05** Unit of measure behavior shall use kernel conversions, with stock, purchase, sales and counting units and rounding rules; changes to the stock unit after movements shall be blocked.
- **FR-INV-ITM-06** Kits or bundles (sets of items sold or issued together) shall be definable with components and quantities, exploded on issue or sale; multi-level bills of materials are part of a later manufacturing wave. (S)
- **FR-INV-ITM-07** Item identifiers (barcodes, GS1 data, supplier part numbers) shall be maintained through the kernel catalog and used by scanning screens.
- **FR-INV-ITM-08** Item status (active, blocked for purchase, blocked for sale, blocked for stock movement, discontinued) shall control transactions with reasons.
- **FR-INV-ITM-09** Item attribute templates shall let tenants add custom fields and per-category attribute sets through the metadata engine.
- **FR-INV-ITM-10** Mass maintenance and import of stock attributes shall be available through the import center with validation and preview.
- **BR:** an item with stock on hand cannot change tracking type without an approved conversion procedure; valuation method changes post revaluation per section 6.
- **AC:** a service item has no stock attributes; a catalog item becomes a stock item when Inventory is activated, with attributes set from its category defaults and no data migration of the catalog record.

## 4. Warehouses and locations (WHS)

**Purpose:** model where stock physically and logically sits. **Actors:** Warehouse Manager, Tenant Admin.

- **FR-INV-WHS-01** Warehouses shall have code, name, company, address, type (main, branch, transit, quarantine, scrap, consignment, vendor-owned, customer-held, virtual), status, time zone, operating calendar, and responsible person.
- **FR-INV-WHS-02** A location hierarchy within a warehouse shall support configurable levels (zone, aisle, rack, shelf, bin), with codes, barcodes, capacity (quantity, weight, volume), restrictions (item groups, hazardous, temperature), and status.
- **FR-INV-WHS-03** Stock keeping shall be configurable per warehouse as warehouse-level only, or location-level (bin-managed), so small sites need not manage bins.
- **FR-INV-WHS-04** Putaway rules shall suggest locations by item, group, batch attributes, capacity, fixed bins, nearest empty bin and mixing restrictions.
- **FR-INV-WHS-05** Picking rules shall be configurable per warehouse and item: FIFO, FEFO (first expired first out), LIFO, nearest location, and minimum remaining shelf life by customer.
- **FR-INV-WHS-06** Special-purpose locations (receiving dock, staging, quality hold, damaged, returns) shall be flagged so stock there is treated as unavailable or restricted by status.
- **FR-INV-WHS-07** Warehouses shall be assignable to one or more companies or shared across companies with ownership tracked at the movement level (inter-company stock). (S)
- **FR-INV-WHS-08** Location labels and barcodes shall be printable from templates in bulk.
- **FR-INV-WHS-09** Warehouse permissions shall restrict users to assigned warehouses for viewing and posting through kernel data scopes.
- **AC:** a warehouse with zones and bins applies a fixed-bin putaway rule for fast movers, FEFO picking for perishable items, and a quarantine location that excludes stock from availability.

## 5. Stock transactions (MOV)

**Purpose:** record every change in stock accurately, with full traceability and control. **Actors:** Storekeeper, Warehouse Manager, Inventory Controller.

### 5.1 General rules

- **FR-INV-MOV-01** Every stock change shall be an append-only movement in the stock ledger with item, warehouse, location, batch, serial, quantity, unit, value, movement type, source document, user, and timestamp, written in the same transaction as the document posting.
- **FR-INV-MOV-02** Movement types shall be configurable (receipt, issue, transfer out and in, adjustment plus and minus, return, scrap, sample, production consumption and output in a later wave) with direction, approval rule, reason requirement, valuation behavior and accounting event mapping.
- **FR-INV-MOV-03** On-hand, reserved, available, in-transit and on-order quantities shall be maintained transactionally by item, warehouse, location, batch and serial, and reconcile at all times to the sum of movements.
- **FR-INV-MOV-04** Negative stock policy shall be configurable per warehouse and item group (block, warn, allow with later cost correction); allowed negative stock shall be flagged for review.
- **FR-INV-MOV-05** Backdated postings shall follow configured rules: allowed within open periods and a day limit by permission, with an impact analysis of affected costs and a recalculation job for subsequent movements.
- **FR-INV-MOV-06** Posting dates shall obey Finance period locks when Finance is licensed and the inventory period status otherwise.
- **FR-INV-MOV-07** All stock documents shall support attachments, reason codes, custom fields, numbering, workflow approvals and barcode scanning on web and mobile.

### 5.2 Receipts

- **FR-INV-MOV-10** Receipts shall be created from Procurement receipts (through the Inventory receiving contract), from direct receipts without an order (permitted by role and reason), from production output (later), and from customer returns (with Sales).
- **FR-INV-MOV-11** A receipt shall capture quantity, unit, location, batch, expiry, serial numbers, supplier lot, condition, and inspection need; receipts requiring inspection shall go to quarantine until the inspection outcome.
- **FR-INV-MOV-12** Valuation of a receipt shall use the order price and landed costs where available, or a provisional cost corrected when the invoice arrives, with variance handling by posting rules.
- **FR-INV-MOV-13** Inventory shall publish GoodsReceiptPosted with item, quantity, value, warehouse and dimensions for Finance and for Procurement status.

### 5.3 Issues

- **FR-INV-MOV-20** Issues shall be created for internal consumption (cost center, department, project, maintenance work order, employee issue), sales deliveries (with Sales), samples, scrap and write-offs, with required dimensions and reasons per type.
- **FR-INV-MOV-21** Issue pick-up shall propose batches, serials and locations by picking rules, with scanning confirmation and override control.
- **FR-INV-MOV-22** Issues above approval thresholds or to restricted categories shall route through workflow.
- **FR-INV-MOV-23** Inventory shall publish GoodsIssuePosted with item, quantity, value and dimensions for Finance (consumption or cost of goods sold) according to movement type.

### 5.4 Transfers

- **FR-INV-MOV-30** Transfers between warehouses or locations shall support one-step and two-step (issue to transit, receive at destination) modes with in-transit quantities, expected arrival, and partial receipt.
- **FR-INV-MOV-31** Inter-company transfers shall create matching documents in each company, valued at transfer price or cost, and publish events for intercompany accounting in Finance.
- **FR-INV-MOV-32** Transfer requests shall be created by warehouses, approved by workflow, and fulfilled by pick lists and dispatch.
- **FR-INV-MOV-33** Differences between sent and received quantities shall be recorded as transit loss or gain with reasons and approvals.

### 5.5 Adjustments and returns

- **FR-INV-MOV-40** Stock adjustments (gain, loss, damage, expiry, found, theft, reclassification between batches or locations) shall require a reason code and approval by value thresholds, and attachments for evidence.
- **FR-INV-MOV-41** Returns to supplier shall reference the receipt, remove stock at the original cost, and publish events for credit expectation; returns from customers shall add stock with condition and disposition (restock, quarantine, scrap).
- **FR-INV-MOV-42** Stock status changes (available, quarantine, blocked, expired) shall be movements or status transactions with audit and effect on availability.
- **FR-INV-MOV-43** Reversal of a posted stock document shall create linked reversing movements, valued at the original cost, and shall be blocked if later movements make it impossible (for example consumed batch) unless corrected by an approved adjustment.
- **BR:** a movement cannot reference an inactive item, closed warehouse, or blocked batch; serial-tracked items move one unit per serial; the ledger is the only way to change stock.
- **AC:** a transfer in two steps shows in-transit stock, partial receipt records 95 of 100 units with five lost in transit by an approved adjustment, and on-hand quantities equal the sum of ledger movements for every item and location.

## 6. Valuation and costing (VAL)

**Purpose:** give every unit of stock a correct, explainable cost. **Actors:** Inventory Controller, Financial Controller.

- **FR-INV-VAL-01** Valuation methods shall include FIFO, weighted average (moving average recalculated on each receipt), standard cost, and specific identification (by batch or serial), configurable per item group or item, per company.
- **FR-INV-VAL-02** The system shall keep cost layers for FIFO and specific identification (receipt, remaining quantity, unit cost, landed cost) and a running average for weighted average, each reconcilable to the stock ledger.
- **FR-INV-VAL-03** Every movement shall carry quantity and value; issues shall take the cost determined by the method, and the value shall be stored on the movement so it never changes silently.
- **FR-INV-VAL-04** Landed cost allocation shall add freight, duties, insurance and handling to received goods by value, quantity, weight or volume rules, either at receipt or later by an allocation document, with revaluation of remaining and consumed quantities as configured.
- **FR-INV-VAL-05** Standard costs shall be maintained by item and effective date with a roll-up process and approval; purchase price variance, usage variance and revaluation differences shall be posted through events and rules.
- **FR-INV-VAL-06** Revaluation documents shall adjust unit cost or value for a selection of items with reason, approval and effective date, publishing events for Finance.
- **FR-INV-VAL-07** Provisional receipt cost corrected by the supplier invoice shall adjust remaining stock value and, for already issued quantities, post the difference to a configured variance account.
- **FR-INV-VAL-08** Backdated movements shall trigger recalculation of subsequent costs for FIFO and average methods through a resumable job, with a before and after report and Finance notification of the resulting adjustments.
- **FR-INV-VAL-09** Negative stock valuation shall use configured rules (last known cost, standard cost, estimated) and be corrected when receipts arrive, with the correction posted as a variance.
- **FR-INV-VAL-10** Net realizable value and obsolescence provisions shall be supportable by rules (ageing, expiry, slow-moving) producing proposals for Finance review. (S)
- **FR-INV-VAL-11** Multi-currency receipts shall store the transaction value and company currency value, with rates at receipt; later payments do not change stock value except through landed cost or invoice correction rules.
- **FR-INV-VAL-12** A valuation report as of any date shall present quantity, unit cost and value by item, warehouse, batch and group, and reconcile to the Finance inventory control accounts; differences shall block period close when Finance is licensed.
- **FR-INV-VAL-13** Changing the valuation method for an item with stock shall require an approved conversion that revalues stock at a defined date, with the difference posted by an adjustment document.
- **FR-INV-VAL-14** Account determination for stock events shall be performed by Finance posting rules from item group, movement type, warehouse and company, never in Inventory code.
- **BR:** valuation by quantity and cost layers equals the sum of ledger movements; changes to historical cost happen only through documented revaluation, correction or recalculation documents; weighted average cost is never recalculated retroactively without the backdating job.
- **AC:** a FIFO item receives three lots at different costs; issues consume the oldest layers; a landed cost allocation raises the cost of remaining layers and posts the difference for consumed units; the valuation report ties to the Finance inventory account.

## 7. Batch, serial and expiry (TRK)

**Purpose:** trace goods by lot and unit, and manage shelf life. **Actors:** Storekeeper, Quality Inspector, Warehouse Manager.

- **FR-INV-TRK-01** Batches shall have batch number (supplier lot and internal), manufacturing date, expiry date, supplier, country of origin, attributes, status (available, quarantine, blocked, expired) and documents such as certificates of analysis.
- **FR-INV-TRK-02** Serial numbers shall have a status lifecycle (in stock, issued, installed, returned, scrapped), current location or holder, warranty dates, and history of movements.
- **FR-INV-TRK-03** Numbers shall be generated automatically by pattern, entered, scanned or imported at receipt; duplicates shall be blocked within an item.
- **FR-INV-TRK-04** Issuance shall assign batches and serials automatically by rules (FEFO, FIFO, specific) or by scanning, with minimum remaining shelf life checks.
- **FR-INV-TRK-05** Traceability reports shall show forward trace (where a batch or serial went: customers, work orders, assets) and backward trace (supplier, receipts, inspections) within seconds for recall handling.
- **FR-INV-TRK-06** Expiry management shall provide near-expiry alerts by item and customer rules, automatic quarantine or blocking on expiry, and workflows for destruction or return.
- **FR-INV-TRK-07** Recall management shall let an authorized user flag a batch or serial range, list affected stock and shipments, block usage, and track notification and recovery.
- **FR-INV-TRK-08** Batch merging, splitting and re-labeling shall be supported with audit and traceability preserved.
- **FR-INV-TRK-09** Label printing for batches, serials, locations and handling units shall use templates with barcodes and QR codes, including GS1 formats where required.
- **FR-INV-TRK-10** Warranty information on serial numbers shall flow to asset records when a serialized item becomes an asset (with Assets licensed).
- **BR:** a serial number exists at one location at a time; expired or blocked batches cannot be issued without an authorized override; traceability data cannot be deleted.
- **AC:** a recall for one batch lists remaining stock by warehouse and every delivery from that batch within five seconds, blocks the remaining stock, and records the customer notification status.

## 8. Reservations and availability (RSV)

**Purpose:** promise stock to documents without overselling. **Actors:** Planner, Storekeeper, Sales and project users (through contracts).

- **FR-INV-RSV-01** Reservations shall reserve quantities for documents (sales orders, transfers, maintenance work orders, projects, production in later waves) by item, warehouse, location, batch or serial, with expiry and priority.
- **FR-INV-RSV-02** Reservation modes shall be configurable: soft (informational), hard (blocks other use), and automatic at order approval or at release for picking.
- **FR-INV-RSV-03** Available-to-promise shall compute from on hand, reserved, quarantined, in transit, and planned receipts from open purchase orders and production, by warehouse and date, and be callable synchronously by Sales and Supply Chain.
- **FR-INV-RSV-04** Allocation rules shall assign scarce stock by priority (customer class, order date, due date) with manual override and audit.
- **FR-INV-RSV-05** Releasing a reservation (document cancelled, quantity changed, expiry) shall return stock to availability and notify affected users.
- **FR-INV-RSV-06** Back-to-back and cross-dock flows (receipt directly allocated to a pending demand) shall be supported. (S)
- **FR-INV-RSV-07** A reservation report shall show demand, reserved, uncovered and shortage items with suggested actions.
- **AC:** two sales orders compete for the last 30 units; priority rules reserve for the earlier customer, the second order shows a shortage with an expected date from the open purchase order.

## 9. Stock counts (CNT)

**Purpose:** keep records equal to physical stock with minimal disruption. **Actors:** Warehouse Manager, Storekeeper, Inventory Controller.

- **FR-INV-CNT-01** Count plans shall be full (annual), cyclic by ABC class, by location or zone, spot counts, or event-driven (after negative stock, after discrepancy), with schedules and assignments.
- **FR-INV-CNT-02** Starting a count shall snapshot book quantities by item, location and batch; optionally freeze movements for counted locations or allow movements with later adjustment of the snapshot.
- **FR-INV-CNT-03** Count sheets shall be printable and mobile counting shall support barcode scans, blind counting (book quantity hidden), team assignment and offline entry.
- **FR-INV-CNT-04** Recount rules shall trigger second counts when variances exceed thresholds by quantity or value, with different counters where configured.
- **FR-INV-CNT-05** Variances shall be reviewed in a workspace showing book versus counted quantity and value, causes, movement since the snapshot, and proposed adjustments, with approval by value thresholds.
- **FR-INV-CNT-06** Approval shall generate stock adjustment documents with the reason count variance, linked to the count, posting through the standard accounting events.
- **FR-INV-CNT-07** Count accuracy metrics by location, counter and item class shall be reported with trends, and used to adjust cycle count frequency.
- **FR-INV-CNT-08** Counts of consignment and third-party held stock shall be supported with confirmation documents. (S)
- **BR:** counts cannot be posted without approval above the variance threshold; movements during a counting window are reconciled to the snapshot so adjustments are correct.
- **AC:** a cyclic count of class A items on 200 locations is done on mobile with blind counts, ten variances trigger recounts, approved adjustments post, and accuracy for the zone is reported.

## 10. Replenishment (RPL)

**Purpose:** keep the right stock levels with simple, transparent rules. Advanced demand planning belongs to Supply Chain (document 10). **Actors:** Planner, Warehouse Manager.

- **FR-INV-RPL-01** Replenishment policies shall include reorder point with order quantity, min-max, periodic review, fixed order multiples, and safety stock, by item and warehouse.
- **FR-INV-RPL-02** The system shall calculate suggestions from on hand, reserved, in transit, on order and forecast demand inputs, producing purchase requisition proposals, transfer proposals between warehouses, or production proposals in later waves.
- **FR-INV-RPL-03** Suggestions shall be reviewable in a worklist with explanations, edit, approve, and bulk conversion into requisitions or transfers, with source determination from Procurement.
- **FR-INV-RPL-04** ABC and XYZ classification shall be calculated from consumption value and variability on a schedule, with manual override, and used for count frequency and policy defaults; when Supply Chain is licensed, its segmentation supersedes this classification and writes the class back (decision D-13).
- **FR-INV-RPL-05** Lead time and demand statistics shall be derived from history (receipt delays, consumption) and suggested for parameter updates with approval.
- **FR-INV-RPL-06** Stock-out and excess alerts shall be configurable with thresholds and notifications.
- **FR-INV-RPL-07** Automatic requisition creation shall be possible per item group, with approval workflows determined by Procurement.
- **AC:** items below reorder point generate a worklist; the planner converts selected lines to requisitions with suggested suppliers; transfers from an overstocked warehouse are proposed first where configured.

## 11. Quality inspection (QLT)

**Purpose:** control the quality of goods entering and leaving stock. **Actors:** Quality Inspector, Warehouse Manager, Buyer.

- **FR-INV-QLT-01** Inspection plans shall be defined by item, group, supplier or movement type with characteristics (measurements, visual checks), specifications and tolerances, sampling rules (all, percentage, AQL tables), and required evidence.
- **FR-INV-QLT-02** Receipts flagged for inspection shall create inspection lots and place stock in quarantine until a decision.
- **FR-INV-QLT-03** Results entry shall support numeric, pass or fail, and attachment values on web and mobile, with automatic evaluation against tolerances.
- **FR-INV-QLT-04** Decisions shall be accept, reject, partially accept, use as is with approval, return to supplier, or rework; stock status and Procurement documents update automatically.
- **FR-INV-QLT-05** Non-conformance reports shall record defects, root cause, corrective actions, owners and due dates, linked to batches and suppliers, feeding supplier scorecards in Procurement.
- **FR-INV-QLT-06** Periodic re-inspection of stored items (shelf-life checks, calibration of reference items) shall be schedulable.
- **FR-INV-QLT-07** Certificates and test reports shall be attached and visible in traceability reports.
- **AC:** a receipt of a controlled item is quarantined, inspected per the plan with ten samples, results fall within tolerance for eight lots and fail for two, and stock status and the order record reflect the split decision.

## 12. Warehouse operations (OPS)

**Purpose:** guide people through physical work with mobile devices. Basic operations are in wave B; advanced warehouse management (wave planning, slotting optimization, automation) is a later wave. **Actors:** Storekeeper, Warehouse Manager.

- **FR-INV-OPS-01** Receiving tasks shall guide unloading against expected receipts, scan items, capture quantities, batches and serials, print labels and propose putaway locations.
- **FR-INV-OPS-02** Putaway tasks shall be generated from receipts using putaway rules, with confirmation by scanning the location, and exception handling when a location is full.
- **FR-INV-OPS-03** Pick lists shall be generated from approved issues, transfers and deliveries, sorted by location path, grouped by order or wave (basic), and confirmed by scanning item and location.
- **FR-INV-OPS-04** Packing shall support packing lists, handling units and weight and volume capture, with labels and carrier information for dispatch. (S)
- **FR-INV-OPS-05** Dispatch shall confirm shipment, generate delivery documents and gate passes, and publish events to Sales and Finance through the owning documents.
- **FR-INV-OPS-06** Mobile devices (handheld scanners, phones, tablets) shall run an offline-capable warehouse app with barcode, QR and optional RFID scanning, with task lists, exception reasons and sync.
- **FR-INV-OPS-07** Task assignment shall support queues by zone and skill, priorities, and monitoring of task age and productivity. (S)
- **FR-INV-OPS-08** Warehouse dashboards shall show inbound and outbound workload, dock schedule, picking progress, task aging and exceptions.
- **AC:** a receipt of 20 pallets is scanned on a handheld offline, putaway tasks route each pallet to a bin, and a pick list for an order guides the storekeeper through the shortest bin path with scan confirmation.

## 13. Inventory reporting (IRP)

Inventory reporting uses the kernel semantic layer and builders (document 9); this section defines standard content.

- **FR-INV-IRP-01** Stock reports shall show on hand, reserved, available, in transit and on order by item, warehouse, location, batch and serial, as of any date, with drill-down to the stock ledger.
- **FR-INV-IRP-02** The stock ledger report shall list movements with filters by item, warehouse, type, document, batch and user, with running quantity and value.
- **FR-INV-IRP-03** Valuation, ageing, slow-moving and non-moving, obsolete stock, expiry and near-expiry reports shall use configurable buckets and thresholds.
- **FR-INV-IRP-04** Analytic reports shall include ABC and XYZ analysis, turnover and days of stock, stock-out history, service level, count variance, adjustment analysis, shrinkage by cause, and supplier lead-time performance.
- **FR-INV-IRP-05** Reconciliation reports shall compare inventory valuation to Finance control accounts and goods-received-not-invoiced to Procurement receipts.
- **FR-INV-IRP-06** All standard reports shall be cloneable through the report builder, scheduled and exported, and shall respect warehouse data scopes and cost-visibility permissions (users without cost permission see quantities only).
- **FR-INV-IRP-07** Dashboards shall present inventory value, turnover, stock-outs, expiring stock, count accuracy and open tasks per role.

## 14. Asset Management overview

### 14.1 Capability map

| Area | Summary |
| --- | --- |
| Register and classes | Asset master, classes, tags, hierarchy, location, custodian |
| Acquisition and capitalization | From purchases, manual entry, construction in progress, transfers in |
| Depreciation | Multiple books, methods and conventions, runs, estimate changes |
| Transfers and adjustments | Location, cost center, custodian, company, reclassification, split, merge, revaluation, impairment |
| Disposal | Sale, scrap, donation, loss, write-off with gain or loss per book |
| Physical verification | Audit plans, scanning, reconciliation of missing and found assets |
| Maintenance | Preventive and corrective maintenance, work orders, spares, cost capture |
| Insurance, warranty and compliance | Policies, claims, warranties, permits and certificates |
| Custody | Assignment to employees and units, acknowledgements, return |
| Reporting | Registers, roll-forward, depreciation schedules, forecasts, maintenance cost |

### 14.2 Lifecycle and statuses

```
Request -> Purchase or construction -> CWIP (optional) -> Capitalize (in service) -> Use and maintain
        -> Transfer or adjust -> Impair or revalue (optional) -> Dispose or retire
```

| Document or record | Key statuses |
| --- | --- |
| Asset | planned, CWIP, active, under maintenance, idle, held for sale, disposed, lost, written off |
| Acquisition | draft, approved, capitalized, cancelled |
| Depreciation run | draft, simulated, approved, posted, reversed |
| Transfer | draft, approved, completed |
| Disposal | draft, approved, completed |
| Verification | planned, in progress, reconciled, approved |
| Work order | requested, approved, planned, in progress, completed, closed |

### 14.3 Principles

1. The register is the single source of truth for each asset's cost, depreciation and status; Finance holds the control accounts and receives postings through events.
2. Depreciation is calculated per book; each book maps to a Finance ledger, so IFRS, local and tax results are kept in parallel.
3. Posted asset transactions are immutable; corrections are reversals, adjustments and new transactions with full history.
4. Maintenance is basic but connected: costs flow to the asset and to Finance without a separate enterprise asset management product.
5. Everything works without Finance and without Inventory: calculations and registers remain available and results can be exported.

## 15. Asset master and classes (AMS)

**Purpose:** a complete, reliable register of assets with the right defaults. **Actors:** Asset Manager, Financial Controller, Custodian.

- **FR-AST-AMS-01** Asset classes shall be configurable (land, buildings, plant and machinery, vehicles, IT equipment, furniture, leasehold improvements, intangible assets, low-value assets) with default depreciation books, methods, lives, salvage rules, accounts (by reference to Finance mappings), tax class, numbering pattern, and capitalization threshold.
- **FR-AST-AMS-02** Each asset shall have a unique asset number generated through kernel numbering, plus tag number, description in multiple languages, class, serial number, manufacturer and model, supplier, purchase reference, acquisition and in-service dates, cost, location, cost center, custodian, status, photos, documents and custom fields.
- **FR-AST-AMS-03** Tags shall be printable as barcode, QR code or RFID labels from templates, and scanning shall open the asset on web and mobile.
- **FR-AST-AMS-04** Assets shall form hierarchies (parent and components) with options to depreciate components separately or with the parent, and shall support multiple units under one record (quantity greater than one) or individual records per unit by configuration.
- **FR-AST-AMS-05** A location hierarchy (site, building, floor, room) for assets shall be maintained, linked to kernel locations, with a map or floor reference where available. (S)
- **FR-AST-AMS-06** Assets shall belong to a company and legal entity with ownership tracked on inter-company transfers.
- **FR-AST-AMS-07** Asset identifiers from legacy registers shall be preserved as alternate numbers searchable in all screens.
- **FR-AST-AMS-08** Opening register import shall load cost, accumulated depreciation by book, in-service date, remaining life and class, with validation and a tie-out to opening balances in Finance before activation.
- **FR-AST-AMS-09** Bulk create, update and tag printing shall be supported through the import center with preview and approval.
- **FR-AST-AMS-10** Serialized stock items issued as assets shall convert to assets from Inventory with serial, cost and warranty data carried over (with Inventory licensed).
- **FR-AST-AMS-11** The asset record shall show a complete history timeline (acquisition, capitalization, transfers, depreciation, maintenance, verification, disposal) from the audit log and documents.
- **BR:** an asset number is never reused; class changes follow the reclassification procedure; assets with posted depreciation cannot be deleted.
- **AC:** a class defines straight-line depreciation over five years in the local book and declining balance in the tax book; creating an asset in the class inherits both books, tag labels print with a QR code, and scanning the tag opens the record.

## 16. Acquisition and capitalization (ACQ)

**Purpose:** bring assets into the register at the right cost and date. **Actors:** Asset Manager, AP Clerk, Buyer, Financial Controller.

- **FR-AST-ACQ-01** Assets shall be created from purchase order or invoice lines flagged as assets, from manual entry, from completed construction in progress, from transfers in, from donations or grants, and from internal development of intangible assets.
- **FR-AST-ACQ-02** An asset-flagged line with quantity greater than one shall create separate assets (one per unit) or one multi-unit asset by class setting, with serial numbers assignable at receipt.
- **FR-AST-ACQ-03** Capitalization thresholds shall be enforced by rules: below the threshold, the purchase is expensed (with optional low-value asset registration for tracking); above, it must be capitalized; overrides require approval.
- **FR-AST-ACQ-04** Cost shall be composed of purchase price, duties, non-recoverable taxes, freight, installation, testing and other directly attributable costs, each recorded as a component with source documents; later additions to an existing asset shall be recorded as cost increases with their own dates and depreciation treatment.
- **FR-AST-ACQ-05** Construction in progress shall accumulate costs from invoices, journals, payroll allocations and stock issues against a project or asset, with budgets and status; capitalization shall move accumulated cost into one or more assets by amount or percentage.
- **FR-AST-ACQ-06** Acquisition date and in-service date shall be separate; depreciation starts from the in-service date according to the convention of each book.
- **FR-AST-ACQ-07** Foreign-currency purchases shall record original currency and company-currency cost at the transaction rate.
- **FR-AST-ACQ-08** Acquisition and capitalization shall follow configurable approval workflows by value and class, with attachments (purchase documents, installation reports).
- **FR-AST-ACQ-09** Capitalization shall publish AssetAcquired with cost components and dimensions; Finance posts the transfer from the clearing or CWIP account to the asset cost account by posting rules.
- **FR-AST-ACQ-10** Government grants and donated assets shall be recordable with deferred income handling by pack rules. (S)
- **FR-AST-ACQ-11** Capital expenditure tracking shall compare capital budgets (Finance) with CWIP and capitalized spend by project. (S)
- **AC:** a vendor invoice for ten laptops creates ten assets with serial numbers from the receipt, each inherits the class books, capitalization posts through Finance, and a laptop below the threshold remains an expense with a low-value tracking record.

## 17. Depreciation (DEP)

**Purpose:** calculate depreciation correctly in parallel books and post it reliably. **Actors:** Asset Manager, Financial Controller.

- **FR-AST-DEP-01** Multiple depreciation books per tenant shall be supported (for example local GAAP, IFRS, tax, management), each mapped to a Finance ledger, with its own currency, fiscal calendar and rules.
- **FR-AST-DEP-02** Depreciation methods shall include straight line, declining balance (with switch to straight line), sum of years digits, units of production (using usage or meter data), custom schedule by period or percentage, annual statutory rates (for tax books), and no depreciation (for land and CWIP).
- **FR-AST-DEP-03** Each asset and book shall hold useful life or rate, salvage value (amount or percentage), method, start convention (daily, monthly, mid-month, mid-year, full-year, half-year) and disposal convention, defaulting from the class and overridable per asset with approval.
- **FR-AST-DEP-04** Component depreciation shall allow major parts of an asset to have separate lives and methods.
- **FR-AST-DEP-05** Depreciation runs shall be executed per company, book and period (or a range) for all or selected assets as resumable jobs, with a simulation mode that shows results without posting.
- **FR-AST-DEP-06** Run results shall list each asset's opening book value, depreciation, accumulated depreciation and closing book value, with exception lists (assets without class books, missing in-service date, negative book value) before approval.
- **FR-AST-DEP-07** Approved runs shall publish DepreciationCalculated per company, book and dimension combination; Finance posts journals per ledger through posting rules; results shall be immutable after posting and corrected by reversal or adjustment.
- **FR-AST-DEP-08** Backdated acquisitions, capitalizations and cost changes shall compute catch-up depreciation for earlier periods in open periods, with the closed-period portion recorded in the current period as configured.
- **FR-AST-DEP-09** Changes in estimate (life, salvage, method) shall apply prospectively from an effective date with approval and a history of changes; error corrections shall be separate, approved adjustments.
- **FR-AST-DEP-10** Depreciation shall be stoppable and resumable per asset (idle, held for sale) with reasons, and shall stop automatically at full depreciation or salvage value.
- **FR-AST-DEP-11** Depreciation forecast reports shall project charges by period, class, book and cost center for budgeting, with what-if changes to lives or additions.
- **FR-AST-DEP-12** Performance: a monthly run for 100,000 assets across three books shall complete in under 15 minutes, chunked and resumable.
- **FR-AST-DEP-13** Lease-related assets (right-of-use) and asset retirement obligations shall be added in a later wave with their own specification. (wave C)
- **BR:** a posted period run cannot be changed; depreciation never exceeds depreciable amount; each book posts only to its mapped ledger; runs in closed periods go to the Finance exception queue.
- **AC:** a month-end run in two books for 5,000 assets shows differing results (straight line versus declining balance), posts to two ledgers, ties to the asset subledger by class, and a life change effective next month adjusts only future periods.

## 18. Transfers, adjustments, revaluation and impairment (TRF)

**Purpose:** keep the register current as assets move and values change. **Actors:** Asset Manager, Custodian, Financial Controller.

- **FR-AST-TRF-01** Transfers shall change location, cost center, custodian, department or company, with effective date, approvals, acknowledgement by the receiving custodian, and a history of holders.
- **FR-AST-TRF-02** Inter-company transfers shall create a disposal-style outgoing transaction and acquisition-style incoming transaction at book value or transfer price, publishing events for intercompany accounting in Finance, and carrying acquisition and depreciation history.
- **FR-AST-TRF-03** Cost-center transfers shall move future depreciation to the new dimension from the effective date without altering past postings.
- **FR-AST-TRF-04** Reclassification between classes shall adjust cost, accumulated depreciation and depreciation parameters per book with approval and accounting through events.
- **FR-AST-TRF-05** Split and merge shall divide one asset into several, or combine assets, allocating cost and accumulated depreciation by amount or percentage with a trail from source to resulting assets.
- **FR-AST-TRF-06** Cost adjustments (positive or negative) shall be separate documents with reason, approval and effect on depreciation prospectively or with catch-up.
- **FR-AST-TRF-07** Revaluation (where the revaluation model is used) shall record fair value by asset or class with valuer reference, updating cost and accumulated depreciation per book, with the surplus or deficit posted through events and reserve accounts defined in Finance.
- **FR-AST-TRF-08** Impairment shall record recoverable amount and loss with reason and evidence, reduce book value, and reverse where permitted, publishing events for Finance.
- **FR-AST-TRF-09** Held-for-sale classification shall stop depreciation and report these assets separately.
- **FR-AST-TRF-10** Bulk transfer and bulk adjustment by selection or import shall support preview and approval.
- **BR:** transfers cannot be backdated into closed periods; every adjustment preserves history; transfers between companies keep the asset's original acquisition date and cumulative depreciation.
- **AC:** moving a vehicle between cost centers effective the first of next month sends its depreciation to the new center from that date; an inter-company transfer creates both company transactions with matching values and the receiving custodian acknowledges receipt.

## 19. Disposal and retirement (DSP)

**Purpose:** remove assets from the register correctly, with gain or loss in every book. **Actors:** Asset Manager, Financial Controller, Custodian.

- **FR-AST-DSP-01** Disposal types shall include sale, scrap, donation, transfer out, trade-in, loss or theft, write-off and expiry of a lease or contract, each with its own workflow, documents and accounting behavior.
- **FR-AST-DSP-02** A disposal request shall state the asset or assets, reason, expected proceeds, buyer or recipient, and attachments; approval shall route by value, class and book value, with evidence required for loss and theft.
- **FR-AST-DSP-03** On completion the system shall run depreciation up to the disposal date per book convention, calculate book value, proceeds and gain or loss per book, and publish AssetDisposed with the components for Finance.
- **FR-AST-DSP-04** Disposals by sale shall create or link a sales invoice (through Sales when licensed, or Finance AR) for the proceeds, and the gain or loss shall use the invoiced amount.
- **FR-AST-DSP-05** Partial disposals (a quantity of a multi-unit asset, or a component) shall be supported with proportional cost and accumulated depreciation.
- **FR-AST-DSP-06** Insurance claims linked to loss or damage shall be tracked with claim amount, status and recovery, and recoveries shall be recorded against the asset's disposal or a gain account.
- **FR-AST-DSP-07** Gate passes and handover records shall be generated from templates for physical removal, with custodian sign-off.
- **FR-AST-DSP-08** Disposed assets shall remain in the register as history with their full record and shall be excluded from active lists and depreciation runs.
- **FR-AST-DSP-09** Disposal reversal shall be possible within the open period under approval, restoring the asset and its depreciation state, with accounting reversal.
- **FR-AST-DSP-10** Bulk disposal (auction lots, retired fleets) shall be supported with allocation of proceeds across assets by book value or fair value.
- **BR:** an asset cannot be disposed before its acquisition or in a closed period; every disposal shows book value, proceeds and gain or loss for each book before approval.
- **AC:** a machine sold mid-month is depreciated to the sale date in each book, a sales invoice records the proceeds, gain differs between the local and tax books, and Finance receives one event with both results.

## 20. Physical verification (VER)

**Purpose:** confirm that recorded assets exist where they should. **Actors:** Asset Manager, Custodian, Auditor.

- **FR-AST-VER-01** Verification plans shall be created by site, location, class, custodian or sample, with scheduled dates, teams and instructions, repeating on cycles.
- **FR-AST-VER-02** Verification shall run on mobile devices with barcode, QR or RFID scanning and manual search, offline-capable, recording found location, condition, photo and comments.
- **FR-AST-VER-03** The system shall compare scanned results to the register and classify assets as found, found elsewhere, not found, damaged, unregistered (found but not in the register), or duplicate tag.
- **FR-AST-VER-04** Discrepancies shall go to a review workspace with actions: correct location or custodian, register a found asset, start a loss investigation, request disposal, or approve write-off, each with approval.
- **FR-AST-VER-05** Custodian confirmation campaigns shall let employees confirm the assets assigned to them through self-service, with reminders and exceptions. (S)
- **FR-AST-VER-06** Verification results shall be retained as evidence for audit with date, user and device, and a verification report shall be produced per plan.
- **FR-AST-VER-07** Accuracy metrics (location accuracy, missing rate) shall be reported by site and class over time.
- **AC:** a verification of a head office floor with 800 assets is done offline on two phones; the review shows 783 found, 9 found elsewhere, 5 missing and 3 unregistered; approved corrections update the register with history.

## 21. Maintenance (MNT)

**Purpose:** keep assets working and capture maintenance cost against them. This is basic maintenance management; full enterprise asset management (work crews, shutdown planning, reliability analytics) is a later wave. **Actors:** Maintenance Planner, Technician, Custodian, Asset Manager.

- **FR-AST-MNT-01** Maintenance requests shall be raised by users (web, mobile, QR scan on the asset) with description, priority, photos and location, and be triaged by planners.
- **FR-AST-MNT-02** Preventive maintenance plans shall be defined per asset or class with triggers by calendar interval, meter reading (hours, kilometers, cycles), or condition, generating work orders ahead of due dates.
- **FR-AST-MNT-03** Work orders shall hold type (corrective, preventive, inspection, calibration, modification), asset, priority, planned dates, assignees (internal or vendor), tasks and checklists, required spares and tools, estimated cost and time, and approval by cost or priority.
- **FR-AST-MNT-04** Spare parts shall be reserved and issued from Inventory against work orders (when Inventory is licensed), and external services shall be ordered through Procurement with the order linked to the work order.
- **FR-AST-MNT-05** Technicians shall record time, tasks completed, readings, findings, parts used and photos on mobile, offline-capable, with sign-off by the requester or planner.
- **FR-AST-MNT-06** Costs (labor, parts, external services) shall accumulate on the work order and the asset, with maintenance cost reports by asset, class, cost center and period; capital improvements discovered during maintenance shall be proposable as cost additions (FR-AST-ACQ-04).
- **FR-AST-MNT-07** Downtime shall be recorded (start, end, reason), and availability, mean time between failures and mean time to repair shall be reported per asset and class.
- **FR-AST-MNT-08** Meter readings (manual, imported, or from connected devices through connectors) shall be recorded and feed maintenance triggers and units-of-production depreciation.
- **FR-AST-MNT-09** Warranty claims shall be raised from work orders when the asset is under warranty, tracking supplier response and recoveries.
- **FR-AST-MNT-10** Vehicle-specific features (fuel entries, mileage, driver assignment, service schedules, registration and insurance renewals) shall be available through asset class templates and custom fields. (S)
- **FR-AST-MNT-11** SLA targets for response and completion shall be configurable by priority and asset criticality, with escalation notifications.
- **FR-AST-MNT-12** Safety and compliance inspections (fire equipment, lifts, pressure vessels) shall be scheduled with certificates stored and expiry reminders.
- **BR:** a work order cannot close without recorded completion information; spare parts issued cannot exceed approved quantities without approval; maintenance on an asset in a disposed status is blocked.
- **AC:** a preventive plan every 5,000 kilometers creates a work order when a meter reading crosses the interval; the technician records parts and time offline; costs accumulate on the asset and the cost report ranks the most expensive assets by cost center.

## 22. Insurance, warranty and compliance (INS)

- **FR-AST-INS-01** Insurance policies shall be recorded with insurer, policy number, coverage, premium, validity, insured assets and values, with renewal reminders and document storage.
- **FR-AST-INS-02** Insured value versus book value and replacement value shall be reported to highlight under-insurance.
- **FR-AST-INS-03** Claims shall be tracked from incident to settlement, linked to assets, work orders and disposals.
- **FR-AST-INS-04** Warranties shall record supplier, terms, start and end dates, and coverage, with alerts before expiry and links to maintenance claims; serial-level warranty data from Inventory shall be visible on assets.
- **FR-AST-INS-05** Licenses, permits, registrations and certificates tied to assets (vehicle registration, calibration, safety inspection) shall have expiry tracking with owner reminders and escalation.
- **FR-AST-INS-06** Lease and rental contract references shall be recordable on assets with period, payment amounts and responsible party; accounting for leases is specified in a later wave. (S)
- **FR-AST-INS-07** Asset-related tax and statutory registers required by country packs (for example fixed-asset schedules for tax filing) shall be generated from the register.

## 23. Custody (CST)

- **FR-AST-CST-01** Assets shall be assigned to employees, departments, locations or projects, with issue date, condition, accessories, and a digital acknowledgement by the custodian.
- **FR-AST-CST-02** Return and reassignment shall record condition and damage, with deductions or recoveries by policy (through HRMIS final settlement when licensed).
- **FR-AST-CST-03** HRMIS shall be able to issue and return assets through the Assets module (documents 6 and 8) and show unreturned assets in offboarding clearance; without HRMIS, custodians are maintained manually or by directory import.
- **FR-AST-CST-04** Employees shall see assets assigned to them in self-service, report faults and request transfers or returns.
- **FR-AST-CST-05** Shared and pooled assets (vehicles, equipment) shall be bookable with calendars, approval, check-out and check-in records. (S)
- **FR-AST-CST-06** Custody history shall be queryable by asset and by person for audit.
- **AC:** issuing a laptop records the custodian's acknowledgement; at resignation, offboarding lists it as outstanding, the return records condition, and the asset becomes available for reassignment.

## 24. Asset reporting (ARP)

Asset reporting uses the kernel semantic layer and builders (document 9); this section defines standard content.

- **FR-AST-ARP-01** The asset register shall be reportable by company, class, location, cost center, custodian, status, acquisition period and custom fields, with filters and export.
- **FR-AST-ARP-02** The asset roll-forward (opening cost, additions, transfers, disposals, closing cost; opening accumulated depreciation, charge, disposals, closing; net book value) shall be produced per book, class and period, and reconcile to Finance control accounts; differences shall block period close when Finance is licensed.
- **FR-AST-ARP-03** Depreciation schedules and forecasts shall be reported per asset, class and book, in summary and detail, with comparison between books.
- **FR-AST-ARP-04** Additions, disposals, transfers, impairments and revaluation reports shall be available by period with drill-down to documents.
- **FR-AST-ARP-05** Utilization, idle assets, age profile, remaining life and replacement planning reports shall support asset strategy.
- **FR-AST-ARP-06** Maintenance reports shall cover cost, backlog, preventive compliance, downtime, and failure analysis.
- **FR-AST-ARP-07** Insurance, warranty and certificate expiry reports shall be scheduled to owners.
- **FR-AST-ARP-08** Dashboards shall present net book value, additions, depreciation charge, verification accuracy, maintenance backlog and expiring documents per role.
- **FR-AST-ARP-09** Reports shall respect data scopes and hide cost and book-value fields from users without financial permission.

## 25. Country notes

These are planning inputs to be verified by qualified local accountants, tax advisers and regulatory specialists before design is frozen.

| Topic | Pakistan | Saudi Arabia | United States |
| --- | --- | --- | --- |
| Stock valuation | Local reporting frameworks aligned with international standards; FIFO and weighted average common | Same; zakat base considers inventory | FIFO, weighted average, and LIFO under US accounting (LIFO is not permitted under international standards); LIFO available only through the US pack |
| Tax depreciation | Statutory classes with declining-balance rates and possible initial allowances, held in a tax book | Statutory groups with declining-balance rates in a tax book | MACRS classes and accelerated expensing options in a tax book, with state differences |
| Fixed-asset reporting | Fixed-asset schedules for tax and statutory accounts | Schedules for zakat or corporate tax and statutory accounts | Form-supporting schedules for tax filing |
| Documents | Delivery challans, gate passes and stock transfer notes in Urdu and English | Bilingual delivery and transfer documents, Arabic labels | English, optional Spanish |
| Traceability | Batch and expiry requirements for regulated goods as scoped by sector | Product batch, expiry and barcode requirements for regulated goods, to be scoped by sector | Traceability rules for food and pharmaceuticals as scoped by sector |
| Stock and tax | Input tax adjustments on stock losses and write-offs per local rules | VAT adjustments on losses and write-offs per rules | Sales and use tax on transfers and inter-state movements |

Pack responsibilities: tax depreciation tables and conventions, fixed-asset schedule formats, document and label templates, regulatory traceability fields, valuation options by jurisdiction, and starter classes and reason codes.

## 26. Events and contracts

### 26.1 Events published

| Event | Publisher | Consumers |
| --- | --- | --- |
| GoodsReceiptPosted | Inventory | Finance (stock and goods-received-not-invoiced), Procurement (status), Supply Chain |
| GoodsIssuePosted | Inventory | Finance (consumption or cost of goods sold), Sales, Assets (spares for work orders) |
| StockAdjustmentPosted, StockRevaluationPosted | Inventory | Finance |
| StockTransferShipped, StockTransferReceived | Inventory | Finance (inter-company), Supply Chain |
| StockBelowReorder, ReplenishmentProposed, StockAvailabilityChanged (throttled), CustomerReturnReceived | Inventory | Procurement (requisitions), Sales and Supply Chain (availability and returns), notifications |
| BatchBlocked, RecallRaised | Inventory | Sales, Procurement, notifications |
| AssetAcquired | Assets | Finance (cost postings) |
| DepreciationCalculated | Assets | Finance (journals per ledger) |
| AssetTransferred, AssetDisposed, AssetRevalued, AssetImpaired, AssetStatusChanged | Assets | Finance, HRMIS (custody changes), Supply Chain (fleet availability), notifications |
| AssetIssued, AssetReturned | Assets | HRMIS (custody and offboarding), notifications |
| MaintenanceWorkOrderCompleted | Assets | Finance (internal cost allocation when configured), Procurement (service acceptance) |

### 26.2 Events consumed

| Event | Source | Effect |
| --- | --- | --- |
| PurchaseOrderIssued, Revised, Cancelled | Procurement | Expected receipts and on-order quantities updated |
| ReceiptPosted, GoodsReturnRecorded | Procurement | Stock movements are created by the ReceiveGoods contract call (decision D-15); the event is informational for expected receipts and returns |
| SupplierInvoiceApproved | Procurement | Invoice cost corrections to receipts; asset-flagged lines create draft acquisitions (also from VendorInvoicePosted for invoices keyed in Finance AP) |
| ReturnAuthorized | Sales | Expected customer returns; stock issues and receipts happen through the IssueGoods and ReceiveReturn contracts (decisions D-15 and D-17) |
| PeriodClosed, PeriodReopened | Finance | Posting date locks for stock and asset transactions |
| EmployeeChanged, EmployeeTerminated | HRMIS | Custodian updates and clearance tasks |
| ReplenishmentProposed | Supply Chain | Replenishment worklist entries; expected receipts (AsnReceived) and supplier forecast views (SupplierForecastShared) |

### 26.3 Synchronous contracts

| Contract | Provider | Consumers | Behavior without provider |
| --- | --- | --- | --- |
| Get availability and promise date | Inventory | Sales, Supply Chain | Not shown or always allowed |
| Receive goods command | Inventory | Procurement | Receipt confirms quantities only |
| Issue goods command | Inventory | Sales, Assets (spares), HRMIS (issues) | Issue documents handled in the owning module without stock effect |
| Get stock position | Inventory | Procurement, Supply Chain | Not shown |
| Create acquisition draft | Assets | Procurement, Finance | Asset flag informational only |
| Get custody list, issue and return asset | Assets | HRMIS | Lightweight HR register is used |
| Get exchange rate | Kernel | All | Kernel service answers |

Additional contract (document 14, decision D-13): UpdatePlanningParameters, a command provided by Inventory and consumed by Supply Chain, writes approved reorder point, minimum and maximum, safety stock, lead time and classification values into the Inventory parameters. Without Supply Chain the parameters are maintained only in Inventory. Receiving, issuing and returns use the ReceiveGoods, IssueGoods and ReceiveReturn commands, and Inventory publishes its events after success (decisions D-15 and D-17).

## 27. Roles, permissions and default workflows

### 27.1 Baseline roles

| Role | Main permissions |
| --- | --- |
| Storekeeper | Post receipts, issues, transfers and counts for assigned warehouses |
| Warehouse Manager | Approve adjustments and counts, manage locations and rules |
| Inventory Controller | Manage valuation, costing, revaluation and reconciliation |
| Planner | Manage reorder parameters, replenishment and classification |
| Quality Inspector | Record inspection results and non-conformances |
| Asset Manager | Manage register, acquisitions, depreciation runs, transfers, disposals, verification |
| Maintenance Planner | Manage plans, requests and work orders |
| Technician | Execute work orders and record results |
| Custodian | Acknowledge and return assigned assets, request maintenance |
| Financial Controller | Approve valuation changes, depreciation postings, disposals, revaluation and impairment |
| Auditor | Read-only with drill-down; cost and book-value fields by approval |

### 27.2 Default workflows (all editable)

| Workflow | Default route |
| --- | --- |
| Stock adjustment | Storekeeper submits; warehouse manager above threshold; controller above higher threshold |
| Stock count variance | Counter, supervisor, warehouse manager by value |
| Inter-company transfer | Both warehouse managers, finance for transfer price |
| Revaluation and valuation method change | Inventory controller, financial controller |
| Asset acquisition and capitalization | Asset manager, financial controller by value |
| Depreciation run | Asset manager prepares, controller approves posting |
| Asset disposal | Custodian manager, asset manager, controller by book value and proceeds |
| Impairment and revaluation | Asset manager, controller, executive by value |
| Verification discrepancy | Asset manager, controller for write-offs |
| Maintenance work order | Planner, cost approver above threshold |

## 28. Data model overview

Tables live in the inv and ast schemas (kernel references as in document 3). The stock ledger, depreciation results and posted asset transactions are append-only.

| Area | Main tables |
| --- | --- |
| Inventory master | item\_stock\_attributes, item\_warehouse\_params, costing\_groups, item\_kits, kit\_components |
| Warehouses | warehouses, locations, location\_rules, putaway\_rules, picking\_rules |
| Stock transactions | stock\_documents, stock\_document\_lines, stock\_movements (partitioned, append-only), stock\_balances, in\_transit\_balances, stock\_status\_changes |
| Valuation | cost\_layers, average\_cost\_state, standard\_costs, revaluation\_documents, landed\_cost\_documents, landed\_cost\_allocations, valuation\_snapshots |
| Tracking | batches, serial\_numbers, serial\_history, recalls, recall\_items |
| Reservations | reservations, allocations, availability\_cache |
| Counts | count\_plans, count\_sessions, count\_lines, count\_variances |
| Replenishment | reorder\_policies, replenishment\_suggestions, abc\_classifications |
| Quality | inspection\_plans, inspection\_lots, inspection\_results, nonconformance\_reports |
| Operations | pick\_lists, pick\_tasks, putaway\_tasks, handling\_units, dispatches |
| Asset register | asset\_classes, class\_books, assets, asset\_components, asset\_books, asset\_cost\_components, asset\_locations, asset\_tags |
| Asset transactions | acquisitions, cwip\_projects, cwip\_costs, capitalizations, transfers, adjustments, revaluations, impairments, disposals |
| Depreciation | depreciation\_books, depreciation\_runs, depreciation\_results (append-only), estimate\_changes, depreciation\_forecasts |
| Verification | verification\_plans, verification\_sessions, verification\_lines, discrepancies |
| Maintenance | maintenance\_plans, maintenance\_requests, work\_orders, work\_order\_tasks, work\_order\_parts, work\_order\_labor, meter\_readings, downtime\_records |
| Insurance and custody | insurance\_policies, insured\_assets, claims, warranties, certificates, custody\_assignments, asset\_bookings |
| Outbox and inbox | outbox\_messages, inbox\_messages in each schema |

Key design points: stock\_movements is partitioned by movement date and carries value; balances are maintained in the posting transaction; cost layers reconcile to movements; depreciation results are stored per asset, book and period; asset\_books carry the per-book parameters; document links join receipts, issues, assets, work orders and invoices without cross-module foreign keys.

## 29. AI skills

All skills run through the AI Gateway with the user's permissions, propose and never decide, and are switchable per tenant (kernel document 2, section 22).

| Skill | Description | Module | Wave |
| --- | --- | --- | --- |
| Reorder and demand suggestions | Propose parameters and quantities from history, seasonality and lead times | Inventory | B to C |
| Adjustment and shrinkage anomaly detection | Flag unusual adjustments, locations, users and patterns | Inventory | B |
| Count planning | Suggest count frequency and locations from variance history | Inventory | B |
| Expiry risk | Highlight batches at risk and suggest actions | Inventory | B |
| Delivery note extraction | Read supplier delivery notes and prefill receipts | Inventory | B |
| Natural-language stock questions | Query stock, batches and movements within scope | Inventory | B |
| Asset data cleanup | Classify assets and suggest class and life from descriptions and invoices | Assets | B |
| Verification assistant | Suggest matches for unregistered or misplaced assets | Assets | B |
| Photo-based asset capture | Propose an asset record from a photo and tag scan | Assets | C |
| Predictive maintenance | Estimate failure risk from meter and history data | Assets | C |
| Disposal value estimate | Advisory estimate from history and condition | Assets | C |

AI shall not post stock or asset transactions, approve adjustments, change valuations or classify assets without human confirmation.

## 30. Non-functional requirements

| Area | Requirement |
| --- | --- |
| Inventory volume | At least 20 million stock movements per year and 1 million items per tenant |
| Inventory performance | Posting a document with 100 lines in under 2 seconds at the 95th percentile; availability query in under 300 milliseconds; cost recalculation of 1 million movements in under 30 minutes as a resumable job |
| Concurrency | At least 1,000 concurrent scanning users per tenant without lost updates; stock availability guaranteed by row locks only where required |
| Asset volume and performance | At least 500,000 assets per tenant; monthly depreciation for 100,000 assets across three books in under 15 minutes |
| Mobile | Offline-capable receiving, counting, verification and work orders with conflict handling and sync |
| Integrity | The stock ledger equals balances; cost layers equal ledger values; asset subledger ties to Finance control accounts; invariants tested on each build |
| Auditability | Complete history of movements, adjustments, valuations, depreciation and asset changes |
| Localization | English, Urdu and Arabic screens, documents and labels; right-to-left layouts |
| Configurability | Movement types, status models, rules, labels, approvals and reports configurable without code |

## 31. Acceptance and testing

- **Inventory scenarios:** receipt with inspection, putaway, pick and issue with FEFO; two-step transfer with transit loss; count with recount and approved adjustment; recall trace by batch; backdated receipt with cost recalculation; negative stock and later correction.
- **Valuation golden datasets:** independent calculations for FIFO, weighted average, standard cost and specific identification, including landed cost, invoice correction and revaluation, reviewed by an accountant.
- **Asset scenarios:** purchase with asset flag through capitalization, depreciation in two books with different methods, estimate change, cost-center transfer, inter-company transfer, impairment, disposal by sale, roll-forward reconciled to Finance.
- **Configuration variants:** run flows with Finance only, with Procurement only, with neither, and with all modules (graceful degradation per document 2, GR-12).
- **Reconciliation invariants:** inventory valuation equals Finance inventory accounts; asset roll-forward equals control accounts; stock on hand equals sum of movements by item and location; no negative stock where blocked.
- **Mobile and offline tests:** receiving, counting and verification on target devices with network loss and recovery.
- **Performance:** volume and concurrency tests at 1.5 times expected load.
- **Pilot exit criteria:** one full month of stock transactions and a depreciation cycle completed by the pilot customer, a physical count reconciled, zero open severity-1 and severity-2 defects, and Finance reconciliations clean.

## 32. Assumptions and open items

**Assumptions:** Inventory owns stock movements and valuation; Procurement owns receiving and matching documents; Finance owns accounting through posting rules; Assets owns the register and depreciation calculations while Finance owns the ledgers; country rules arrive as packs.

**Open decisions:** scope and timing of manufacturing, multi-level bills of materials and work orders (currently kits only); depth of warehouse management for wave C; RFID and sensor integrations; lease accounting scope and timing; whether quality management becomes its own module; depth of enterprise asset management beyond basic maintenance; component accounting defaults by class.

**Next documents:** 10 Supply Chain Functional Specification, 11 Sales and Distribution, 9 Analytics, Reporting and AI, and 13 UX and Design System Principles.

## 33. Baseline 1.0 addendum (this document is now version 0.2)

*Added 14 November 2026 by the comprehensive review (Doc 16). Sections 1 to 32 are unchanged. The previous state (version 0.1) is kept in this document's version history and in the frozen originals archive. Items tied to a proposed decision take effect on its approval.*

### 33.1 Binding cross-module rules

Doc 16 section 6 applies to both modules. Where a requirement in sections 3 to 24 is looser than an FR-XM rule, the FR-XM rule applies.

| Rule | Effect | Sections touched |
| --- | --- | --- |
| FR-XM-01, 03 | Stock movements with value and asset transactions post atomically through the Finance posting contract (proposed D-31); the wording "through events" in sections 2 and 14 then describes deferred mode only | 5, 6, 14, 26 |
| FR-XM-05, 08 | A posted movement is never edited; correction is a reversing movement at the original cost; a movement cannot be reversed while later documents depend on it | 5 |
| FR-XM-12, 16 | Supplier returns leave stock at original receipt cost; late landed cost is split between stock on hand and stock already issued | 5, 6 |
| FR-XM-17 | Backdated movements are allowed only in an open inventory period and trigger cost recalculation of later movements, posted as a dated adjustment | 6 |
| FR-XM-18 | Negative stock is refused by default; where MOV-04 allows it, issues use last known cost and are corrected when the covering receipt posts | 5, 6 |
| FR-XM-19 | Transfers within a legal entity pass through an in-transit location with no profit; transfers between legal entities are an intercompany sale and purchase | 5 |
| FR-XM-20, 21 | Reservations are released only explicitly, by expiry, cancellation or short-close; count differences above a threshold need approval before posting | 8, 9 |
| FR-XM-23 | Customer returns come back at original issue cost | 5 |
| FR-XM-24, 25 | Receipt of a capital item does not start depreciation; capitalization is an explicit step; depreciation runs once per asset per period; changes apply prospectively unless a catch-up in the current open period is chosen | 16, 17, 18 |
| FR-XM-29 | Custody (section 23) reports unreturned assets to HRMIS final settlement | 23 |
| Section 6.8 | Inventory closes first in the period close order; Asset Management closes after payroll | 6, 17 |

### 33.2 Data notes (corrections, apply now)

- **C-40** Stock on hand is maintained by appending quantity and value deltas that a job rolls into balances; no movement transaction locks a single on-hand row for a hot item. Availability checks that must be exact (reservation, negative-stock refusal) take a lock on the item-warehouse key only.
- **C-41** Kernel item `default_cost` and `default_price` are informational. Valuation reads only Inventory cost records per company, warehouse or batch according to the costing method.
- **C-39** Movements and asset transactions carry `branch_id`; warehouses belong to a branch.
- New invariant: the stock ledger value equals the inventory control accounts for each company and period (Doc 3 section 14.6).

### 33.3 Phase and size profile

Both modules are built in Phase 3 after one shared data design gate with Procurement and Sales (proposed D-32, D-35). Phase 3 Inventory scope: ITM, WHS, MOV, VAL (weighted average and FIFO), TRK, RSV, CNT, RPL basic, IRP. Phase 3 Asset scope: AMS, ACQ, DEP, TRF, DSP, VER, CST, ARP. Phase 4: QLT, OPS advanced warehousing, standard costing, MNT, INS, revaluation and impairment.

The Micro size profile shows one warehouse, no bins, weighted average cost and no batches; the warehouse, default location and costing records exist and can be extended without conversion.
