# Review: R-305 fix, manager approver follows the subject's employment (CR-116)

- **Reviewer model:** Opus 5.5 (fresh review session, high effort)
- **Date:** 2026-11-25
- **Pull request:** #136, branch `fix/R-305-manager-approver`
- **Diff head reviewed:** `28a46ff15bd10b77ed339cef47417bc68047089d` (`git diff origin/main...HEAD`)
- **Scope:** approval routing, class B (FR-XM-32, FR-KRN-WFL-02). The reviewer read the code and changed none.

## Summary

The fix does what CR-116 says for the two document types it covers. With the old code, a request HR entered for an employee with no sign-in went to HR's own manager. It now goes to the employee's own manager. When the employee has no value or no manager, it goes to the route's fallback. The engine still keeps the requester and the subject out (`Excludes`), and no path added here lets either of them decide. Escalation still goes first to the assignee's manager. The activation check (`WorkflowAdministrationService.SpecProblemAsync`) is unchanged and still correct, because it only asks `Supports`. The new SQL uses parameters only and reads the tenant's own database, and the change adds no endpoint, so tenant isolation is unaffected. No existing test was weakened.

The main weaknesses are in four areas: the new date rule, how much of the defect class is really closed, the tests for the new behaviour, and the accuracy of what the documentation claims.

**Counts:** high 0, medium 3, low 6.

## Product owner ruling (2026-11-25)

**M1: option 1, today's manager.** The `manager` approver of a request is the subject's manager **today** (the business date of the request), the same rule as leave (`LeaveRequestStore.LineManagerAsync`), whatever day the request is about. The document date is not used to pick the manager. This also removes the case of a separated or transferred manager being asked. To record in CR-116 as the ruling; the fix and its tests (M3) follow it.

M2, M3 and L1 to L6 go ahead on the recommendations below (CR-083).

## Findings

### M1 (medium): the "as of the document date" rule can route to a former, separated or transferred manager, and leave does it differently

- **Where:** `src/Platform/Erp.Kernel.Infrastructure/Workflow/ApproverResolution.cs:34-36` and `:59`; `src/Modules/Hrm/Erp.Hrm.Infrastructure/Employments/HrmApproverDirectory.cs:20`, `:55-59`.
- **What:** `DocumentDate` passes `work_date` or `start_date` as `asOf`. `ManagerUsersAsync` then checks the manager's employment with `Current` **on that date**, not today. The only filter applied on today is `ActiveAsync`, which checks whether the user account is active. Nothing in the kernel deactivates a user when their employment is separated (no consumer of `hrm.employee_separated` there), and an inter-company transfer keeps the user active on purpose.
- **Failure scenario:**
  1. Manager A manages employee X until 10 November, then transfers to company 2 or leaves the company. A's user account stays active.
  2. On 25 November, HR enters a regularization for X for 3 November.
  3. A's old employment was current on 3 November, so A is resolved as the approver. If A holds `wf.workflow_tasks.update` without a company limit (a role granted for all companies), A gets the task and can decide attendance for a former report. X's current manager B is never asked.
  4. Even when A's grant is limited to company 2 and A is dropped, the request goes to the HR fallback instead of to B.
  5. A future-dated duty request has the mirror problem: it goes to a manager who takes over next month and does not yet have X in their data scope. That manager can decide the request but cannot open the linked document.
- **Inconsistency:** leave (`LeaveRequestStore.LineManagerAsync`, `LeaveRequestStore.cs:107`) uses the manager **today**. The two HR flows now pick different people for the same employee on the same day. The specification says nothing on which date applies, so per CLAUDE.md this is a rule to confirm, not one to invent.
- **Recommendation:** for a past date, require the manager's employment to be current **both** on the document date and today, or simply resolve as of `min(document date, today)` and also require the manager to be current today. Record the rule in CR-116. Add tests (see M3).

### M2 (medium): R-305 is closed, but the same wrong routing is still reachable through routes a customer configures

