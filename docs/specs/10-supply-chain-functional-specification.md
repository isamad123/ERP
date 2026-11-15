# Supply Chain: Functional Specification (v0.2)

Document 10 of the planning set | Prepared for Ibrahim | 10 November 2026 | Companion to the Platform Kernel, Data Architecture, Finance, Procurement and Inventory and Asset Management specifications

## 1. Introduction

### 1.1 Purpose and scope

This document specifies the Supply Chain module: demand planning, supply planning (net requirements and distribution), sales and operations planning, inventory optimization, order promising and fulfillment orchestration, transportation and logistics, advanced warehouse management, supplier collaboration, returns and reverse logistics, the supply chain control tower, trade and customs references, and supply chain analytics.

Supply Chain is built in wave C. It sits on top of data from Inventory, Procurement and Sales and is specified now so that those modules publish the events, contracts and data the planning and execution capabilities will need. Simple replenishment (reorder point and min-max) is already delivered by Inventory in wave B; this module adds forecast-driven, network-wide planning and logistics execution.

### 1.2 Dependencies (decision D-10)

| Module | Relationship |
| --- | --- |
| Kernel | Hard dependency: organization, catalog, parties, calendars, tax, localization, workflow, rules, jobs, documents, notifications, reporting, AI Gateway |
| Inventory | Optional integration and main stock data source: on hand, reserved, in transit, on order, movements |
| Procurement | Optional integration: purchase orders, supplier lead times, requisition creation from plans, supplier portal |
| Sales | Optional integration and main demand source: orders, shipments, returns, customers, promotions |
| Finance | Optional integration: financial view in sales and operations planning, freight accruals and invoices |
| Assets | Optional integration: own-fleet vehicles |
| HRMIS | Optional integration: drivers and warehouse labor |

A tenant can use planning without Inventory, Procurement or Sales by loading stock, demand and supply data through import templates and connectors (kernel INT). Each integration degrades gracefully when the other module is not licensed (document 2, GR-12).

### 1.3 Conventions

- Requirement IDs: FR-SCM-\<area>-\<number>. Priority (M), (S), (C); unmarked is (M). Wave C unless stated.
- Each area lists Functional Requirements, Business Rules (BR) and Acceptance Criteria (AC).
- Every configuration is reachable through the admin console and the API (documents 2 and 4).
- Planning calculations run as chunked, resumable jobs with traceable results.

### 1.4 Actors

| Actor | Role |
| --- | --- |
| Demand Planner | Builds and maintains forecasts |
| Supply Planner | Runs planning, reviews exceptions, releases orders |
| S&OP Lead | Runs the sales and operations planning cycle |
| Logistics Planner | Plans shipments and loads |
| Transport Coordinator | Dispatches, tracks and settles shipments |
| Warehouse Manager | Runs warehouse operations and labor |
| Customer Service Representative | Promises and expedites orders |
| Supply Chain Manager | Owns KPIs and policies |
| Supplier and Carrier (portal) | Collaborate on forecasts, orders, shipments and proof of delivery |
| Executive | Approves plans and reviews control tower views |
| Auditor | Read-only access with drill-down |

### 1.5 Key terms

| Term | Meaning |
| --- | --- |
| Planning item | An item at a location with planning parameters |
| Planning bucket | A time period for planning (day, week, month) |
| Time fence | A horizon within which plans are frozen or restricted |
| Net requirement | Demand not covered by existing and planned supply |
| DRP | Distribution requirements planning across a network of locations |
| S&OP | Sales and operations planning: the cross-functional planning cycle |
| ATP and CTP | Available to promise and capable to promise |
| OTIF | On time and in full delivery performance |
| VMI | Vendor-managed inventory |
| POD | Proof of delivery |
| Control tower | An end-to-end view with exception management |

## 2. Module overview

### 2.1 Capability map

| Area | Summary |
| --- | --- |
| Planning master data | Planning parameters, network nodes and lanes, sourcing rules, planning calendars and versions |
| Demand planning | History, statistical forecasting, consensus, accuracy |
| Supply planning | Net requirements, planned orders, lot sizing, safety stock, exceptions, pegging |
| Sales and operations planning | Cycle, scenarios, financial view, decisions |
| Inventory optimization | Segmentation, safety stock and reorder optimization, excess and obsolete handling, rebalancing |
| Order promising and fulfillment | Sourcing rules, ATP and CTP, allocation, backorders |
| Transportation and logistics | Carriers, rates, shipment planning, tracking, proof of delivery, freight settlement, own fleet |
| Advanced warehouse management | Waves, slotting, cross-docking, labor, dock and yard, automation integration |
| Supplier collaboration | Forecast sharing, confirmations, advance shipping notices, VMI |
| Returns and reverse logistics | Authorization, disposition, reverse shipments |
| Control tower | End-to-end visibility, KPIs, exceptions, risk alerts |
| Trade and customs | References, documents, duty and compliance hooks |
| Analytics | Forecast accuracy, plan adherence, service levels, transport and supplier KPIs |

### 2.2 Planning process

```
History and signals -> Demand plan (statistical forecast, consensus)
   -> S&OP (demand review, supply review, scenarios, decision)
   -> Supply plan (net requirements, lot sizing, safety stock, sourcing)
   -> Planner review of exceptions -> Release: requisitions, transfers, (production later)
   -> Execution: purchase orders, receipts, transfers, shipments, deliveries
   -> Measure: accuracy, service level, plan adherence -> back to planning
```

### 2.3 Principles

1. Plans are explainable: every planned order traces to the demand and supply that caused it (pegging) and to the parameters used.
2. Plans never post transactions by themselves; planners release them, and releases create ordinary documents in Procurement or Inventory through contracts.
3. Scenarios are first-class: a planner can run what-if versions without touching the operational plan.
4. Simple first: rules and statistics are transparent; optimization and machine learning are added where they beat the rules measurably and stay explainable.
5. Data quality is visible: missing lead times, outliers and gaps are reported before planning runs.

## 3. Planning master data (PMD)

**Purpose:** describe the supply network and the parameters planning needs. **Actors:** Supply Planner, Supply Chain Manager.

