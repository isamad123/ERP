using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.Hrm.Application.Employments;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Domain.Generated;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// Transfers between companies and rehires (slice S1.20 Build items 11 and 12; FR-XM-30, FR-XM-28, BR-01, FR-HRM-EMP-04, rulings
/// Q9, Q10, Q11). A transfer ends the employment on the day before its date (a <c>separation</c> event, reason <c>transfer</c>) and
/// starts a linked one in the other company (a <c>hire</c> event, reason <c>transfer_in</c>), with the three carry-over choices on
/// both events, in one transaction; it waits for the approval route when either company lists <c>transfer</c> in
/// <c>hrm.employment_events.approval_kinds</c> (FR-XM-32). A rehire is a hire of the person of a former employment, linked to it.
/// </summary>
internal sealed partial class EmploymentService : IEmploymentMoves
{
    public const string TransferSameCompany = "errors.hrm.transfer.same_company";
    public const string TransferBeforeJoining = "errors.hrm.transfer.before_joining";
    public const string TransferPendingChanges = "errors.hrm.transfer.pending_changes";
    public const string TransferLaterChanges = "errors.hrm.transfer.later_changes";
    public const string RehireNotSeparated = "errors.hrm.rehire.not_separated";
    public const string RehireBeforeSeparation = "errors.hrm.rehire.before_separation";

    private static readonly string[] CarryOverKeys = ["service", "leave", "loans"];

    public Task<PeopleResult> CheckTransferAsync(Guid employmentId, TransferRequest request, CancellationToken cancellationToken) =>
        TransferCoreAsync(employmentId, request, save: false, cancellationToken);

    public Task<PeopleResult> TransferAsync(Guid employmentId, TransferRequest request, CancellationToken cancellationToken) =>
        TransferCoreAsync(employmentId, request, save: true, cancellationToken);

    private async Task<PeopleResult> TransferCoreAsync(Guid employmentId, TransferRequest r, bool save, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!await HasAsync(EmploymentPermissions.Transfer, ct).ConfigureAwait(false)
            || (r.EmployeeNo is not null && !await HasAsync(EmploymentPermissions.NumberOverride, ct).ConfigureAwait(false)))
        {
            return PeopleResult.Forbidden();
        }

        if (save && r.RowVersion is null)
        {
            return PeopleResult.Invalid(RowVersionRequired);
        }

