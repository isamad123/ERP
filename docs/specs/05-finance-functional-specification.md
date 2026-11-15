# Finance: Functional Specification (v0.2)

Document 5 of the planning set | Prepared for Ibrahim | 10 November 2026 | Companion to the Platform Kernel Specification, Data Architecture and Admin Console specifications

## 1. Introduction

### 1.1 Purpose and scope

This document specifies the Finance module: general ledger, posting engine, accounts payable, accounts receivable, banking and cash, tax, budgeting, period close, multi-company and consolidation, cost accounting, and financial reporting. It also defines how other modules (HRMIS, Procurement, Inventory, Assets, Sales) post into Finance, and how Finance behaves when it is not licensed (Accounting Export connector, kernel document 2, INT-09).

Country-specific accounting and tax rules are delivered by country packs (Pakistan, United States, Saudi Arabia). Where this document gives country examples, they are planning inputs to be verified by qualified local accountants and tax advisers before implementation.

### 1.2 Conventions

- Requirement IDs: FR-FIN-\<area>-\<number>. Priority (M), (S), (C); unmarked is (M).
- Wave: A (first release), B (second wave with Procurement, Inventory and Assets), C (later). Unmarked requirements are wave A.
- Each area lists Functional Requirements (FR), Business Rules (BR) and Acceptance Criteria (AC).
- All configuration is available through the admin console and the API (document 4).

### 1.3 Actors

| Actor | Role in Finance |
| --- | --- |
| Chief Financial Officer | Oversees reporting, budgets, close, controls |
| Financial Controller | Runs close, reviews journals, manages COA and ledgers |
| Accountant | Enters and posts journals, reconciles |
| AP Clerk and AR Clerk | Process invoices, payments and receipts |
| Treasury Officer | Manages bank accounts, payments, cash position |
| Tax Officer | Manages tax codes, returns and certificates |
| Budget Owner | Maintains and monitors budgets |
| Approver | Acts on journal, payment and budget workflows |
| Auditor | Read-only access with drill-down |
| System (posting engine) | Creates journals from events of other modules |

### 1.4 Key terms

| Term | Meaning |
| --- | --- |
| Ledger | A complete set of books for a company under one accounting standard or purpose (for example IFRS, local GAAP, tax) |
| Leading ledger | The primary ledger of a company |
| Account | A node of the chart of accounts that can hold balances |
| Dimension | An analytical attribute on a posting line (cost center, department, project, partner, item, location, custom) |
| Control account | A GL account that is maintained by a subledger (AP, AR, inventory, assets, bank) and does not allow manual posting |
| Posting rule | Data that turns a business event into a journal |
| Subledger | Detailed records (vendor invoices, customer invoices, bank lines) that reconcile to control accounts |

## 2. Module overview

### 2.1 Capability map

| Area | Summary | Wave |
| --- | --- | --- |
| Chart of accounts and ledgers | Flexible COA, multiple ledgers, templates by country | A |
| Journals and general ledger | Manual, recurring, reversing, allocation journals; approvals; drill-down | A |
| Posting engine | Rules from any module's events to journals, with exception queue | A |
| Accounts payable | Vendor invoices, payments, withholding, ageing | A |
| Accounts receivable | Customer invoices, receipts, allocation, dunning, credit | A |
| Banking and cash | Bank accounts, statements, reconciliation, cheques, cash position | A |
| Tax | Determination, postings, returns, certificates, e-invoicing integration points | A |
| Period close | Checklists, revaluation, accruals, year-end | A |
| Financial reporting | Statements, trial balance, ageing, statutory formats, report writer | A |
| Budgeting and control | Budgets by dimension and version, budget checks, commitments | A basic, B full |
| Multi-company and consolidation | Intercompany, eliminations, translation, consolidated statements | B |
| Cost accounting | Allocation cycles, cost and profit centers, product costing hooks | B |
| Fixed-asset accounting hooks | Acquisition and depreciation postings from the Assets module | B |
| Project accounting, revenue recognition, lease accounting | Later | C or D |

### 2.2 Documents in Finance

| Document | Owner | Subledger or GL |
| --- | --- | --- |
| Journal entry | Finance | GL |
| Vendor invoice, vendor credit note, vendor advance | Finance (AP) | AP |
| Payment proposal, payment run, payment | Finance (AP) | AP and bank |
| Customer invoice, customer credit note, receipt, customer advance | Finance (AR) | AR |
| Bank statement, bank reconciliation, bank transfer, cheque | Finance (Banking) | Bank |
| Tax return and certificate | Finance (Tax) | Tax |
| Budget, budget transfer | Finance (Budgeting) | Control only |
| Period close checklist | Finance | Control only |

Customer invoices issued by Sales post into AR through events (Sales issues them when licensed; otherwise invoices are entered directly in Finance AR). Supplier invoices that relate to purchase orders are captured and matched in Procurement and handed to AP when approved; supplier invoices without orders are entered directly in Finance AP (decisions D-12 and D-16).

### 2.3 Dependencies

Finance hard-depends on the kernel only: organization, identity, parties, catalog, tax, localization, numbering, workflow, rules, documents, audit, and reporting. Other modules post into Finance through events (kernel document 2 and Architecture section 4.4).

## 3. Chart of accounts and ledgers (COA)

**Purpose:** model the company's accounts, ledgers and currencies once and use them everywhere.

- **FR-FIN-COA-01** The system shall support one or more charts of accounts per tenant; each company references one chart; charts may be shared across companies or be company-specific.
- **FR-FIN-COA-02** Account codes shall be flexible in length and structure (for example segmented codes with separators), unique per chart, with multilingual names and descriptions.
- **FR-FIN-COA-03** Accounts shall have a type (asset, liability, equity, revenue, expense), subtype, normal balance side, and belong to a configurable hierarchy of account groups of unlimited depth with roll-up subtotals.
- **FR-FIN-COA-04** Account attributes shall include: postable or header, manual posting allowed, control account type (accounts payable, accounts receivable, inventory, fixed assets, bank, tax, other), reconciliation required, cash or bank flag, allowed currencies (any, single, list), mandatory dimensions, default tax code, blocked state, and validity dates.
- **FR-FIN-COA-05** The system shall provide COA templates through country and industry packs, loadable at setup and adjustable afterwards.
- **FR-FIN-COA-06** The COA shall support alternate and group account mappings so that a local chart can map to a group chart or to statutory reporting lines.
- **FR-FIN-COA-07** Accounts shall support bulk import, export, rename, merge (with re-pointing of balances only through approved adjustment journals) and blocking.
- **FR-FIN-COA-08** Account code changes after posting shall be blocked; renaming is allowed; replacing an account on open items shall be done through reclassification journals.
- **FR-FIN-COA-09** Multiple ledgers per company shall be supported: one leading ledger and additional ledgers (for example IFRS, local GAAP, tax, management), each with its own accounting standard label, currencies, calendar and posting scope.
- **FR-FIN-COA-10** A posting shall go to all ledgers by default, with the ability to restrict a journal, a posting rule or an adjustment to selected ledgers.
- **FR-FIN-COA-11** Currencies per company shall be: transaction currency, base (functional) currency, optional group currency, and optional additional reporting currencies, each with rate type configuration.
- **FR-FIN-COA-12** Dimensions shall be configurable: standard dimensions (cost center, department, project, partner, item, location) and tenant-defined dimensions, each with values, hierarchy, validity, mandatory rules per account or document type, and status.
- **FR-FIN-COA-13** Dimension combination validation shall allow rules such as a cost center being valid only for specified departments or accounts.
- **BR:** a control account cannot receive manual journals unless the control account type is explicitly allowed for adjustment by a permitted role; accounts with posted lines cannot be deleted.
- **AC:** a company with local GAAP and IFRS ledgers posts an invoice once and sees it in both ledgers; a tax-only adjustment posted to the tax ledger does not appear in the IFRS ledger.