- **FR-SCM-PMD-01** Planning items shall extend kernel and Inventory items per location with parameters: planning method (forecast driven, order driven, reorder point, manual), lead time (internal, supplier, transfer), lot-sizing rule (lot for lot, fixed quantity, minimum and multiple, period batching, economic order quantity), safety stock policy (fixed, days of cover, service level), planning calendar, time fences, shelf-life constraints, and planner assignment. Where a parameter also exists in Inventory (reorder point, minimum and maximum, safety stock, lead time), Inventory stores the value and this module writes approved changes back through the UpdatePlanningParameters contract (decision D-13).
- **FR-SCM-PMD-02** The network shall model nodes (warehouses, suppliers, customers, plants in later waves, cross-docks, third-party sites) and lanes between them with transport mode, lead time, cost, capacity, and calendar.
- **FR-SCM-PMD-03** Sourcing rules shall define for each item and destination the allowed sources (suppliers, internal warehouses), priority, split percentages (quotas), validity dates, minimum and multiple quantities, and constraints; Procurement contracts and price lists shall inform source preference.
- **FR-SCM-PMD-04** Planning calendars shall define working days, holidays and planning buckets (day, week, month, custom) per location, consistent with kernel calendars.
- **FR-SCM-PMD-05** Planning horizons shall be configurable by item class (frozen, slushy, liquid periods) with the actions allowed in each.
- **FR-SCM-PMD-06** Planning versions and scenarios shall copy master data and assumptions for what-if analysis, with comparison and promotion of a scenario to the operational plan after approval.
- **FR-SCM-PMD-07** Data quality checks shall report missing or inconsistent parameters (no lead time, no source, zero lot size, negative safety stock, inactive source), with counts by planner and a fix worklist.
- **FR-SCM-PMD-08** Parameter mass maintenance and import with validation, preview and history shall be supported.
- **FR-SCM-PMD-09** Supplier lead-time and demand variability statistics derived from history shall be proposed for parameter updates with approval.
- **AC:** a planner imports 5,000 planning items, sees 120 flagged for missing lead time, fixes them from the worklist, and a scenario copy of the network is created for a what-if run.

## 4. Demand planning (DEM)

**Purpose:** produce a reliable, agreed forecast. **Actors:** Demand Planner, Sales Manager, S&OP Lead.

### 4.1 History and data preparation

- **FR-SCM-DEM-01** Demand history shall be loaded from Sales shipments and orders, Inventory issues, point-of-sale feeds or imports, by item, location, customer group, channel and period, with units and value.
- **FR-SCM-DEM-02** Cleansing shall detect and treat outliers, stock-out periods, one-off orders and returns, with rules and manual overrides recorded with reasons; both original and cleansed history shall be kept.
- **FR-SCM-DEM-03** Hierarchies (product family, category, brand; region, warehouse; customer group, channel) shall be configurable for aggregation and disaggregation of forecasts.
- **FR-SCM-DEM-04** Master data changes (item replaced by another, split or merged items, location closures) shall be handled by history mapping rules.

### 4.2 Forecasting

- **FR-SCM-DEM-10** Statistical methods shall include moving average, exponential smoothing (with trend and seasonality), seasonal decomposition, regression with causal drivers, and methods for intermittent demand, with parameters visible to planners.
- **FR-SCM-DEM-11** The system shall select the best method per series automatically by back-testing accuracy, and let planners override the method, with explanation of why a method was chosen.
- **FR-SCM-DEM-12** Causal factors (promotions, price changes, events, seasons, holidays including Ramadan and Eid shifts, weather indicators) shall be maintainable and usable as drivers, with effects estimated and shown.
- **FR-SCM-DEM-13** New products shall be forecast by analogy to similar products with launch curves, and phase-out shall follow decay rules.
- **FR-SCM-DEM-14** Forecasts shall be produced at configurable levels and disaggregated or aggregated by proportions from history, with reconciliation across the hierarchy.
- **FR-SCM-DEM-15** AI-assisted forecasting shall be available as an additional method through the AI Gateway, with comparison to statistical methods and explainable drivers. (S)

### 4.3 Consensus and control

- **FR-SCM-DEM-20** Planners, sales and finance shall adjust forecasts by item, customer or group with comments and reasons; every adjustment shall be versioned with user, time and effect on the total.
- **FR-SCM-DEM-21** Approval workflows shall control submission of the consensus forecast by value change thresholds, with locking after approval and unlock only by permission.
- **FR-SCM-DEM-22** Forecast accuracy shall be measured (mean absolute percentage error, weighted absolute error, bias, tracking signal) by item, family and horizon, with forecast value added analysis comparing statistical, adjusted and consensus forecasts.
- **FR-SCM-DEM-23** Accuracy exceptions (persistent bias, accuracy below target) shall raise alerts and tasks for owners.
- **FR-SCM-DEM-24** Collaborative inputs from sales representatives, key customers and distributors shall be collectable through forms or the portal with approval before they affect the forecast. (S)
- **FR-SCM-DEM-25** Short-term demand sensing from recent orders and point-of-sale signals shall adjust near-term forecasts. (C, wave D)
- **BR:** forecasts in a locked version cannot change; history cleansing never deletes original data; adjustments always carry reasons.
- **AC:** a planner reviews 10,000 series, accepts automatic methods for most, overrides 50 with promotion effects, submits the consensus forecast for approval, and accuracy by family is reported next month against the locked version.

## 5. Supply planning (SUP)

**Purpose:** turn demand into timely supply proposals across a network. **Actors:** Supply Planner, Buyer, Warehouse Manager.

### 5.1 Net requirements and planned orders