- **Where:** `ApproverResolution.cs:32` (`AboutPerson` falls back to the requester), `:61-64`; `docs/registers/risks-and-changes.md:356` (R-305 closed); `docs/handoff/2026-11-25-R-305.md:13`.
- **What:** only `hrm.regularization` and `hrm.duty_request` declare the new keys. The route editor (`web/apps/web/src/modules/admin/routes-model.ts:11`) lets an administrator choose `manager` or `department_head` on **any** document type. For a type without the keys, and for `department_head` on every type, the kernel still uses "the first subject's user, else the requester".
- **Failure scenarios:**
  - A customer changes the `hrm.employment_event` route (promotion, transfer and so on) or `hrm.strength_exception` to a `manager` step. HR then enters a change for an employee with no sign-in, and the step goes to HR's own manager. That is exactly R-305.
  - A customer replaces the leave route's `document_user` step with `manager`, and leave regresses the same way.
  - A customer adds a `department_head` step to `hrm.regularization`. `DepartmentHeadsAsync(requester)` then answers with the head of HR's own department.
- **Why medium:** the requester never decides, so FR-XM-32's "never approves own document" still holds. But the segregation-of-duties weakness that R-305 describes is still there, and the register says it is closed. The "Open" line of the handoff mentions `department_head`, but no risk row records it. R-312 and R-313 were reserved for this fix and neither is used.
- **Recommendation:** pick one of two options.
  - **(a)** All four HR types with an `employee` subject also declare `subject_employment_field`. HR employment events, strength exceptions and both leave types already know the employment. `department_head` then gets a subject-employment twin as well.
  - **(b)** For a type that declares `subject_fields`, `manager` and `department_head` resolve to nobody when no subject user is present, instead of falling back to the requester.

  Also keep R-305 open, or open R-312 for the remaining routes and for `department_head`.

### M3 (medium): the new date rule and the engine branch have no test that would fail if they were wrong

- **Where:** `tests/Erp.IntegrationTests/Attendance/AttendanceRequestTests.cs:361`, `:382`, `:395` and the fourth test; `tests/Erp.IntegrationTests/Workflow/WorkflowSupport.cs:370-374`.
- **What:**
  - All four new integration tests use a future day (`o.Monday.AddDays(7)`), and none changes the manager over time. If `DocumentDate` were replaced by `today`, or the date were ignored altogether, every test would still pass. The new contract promises the date rule, yet it goes unchecked.
  - Nothing covers a past `work_date` after a manager change, an employment that has ended (the service accepts `terminated`), or a manager who has since separated.
  - `TestDirectory.SubjectManagers` was added but no test uses it. No engine-level test (`tests/Erp.IntegrationTests/Workflow`) covers these cases:
    - a type that declares the key but has no value, which should give nobody and then the fallback;
    - a step with `levels: 2`;
    - an escalation spec of kind `manager` on such a type.
- **Recommendation:** add tests that cite FR-KRN-WFL-02 and FR-XM-32:
  - a regularization for a past day with manager A then B, asserting whichever rule M1 settles on;
  - a manager separated after the work date;
  - an engine-level test through `TestDirectory.SubjectManagers` for a missing value and for levels.

### L1 (low): "never the requester's manager" is not true when the subject's manager entered the request

- **Where:** `src/Platform/Erp.Kernel.Infrastructure/Workflow/WorkflowEngine.cs:204-209` ("moved_up"); handoff line 10, CR-116 **b**; test at `AttendanceRequestTests.cs:390-405`.
- **What:** suppose manager M enters a request for their own report X. Then `manager` resolves to M, who is excluded as the requester, so the engine moves up to `ManagersOfAsync(M)`. That is **the requester's manager**. This matches ruling Q6: M's manager is also X's second-level manager, so the routing is fine. But the handoff and CR-116 say the step goes "never [to] the requester or the requester's manager".
- **Test gap:** the test gives M no manager, so it never exercises this path. The comment says the task is "in the HR administrators' queue", but the test does not assert that. `Assert.NotEqual(OK, …)` would also pass on a 404.
- **Recommendation:** correct the wording in CR-116 and the handoff to "moves up one level, as ruling Q6". Give M a manager in the test and assert who gets the task. Assert the `hr_admin` queue and the exact refusal status (forbidden or conflict) in the existing test.

### L2 (low): the kernel neither fixes nor checks the subject employment value