## 4. Journals and general ledger (JRN)

**Purpose:** record and control all accounting entries with full traceability.

### 4.1 Journal entry

- **FR-FIN-JRN-01** A journal entry shall have header (company, ledger scope, journal type, document date, posting date, period, currency, reference, narration per language, attachments) and lines (account, debit or credit, amount in transaction currency, rate, base amounts, dimensions, partner, narration, tax code).
- **FR-FIN-JRN-02** Journal types shall be configurable (general, adjusting, accrual, reversing, opening balance, closing, allocation, intercompany, bank, cash, payroll, depreciation, revaluation) with numbering, workflow, default ledgers and permissions per type.
- **FR-FIN-JRN-03** Validation on posting shall require: balanced debits and credits per currency and in base currency, open period for the module and role, postable and unblocked accounts, valid dimension combinations, mandatory dimensions present, valid rates, and company consistency.
- **FR-FIN-JRN-04** Journals shall move through statuses draft, submitted, approved, posted, reversed, rejected; the workflow shall be configured by journal type, amount, account or dimension through the kernel workflow engine.
- **FR-FIN-JRN-05** Posted journals shall be immutable; corrections shall be through reversal or adjusting journals that reference the original, and the original shall show its reversal link.
- **FR-FIN-JRN-06** Auto-reversing journals shall create the reversal on a chosen date or at the start of the next period, posted by a job with visibility in the job center.
- **FR-FIN-JRN-07** Recurring journals shall be defined with schedule, template, optional variable amounts, end date and approvals, generating drafts or posting automatically as configured.
- **FR-FIN-JRN-08** Allocation journals shall distribute amounts across dimensions using fixed percentages, statistical drivers (headcount, area, usage) or formulas, with run history and reversal. (wave B for driver-based)
- **FR-FIN-JRN-09** Cross-company journals shall create the offsetting intercompany lines automatically using configured intercompany accounts, and keep the two sides linked.
- **FR-FIN-JRN-10** Journals shall be importable from spreadsheet templates and through the API in batches, with validation preview, duplicate protection by external reference, and error reports.
- **FR-FIN-JRN-11** Opening balances shall be loadable by account, subledger item (open invoices, open bank items) and dimension, with a control that totals tie out before activation.
- **FR-FIN-JRN-12** Users shall copy, template and reverse journals, attach files, and add comments; a reviewer shall be able to see differences between versions of a draft.
- **FR-FIN-JRN-13** Batch posting and batch approval of selected drafts shall be supported with per-item results.
- **FR-FIN-JRN-14** The journal screen shall support keyboard-first entry, line copy, balancing line suggestion, account search by code or name in any language, and recent-account shortcuts.
- **FR-FIN-JRN-15** Every GL line shall allow drill-down to its source document and to the posting rule that created it, and every source document shall show its accounting entries (accounting preview, even before posting).
- **BR:** posting date must fall in an open period for the user's role; a journal cannot post to a closed period unless a permitted role posts under an approved exception.
- **AC:** a user entering a journal that does not balance cannot post; a posted entry cannot be edited; the reversal links to the original and both appear in the trial balance effects correctly.

### 4.2 Balances and ledger inquiry

- **FR-FIN-JRN-20** The system shall maintain balances per ledger, account, period and dimension combination for fast trial balance and inquiry, updated in the posting transaction.
- **FR-FIN-JRN-21** Ledger inquiry shall filter by account ranges, dimensions, date ranges, partner, source module and document type, with running balance, drill-down, and export.
- **FR-FIN-JRN-22** Trial balance shall be available by period and cumulative, by any dimension, with comparatives, in transaction, base or group currency.
- **FR-FIN-JRN-23** Balance carry-forward at year end shall create opening balances of the next year by a controlled process (section 12).

### 4.3 Foreign currency

- **FR-FIN-JRN-30** Documents in foreign currency shall record transaction amount, rate, rate type and date, and base-currency amount; rate lookup uses the document date unless overridden with permission.
- **FR-FIN-JRN-31** Realized exchange differences shall be posted on settlement of open items automatically to configured accounts.
- **FR-FIN-JRN-32** Unrealized exchange differences shall be calculated by a revaluation run for open items and foreign-currency balance-sheet accounts, posted with automatic reversal on the next period, per ledger.
- **FR-FIN-JRN-33** Rate gaps shall be reported before period close, and manual rate override shall be audited.
- **AC:** a vendor invoice in USD paid at a different rate posts a realized difference; the month-end revaluation posts an unrealized difference that reverses automatically on day one of the next month.

## 5. Posting engine and rules (PST)

**Purpose:** let every module create accounting entries without hard-coded accounts, and let customers change the mapping through configuration.

- **FR-FIN-PST-01** Finance shall subscribe to published events of other modules (kernel Architecture section 4.4) and create journals through posting rules.
- **FR-FIN-PST-02** A posting rule shall define: event type, conditions (rules engine), ledger scope, journal type, line templates (debit or credit, amount expression, account determination, dimension sources, narration template, tax line behavior), and effective dates.
- **FR-FIN-PST-03** Account determination shall resolve accounts from tables keyed by combinations such as item category, partner group, tax code, document type, location, company and payment method, with fallbacks and a trace of how each account was resolved.
- **FR-FIN-PST-04** Rules shall be versioned and effective-dated; changes require maker-checker and a passing simulation before activation.
- **FR-FIN-PST-05** A simulator shall let users run rules against a sample or real source document and show the journals that would result, without posting.
- **FR-FIN-PST-06** Posting shall be idempotent on (company, ledger, source module, source reference, posting version) \[Baseline 1.0 correction C-37: ledger added to the key; see section 24.2\]; replays shall not duplicate entries; reversals and amendments of the source document shall create linked reversing and new entries.
- **FR-FIN-PST-07** A posting that fails (missing account mapping, closed period, invalid dimension, missing rate) shall go to the posting exception queue with reason and suggested fix; users with permission shall correct the cause and replay; failures shall not block the originating module. \[Baseline 1.0 note: proposed decision D-31 makes atomic posting the default and this queue the exception; see section 24.1. Pending approval.\]
- **FR-FIN-PST-08** Posting rules and mappings shall ship as baseline rule sets by module and country pack (for example payroll expense and liability mapping, goods receipt and invoice receipt for inventory), editable by the tenant.
- **FR-FIN-PST-09** Document-level account overrides shall be permitted on source documents for roles allowed to do so, recorded and audited.
- **FR-FIN-PST-10** Posting statistics shall show volumes, failure rates, latency and backlog, with alerts.
- **FR-FIN-PST-11** When Finance is not licensed, the same events shall be consumed by the Accounting Export connector, which maps them to files or to an external system's format (kernel document 2, INT-09).
- **BR:** a source document in a closed period cannot post silently; the exception queue shows it and requires a controlled decision (reopen, post to current period by policy, or reject).
- **AC:** an administrator changes the account determination for a category from one expense account to another with a new effective date; documents dated before keep the old account; the simulator shows both outcomes.