- **FR-SCM-SUP-01** Planning runs shall compute, per planning item, location and bucket, gross demand (consensus forecast, customer orders, transfer demands, reservations, dependent demands in later waves) against supply (on hand, quarantine and blocked stock treated per settings, in transit, open purchase orders and transfers, planned orders) to find net requirements.
- **FR-SCM-SUP-02** The engine shall generate planned orders of type purchase, transfer and (in later waves) production, by applying lead times, calendars, lot-sizing rules, safety stock, minimums and multiples, shelf-life limits and sourcing rules including quotas.
- **FR-SCM-SUP-03** Distribution requirements planning shall plan level by level through the network: demand at downstream locations becomes dependent demand on upstream locations, down to purchase demand on suppliers.
- **FR-SCM-SUP-04** Time fences shall freeze or restrict changes inside defined horizons; firm planned orders shall not be rescheduled by later runs without explicit action.
- **FR-SCM-SUP-05** Safety stock shall be computed by the chosen policy (fixed, days of cover, service level using demand and lead-time variability) and shown with its calculation.
- **FR-SCM-SUP-06** Runs shall be net change or regenerative, for all items or a selection, in operational or scenario versions, as resumable jobs with progress, and shall complete within the performance targets of section 21.
- **FR-SCM-SUP-07** Constraints (supplier capacity and minimum order, lane and warehouse capacity, shelf life) shall be considered in basic form with flagged violations; optimized constrained planning is a later enhancement. (S)
- **FR-SCM-SUP-08** Allocation of scarce supply across competing demands shall follow configurable priority and fair-share rules, producing shortage lists by customer and item.

### 5.2 Review, release and traceability

- **FR-SCM-SUP-10** The planner workbench shall present items by exception with projected stock over time, planned orders, and messages: shortage, excess, past-due order, reschedule in, reschedule out, cancel, expedite, with filters and bulk actions.
- **FR-SCM-SUP-11** Pegging shall show for each planned or open supply the demands it covers and for each demand the supplies that cover it, across levels.
- **FR-SCM-SUP-12** Planners shall edit, split, merge, firm, and release planned orders; release shall create draft requisitions or purchase orders in Procurement and transfers in Inventory through their contracts, with links back to the plan.
- **FR-SCM-SUP-13** Release rules shall allow automatic release of low-risk items within thresholds, with approval workflow for others.
- **FR-SCM-SUP-14** The plan shall compare to the previous run, showing changes by item and reason, so planners handle only what changed.
- **FR-SCM-SUP-15** Projected inventory value and supply cost by period shall be calculated for finance review and S&OP.
- **FR-SCM-SUP-16** Every planned order shall record the run, parameters and rules used, enabling audit and reproduction of results.
- **BR:** planning never changes documents outside the planning tables until a planner releases; released orders are normal documents governed by their modules; frozen horizon changes require permission and reason.
- **AC:** a run over 20,000 items and 10 locations completes within the target; the workbench lists 300 exceptions; a planner pegs a shortage to two sales orders, expedites an open purchase order, releases 40 planned orders as requisitions and transfers, and the next run shows only changes.

## 6. Sales and operations planning (SOP)

**Purpose:** align demand, supply, finance and strategy in one monthly cycle with explicit decisions. **Actors:** S&OP Lead, Demand Planner, Supply Planner, Finance Controller, Executive.

- **FR-SCM-SOP-01** An S&OP cycle shall be defined with steps (data preparation, demand review, supply review, pre-meeting, executive meeting), calendar dates, owners, checklists and status, repeating monthly or on a custom cadence.
- **FR-SCM-SOP-02** Each cycle shall assemble a plan pack: consensus demand, supply plan and capacity view, projected inventory, service level, revenue and margin, working capital, and risks, by product family and horizon.
- **FR-SCM-SOP-03** Financial translation shall convert units to revenue, cost and inventory value using prices and costs from Sales, Procurement and Inventory, and compare to budgets and forecasts from Finance when licensed.
- **FR-SCM-SOP-04** Scenarios (base, upside, downside, supply disruption, price change) shall be created from the plan, compared side by side on service, cost, inventory and profit, and one chosen as the approved plan.
- **FR-SCM-SOP-05** Gaps between demand and supply shall be listed with proposed actions (add supplier capacity, shift demand, expedite, build inventory, change promotion), owners and due dates.
- **FR-SCM-SOP-06** Decisions, assumptions and attendees shall be recorded in a decision log; the approved plan shall be locked and shall drive the supply planning run as the consensus demand and supply targets.
- **FR-SCM-SOP-07** Executive approval shall use the kernel workflow engine, with comments and attachments, and notifications to affected planners.
- **FR-SCM-SOP-08** Plan versus actual review shall compare last cycle's plan to results and show variance causes, with accuracy and plan adherence scores for each function.
- **FR-SCM-SOP-09** Participants outside the planning team (sales, finance, operations) shall contribute through forms and review screens with role-based permissions.
- **AC:** the monthly cycle pack shows a 12-month plan by family; two scenarios are compared; the executive approves the downside-protected plan; the decision log is stored and the locked plan feeds the next supply run.

## 7. Inventory optimization (OPT)

**Purpose:** hold the right stock at the lowest cost for the service level required. **Actors:** Supply Planner, Supply Chain Manager, Finance Controller.

- **FR-SCM-OPT-01** Segmentation shall classify items by value (ABC), variability (XYZ), criticality, lifecycle and shelf life, and assign policies by segment, with manual overrides recorded.
- **FR-SCM-OPT-02** Safety stock, reorder point and order quantity optimization shall use service-level targets, demand and lead-time variability, costs of holding and ordering, and constraints, producing proposed parameters with expected service and inventory effects.
- **FR-SCM-OPT-03** Trade-off curves shall show inventory investment against service level for selected segments, supporting target-setting discussions.
- **FR-SCM-OPT-04** Excess and obsolete analysis shall identify stock above coverage thresholds, slow-moving, non-moving, near-expiry and obsolete items, proposing actions (transfer, discount, return to supplier, donate, scrap, provision) with value effects.
- **FR-SCM-OPT-05** Rebalancing proposals shall suggest transfers from locations with excess to locations with shortage or expected shortage, considering lead time, transport cost and service impact.
- **FR-SCM-OPT-06** Parameter changes proposed by optimization shall be reviewed in a worklist, approved by role, and written to planning parameters (and, for parameters stored in Inventory, back to Inventory through the UpdatePlanningParameters contract) with history; automatic application may be enabled for low-risk segments.
- **FR-SCM-OPT-07** Working capital impact reports shall show inventory value, days of supply, and projected change from proposed parameters, linked to Finance views when licensed.
- **FR-SCM-OPT-08** Lot-size and order-frequency optimization shall consider quantity discounts, freight breaks and storage costs. (S)
- **FR-SCM-OPT-09** What-if analysis shall run policy changes against historical demand and lead times (simulation) to show likely service and inventory outcomes before activation.
- **AC:** optimization of class A items proposes lower safety stock for 30 percent of items and higher for 10 percent; the planner approves, the simulation shows service holding at 97 percent with 12 percent lower inventory value, and parameters update with history.