        // A first pass without locks: validation, scopes, carry-over, the impact check (shown before saving, GHR-04).
        var (plan, problem) = await PlanTransferAsync(employmentId, r, ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        if (problem is not null)
        {
            return problem;
        }

        if (!save)
        {
            return new PeopleResult(PeopleOutcome.Done, Body: new
            {
                valid = true,
                plan!.NeedsApproval,
                status = plan.NeedsApproval ? "pending_approval" : plan.Date <= plan.Today ? "effective" : "approved",
                separationDate = Iso(plan.Date.AddDays(-1)),
                carryOver = plan.CarryOver,
                serviceStartDate = Iso(plan.To.ServiceStartDate),
                impact = plan.Report,
            });
        }

        // The employee number of the receiving company, taken before the transaction like a hire's (gap-allowed series).
        string number;
        try
        {
            if (r.EmployeeNo is { } typed)
            {
                var check = await numbers.CheckManualAsync(new NumberRequest("hrm.employee_no", plan!.To.CompanyId, null, plan.Date), typed, ct).ConfigureAwait(false);
                if (!check.Accepted)
                {
                    return Field("employeeNo", check.ErrorKey!);
                }

                number = typed.Trim();
            }
            else
            {
                number = await numbers.AssignOnCreateAsync(new NumberRequest("hrm.employee_no", plan!.To.CompanyId, null, plan.Date), ct).ConfigureAwait(false);
            }
        }
        catch (NumberingException ex)
        {
            return PeopleResult.Invalid(ex.ErrorKey);
        }

        return await InTransactionAsync(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {employmentId} for update", ct).ConfigureAwait(false);
            var (locked, again) = await PlanTransferAsync(employmentId, r, ct).ConfigureAwait(false);
            return again ?? await SaveTransferAsync(locked!, number, r.RowVersion!.Value, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private sealed record TransferPlan(Employment From, Employment To, JsonObject Assignment, TransferCarryOver CarryOver, DateOnly Date, DateOnly Today,
        string? Notes, ImpactReport Report, bool NeedsApproval);

    private async Task<(TransferPlan? Plan, PeopleResult? Problem)> PlanTransferAsync(Guid employmentId, TransferRequest r, CancellationToken ct)
    {
        var from = await db.Set<Employment>().FirstOrDefaultAsync(e => e.Id == employmentId, ct).ConfigureAwait(false);
        if (from is null || !await AllowsAsync(from, EmploymentAccess.Scope, "read", ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.NotFound);
        }

        if (!await AllowsAsync(from, EmploymentAccess.Scope, "transfer", ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.Forbidden(AccessDeniedException.OutOfScope));
        }

        if (!CurrentStatuses.Contains(from.Status))
        {
            return (null, PeopleResult.Conflict(NotCurrent));
        }

        if (r.RowVersion is { } expected && from.RowVersion != expected)
        {
            return (null, PeopleResult.Conflict(Concurrency));
        }

        var errors = new Errors();
        if (r.ToCompanyId is not { } toCompany)
        {
            errors.Add("toCompanyId", Required);
        }
        else if (toCompany == from.CompanyId)
        {
            errors.Add("toCompanyId", TransferSameCompany); // a move within the company is a change of kind transfer
        }

        if (r.EffectiveDate is not { } date)
        {
            errors.Add("effectiveDate", Required);
        }
        else if (date <= from.JoiningDate)
        {
            errors.Add("effectiveDate", TransferBeforeJoining); // the first employment keeps at least its first day
        }

        var type = r.EmployeeType ?? from.EmployeeType;
        if (!Catalogue.IsEmployeeType(type))
        {
            errors.Add("employeeType", UnknownCode);
        }

        var notes = Text(r.Notes, "notes", NotesLength, errors);
        var delta = AssignmentDelta(r.Assignment ?? new AssignmentInput(), errors, hire: true);
        foreach (var (key, value, choices) in new[] { ("service", r.CarryOver?.Service, TransferCarryOver.ServiceChoices), ("leave", r.CarryOver?.Leave, TransferCarryOver.LeaveChoices), ("loans", r.CarryOver?.Loans, TransferCarryOver.LoanChoices) })
        {
            if (value is not null && !choices.Contains(value, StringComparer.Ordinal))
            {
                errors.Add("carryOver." + key, UnknownCode);
            }
        }

        if (errors.Any)
        {
            return (null, PeopleResult.Fields(errors.ToDictionary()));
        }

        if (await PayGroupProblemAsync(r.ToCompanyId!.Value, delta, ct).ConfigureAwait(false) is { } payGroupProblem)
        {
            return (null, payGroupProblem);
        }

        var day = r.EffectiveDate!.Value;
        var target = r.ToCompanyId!.Value;

        // Nothing of the first employment may be waiting or dated on or after the transfer: it would apply to an ended employment.
        var events = await applier.EventsAsync(from.Id, ct).ConfigureAwait(false);
        if (events.Any(e => e.Status == "pending_approval"))
        {
            return (null, PeopleResult.Conflict(TransferPendingChanges));
        }

        if (events.Any(e => e.Status is "approved" or "effective" && (e.EffectiveDate >= day || e.Kind == EmploymentApplier.SeparationKind) && !EmploymentTimeline.ReplacedBy([.. events.Select(EmploymentApplier.ToTimeline)]).ContainsKey(e.Id)))
        {
            return (null, PeopleResult.Conflict(TransferLaterChanges));
        }

        // The choices recorded: as asked, else the receiving company's settings (ruling Q9).
        async Task<string> ChoiceAsync(string key, string? asked, IReadOnlyList<string> choices)
        {
            if (asked is not null)
            {
                return asked;
            }

            var value = (await settings.GetAsync("hrm.transfer.carry_over." + key, new SettingContext(target, null), ct).ConfigureAwait(false)).Value;
            return value.ValueKind == JsonValueKind.String && choices.Contains(value.GetString()!, StringComparer.Ordinal) ? value.GetString()! : choices[0];
        }

        var carry = new TransferCarryOver(
            await ChoiceAsync("service", r.CarryOver?.Service, TransferCarryOver.ServiceChoices).ConfigureAwait(false),
            await ChoiceAsync("leave", r.CarryOver?.Leave, TransferCarryOver.LeaveChoices).ConfigureAwait(false),
            await ChoiceAsync("loans", r.CarryOver?.Loans, TransferCarryOver.LoanChoices).ConfigureAwait(false));

        var to = new Employment
        {
            CompanyId = target,
            PersonId = from.PersonId,
            EmployeeNo = string.Empty,
            EmployeeType = type,
            JoiningDate = day,
            ServiceStartDate = carry.Service == "keep" ? from.ServiceStartDate : day,
            ExpectedEndDate = null,
            PreviousEmploymentId = from.Id,
            UserRef = from.UserRef, // the same human: the user follows the person (one current employment per user per company)
            IsPrimary = false, // the person's main employment from the transfer date, once the first one has ended
            Status = "pre_joining",
        };
        Stamp(to);

        // The caller's transfer scope covers the receiving company and the first job details there; their fields are editable.
        var hire = new EmploymentEvent { CompanyId = target, EmploymentId = to.Id, Kind = "hire", ReasonCode = TransferApplier.TransferInReason, EffectiveDate = day, Status = "draft", Id = Guid.CreateVersion7(clock.UtcNow) };
        if (!await AllowsAsync(to, EmploymentAccess.Scope, "transfer", ct).ConfigureAwait(false)
            || !await AllowsAsync(FirstRow(to, hire, delta), EmploymentAssignmentAccess.Scope, "read", ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.Forbidden(AccessDeniedException.OutOfScope));
        }

        var denied = await NotEditableInCompanyAsync(EmploymentAccess.Fields, "transfer", target, ["employee_no", "employee_type", "joining_date", "service_start_date"], ct).ConfigureAwait(false);
        denied.AddRange(await NotEditableInCompanyAsync(EmploymentAssignmentAccess.Fields, "read", target, [.. delta.Select(d => d.Key)], ct).ConfigureAwait(false));
        denied.AddRange(await NotEditableAsync(EmploymentAccess.Fields, EmploymentAccess.Scope, from, ["status", "separation_date"], ct).ConfigureAwait(false));
        if (denied.Count > 0)
        {
            return (null, NotEditable(denied));
        }

        // The manager in the receiving company must be one of its current employees (the database checks the company too).
        if (delta[AssignmentKeys.Manager] is { } manager
            && !await db.Set<Employment>().AsNoTracking().AnyAsync(m => m.Id == Guid.Parse(manager.GetValue<string>()) && m.CompanyId == target && CurrentStatuses.Contains(m.Status), ct).ConfigureAwait(false))
        {
            return (null, Field("assignment.managerEmploymentId", ManagerNotCurrent));
        }

        // The end of the first employment is checked like a termination (BR-02), and every module names the open items the chosen
        // carry-over does not carry (FR-XM-30: "carried by explicit rule"); either refuses the transfer.
        var today = await applier.TodayAsync(from.CompanyId, clock.UtcNow, ct).ConfigureAwait(false);
        var report = await impact.RunAsync(new EmploymentChange(from.Id, from.CompanyId, EmploymentApplier.SeparationKind, day, day, IsTermination: true, carry), today, ct).ConfigureAwait(false);
        if (report.TerminationNeedsOverride)
        {
            return (null, PeopleResult.Conflict(ImpactReport.TerminationLocked));
        }

        if (report.OpenItems is { Count: > 0 } open)
        {
            return (null, PeopleResult.Conflict(ImpactReport.TransferOpenItems, new Dictionary<string, object?> { ["openItems"] = open }));
        }

        var needsApproval = await NeedsApprovalAsync("transfer", from.CompanyId, ct).ConfigureAwait(false) || await NeedsApprovalAsync("transfer", target, ct).ConfigureAwait(false);
        return (new TransferPlan(from, to, delta, carry, day, today, notes, report, needsApproval), null);
    }

    private async Task<PeopleResult> SaveTransferAsync(TransferPlan p, string number, long expected, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var from = p.From;
        var to = p.To;
        to.EmployeeNo = number;
        var carry = JsonSerializer.SerializeToDocument(new JsonObject { ["service"] = p.CarryOver.Service, ["leave"] = p.CarryOver.Leave, ["loans"] = p.CarryOver.Loans });
        var separation = Stamp(new EmploymentEvent
        {
            CompanyId = from.CompanyId,
            EmploymentId = from.Id,
            Kind = EmploymentApplier.SeparationKind,
            ReasonCode = TransferApplier.SeparationReason,
            EffectiveDate = p.Date,
            Notes = p.Notes,
            CarryOver = carry,
            Status = "pending_approval",
            Changes = JsonSerializer.SerializeToDocument(new JsonObject
            {
                ["transfer"] = new JsonObject { ["to_employment_id"] = to.Id.ToString(), ["to_company_id"] = to.CompanyId.ToString() },
                ["impact"] = JsonSerializer.SerializeToNode(p.Report, JsonSerializerOptions.Web),
            }),
        });
        var start = Stamp(new EmploymentEvent
        {
            CompanyId = to.CompanyId,
            EmploymentId = to.Id,
            Kind = "hire",
            ReasonCode = TransferApplier.TransferInReason,
            EffectiveDate = p.Date,
            Notes = p.Notes,
            CarryOver = JsonSerializer.SerializeToDocument(carry.RootElement),
            Status = "pending_approval",
            Changes = JsonSerializer.SerializeToDocument(new JsonObject
            {
                ["assignment"] = p.Assignment,
                ["transfer"] = new JsonObject { ["from_employment_id"] = from.Id.ToString(), ["from_company_id"] = from.CompanyId.ToString() },
            }),
        });

        // The first employment's row version is checked on the first save that writes it (optimistic concurrency).
        Touch(from, expected);
        db.Add(separation);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.Add(to);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.Add(start);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (p.NeedsApproval)
        {
            // FR-XM-32 (ruling Q11): the transfer waits for the route of the first company; the requester and the employee never
            // approve it. The linked employment waits as pre-joining, with no job details, until the decision.
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["company"] = JsonSerializer.SerializeToElement(from.CompanyId.ToString()),
                ["kind"] = JsonSerializer.SerializeToElement("transfer"),
                ["effective_date"] = JsonSerializer.SerializeToElement(Iso(p.Date)),
                ["employee_no"] = JsonSerializer.SerializeToElement(from.EmployeeNo),
                ["to_company"] = JsonSerializer.SerializeToElement(to.CompanyId.ToString()), // routes can tell a transfer between companies apart (finding 9)
            };
            if (from.UserRef is { } user)
            {
                values["employee"] = JsonSerializer.SerializeToElement(user.ToString());
            }

            var started = await workflow.StartAsync(new WorkflowStartRequest(DocumentType, separation.Id, from.CompanyId, from.UserRef is { } subject ? [subject] : [], values), Transaction, ct).ConfigureAwait(false);
            separation.WorkflowInstanceId = started.InstanceId;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Pending, Body: new { id = to.Id, employeeNo = to.EmployeeNo, separationEventId = separation.Id, eventId = start.Id, status = separation.Status, instanceId = started.InstanceId, rowVersion = to.RowVersion, fromRowVersion = from.RowVersion, impact = p.Report });
        }

        if (await TransferApplier.ApplyAsync(db, applier, from, separation, to, start, p.Today, now, actor.UserId, ct).ConfigureAwait(false) is { } problem)
        {
            return PeopleResult.Invalid(problem);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Data scope after the save (the linked employment now locates its person).
        if (!await AllowsAsync(to, EmploymentAccess.Scope, "read", ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        return new PeopleResult(PeopleOutcome.Created, Body: new
        {
            id = to.Id,
            companyId = to.CompanyId,
            employeeNo = to.EmployeeNo,
            status = to.Status,
            serviceStartDate = Iso(to.ServiceStartDate),
            eventId = start.Id,
            separationEventId = separation.Id,
            separationStatus = separation.Status,
            carryOver = p.CarryOver,
            rowVersion = to.RowVersion, // each id with its own row's version (review S1.20c finding 5)
            fromRowVersion = from.RowVersion,
            impact = p.Report,
        });
    }

    // ================================================================== rehire (BR-01)

    public async Task<PeopleResult> RehireAsync(Guid formerEmploymentId, RehireRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await HasAsync(EmploymentPermissions.EmploymentsCreate, cancellationToken).ConfigureAwait(false)
            || (request.EmployeeNo is not null && !await HasAsync(EmploymentPermissions.NumberOverride, cancellationToken).ConfigureAwait(false)))
        {
            return PeopleResult.Forbidden();
        }

        var former = await ReadableAsync(formerEmploymentId, cancellationToken).ConfigureAwait(false);
        if (former is null)
        {
            return PeopleResult.NotFound;
        }

        // Only an ended employment is rehired from; the person's current one is changed, not rehired (FR-XM-28: one person).
        if (former.Status != "separated" || former.SeparationDate is not { } separated)
        {
            return PeopleResult.Conflict(RehireNotSeparated);
        }

        if (request.JoiningDate is { } joining && joining <= separated)
        {
            return Field("joiningDate", RehireBeforeSeparation);
        }

        // The hire service does the rest (numbering, limit, scopes, field security, first assignment, probation), as a rehire event
        // linked to the former employment, which is left as it is (BR-01).
        var hire = new HireRequest
        {
            PersonId = former.PersonId,
            CompanyId = request.CompanyId ?? former.CompanyId,
            EmployeeType = request.EmployeeType,
            JoiningDate = request.JoiningDate,
            ServiceStartDate = request.ServiceStartDate,
            ExpectedEndDate = request.ExpectedEndDate,
            EmployeeNo = request.EmployeeNo,
            ReasonCode = request.ReasonCode ?? "rejoining",
            Notes = request.Notes,
            Assignment = request.Assignment,
        };
        return await HireAsync(hire, former.Id, cancellationToken).ConfigureAwait(false);
    }
}