## 6. Accounts payable (AP)

**Purpose:** control what is owed to suppliers from invoice to payment. **Actors:** AP Clerk, Approver, Treasury Officer, Financial Controller.

### 6.1 Vendor invoices and credit notes

- **FR-FIN-AP-01** Vendor invoices shall be entered directly, imported, received from the Procurement module through events, or created from extracted documents; each has header (vendor, vendor invoice number, invoice date, receipt date, due date, currency, payment terms, tax summary, withholding) and lines (account or item or service, quantity, price, tax code, dimensions, narration).
- **FR-FIN-AP-02** The system shall detect duplicate invoices by vendor, invoice number, date and amount, with configurable strictness, and block or warn.
- **FR-FIN-AP-03** Matching of invoices against purchase orders and receipts is performed in Procurement (document 7, MAT); Finance AP shall show the match status, hold unmatched or blocked invoices received from Procurement, and run duplicate detection across invoices keyed in Finance and invoices handed over by Procurement. (wave B)
- **FR-FIN-AP-04** Invoice approval shall follow configurable workflows by amount, vendor, account, dimension and invoice type, with holds, releases and reason codes.
- **FR-FIN-AP-05** Debit notes (vendor credit notes) shall reference the original invoice, restore or reduce payable, and be applicable to open invoices or refunded.
- **FR-FIN-AP-06** Payment terms shall support due date calculation (days, end of month, installments), early-payment discounts with discount accounting, and per-vendor defaults.
- **FR-FIN-AP-07** Recurring vendor invoices (rent, subscriptions) shall be generated from schedules with approval.
- **FR-FIN-AP-08** Withholding tax shall be calculated at invoice or payment according to the tax rules, vendor registration status and thresholds, and shown on the invoice and payment.
- **FR-FIN-AP-09** Landed-cost allocation to inventory receipts and fixed assets shall be supported through the Inventory and Assets modules. (wave B)
- **FR-FIN-AP-10** Document images and e-invoices shall be attachable; AI-assisted extraction shall propose header and line values and account coding for review (document 9). (S, wave B)

### 6.2 Advances and prepayments

- **FR-FIN-AP-20** Vendor advances shall be recorded against a vendor or purchase order and settled against later invoices, with automatic suggestion of open advances.
- **FR-FIN-AP-21** Advance balances and ageing shall be reported separately from payables.

### 6.3 Payments

- **FR-FIN-AP-30** A payment proposal shall select open items by due date, discount deadline, vendor, group, currency, payment method, bank account and cash availability, excluding blocked items, and allow manual adjustment before approval.
- **FR-FIN-AP-31** Payment runs shall be approved through workflow with maker-checker, create payment documents per vendor, post to the bank or clearing account, and update invoice statuses, including partial payments.
- **FR-FIN-AP-32** Payment methods shall be configurable (bank transfer, cheque, cash, card, online payment) with accounts, rules and output formats.
- **FR-FIN-AP-33** Bank payment files shall be generated in formats supplied by country and bank packs and connectors (for example bulk transfer files), with file validation, secure delivery options, and import of acknowledgement files to update statuses. (formats per pack, verified with banks)
- **FR-FIN-AP-34** Remittance advices shall be generated and sent by email or portal in the vendor's language.
- **FR-FIN-AP-35** Payments shall be voidable under control before clearing, creating reversal entries and restoring open items; payments already reconciled to the bank require the reconciliation to be reopened.
- **FR-FIN-AP-36** Direct payments (without a proposal) and petty-cash payments shall be supported with the same controls.
- **FR-FIN-AP-37** Payment segregation of duties shall prevent the creator of a payment from approving it and shall require a second approver above configurable amounts; vendor bank-account changes shall follow maker-checker (kernel PTY-05).
- **FR-FIN-AP-38** Vendor refunds and offsets (netting of payables against receivables for partners that are both) shall be supported with approval.

### 6.4 Reconciliation and reporting

- **FR-FIN-AP-40** Vendor statement reconciliation shall compare vendor-supplied statements to open items and highlight differences. (S)
- **FR-FIN-AP-41** Reports shall include payables ageing (by due date or invoice date, configurable buckets), vendor ledger and balances, open items, payments register, cash requirements by week, discounts taken and lost, withholding summary, and a payables-to-GL reconciliation.
- **FR-FIN-AP-42** Vendor balance confirmation letters shall be generated in bulk with response tracking. (S)
- **BR:** the sum of open vendor items equals the AP control account balance for each company, ledger and currency at any time; invoices in closed periods cannot be edited.
- **AC:** a payment run for 200 invoices is proposed, adjusted, approved by a second user, produces payments, a bank file and remittance emails, and the payables ageing and AP control account match afterwards.

## 7. Accounts receivable (AR)

**Purpose:** invoice customers, collect cash and manage credit risk. **Actors:** AR Clerk, Treasury Officer, Credit Controller, Financial Controller.

### 7.1 Customer invoices and credit notes

- **FR-FIN-AR-01** Customer invoices shall be entered directly or created from Sales events, with header (customer, invoice date, due date, currency, terms, tax summary, references) and lines (item, service or account, quantity, price, discount, tax, dimensions).
- **FR-FIN-AR-02** Invoice types shall be configurable (tax invoice, simplified invoice, pro forma, down-payment, credit note, debit note) with numbering, templates, tax treatment and e-invoicing behavior per type.
- **FR-FIN-AR-03** Credit and debit notes shall reference original invoices, adjust receivable and tax, and be applicable or refundable.
- **FR-FIN-AR-04** Recurring invoices and billing schedules (retainers, subscriptions, installments) shall generate invoices on schedule with approval and notification. (wave B)
- **FR-FIN-AR-05** Invoices shall be issued in the customer's language and currency with company letterhead, bank details, QR codes where required, and be emailable and printable in bulk.
- **FR-FIN-AR-06** Invoice approval workflows shall be configurable by amount, customer and type before issue.
- **FR-FIN-AR-07** Discounts, rounding, multiple taxes per line, inclusive and exclusive pricing shall follow the kernel tax engine.
- **FR-FIN-AR-08** Interest on overdue balances shall be calculable by configurable rules and generate invoices or debit notes on approval. (S)

### 7.2 Receipts and allocation

- **FR-FIN-AR-20** Receipts shall be recorded for cash, cheque, bank transfer, card and online payments, with bank account, reference, date, currency and rate.
- **FR-FIN-AR-21** Allocation shall match receipts to open invoices manually, by suggestion, or by rules (reference, amount, customer), support partial, over and under payments, and keep unapplied cash visible as customer credit.
- **FR-FIN-AR-22** Customer advances and down-payments shall be recorded and settled against later invoices.
- **FR-FIN-AR-23** Write-offs and small-difference adjustments shall follow thresholds and approvals and post to configured accounts.
- **FR-FIN-AR-24** Batch deposits (a group of cheques or cash banked together) shall be recorded and reconciled as one bank line.
- **FR-FIN-AR-25** Receipts shall be reversible under control, with restoration of open items and notifications.
- **FR-FIN-AR-26** Withholding tax deducted by customers at payment shall be recorded against the receipt, tracked as a tax receivable by certificate until collected or claimed, and shown in customer statements.