## 8. Order promising and fulfillment orchestration (OFM)

**Purpose:** promise realistic delivery dates and choose the best fulfillment source. **Actors:** Customer Service Representative, Logistics Planner, Supply Planner. **Dependency:** most capabilities need Sales; without Sales they operate on imported or manually entered orders.

- **FR-SCM-OFM-01** Available-to-promise shall consider on-hand stock, reservations, in-transit and planned receipts by location and date, and return a promise date and quantity per line through a synchronous contract to Sales and the portal.
- **FR-SCM-OFM-02** Capable-to-promise shall consider planned supply and capacity (basic) to promise dates beyond current stock, with explanation of the source of supply.
- **FR-SCM-OFM-03** Fulfillment rules shall choose the source warehouse or supplier (drop-ship in later waves) per order line by rules: stock availability, proximity, cost, customer priority, service commitments, and inventory policy, with traceable results.
- **FR-SCM-OFM-04** Order splitting and consolidation shall be supported: split a line across locations or dates, consolidate several orders to one shipment, with customer preferences (ship complete or partial).
- **FR-SCM-OFM-05** Backorder management shall list unfulfilled lines with expected dates, priorities, reallocation options and customer communication templates.
- **FR-SCM-OFM-06** Allocation shall follow priority rules (customer class, order date, contract commitments) and re-run when supply changes, with a log of reallocations.
- **FR-SCM-OFM-07** Order change impact (quantity or date changes, expedites) shall be shown with effects on other orders and planned supply before confirmation.
- **FR-SCM-OFM-08** Delivery commitment tracking shall compare promised and actual dates to feed OTIF and customer service reports.
- **FR-SCM-OFM-09** Service-level agreements by customer (lead time, fill rate) shall be configurable, with alerts when the plan puts them at risk.
- **AC:** a three-line order is promised from two warehouses with different dates, one line is back-ordered with an expected date from an open purchase order, and the customer service representative sends the confirmation with the promised dates.

## 9. Transportation and logistics (TMS)

**Purpose:** plan, execute and settle movements of goods. **Actors:** Logistics Planner, Transport Coordinator, Carrier, Driver, Warehouse Manager.

### 9.1 Master data and rates

- **FR-SCM-TMS-01** Carriers shall be maintained as business partners with role carrier, services (road, rail, sea, air, courier), service levels, coverage, insurance and compliance documents with expiry, performance scores, and portal access.
- **FR-SCM-TMS-02** Rate tables shall define freight cost by carrier, lane, mode, weight, volume, distance, zone or flat rate, with surcharges (fuel, handling, remote area), minimum charges, validity dates, currencies, and contract references.
- **FR-SCM-TMS-03** Vehicle and equipment types shall be defined with capacity (weight, volume, pallets), restrictions and cost parameters, for own and hired fleets.
- **FR-SCM-TMS-04** Locations, delivery windows, dock hours and customer delivery constraints shall be maintained for planning and appointment booking.

### 9.2 Planning

- **FR-SCM-TMS-10** Shipment planning shall create loads from deliveries, transfers, purchase orders (inbound) and returns, grouped by destination, date, mode and constraints, with consolidation proposals and manual adjustment.
- **FR-SCM-TMS-11** Carrier selection shall follow rules (cost, service level, capacity, performance, contract allocation) and propose carriers, with tendering to carriers through the portal or email with acceptance, rejection and counter-offer tracking.
- **FR-SCM-TMS-12** Multi-stop route sequencing shall order stops by distance or time using map services through a connector, with vehicle capacity, driver hours and delivery windows considered at a basic level; advanced optimization may use external engines through connectors. (S)
- **FR-SCM-TMS-13** Freight cost estimates shall be calculated at planning from rate tables and shown on the shipment; planned versus actual shall be tracked.
- **FR-SCM-TMS-14** Dock and delivery appointment scheduling shall book time slots per dock or customer with conflicts, reminders and carrier confirmation. (S)

### 9.3 Execution and tracking

- **FR-SCM-TMS-20** Dispatch shall release shipments to carriers and drivers with documents (delivery notes, waybills, labels, gate passes) generated from templates in the right languages.
- **FR-SCM-TMS-21** Tracking shall record milestones (picked up, in transit, delayed, arrived, delivered, exception) from carrier APIs, files, driver mobile updates, or telematics and GPS connectors, with map and timeline views.
- **FR-SCM-TMS-22** Exceptions (delay, damage, route deviation, failed delivery) shall create alerts and tasks with owners, customer notifications and reason codes.
- **FR-SCM-TMS-23** Electronic proof of delivery shall capture signature, photo, time, location and received quantities on mobile, offline-capable, creating discrepancy records for shortages and damage.
- **FR-SCM-TMS-24** Delivery completion shall update Sales and Inventory documents through events (delivery confirmed, quantities received) and trigger invoicing rules where configured.
- **FR-SCM-TMS-25** Own-fleet management shall cover vehicles (linked to Assets when licensed), drivers (linked to HRMIS when licensed), trips, odometer and fuel records, driver allowances, and compliance documents with expiry.

### 9.4 Freight settlement