- **Where:** `WorkflowEngine.cs:145` and `:900-935` (`PrepareValues`); `HrmApproverDirectory.cs:40-49`.
- **What:**
  - On resubmit, the subject *user* fields must keep naming the request's subjects (`fixedSubjects`), but `subject_employment_field` can change freely.
  - `ManagersOfSubjectAsync` does not check that the employment belongs to the request's company or to its subject user.
  - Today, only module code supplies values (there is no generic start endpoint), and attendance requests have no resubmit path. So nothing can exploit this yet.
- **Failure scenario (future):** a module adds resubmit for these types and passes the client's values through. A requester then changes `employment` to a colleague's employment whose manager is a friend.
- **Recommendation:** treat `subject_employment_field` like a subject field at resubmit (it must equal the stored value). Add `and e.company_id = $company` to the start of the HR walk, or check it in the engine.

### L3 (low): requests already open keep no employment value, so later steps of a longer route change behaviour

- **Where:** `ApproverResolution.cs:58-60`.
- **What:** instances started before this change have no `employment` in their frozen values. Suppose a customer has a two-step route on `hrm.regularization` or `hrm.duty_request`, such as `manager` and then `manager` with `levels: 2`. After this deployment, the second step of an open request resolves to nobody and goes to the fallback, or to `cannot_assign` if the step has no fallback. Before, it went to the subject's manager. The direction is safe (HR or an administrator), but the handoff says "Existing open requests keep their tasks", which covers only tasks already open.
- **Recommendation:** say so in the handoff, or fall back to the old subject-user rule for instances without the value when that subject has a user.

### L4 (low): approvers see a raw employment id

- **Where:** `src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/workflow.json:54`, `:79`; `web/apps/web/src/modules/kernel/approvals.tsx:272`.
- **What:** the approvals detail lists every declared field. The new text field "Employment of the employee" shows a UUID to the manager deciding, and in the Urdu screen too. The field also appears in the route editor's condition builder, where a condition on it makes no sense.
- **Recommendation:** add a field type the screen hides (or an `internal` flag), or have the engine leave the subject employment field out of `context.fields`.

### L5 (low): the contract register's HR implementer rows do not mention the new member

- **Where:** `docs/registers/contracts.md:96` and `:99`.
- **What:** row 95 adds the kernel contract. The HR `IApproverDirectory` rows still describe only `ManagersAsync`, department heads and positions; row 96, from S1.20b, also says `Supports("manager")` only, which row 99 has already superseded. A reader of the HR contract does not learn that HR answers `ManagersOfSubjectAsync` by employment id, or what "current" means for it (see M1).
- **Recommendation:** extend row 99 with the new member and its date rule, and mark row 96 as superseded.

### L6 (low): reserved numbers recorded without their outcome

- **Where:** `docs/registers/risks-and-changes.md:3`.
- **What:** R-312 and R-313 are reserved for "the R-305 fix" and neither is used. Other reservations in the same paragraph say "not used" when that happens.
- **Recommendation:** use R-312 for the residual risk in M2, or mark the two as not used.

## Checked and found sound

- **Escalation** (`WorkflowEngine.Escalation.cs:109-118`): the first level is still the assignee's manager by user. A `manager` escalation spec on these types now resolves to the subject's manager. That person is normally the assignee and is dropped by `p != task.AssigneeUserId`, so the result is "not possible" rather than an approver outside the rules. The requester and subjects are still excluded.
- **Activation check:** unchanged. A route with a `manager` step on these types activates only when the directory supports `manager`.
- **Manifest validation** (`WorkflowCatalog.cs:237-245`): the employment field must be a declared text field, and the date field must be a declared date field and needs the employment field. Unit tests cover all three refusals.
- **`ManagerUsersAsync` SQL:** the parameters are typed (`uuid[]`, `date`). The walk follows `manager_employment_id` of the assignment valid on the date. Only managers whose employment is current are kept, and only those with a linked user are returned. Assignments have no soft-delete column, so no filter is missing. Apart from the date question in M1, the behaviour is the same as the existing `ManagersAsync`.
- **Fallback when no value or no manager:** this gives `ApproverTarget.Nobody`, then the step fallback, then the `hr_admin` queue, as the fourth new test shows. Nothing is approved automatically.
- **Tenant isolation:** the directory uses the tenant's own `DataSource`, the value comes from the same customer's request, and there are no new endpoints.
- **Tests:** they cite FR ids in their names, use real PostgreSQL, and delete, skip or weaken no existing test.