### 7.3 Credit, collections and dunning

- **FR-FIN-AR-30** Credit limits per customer and company shall consider open invoices, unapplied cash, open orders from Sales and uninvoiced deliveries; Sales shall be able to query exposure and receive allow, warn or block answers. (query contract available in wave A; Sales consumption in wave C)
- **FR-FIN-AR-31** Credit holds and releases shall be recorded with reason, user and workflow approval.
- **FR-FIN-AR-32** Dunning shall define levels, days overdue, templates per language, channels (email, SMS, letter), fees and holds, run on a schedule with review before sending, and keep a dunning history per customer.
- **FR-FIN-AR-33** A collections worklist shall prioritize customers by overdue amount, risk and promised dates, with notes, promises to pay, disputes, assignment to collectors, and next-action dates.
- **FR-FIN-AR-34** Disputed invoices shall be flagged with reason, owner and resolution, excluded from dunning while disputed, and reported.
- **FR-FIN-AR-35** Provisioning for doubtful debts shall be calculable from ageing-based rates or rules with journal proposals reviewed and posted by the controller; expected-credit-loss methods follow later waves. (basic in wave A)
- **FR-FIN-AR-36** Customer statements and balance confirmations shall be generated in bulk and delivered by email or portal.

### 7.4 Reporting

- **FR-FIN-AR-40** Reports shall include receivables ageing, customer ledger and balances, open items, collections effectiveness (days sales outstanding), receipts register, unapplied cash, dunning history, credit exposure, and a receivables-to-GL reconciliation.
- **BR:** the sum of open customer items equals the AR control account balance for each company, ledger and currency; invoices issued with legal numbers cannot be deleted, only credited.
- **AC:** a receipt for three invoices is auto-allocated by reference, a short payment leaves a partial balance, a withholding certificate amount is tracked, and the ageing and AR control account agree.

## 8. Banking and cash (BNK)

**Purpose:** know cash positions, reconcile bank activity and control instruments such as cheques. **Actors:** Treasury Officer, Accountant.

### 8.1 Bank accounts and statements

- **FR-FIN-BNK-01** Bank accounts shall be defined per company with bank, account number (encrypted), IBAN, currency, GL account, signatories, payment and receipt methods, and status.
- **FR-FIN-BNK-02** Statements shall be imported from files (CSV and Excel with mapping templates, MT940, CAMT.053, OFX and bank-specific formats supplied by packs) and, later, through bank connectors; duplicate statement lines shall be rejected by line identity.
- **FR-FIN-BNK-03** Statement import templates shall be configurable per bank with column mapping, date and number formats, and transformation rules, and be saved for reuse and scheduling.

### 8.2 Reconciliation

- **FR-FIN-BNK-10** The reconciliation workspace shall show bank lines and book items side by side, with automatic matching by rules (amount, date tolerance, reference, partner, description patterns), one-to-one, one-to-many and many-to-many matching, and manual match, split and merge.
- **FR-FIN-BNK-11** Rules shall also create entries from unmatched statement lines (bank charges, interest, tax) with configured accounts and dimensions, subject to approval where configured.
- **FR-FIN-BNK-12** Reconciliation shall have statuses per line and per statement, produce a reconciliation report (book balance, bank balance, outstanding items), and be lockable with approval.
- **FR-FIN-BNK-13** AI-assisted match suggestions shall propose matches and entries from history with explanations, always confirmed by the user. (S)
- **FR-FIN-BNK-14** Unreconciled item ageing and a reconciliation status dashboard by bank account shall be available.

### 8.3 Cheques and instruments

- **FR-FIN-BNK-20** Cheque books shall be registered per bank account with series, leaves, status and custody; issuing a cheque shall consume a leaf and prevent reuse.
- **FR-FIN-BNK-21** Cheque printing shall use configurable templates per bank with amount in words in English, Urdu and Arabic, date formats, crossing and payee fields, and positive-pay style export where banks support it.
- **FR-FIN-BNK-22** Post-dated cheques issued and received shall be tracked with maturity date, status and reminders; their accounting treatment (memorandum, clearing or receivable accounts) shall be configurable.
- **FR-FIN-BNK-23** Cheque statuses shall include issued, printed, handed over, presented, cleared, bounced, cancelled, lost and stale; a bounced cheque shall reopen the underlying invoice or payable and trigger notifications and optional charges.
- **FR-FIN-BNK-24** Stale-dated cheque rules shall be configurable with reminders and reversal entries.

### 8.4 Transfers, petty cash and cash position

- **FR-FIN-BNK-30** Transfers between company bank and cash accounts shall support same-currency, cross-currency (with rate and difference accounting), and intercompany transfers with automatic balancing.
- **FR-FIN-BNK-31** Petty cash funds shall be imprest-based with custodians, vouchers, approvals, replenishment requests, and counts with difference accounting.
- **FR-FIN-BNK-32** A cash position dashboard shall show balances by bank and currency, in transit items, and available cash.
- **FR-FIN-BNK-33** Cash flow forecasting shall project inflows and outflows from open AR and AP, payroll and recurring items, and budgets by week and month, with scenarios. (wave B)
- **FR-FIN-BNK-34** Letters of credit, bank guarantees and loan tracking shall be supported in a later wave with their own specification. (wave C)
- **BR:** a reconciled statement line cannot be changed without reopening the reconciliation with approval; cheque leaves cannot be reused once issued.
- **AC:** 2,000 imported statement lines reconcile at least 80 percent automatically on sample data with configured rules, bank charges create entries by rule, and a bounced cheque reinstates the invoice with a notification.

## 9. Tax (TAX)

**Purpose:** record taxes correctly, support returns and certificates, and connect to authority systems through packs. **Actors:** Tax Officer, Accountant, Financial Controller.

- **FR-FIN-TAX-01** Tax lines on documents shall use the kernel tax engine and post to configured tax accounts through posting rules, separately for input, output, withholding and other taxes, per ledger.
- **FR-FIN-TAX-02** Tax periods shall be configurable per company and tax type (monthly, quarterly, annual), with statuses and lock after filing.
- **FR-FIN-TAX-03** Tax reports shall include transaction-level detail, summary by tax code and period, input and output reconciliation to the GL, exemption and reverse-charge listings, and withholding summaries; statutory formats shall come from country packs.
- **FR-FIN-TAX-04** Return preparation worksheets shall assemble return boxes from tax data with drill-down to documents, adjustments by journal with reason, and export in authority-accepted formats supplied by packs.
- **FR-FIN-TAX-05** Withholding certificates shall be generated from payments with numbering, templates per language and delivery to payees, and tracked until reported; annual withholding statements shall be produced from the same data.
- **FR-FIN-TAX-06** Tax payments shall be recorded and reconciled to liability accounts, with reminders for due dates by calendar.
- **FR-FIN-TAX-07** Tax adjustments (bad debt relief, corrections, credit notes) shall follow country rules through pack-provided posting rules.
- **FR-FIN-TAX-08** E-invoicing integration points shall allow packs and connectors to submit, clear or report invoices to authorities, store responses (identifiers, QR data, status), handle rejections with a work queue, and block or warn on issuing invoices that failed required submission.
- **FR-FIN-TAX-09** Invoice templates shall carry required legal elements (tax IDs, QR codes, sequence numbers, language and wording) from packs and be versioned with effective dates.
- **FR-FIN-TAX-10** A tax audit export shall produce a standard audit file of ledger and subledger data for a period for authorities or auditors. (S)
- **FR-FIN-TAX-11** Tax determination via external services (for example US sales tax) shall be called through connectors with caching, fallbacks and traceability of the result used.
- **BR:** tax periods that have been filed are locked; adjustments go in the next period with reference; every tax amount is traceable to a document and rule.
- **AC:** with a country pack installed, a tax return worksheet reproduces the totals of the tax ledger for the period and every box drills down to the source documents.