- **FR-SCM-TMS-30** Freight costs shall be accrued at shipment completion using rate tables or carrier confirmations and published for Finance accrual when licensed.
- **FR-SCM-TMS-31** Carrier invoices shall be matched to shipments and expected charges (audit), with tolerances, dispute workflows and approval, then handed to Finance AP through the Procurement matching approach.
- **FR-SCM-TMS-32** Freight allocation to orders, items and customers shall be supported for landed cost and customer profitability, with allocation rules by weight, volume or value.
- **FR-SCM-TMS-33** Claims for damage, loss and late delivery shall be raised against carriers with evidence, status and recoveries.
- **FR-SCM-TMS-34** Transport reports shall show cost per unit, per kilometer and per customer, carrier performance (on-time, damage rate, acceptance rate), utilization and empty running.
- **BR:** a shipment cannot be dispatched without required documents; proof of delivery is immutable once confirmed and corrected by discrepancy records; carrier invoices cannot be approved above audit tolerance without an approver.
- **AC:** a planner consolidates 25 deliveries into six loads, tenders them to carriers by rate, drivers confirm delivery with signature and photo offline, a short delivery creates a discrepancy that updates the delivery note, and the carrier invoice matches with one disputed surcharge.

## 10. Advanced warehouse management (WMS)

This section extends the basic warehouse operations of Inventory (document 8, OPS). It is planned for wave C and wave D and is separately licensable.

- **FR-SCM-WMS-01** Wave planning shall group orders into waves by carrier cut-off, priority, zone and workload, releasing pick tasks with controlled timing, and showing wave progress and exceptions. (wave C)
- **FR-SCM-WMS-02** Task management shall interleave putaway, picking, replenishment and counting tasks by worker location, skill and equipment, with priorities and live task queues. (wave C)
- **FR-SCM-WMS-03** Pick strategies shall include discrete, batch, zone and cluster picking, with pick path optimization and short-pick handling. (wave C)
- **FR-SCM-WMS-04** Slotting analysis shall recommend locations for items based on velocity, cube, weight and affinity, producing relocation tasks. (wave D)
- **FR-SCM-WMS-05** Cross-docking shall allocate inbound receipts directly to outbound demand, bypassing storage, with exceptions if timing fails. (wave C)
- **FR-SCM-WMS-06** Labor management shall measure productivity by task, worker and zone against standards, with dashboards and incentive data export to HRMIS. (wave D)
- **FR-SCM-WMS-07** Dock and yard management shall schedule trailers and doors, track check-in and check-out, and detention times. (wave D)
- **FR-SCM-WMS-08** Value-added services (kitting, labeling, repackaging) shall be recordable as work tasks that consume and produce stock. (wave C)
- **FR-SCM-WMS-09** Integration with warehouse automation (conveyors, sorters, pick-to-light, put-to-light, voice, automated storage) shall use the connector framework with standard messages and device status monitoring. (wave D)
- **FR-SCM-WMS-10** Third-party logistics support shall allow a warehouse operator to serve multiple owner companies, with owner-level stock, billing events (storage, handling) and customer portals. (wave D)
- **AC:** a wave of 400 orders is released at the carrier cut-off, workers receive interleaved tasks on handhelds, short picks trigger replenishment tasks, and wave progress is visible to the warehouse manager.

## 11. Supplier collaboration (SCO)

**Purpose:** plan and execute together with suppliers. It extends the Procurement supplier portal (document 7, section 12) with planning data. **Actors:** Supply Planner, Buyer, Supplier.

- **FR-SCM-SCO-01** Rolling forecasts and planned requirements shall be shared with selected suppliers through the portal by item, location and bucket, with version history, firm versus planning horizon labels, and acknowledgement by suppliers.
- **FR-SCM-SCO-02** Suppliers shall commit capacity and confirm or counter planned quantities and dates, with differences shown to planners and feeding supply constraints and plan exceptions.
- **FR-SCM-SCO-03** Advance shipping notices shall be submitted by suppliers (items, quantities, batches, packaging, expected arrival, carrier, tracking) and shall create expected receipts and inbound shipments.
- **FR-SCM-SCO-04** Vendor-managed inventory shall let suppliers see stock levels, consumption and reorder parameters for their items at agreed locations and propose or trigger replenishment within agreed limits, with ownership and invoicing rules (on consumption or on delivery).
- **FR-SCM-SCO-05** Consignment replenishment shall integrate with Inventory consignment stock so that suppliers replenish to agreed levels and are paid on consumption. (S)
- **FR-SCM-SCO-06** Collaboration issues (late confirmation, shortage, quality problem) shall be managed as shared cases with owners, due dates, messages and attachments between buyer and supplier.
- **FR-SCM-SCO-07** Supplier performance for collaboration (forecast commitment accuracy, confirmation timeliness, ASN accuracy, on-time-in-full, lead-time adherence) shall feed Procurement scorecards.
- **FR-SCM-SCO-08** Visibility shall be limited to what each supplier needs: no access to other suppliers' data, item cost data not agreed for sharing, or internal plans beyond the shared view.
- **AC:** a supplier receives a 12-week forecast, commits capacity for eight weeks, sends an advance shipping notice for a planned delivery, and the receiving warehouse sees the expected receipt with batch data.

## 12. Returns and reverse logistics (RLG)

**Purpose:** handle returned goods quickly and recover value. **Actors:** Customer Service Representative, Warehouse Manager, Buyer.

- **FR-SCM-RLG-01** Customer return authorizations are owned by Sales (document 11, RET-01) and are extended here with reverse shipments and dispositions; supplier return authorizations shall be created from Procurement, with reason codes, eligibility rules (time window, condition), and approval workflows.
- **FR-SCM-RLG-02** Reverse shipments shall be planned, tracked and received like outbound shipments, with return labels and pickup scheduling.
- **FR-SCM-RLG-03** Receiving returned goods shall record condition and select a disposition: restock, refurbish, repackage, quarantine for inspection, return to vendor, donate or scrap, with stock effects through Inventory and quality inspection where required.
- **FR-SCM-RLG-04** Credit, refund or replacement processing shall trigger events to Sales and Finance according to the disposition and policy.
- **FR-SCM-RLG-05** Supplier warranty and return claims shall track defective goods, supplier responses and credits.
- **FR-SCM-RLG-06** Reverse logistics analytics shall show return rates, reasons, cost, recovery value and cycle time by item, customer and supplier.
- **FR-SCM-RLG-07** Refurbishing and remanufacturing flows shall be supported with the manufacturing wave. (wave D)
- **AC:** a customer return is authorized, collected, inspected, 80 percent restocked and 20 percent scrapped, a credit event reaches Sales and Finance, and the supplier claim for the defective part is raised.

## 13. Control tower and visibility (CTW)

**Purpose:** a single view of the supply chain with exceptions that someone owns. **Actors:** Supply Chain Manager, Planners, Executives.

- **FR-SCM-CTW-01** The control tower shall show end-to-end flows: orders, inventory by location, inbound and outbound shipments, supplier confirmations, and planned versus actual, filtered by product, location, customer, supplier or region.
- **FR-SCM-CTW-02** Key indicators shall include OTIF, fill rate, perfect order rate, order cycle time, inventory days of supply, stock-outs, forecast accuracy, plan adherence, transport cost per unit, supplier on-time rate, and backlog, with targets, trends and drill-down.
- **FR-SCM-CTW-03** Exception rules shall be configurable (late shipment, projected stock-out, supplier delay, quality hold, lane disruption, forecast bias) with severity, thresholds, owners and escalation, and shall create tasks through the workflow engine.
- **FR-SCM-CTW-04** An exception work queue shall show open exceptions by owner and age with proposed actions and the objects involved, with resolution recorded and root-cause codes.
- **FR-SCM-CTW-05** Risk monitoring shall combine internal signals (supplier performance, single-source items, lane delays) with optional external data connectors (weather, port status, news, sanctions lists) to raise alerts. (S)
- **FR-SCM-CTW-06** Maps shall display facilities, shipments in transit and delays when location data exist.
- **FR-SCM-CTW-07** What-if simulation shall let managers test the effect of a disruption (supplier outage, port closure) on service and inventory using the planning engine in a scenario.
- **FR-SCM-CTW-08** Executives shall receive scheduled summaries with narrative explanations of changes and risks generated by the AI Gateway for human review. (S)
- **FR-SCM-CTW-09** All views shall respect data scopes, hide cost fields from unauthorized users, and be available on mobile for key indicators and exceptions.
- **AC:** a projected stock-out for a key item appears as an exception with the late supplier shipment, proposes expediting and an alternative transfer, assigns the planner, and closes with a root-cause code once resolved.

## 14. Trade and customs (TRD)

This area covers references and documents for cross-border flows; deeper trade compliance is added by demand. Wave C for basics, wave D for compliance depth.

- **FR-SCM-TRD-01** Items shall carry customs classification codes (harmonized system codes), country of origin, and units, by company and country.
- **FR-SCM-TRD-02** Import and export shipments shall track trade documents (commercial invoice, packing list, bill of lading or airway bill, certificates of origin), customs declarations, broker and agent details, and milestones.
- **FR-SCM-TRD-03** Duty and tax estimates shall use duty tables from country packs and feed landed cost allocation in Inventory and Procurement (document 8, VAL-04; document 7, RCV-09).
- **FR-SCM-TRD-04** Incoterms shall be recorded on orders and shipments and drive responsibility for freight, insurance and risk transfer in cost calculations.
- **FR-SCM-TRD-05** Letters of credit and bank guarantees related to shipments shall be referenced and tracked with Finance treasury functions when available. (wave D)
- **FR-SCM-TRD-06** Restricted-party and sanctions screening of customers, suppliers and shipments shall be available through a screening connector, with holds and review workflows. (wave D)
- **FR-SCM-TRD-07** Export controls and licenses shall be recordable on items and shipments with expiry and quantity tracking. (wave D)
- **FR-SCM-TRD-08** Customs system integrations for declaration filing shall be delivered as country connectors. (wave D)

## 15. Supply chain analytics (SCA)

Analytics uses the kernel semantic layer and builders (document 9); this section defines standard content.

- **FR-SCM-SCA-01** Planning reports shall cover forecast accuracy and bias, forecast value added, plan changes between runs, exception counts and age, planned versus released orders, and plan adherence.
- **FR-SCM-SCA-02** Service and inventory reports shall cover service level, fill rate, OTIF, backorders, inventory value and days of supply by segment, excess and obsolete stock, turnover and working capital effect.
- **FR-SCM-SCA-03** Supplier reports shall cover lead-time reliability, confirmation performance, ASN accuracy, capacity commitment versus actual, and collaboration issues.
- **FR-SCM-SCA-04** Transport reports shall cover cost, mode mix, carrier performance, utilization, delays, claims and freight audit results.
- **FR-SCM-SCA-05** Scenario comparison reports shall present KPIs for several scenarios side by side with drill-down to assumptions.
- **FR-SCM-SCA-06** Dashboards per role shall be provided: demand planner, supply planner, S&OP lead, logistics planner, warehouse manager, supply chain manager and executive.
- **FR-SCM-SCA-07** All standard reports shall be cloneable through the report builder, scheduled and exported, and respect data scopes and cost-visibility permissions.

## 16. Country notes

These are planning inputs to be verified by qualified local logistics and customs specialists before design is frozen.

| Topic | Pakistan | Saudi Arabia | United States |
| --- | --- | --- | --- |
| Domestic transport | Road freight dominated by local carriers and owner-operators; delivery challans and gate passes; tracking by manual updates and local telematics providers | Road freight with growing digital freight platforms; bilingual delivery documents; electronic waybill requirements to be confirmed | Truckload and less-than-truckload carriers with API and EDI messaging; telematics and electronic logging data through connectors |
| Customs | National customs clearing system with goods declarations and agents | National customs clearing platform and bonded zones | Customs entry through brokers and the national system |
| Taxes and duties | Customs duty, sales tax and withholding at import per tariff tables | Customs duty and VAT at import per tariff tables | Duties by tariff schedule; sales and use tax at destination |
| Calendar effects on demand | Ramadan, Eid, wedding and harvest seasons; Hijri and Gregorian shifts | Ramadan, Eid, Hajj and Umrah seasons, national days; Hijri shifts | Holidays, promotions, weather, and retail calendars |
| Language | Urdu and English documents and notices | Arabic and English with right-to-left layout | English, optional Spanish |
| Payments | Freight settled through Finance by transfer or cheque | Transfer and local payment rails through Finance connectors | ACH and check through Finance |