## 10. Fixed-asset accounting hooks (FXA)

**Purpose:** account for fixed assets in the ledger while the Assets module (wave B) owns the asset register, depreciation calculation and lifecycle. **Actors:** Financial Controller, Accountant, Asset Manager.

- **FR-FIN-FXA-01** Finance shall define, per asset class and ledger, the accounts for cost, accumulated depreciation, depreciation expense, construction in progress, disposal proceeds, gain or loss on disposal, revaluation reserve and impairment.
- **FR-FIN-FXA-02** Asset-related postings shall be created through posting rules from Assets events (acquisition, capitalization from construction in progress, depreciation, transfer between companies or cost centers, disposal, revaluation, impairment).
- **FR-FIN-FXA-03** Vendor invoice lines marked as asset purchases shall be published to Assets (through VendorInvoicePosted for invoices keyed in Finance, or SupplierInvoiceApproved for invoices matched in Procurement), which creates the acquisition with the dimensions and class, and the asset control account shall be debited rather than an expense.
- **FR-FIN-FXA-04** Depreciation books of the Assets module (for example local, IFRS, tax) shall map to Finance ledgers so each ledger receives its own depreciation postings.
- **FR-FIN-FXA-05** The asset subledger total by class shall reconcile to the control accounts, with a reconciliation report and a block on period close when it does not tie out.
- **FR-FIN-FXA-06** When the Assets module is not licensed, assets can be accounted for through manual journals and the same control accounts, with an optional lightweight register export from journals. (S)
- **AC:** a depreciation run in Assets for a period creates journals per ledger through the posting engine, the control accounts agree to the asset subledger, and a closed period rejects a late run into the exception queue.

## 11. Budgeting and budget control (BUD)

**Purpose:** plan spending and income and control commitments. **Actors:** Budget Owner, Financial Controller, CFO, Approver. **Wave:** entry, versions, reports and warn-or-block on journals and AP invoices in wave A; commitments from Procurement, rolling forecasts and driver-based planning in wave B.

- **FR-FIN-BUD-01** Budgets shall be defined per company and ledger by account or account group, dimension combination (cost center, department, project, custom), and period, with currency and fiscal year.
- **FR-FIN-BUD-02** Multiple budget versions and scenarios shall exist per year (original, revised, forecast, what-if) with status (draft, submitted, approved, locked), and one version marked as the control version.
- **FR-FIN-BUD-03** Budget entry shall support grid entry, spreadsheet import and export, copy from prior-year actuals or budgets with percentage uplift, spreading by pattern (even, seasonal, custom), formulas and driver-based lines (wave B), and owner assignment by dimension.
- **FR-FIN-BUD-04** Budget approval shall use workflow by owner, amount and dimension, with comments and version compare.
- **FR-FIN-BUD-05** Budget control policies shall be configurable per account, group, dimension and document type with outcomes none, warn, or block, over a configurable control period (month, quarter, year) and level (line, dimension, company).
- **FR-FIN-BUD-06** Control shall be applied on journals, AP invoices and, in wave B, purchase requisitions and orders, with a message that states available budget, committed and actual amounts.
- **FR-FIN-BUD-07** Commitments (encumbrances) shall be recorded from approved requisitions and purchase orders and relieved by invoices or receipts, shown separately as committed amounts, and released when documents close or cancel. (wave B)
- **FR-FIN-BUD-08** Budget amendments and transfers between lines or dimensions shall follow approval workflows and keep a change history; exceptions to a blocking policy shall be possible only through an approved override with reason.
- **FR-FIN-BUD-09** Unspent budget carry-forward to the next year shall be configurable by account type with approval.
- **FR-FIN-BUD-10** Reports shall show budget versus actual versus commitment by any dimension and period, variance in amount and percent, year-to-date and full-year outlook, with drill-down and explanations by owners.
- **FR-FIN-BUD-11** Headcount and payroll cost budgets shall link to HRMIS position plans when licensed. (wave C)
- **AC:** a block policy on travel expense prevents posting an invoice that exceeds the available budget and shows the figures; an approved override posts it and is visible in the budget report.

## 12. Period-end close (CLS)

**Purpose:** close periods and years consistently, with controls. **Actors:** Financial Controller, Accountant, CFO.

- **FR-FIN-CLS-01** A close calendar shall show periods per company and ledger with statuses (future, open, soft-closed, closed) and per-module statuses (AP, AR, bank, inventory, assets, payroll) from the organization calendar.
- **FR-FIN-CLS-02** Close checklists shall be definable per company, with tasks, owners, due dates, dependencies, instructions, attachments, status and sign-off; templates ship with packs and are copied to each close.
- **FR-FIN-CLS-03** Automatic checks shall run on demand and before close: draft or unapproved journals, unposted subledger documents, failed postings in the exception queue, unreconciled bank statements, missing exchange rates, subledger versus control account differences, open workflow items, depreciation not run, payroll not posted.
- **FR-FIN-CLS-04** Standard close activities shall be schedulable and traceable: recurring journals, accrual and deferral releases, prepaid amortization, allocations, revaluation, provisions, and intercompany reconciliation.
- **FR-FIN-CLS-05** Soft close shall allow only designated roles to post; final close shall block all posting to the period; each step shall record who and when.
- **FR-FIN-CLS-06** Reopening a closed period shall require approval, a reason, and an automatic list of dependent runs to repeat; reopening shall be restricted by role and shown in a reopen log.
- **FR-FIN-CLS-07** Year-end close shall run as a controlled process: close profit-and-loss accounts to retained earnings per ledger, carry balances forward to opening balances of the new year (including open items and dimensions), generate a year-end report pack, and lock the year; a trial run shall be possible before the final run.
- **FR-FIN-CLS-08** Adjustment periods shall be available after the last regular period for audit adjustments, included in annual statements and excluded from monthly ones.
- **FR-FIN-CLS-09** Lock dates shall be configurable by role and module (for example AP clerks lock earlier than controllers) and apply to API and imports.
- **FR-FIN-CLS-10** A close dashboard shall show checklist progress, blockers, time since period end, and trends of days to close.
- **BR:** year-end close cannot run while earlier periods are open unless approved by a permitted role; carry-forward balances must equal prior-year closing balances for each ledger.
- **AC:** the controller runs automatic checks, sees three failed postings and one unreconciled statement, resolves them, soft-closes, and then closes the period; a later attempt to post a vendor invoice into it goes to the exception queue.