Pack responsibilities: demand calendars and seasonal events, duty and tariff tables, customs and transport document templates, carrier and telematics connectors, and local carrier and compliance rules.

## 17. Events and contracts

### 17.1 Events published

| Event | Consumers |
| --- | --- |
| ForecastApproved, SopPlanApproved | Planning engine, Finance (budget and forecast views), notifications |
| ReplenishmentProposed (purchase or transfer, released from a plan) | Procurement (draft requisitions), Inventory (draft transfers) |
| PlannedOrdersReleased | Procurement, Inventory, reporting |
| SupplyExceptionRaised, SupplyExceptionResolved | Workflow and notifications, control tower |
| ShipmentPlanned, ShipmentDispatched, ShipmentMilestoneReached | Sales, Inventory (in-transit), notifications, control tower |
| DeliveryConfirmed | Sales (delivery and invoicing rules), Inventory (received quantities), Finance (revenue triggers where configured). Published here when Supply Chain is licensed; otherwise Sales publishes it (decision D-18) |
| FreightAccrued | Finance (accruals) |
| CarrierInvoiceApproved | Finance (payables) or Accounting Export |
| ReturnDispositioned | Sales, Inventory, Finance |
| SupplierForecastShared, AsnReceived | Procurement, Inventory |

### 17.2 Events consumed

| Event | Source | Effect |
| --- | --- | --- |
| OrderCreated, OrderChanged, OrderCancelled | Sales | Demand and reservations updated |
| DeliveryPosted (from Sales), CustomerReturnReceived (from Inventory), ReturnAuthorized (from Sales) | Sales | History and returns flows updated |
| GoodsReceiptPosted, GoodsIssuePosted, StockTransferShipped, StockTransferReceived | Inventory | Stock and history updated |
| StockBelowReorder | Inventory | Exception or planning trigger |
| PurchaseOrderIssued, Revised, Cancelled, Closed; ReceiptPosted | Procurement | Supply and lead-time statistics updated |
| SupplierStatusChanged, ContractActivated, ContractExpired | Procurement | Sourcing rules and risk indicators refreshed |
| AssetStatusChanged | Assets | Own-fleet availability updated |
| EmployeeChanged | HRMIS | Driver and labor assignments refreshed |

### 17.3 Synchronous contracts

| Contract | Provider | Consumers | Behavior without provider |
| --- | --- | --- | --- |
| Get availability and stock position | Inventory | Supply Chain | Imported stock snapshot used |
| Get demand history and open orders | Sales | Supply Chain | Imported history used |
| Promise date and fulfillment source | Supply Chain | Sales, portal | Sales uses its own simple availability check |
| Create requisition draft | Procurement | Supply Chain | Planned orders exported |
| Create transfer draft | Inventory | Supply Chain | Planned orders exported |
| Get supplier lead-time statistics | Procurement | Supply Chain | Manual or imported parameters |
| Get exchange rate | Kernel | All | Kernel service answers |

## 18. Roles, permissions and default workflows

### 18.1 Baseline roles

| Role | Main permissions |
| --- | --- |
| Demand Planner | Maintain history, forecasts and adjustments; submit consensus |
| Supply Planner | Run planning, review exceptions, firm and release orders |
| S&OP Lead | Run cycles, manage scenarios and decisions |
| Logistics Planner | Plan shipments and loads, select carriers |
| Transport Coordinator | Dispatch, track, settle freight, manage claims |
| Warehouse Manager | Manage waves, tasks, labor and slotting |
| Customer Service Representative | Promise and expedite orders, handle backorders and returns |
| Supply Chain Manager | Set policies and parameters, manage KPIs and exception rules |
| Carrier (portal) | Own tenders, shipments, proof of delivery and invoices only |
| Supplier (portal) | Own forecast shares, confirmations and advance shipping notices only |
| Executive | View control tower and approve S&OP plans |
| Auditor | Read-only with drill-down |

### 18.2 Default workflows (all editable)

| Workflow | Default route |
| --- | --- |
| Consensus forecast approval | Demand planner, sales head, finance by change size |
| S&OP plan approval | S&OP lead, finance, executive |
| Parameter change from optimization | Planner proposes; supply chain manager approves by segment |
| Planned order release | Auto-release within thresholds; planner approval above |
| Carrier tender | Logistics planner to carrier; acceptance recorded |
| Freight invoice exception | Transport coordinator, supply chain manager above limits |
| Return authorization | Customer service, warehouse inspection, finance for credit |
| Supply exception | Owner assigned by rule, escalation by severity and age |

## 19. Data model overview

Tables live in the scm schema (kernel references as in document 3). Planning results are versioned and scenario-scoped; tracking events and proof of delivery are append-only.

| Area | Main tables |
| --- | --- |
| Planning master | planning\_items, network\_nodes, network\_lanes, sourcing\_rules, planning\_calendars, planning\_horizons, planning\_versions, data\_quality\_findings |
| Demand | demand\_history, history\_adjustments, forecast\_series, forecast\_versions, forecast\_values, forecast\_adjustments, causal\_events, forecast\_accuracy |
| Supply planning | planning\_runs, planned\_orders, planned\_order\_pegging, supply\_exceptions, safety\_stock\_results, allocations, shortage\_lists |
| S&OP | sop\_cycles, sop\_steps, sop\_scenarios, sop\_scenario\_values, sop\_decisions, sop\_actions |
| Optimization | segmentations, optimization\_runs, parameter\_proposals, rebalancing\_proposals, excess\_obsolete\_findings |
| Order promising | promise\_requests, promise\_results, fulfillment\_rules, backorders, sla\_definitions |
| Transport | carrier\_profiles, rate\_tables, rate\_lines, equipment\_types, loads, shipments, shipment\_stops, tenders, tracking\_events (append-only), proofs\_of\_delivery (append-only), freight\_charges, freight\_audits, transport\_claims, fleet\_trips, delivery\_appointments |
| Warehouse management | waves, wave\_orders, warehouse\_tasks, slotting\_results, labor\_standards, labor\_records, dock\_bookings, yard\_visits |
| Collaboration | supplier\_forecast\_shares, supplier\_commitments, asn\_notices, vmi\_agreements, collaboration\_cases |
| Returns | return\_authorizations, reverse\_shipments, return\_dispositions |
| Control tower | exception\_rules, exceptions, kpi\_definitions, kpi\_snapshots, risk\_signals |
| Trade | trade\_documents, customs\_declarations, duty\_estimates, screening\_results |
| Outbox and inbox | outbox\_messages, inbox\_messages |

Key design points: forecast and plan tables are partitioned by version or period for performance; planning runs store their parameters and rule versions for reproducibility; pegging links demand to supply without cross-module foreign keys; document links connect orders, shipments, deliveries and invoices.

## 20. AI skills

All skills run through the AI Gateway with the user's permissions, propose and never decide, and are switchable per tenant (kernel document 2, section 22).

| Skill | Description | Wave |
| --- | --- | --- |
| AI-assisted forecasting | Additional forecast method with drivers, compared with statistical methods | C |
| Forecast commentary | Explain forecast changes and accuracy for review | C |
| Exception triage | Rank and group exceptions and propose actions with reasons | C |
| Supplier delay prediction | Estimate late deliveries from confirmations, history and signals | C |
| ETA and delay prediction | Estimate arrival times from tracking and history | C |
| Carrier and route suggestions | Propose carriers and stop sequences for review | C |
| Freight audit anomalies | Flag unusual carrier charges | C |
| S&OP pack summary | Summarize cycle packs, gaps and risks | C |
| Natural-language supply questions | Query stock risk, orders and shipments within scope | C |
| Return reason classification | Group return reasons and suggest root causes | C |

AI shall not release planned orders, award carriers, change parameters or approve invoices without human action under the normal workflows; any automatic release uses explicit, auditable thresholds set by an administrator.

## 21. Non-functional requirements

| Area | Requirement |
| --- | --- |
| Planning scale | At least 500,000 item-location combinations and 104 weekly buckets per tenant |
| Planning performance | Net-change run for 100,000 item-locations in under 15 minutes; regenerative run in under 60 minutes; forecast generation for 200,000 series in under 60 minutes |
| Interactivity | Planner workbench loads in under 3 seconds at the 95th percentile with 10,000 exceptions |
| Transport scale | At least 100,000 shipments per year and 1 million tracking events per month per tenant |
| Explainability | Every plan result traceable to inputs, parameters and rule versions |
| Portal security | Separate authentication realms for suppliers and carriers, isolation by account, rate limits, file scanning |
| Mobile | Offline-capable proof of delivery, driver updates, warehouse tasks with conflict handling |
| Auditability | Complete history of forecast changes, plan releases, parameter changes and exceptions |
| Localization | English, Urdu and Arabic screens and documents; right-to-left layouts; Hijri-aware calendars |
| Configurability | Parameters, rules, rate tables, exception rules, workflows and templates configurable without code |

## 22. Acceptance and testing

- **Planning golden datasets:** independent calculations of safety stock, lot sizing, net requirements and distribution planning for representative networks, reviewed by a supply chain practitioner.
- **Forecast back-testing:** forecasting methods back-tested on supplied customer history with accuracy compared to the customer's current process.
- **End-to-end scenarios:** forecast to S&OP to plan to release to purchase order to receipt to shipment to proof of delivery to freight settlement; order promising and backorder; supplier collaboration with advance shipping notice; return with disposition; exception to resolution.
- **Configuration variants:** run planning and execution with Sales and Inventory data from the modules, and with imported data only, to prove graceful degradation (document 2, GR-12).
- **Reconciliation invariants:** released orders exist as documents in their owning modules; plan stock projections reconcile to Inventory at run start; freight accruals reconcile to settled invoices.
- **Portal and mobile tests:** supplier and carrier isolation, offline proof of delivery and sync.
- **Performance:** scale tests at 1.5 times expected load.
- **Pilot exit criteria:** two planning cycles and one S&OP cycle run by a pilot customer, forecast accuracy and service level measured against baseline, zero open severity-1 and severity-2 defects.

## 23. Assumptions and open items

**Assumptions:** Supply Chain plans and orchestrates but never owns stock, orders or accounting documents; releases create ordinary documents in Procurement and Inventory; advanced optimization uses transparent methods first and external engines through connectors when needed.

**Open decisions:** open-source forecasting and optimization libraries and their licenses (document 1, decision D-09); build versus integrate for constrained planning and route optimization; scope and timing of manufacturing planning and bills of materials; which carrier, telematics and map connectors come first; whether advanced warehouse management is sold as a separate module; EDI scope for retail and trading customers; external data providers for the control tower.

**Next documents:** 11 Sales and Distribution Functional Specification, 9 Analytics, Reporting and AI, and 13 UX and Design System Principles.

## 24. Baseline 1.0 addendum (this document is now version 0.2)

*Added 14 November 2026 by the comprehensive review (Doc 16). Sections 1 to 23 are unchanged. The previous state (version 0.1) is kept in this document's version history and in the frozen originals archive.*

- **Phase.** Supply Chain is built in Phase 4 (proposed D-32). It depends on Inventory, Procurement and Sales core from Phase 3 being in production use, because planning quality depends on real demand and stock history.
- **Binding cross-module rules.** Doc 16 section 6 applies. Supply Chain creates no stock or ledger effect of its own: every proposal becomes a purchase requisition, transfer order or production request in the owning module, which then follows FR-XM-13, FR-XM-17, FR-XM-19 and FR-XM-20. Freight accruals and carrier invoices post through the Finance posting contract (FR-XM-01, proposed D-31) and late freight follows FR-XM-16.
- **Planning runs.** A planning run reads a snapshot and runs as a background job (Doc 16 section 5.4); it never holds locks on operational tables.
- **Size profile.** Supply Chain is not shown to Micro and SME profiles. Their replenishment need is met by Inventory reorder alerts (RPL).
- **Data notes.** Tables carry `tenant_id` (proposed D-30) and `branch_id` where they are transactional (correction C-39). Location coordinates for transport follow Doc 3 section 14.4 (no database geospatial extension, proposed D-36).
- **Data design gate** at Phase 4 entry (proposed D-35).