## 13. Multi-company and consolidation (CON)

**Purpose:** operate groups with intercompany activity and produce consolidated statements. **Wave:** B. **Actors:** Group Controller, Financial Controller.

### 13.1 Intercompany

- **FR-FIN-CON-01** Companies shall be representable as intercompany partners with configured receivable, payable, revenue and expense accounts per counterparty.
- **FR-FIN-CON-02** Intercompany transactions (invoices, loans, transfers, cost recharges) shall create mirrored documents or journals in the counterpart company with links and consistent amounts, and flag differences.
- **FR-FIN-CON-03** An intercompany reconciliation report shall list matched and unmatched items by counterparty and period, with adjustment proposals and a close gate.

### 13.2 Consolidation

- **FR-FIN-CON-10** Consolidation groups shall be defined with member companies, ownership percentages, method (full, proportional; equity in a later wave), group currency and group chart mapping with effective dates.
- **FR-FIN-CON-11** Currency translation shall support current, average and historical rates by account class, with translation differences recorded in the configured equity account.
- **FR-FIN-CON-12** Consolidation runs shall convert, map, aggregate and eliminate intercompany balances and transactions by configured elimination rules, producing a consolidation ledger with run versions and the ability to compare runs.
- **FR-FIN-CON-13** Consolidation adjustments (manual and recurring) shall be journals in the consolidation ledger with approval and attachments.
- **FR-FIN-CON-14** Non-controlling interest and goodwill shall be calculated for ownership under 100 percent in a later wave. (wave C)
- **FR-FIN-CON-15** Consolidated statements shall drill down to company ledgers and to source documents.
- **FR-FIN-CON-16** A pre-consolidation check shall report missing mappings, unreconciled intercompany differences, rate gaps and companies not yet closed.
- **AC:** a three-company group in two currencies consolidates a month with intercompany loans and sales; eliminations leave zero intercompany balances and translation differences are shown in equity.

## 14. Financial reporting (RPT)

**Purpose:** statutory and management reporting that is correct, drillable, and configurable. **Actors:** CFO, Financial Controller, Accountant, Auditor, Budget Owner.

- **FR-FIN-RPT-01** Standard reports shall include trial balance, general ledger, journal register, balance sheet, income statement, cash flow statement (indirect, and direct where cash flags are used), statement of changes in equity, comparative statements, and notes templates.
- **FR-FIN-RPT-02** A financial report writer shall let finance users define rows (accounts, ranges, groups, formulas, subtotals, text), columns (periods, year-to-date, budget, prior year, variance, currencies) and filters (companies, ledgers, dimensions), save as reusable formats, and publish to roles.
- **FR-FIN-RPT-03** Statutory formats (local balance sheet and income statement layouts, regulator submissions) shall come from country and industry packs and be editable without altering the pack.
- **FR-FIN-RPT-04** Every figure shall drill down from statement to account, ledger lines, and source documents, within the user's permissions.
- **FR-FIN-RPT-05** Reports shall run for a single company, multiple companies with currency conversion, or a consolidation group, in transaction, base or group currency.
- **FR-FIN-RPT-06** Management reports by dimension shall include profit and loss by cost center, department and project, contribution analysis, and period comparisons.
- **FR-FIN-RPT-07** Subledger reports shall include AP and AR ageing and ledgers, bank reconciliation, tax reports, fixed-asset registers by class (when Assets is licensed), and budget reports.
- **FR-FIN-RPT-08** Reports shall be exportable to PDF and Excel with formatting, scheduled for delivery, and subscribable by role; finalized report packs for a closed period shall be stored and versioned.
- **FR-FIN-RPT-09** Report approval and sign-off shall record who reviewed a statement for a period.
- **FR-FIN-RPT-10** Key financial ratios and KPIs (liquidity, working capital, margins, days sales outstanding, days payable outstanding) shall be available to dashboards through the semantic layer (document 9).
- **FR-FIN-RPT-11** XBRL and regulator-specific electronic filings shall be supported through packs and connectors in later waves. (wave C)
- **FR-FIN-RPT-12** Report performance shall use maintained balances so that a trial balance for a year with 5 million lines returns within 5 seconds at the 95th percentile and drill-down loads within 3 seconds.
- **AC:** a controller builds a comparative income statement by cost center with budget variance in the report writer without developer help, drills into a variance to see the source documents, and schedules it monthly to the CFO.

## 15. Cost accounting (CST)

**Purpose:** attribute costs to responsibility centers and activities. **Wave:** B. **Actors:** Cost Accountant, Financial Controller.

- **FR-FIN-CST-01** Cost centers and profit centers shall be managed as dimensions with hierarchies, owners, validity, and links to organization units.
- **FR-FIN-CST-02** Allocation cycles shall move costs between dimension values by distribution (fixed percentages, drivers) or assessment (to secondary accounts), run in sequence by cycle with simulation, posting to a specified ledger, and reversal.
- **FR-FIN-CST-03** Statistical drivers (headcount, floor area, machine hours, units) shall be maintained manually, imported, or sourced from other modules.
- **FR-FIN-CST-04** Internal charging between cost centers shall be supported by recharge rules and posted as journals with approvals.
- **FR-FIN-CST-05** Product costing hooks shall receive inventory cost data and post variances (purchase price, usage, revaluation) by posting rules when Inventory provides standard-cost methods. (wave C)
- **FR-FIN-CST-06** Project cost and revenue tracking through project dimensions shall report actuals against project budgets; full project accounting with billing is planned in a later wave. (wave C)
- **AC:** a monthly allocation cycle distributes shared service costs across five departments by headcount, the simulation shows the result before posting, and the reversal restores the original balances.

## 16. Country pack notes for Finance

These are planning inputs, not statements of law. Each item must be verified with a qualified accountant or tax adviser in the country, and the pack tested against independent calculations, before release.

| Topic | Pakistan | Saudi Arabia | United States |
| --- | --- | --- | --- |
| Fiscal year and calendars | July to June common; Gregorian | Gregorian or Hijri basis depending on entity; Hijri display needed | Flexible; calendar or custom fiscal year |
| Indirect tax | Federal sales tax and provincial sales taxes on services with different registrations and rates | 15 percent VAT with returns and e-invoicing | State and local sales tax; no federal VAT; rates through an external service |
| Withholding | Withholding on supplies, services and contracts, with certificates and annual statements; customers also withhold from payments to the company | Withholding on certain payments to non-residents | Vendor reporting (for example 1099 forms) and backup-withholding rules |
| Direct tax reporting | Income tax working papers and statutory formats | Zakat and corporate income tax computation worksheets by ownership | Tax-basis ledger and reporting by entity type |
| E-invoicing | Authority integration for certain sectors (to be scoped by sector) | Phased national e-invoicing: invoice generation standards, QR codes, and reporting or clearance integration | Not mandated federally; customer portal formats as connectors |
| Payments | Cheques and post-dated cheques widely used; bank transfer files by bank; domestic real-time rails through bank connectors | Bank transfer files and wage-protection files (with HRMIS); domestic payment rails through connectors | ACH files (NACHA), positive pay, check printing with MICR line |
| Language and documents | Urdu and English invoices and vouchers | Arabic and English invoices; amounts in words in Arabic | English; optional Spanish |
| Charts of accounts | Local template aligned to local reporting formats | Template aligned to local statutory formats | GAAP-oriented templates by entity type |

Pack responsibilities: tax codes and rules, withholding rules and certificates, chart templates, invoice and voucher templates, statutory reports, payment file formats, e-invoicing connectors, holiday and calendar data, and country validations (tax ID formats, bank account formats).

## 17. Events and contracts

### 17.1 Events consumed by Finance

| Event | Publisher | Result in Finance |
| --- | --- | --- |
| PayrollRunFinalized, FinalSettlementApproved | HRMIS | Payroll expense, liabilities, statutory payables journals by posting rules |
| ExpenseClaimApproved, LoanDisbursed (loans and advances), LeaveLiabilityCalculated, GratuityAccrualCalculated | HRMIS | Employee payables or receivables, expense postings, payment requests, leave and gratuity accrual journals |
| SupplierInvoiceApproved | Procurement | AP invoice creation with matching references |
| PurchaseRequisitionApproved, PurchaseRequisitionChanged, PurchaseRequisitionClosed, PurchaseOrderIssued, PurchaseOrderRevised, PurchaseOrderCancelled, PurchaseOrderClosed | Procurement | Budget commitments created or released |
| GoodsReceiptPosted, GoodsIssuePosted, StockAdjustmentPosted, StockRevaluationPosted, StockTransferShipped, StockTransferReceived | Inventory | Inventory, goods-received-not-invoiced, cost of goods sold, adjustment journals |
| AssetAcquired, DepreciationCalculated, AssetTransferred, AssetDisposed, AssetRevalued, AssetImpaired | Assets | Asset, depreciation and disposal journals per ledger |
| SalesInvoiceIssued, SalesCreditNoteIssued, SalesReceiptRecorded, RebateAccrued, BillingPeriodEvent | Sales | AR invoices, credit notes, receipts, rebate accruals, deferred revenue periods |
| PeriodCloseRequested | Kernel organization | Close checks and checklist triggers |

Additional events consumed by Finance (document 14, section 4; decisions D-15 and D-12): from Procurement, ReceiptPosted for non-stock items and ServiceAccepted (accruals for goods and services received not invoiced, never for stock items, which accrue from Inventory's GoodsReceiptPosted) and GoodsReturnRecorded (return adjustments); from Sales, CommissionApproved (payables for external agents); from Supply Chain, FreightAccrued (accruals) and CarrierInvoiceApproved (payables). Supplier invoices matched in Procurement arrive as SupplierInvoiceApproved; invoices without orders are keyed directly in Finance AP.

### 17.2 Events published by Finance

| Event | Consumers |
| --- | --- |
| PeriodClosed, PeriodReopened | All modules (lock dates), reporting |
| JournalPosted, PostingFailed | Reporting, notifications, audit |
| VendorInvoicePosted, VendorInvoicePaid, PaymentIssued, PaymentVoided | Assets (asset-flagged lines of invoices keyed in Finance), Procurement, HRMIS (payment status), notifications |
| CustomerCreditHoldChanged, CustomerExposureChanged, CustomerInvoicePaid, ReceiptAllocated | Sales |
| BudgetCommitmentChanged, BudgetThresholdReached | Procurement, notifications |
| AccountingEntriesAvailable (per source document) | The source module's accounting preview and drill-down |

### 17.3 Synchronous contracts

| Contract | Consumer | Behavior without Finance |
| --- | --- | --- |
| Check budget availability | Procurement, Sales | Always allowed |
| Get customer credit exposure | Sales | Always allowed |
| Get exchange rate | All modules | Kernel service answers |
| Preview accounting entries for a document | Any module | Not shown |

## 18. Roles, permissions and default workflows

### 18.1 Baseline roles

| Role | Main permissions |
| --- | --- |
| CFO | View all, approve budgets and reopen requests, close and sign off periods, view consolidation |
| Financial Controller | Manage COA, ledgers, posting rules (via maker-checker), close, approve journals, reconcile |
| Accountant | Create and post journals within limits, run allocations and revaluation, reconcile bank |
| AP Clerk | Enter and submit vendor invoices, prepare payment proposals |
| AR Clerk | Enter invoices, record receipts, allocate |
| Treasury Officer | Approve payment runs, manage bank accounts, cheques, transfers |
| Tax Officer | Manage tax configuration, returns, certificates |
| Credit Controller | Manage credit limits, holds, dunning, collections |
| Budget Owner | Enter budgets for owned dimensions, view reports |
| Auditor | Read-only with drill-down and export by approval |

### 18.2 Default workflows (all editable)

| Workflow | Default route |
| --- | --- |
| Manual journal | Accountant submits, controller approves above threshold |
| Vendor invoice | AP clerk submits, department approver, finance approver above threshold |
| Payment run | Treasury prepares, second approver above threshold |
| Vendor bank account change | Maker-checker, notify finance head |
| Customer write-off | Credit controller, controller above threshold |
| Period reopen | Controller requests, CFO approves |
| Posting rule and COA change | Maker-checker with passing simulation |
| Budget approval | Budget owner, department head, finance |

## 19. Data model overview

Tables live in the fin schema (kernel references as in document 3). Column-level design follows the data standards and is completed in the migrations.

| Area | Main tables |
| --- | --- |
| COA and ledgers | ledgers, charts\_of\_accounts, accounts, account\_groups, account\_mappings, dimensions, dimension\_values, dimension\_rules, journal\_types |
| Journals | journal\_entries, journal\_lines (partitioned by posting date), account\_balances, recurring\_journal\_defs, allocation\_cycles, allocation\_lines, revaluation\_runs |
| Posting engine | posting\_rules, posting\_rule\_versions, account\_determination\_tables, account\_determination\_rows, posting\_exceptions, outbox\_messages, inbox\_messages |
| Accounts payable | vendor\_invoices, vendor\_invoice\_lines, vendor\_credit\_notes, vendor\_advances, payment\_proposals, payment\_runs, vendor\_payments, ap\_open\_items, vendor\_statements |
| Accounts receivable | customer\_invoices, customer\_invoice\_lines, customer\_credit\_notes, receipts, receipt\_allocations, ar\_open\_items, dunning\_levels, dunning\_runs, collection\_cases, credit\_holds, provisions |
| Banking | bank\_accounts, bank\_statements, bank\_statement\_lines, reconciliation\_matches, reconciliation\_rules, cheque\_books, cheques, petty\_cash\_funds, petty\_cash\_vouchers, bank\_transfers |
| Tax | tax\_periods, tax\_returns, tax\_return\_lines, withholding\_certificates, withholding\_lines, e\_invoice\_submissions |
| Budgeting | budgets, budget\_versions, budget\_lines, budget\_policies, commitments, budget\_transfers |
| Close | close\_calendars, close\_checklists, close\_tasks, close\_checks, reopen\_log |
| Consolidation | intercompany\_pairs, consolidation\_groups, consolidation\_members, elimination\_rules, consolidation\_runs, consolidation\_lines |
| Reporting | report\_formats, report\_rows, report\_columns, report\_packs |

Key design points: journal\_lines and bank\_statement\_lines are partitioned; open-item tables hold the current state of invoices and are reconcilable to journal lines; posted documents and journals are append-only; every document carries the standard columns, custom fields, workflow instance and numbering from the kernel.

## 20. AI skills for Finance

All skills run through the AI Gateway with the user's permissions, propose and never post, and are switchable per tenant (kernel document 2, section 22).

| Skill | Description | Wave |
| --- | --- | --- |
| Bank reconciliation suggestions | Propose matches and entries from history with explanations | A to B |
| Journal drafting | Draft a journal from a description or a document, for review | A |
| Anomaly flags | Highlight unusual journals, duplicate invoices, round-sum or weekend postings, and threshold-splitting patterns | B |
| Invoice extraction and coding | Read invoice documents, propose header, lines and accounts | B |
| Variance commentary | Draft explanations of budget and period variances from data for owner review | B |
| Collections assistant | Prioritize overdue customers and draft reminder messages | B |
| Cash forecast | Forecast inflows and outflows with ranges | B |
| Close assistant | Summarize close status, blockers and suggested next actions | B |
| Natural-language finance questions | Query balances and reports within scope (document 9) | A to B |

## 21. Non-functional requirements

| Area | Requirement |
| --- | --- |
| Posting performance | A single document posts in under 2 seconds at the 95th percentile; a batch of 100,000 journal lines posts in under 10 minutes |
| Volume | At least 5 million journal lines per year per tenant with reports meeting RPT-12 |
| Concurrency | At least 200 concurrent users per tenant posting and querying without lock timeouts; gap-free numbering correct under load |
| Integrity | Invariants of document 3, section 10 hold after every release |
| Availability and recovery | As in Architecture section 3.7 |
| Auditability | Every posting traceable to user, source and rule; immutable posted records |
| Security | Segregation of duties enforced; sensitive fields (bank accounts) encrypted; export controls on ledgers |
| Usability | Keyboard-first entry, search by code or name in any language, accessible screens, right-to-left support |
| Localization | English, Urdu and Arabic documents and screens; amounts in words in all three |
| Configurability | Accounts, dimensions, workflows, templates, posting rules and reports configurable without code |

## 22. Acceptance and testing

- **Invariant suite:** balanced entries, trial balance equals subledgers, immutability, idempotent posting, numbering under load (document 3, section 10).
- **End-to-end scenarios:** procure to pay (invoice to payment to bank reconciliation), order to cash (invoice to receipt to dunning), month-end close, year-end close and carry forward, multi-currency with revaluation, multi-ledger posting, tax return reproduction, budget block and override.
- **Country pack tests:** independent calculations by a qualified practitioner for each tax and withholding scenario, with reproducible test data.
- **Migration and opening balances:** import of a real customer's trial balance and open items with tie-out control.
- **Parallel run:** at least one full month closed in parallel with the pilot customer's existing system, with differences explained.
- **Performance:** volume and concurrency tests at 1.5 times expected load.
- **Security and permissions:** separation-of-duties matrix tests, role-based access tests for every screen and API.
- **Exit criteria for pilot:** zero severity-1 and severity-2 defects open, parallel month reconciled, auditor walkthrough completed.

## 23. Assumptions and open items

**Assumptions:** Finance is the first business module built; other modules post through events; country rules are delivered and verified through packs; consolidation, full commitments and cost accounting follow in wave B.

**Open decisions:** source and review process for chart-of-accounts templates per country; the list of banks and file formats for the first customers; scope and sequence of e-invoicing integrations per country; whether the financial report writer is built or adopted from an open-source component; cheque printing and MICR support scope; whether expected-credit-loss provisioning moves into wave A; project accounting and revenue recognition scope for wave C.

**Next documents:** 6 HRMIS Functional Specification (revised and aligned to the platform), then 12 Claude Code Operating Manual.

## 24. Baseline 1.0 addendum (this document is now version 0.2)

*Added 14 November 2026 by the comprehensive review (Doc 16). Sections 1 to 23 are unchanged except two marked notes at FR-FIN-PST-06 and FR-FIN-PST-07. The previous state (version 0.1) is kept in this document's version history and in the frozen originals archive.*

### 24.1 Atomic posting contract (proposed D-31)

- **FR-FIN-PST-12** Finance shall publish a synchronous posting contract. A source module calls it inside its own database transaction with the source document, and receives either the created entries or a refusal with reason and suggested fix. Document and entries commit together or not at all.
- **FR-FIN-PST-13** The posting contract shall apply the same posting rules, account determination, period checks, dimension validation and idempotency key as event-based posting. There is one posting engine with two entry points.
- **FR-FIN-PST-14** Each source document type shall have a posting mode setting: atomic (default) or deferred. Deferred mode uses FR-FIN-PST-07 as written, with an ageing alert on the exception queue.
- **FR-FIN-PST-15** A period shall not close for a ledger while the exception queue holds an item dated in that period (strengthens FR-FIN-CLS-03 from a check to a block).
- **FR-FIN-PST-16** A refusal shall name the cause in business terms (period closed for payables, no account mapped for item category X, cost center inactive) and, where the user has permission, link to the screen that fixes it.
- **Existing requirement PST-01 (event subscription)** remains valid for deferred sources and for consumers other than posting. If D-31 is rejected, PST-12 to PST-14 are withdrawn and PST-15 and the ageing alert stay.

### 24.2 Corrections (apply now)

- **C-37 Ledger in the idempotency key.** Because one source can post to several ledgers (COA-09, PST-02), the key is (company, ledger, source module, source reference, posting version).
- **C-38, C-39 Dimensions.** Journal lines carry profit center, item, location and branch as explicit columns in addition to cost center, department, project and partner (Doc 3 section 14.2).
- **C-40 Balances.** Posting appends balance deltas; a job rolls them up. No posting transaction locks an account balance row (Doc 3 section 14.2).

### 24.3 Binding cross-module rules

Doc 16 section 6 applies to this module. Finance is the enforcing party for: FR-XM-01 to FR-XM-08 (posting, immutability, reversal date and order), FR-XM-09 to FR-XM-11 and FR-XM-15 (receipt and invoice accruals, variances, supplier debit balances), FR-XM-25 (depreciation corrections), FR-XM-27 (payroll posting), FR-XM-31 (budget commitment life cycle) and the period close order of section 6.8. Where a requirement in sections 3 to 15 is looser than an FR-XM rule, the FR-XM rule applies.

### 24.4 Scope by phase and size profile

| Phase | Finance scope |
| --- | --- |
| 2 | COA, JRN, PST, AP, AR, BNK, TAX, CLS, core RPT, BUD as warnings, single ledger, multi-company without consolidation |
| 3 | BUD with commitments and hard control, FXA hooks used by Asset Management, document accruals from Procurement and Inventory |
| 4 | CON, CST, multiple ledgers, intercompany automation |

The Micro size profile (Doc 16 section 5.2) hides cost centers, budgets, multiple currencies and period locks by sub-ledger; the records behind them exist with defaults.

### 24.5 Data design gate

The Finance data design (section 19 brought to column level, with diagram) is produced and approved at Phase 2 entry before any Finance migration (proposed D-35; Doc 3 section 14.5).
