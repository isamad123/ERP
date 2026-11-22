using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.Hrm.Application.Employments;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Domain.Generated;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// Changes as events (slice S1.20 Build items 6 and 8, FR-HRM-EMP-03, BR-02, GHR-04, FR-XM-05, FR-XM-32; rulings Q2, Q6, Q11, Q12
/// and CR-096): one service for promotion, demotion, transfer within the company, grade change, acting, secondment,
/// confirmation and data correction.
/// </summary>
internal sealed partial class EmploymentService
{
    public const string ConfirmationOnly = "errors.hrm.employment_event.confirmation_only";

    public Task<PeopleResult> CheckChangeAsync(Guid employmentId, EmploymentChangeRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(employmentId, request, save: false, cancellationToken);

    public Task<PeopleResult> RecordChangeAsync(Guid employmentId, EmploymentChangeRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(employmentId, request, save: true, cancellationToken);

    private async Task<PeopleResult> ChangeAsync(Guid employmentId, EmploymentChangeRequest request, bool save, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await HasAsync(EmploymentPermissions.EventsCreate, ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (!save)
        {
            return await PrepareAndSaveAsync(employmentId, request, save: false, ct).ConfigureAwait(false);
        }

        if (request.RowVersion is null)
        {
            return PeopleResult.Invalid(RowVersionRequired);
        }

        return await InTransactionAsync(async () =>
        {
            // One change of an employment at a time: its row is locked until commit, so two changes never plan from the same rows.
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {employmentId} for update", ct).ConfigureAwait(false);
            return await PrepareAndSaveAsync(employmentId, request, save: true, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<PeopleResult> PrepareAndSaveAsync(Guid employmentId, EmploymentChangeRequest r, bool save, CancellationToken ct)
    {
        var employment = await db.Set<Employment>().FirstOrDefaultAsync(e => e.Id == employmentId, ct).ConfigureAwait(false);
        if (employment is null || !await AllowsAsync(employment, EmploymentAccess.Scope, "read", ct).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await AllowsAsync(new EmploymentEvent { CompanyId = employment.CompanyId, EmploymentId = employment.Id, Kind = string.Empty, ReasonCode = string.Empty, Status = "draft" }, EmploymentEventAccess.Scope, "create", ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (!CurrentStatuses.Contains(employment.Status))
        {
            return PeopleResult.Conflict(NotCurrent);
        }

        if (save && employment.RowVersion != r.RowVersion)
        {
            return PeopleResult.Conflict(Concurrency);
        }

        var now = clock.UtcNow;
        var today = await applier.TodayAsync(employment.CompanyId, now, ct).ConfigureAwait(false);
        var errors = new Errors();
        var kind = r.Kind;
        if (kind is null)
        {
            errors.Add("kind", Required);
        }
        else if (!ChangeKinds.Contains(kind, StringComparer.Ordinal))
        {
            errors.Add("kind", Catalogue.Kinds.Any(k => k.Code == kind) ? KindNotRecorded : UnknownCode);
        }

        if (kind is not null && errors.Has("kind") is false && !Catalogue.IsReason(kind, r.ReasonCode))
        {
            errors.Add("reasonCode", r.ReasonCode is null ? Required : UnknownCode);
        }

        if (r.EffectiveDate is null)
        {
            errors.Add("effectiveDate", Required);
        }

        var notes = Text(r.Notes, "notes", NotesLength, errors);
        if (errors.Any)
        {
            return PeopleResult.Fields(errors.ToDictionary());
        }

        var date = r.EffectiveDate!.Value;
        var events = await applier.EventsAsync(employment.Id, ct).ConfigureAwait(false);

        // The event a correction replaces: applied, not replaced yet, no other correction of it waiting for approval.
        EmploymentEvent? target = null;
        if (r.CorrectsEventId is { } targetId)
        {
            target = events.FirstOrDefault(e => e.Id == targetId);
            if (kind == "data_correction" && target is not null && EmploymentTimeline.Replaced([.. events.Select(EmploymentApplier.ToTimeline)]).Contains(targetId))
            {
                errors.Add("correctsEventId", AlreadyCorrected);
            }
            else if (kind != "data_correction" || target is null || !EmploymentTimeline.IsApplied(target.Status))
            {
                errors.Add("correctsEventId", CorrectionTarget);
            }
            else if (events.Any(e => e.CorrectsEventId == targetId && e.Status is "pending_approval" or "draft"))
            {
                errors.Add("correctsEventId", CorrectionPending);
            }
        }

        var delta = r.Assignment is { } input ? AssignmentDelta(input, errors, hire: false) : [];
        if (kind == "acting" && !delta.ContainsKey(AssignmentKeys.IsActing))
        {
            delta[AssignmentKeys.IsActing] = true;
        }

        var employmentDelta = new JsonObject();
        if (r.Employment is { } fieldsInput)
        {
            if (kind != "data_correction")
            {
                errors.Add("employment", EmploymentFieldsCorrection);
            }
            else
            {
                EmploymentDelta(fieldsInput, employment, employmentDelta, errors);
            }
        }

        // What each kind may change (ruling Q2): job details through events, employment fields through a correction.
        var rootKind = target is null ? null : RootKind(target);
        if (AssignmentKinds.Contains(kind, StringComparer.Ordinal) && delta.Count == 0)
        {
            errors.Add("assignment", NoAssignmentChanges);
        }

        if (kind == "confirmation" || rootKind == "confirmation")
        {
            if (delta.Count > 0)
            {
                errors.Add("assignment", ConfirmationOnly);
            }

            if (date < employment.JoiningDate)
            {
                errors.Add("effectiveDate", ConfirmationBeforeJoining); // BR-02
            }

            if (kind == "confirmation" && employment.ConfirmationDate is not null)
            {
                errors.Add("kind", AlreadyConfirmed);
            }

            if (rootKind == "confirmation")
            {
                employmentDelta["confirmation_date"] = Iso(date);
            }
        }
        else if (rootKind != "hire" && date < employment.JoiningDate)
        {
            errors.Add("effectiveDate", BeforeJoining);
        }

        if (kind == "data_correction" && target is null && delta.Count == 0 && employmentDelta.Count == 0)
        {
            errors.Add(r.CorrectsEventId is null ? "correctsEventId" : "assignment", r.CorrectsEventId is null ? CorrectionNeedsTarget : NothingChanges);
        }

        if (errors.Any)
        {
            return PeopleResult.Fields(errors.ToDictionary());
        }

        // A correction carries the changes of the event it replaces, overridden by its own (FR-XM-05: never edited in place).
        var targetAssignment = target is null ? null : EmploymentApplier.Section(target.Changes, "assignment");
        var assignment = targetAssignment is null ? delta : EmploymentTimeline.Apply(targetAssignment, delta);
        var changes = new JsonObject();
        if (assignment.Count > 0)
        {
            changes["assignment"] = assignment;
        }

        if (employmentDelta.Count > 0)
        {
            changes["employment"] = employmentDelta;
        }

        if (rootKind is not null)
        {
            changes["corrects_kind"] = rootKind;
        }

        var e = Stamp(new EmploymentEvent
        {
            CompanyId = employment.CompanyId,
            EmploymentId = employment.Id,
            Kind = kind!,
            ReasonCode = r.ReasonCode!,
            EffectiveDate = date,
            Notes = notes,
            CorrectsEventId = target?.Id,
            Status = "approved", // as it would count once applied; the planned rows are checked with it
            Changes = JsonSerializer.SerializeToDocument(changes),
        });

        // The rows the events call for with this one applied, checked before anything is saved.
        var planned = EmploymentTimeline.Plan([.. events.Select(EmploymentApplier.ToTimeline), EmploymentApplier.ToTimeline(e)], out var problem);
        if (problem is not null)
        {
            return Field("effectiveDate", BeforeJoining);
        }

        if (rootKind == "hire" && planned.Count > 0 && planned[0].ValidFrom > today && employment.Status != "pre_joining")
        {
            return Field("effectiveDate", JoiningAfterToday);
        }

        if (await PlannedProblemAsync(employment, planned, assignment, ct).ConfigureAwait(false) is { } invalid)
        {
            return invalid;
        }

        // Data scope before (the job details in force on the earliest day) and after (those the change puts in force), and field
        // security of the fields it changes (FR-KRN-IAM-13).
        var earliest = target is not null && target.EffectiveDate < date ? target.EffectiveDate : date;
        var stored = await db.Set<EmploymentAssignment>().AsNoTracking().Where(a => a.EmploymentId == employment.Id).ToListAsync(ct).ConfigureAwait(false);
        var before = stored.LastOrDefault(a => a.ValidFrom <= earliest && (a.ValidTo == null || a.ValidTo > earliest)) ?? stored.OrderBy(a => a.ValidFrom).FirstOrDefault();
        if (before is not null && !await AllowsAsync(before, EmploymentAssignmentAccess.Scope, "read", ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (EmploymentTimeline.On(planned, date) is { } after && !await AllowsAsync(Transient(employment, after), EmploymentAssignmentAccess.Scope, "read", ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var denied = before is null ? [] : await NotEditableAsync(EmploymentAssignmentAccess.Fields, EmploymentAssignmentAccess.Scope, before, delta.Select(d => d.Key), ct).ConfigureAwait(false);
        denied.AddRange(await NotEditableAsync(EmploymentAccess.Fields, EmploymentAccess.Scope, employment, employmentDelta.Select(d => d.Key).Where(k => k != "confirmation_date"), ct).ConfigureAwait(false));
        if (denied.Count > 0)
        {
            return NotEditable(denied);
        }

        // The backdated change check (GHR-04, ruling Q6, CR-096): asked before saving and kept on the event; never a refusal here.
        var report = await impact.RunAsync(new EmploymentChange(employment.Id, employment.CompanyId, kind!, date, earliest, IsTermination: false), today, ct).ConfigureAwait(false);
        changes["impact"] = JsonSerializer.SerializeToNode(report, JsonSerializerOptions.Web);
        var needsApproval = await NeedsApprovalAsync(kind!, employment.CompanyId, ct).ConfigureAwait(false);
        if (!save)
        {
            return new PeopleResult(PeopleOutcome.Done, Body: new
            {
                valid = true,
                needsApproval,
                status = needsApproval ? "pending_approval" : date <= today ? "effective" : "approved",
                impact = report,
            });
        }

        e.Changes = JsonSerializer.SerializeToDocument(changes);
        if (needsApproval)
        {
            // FR-XM-32 (ruling Q11): the change waits for the route; the requester and the employee never approve it.
            e.Status = "pending_approval";
            db.Add(e);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var started = await workflow.StartAsync(new WorkflowStartRequest(DocumentType, e.Id, employment.CompanyId,
                employment.UserRef is { } subject ? [subject] : [], RouteValues(employment, e, EmploymentTimeline.On(planned, date))), Transaction, ct).ConfigureAwait(false);
            e.WorkflowInstanceId = started.InstanceId;
            Touch(employment, r.RowVersion!.Value);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Pending, Body: new { eventId = e.Id, status = e.Status, instanceId = started.InstanceId, rowVersion = employment.RowVersion, impact = report });
        }

        EmploymentApplier.SetApplied(e, events, today, now, actor.UserId);
        db.Add(e);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        List<EmploymentEvent> all = [.. events, e];
        if (await applier.SyncRowsAsync(employment, all, now, actor.UserId, ct).ConfigureAwait(false) is { } refused)
        {
            return PeopleResult.Invalid(refused);
        }

        await applier.ApplyEmploymentFieldsAsync(employment, e, today, ct).ConfigureAwait(false);
        if (e.Status == "effective")
        {
            applier.RaiseChanged(employment, e, all, now, actor.UserId);
        }

        Touch(employment, r.RowVersion!.Value);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Created, Body: new { eventId = e.Id, status = e.Status, rowVersion = employment.RowVersion, impact = report });
    }

    /// <summary>The kind a correction finally replaces (a correction of a correction replaces what the first one replaced).</summary>
    private static string RootKind(EmploymentEvent target) =>
        target.Kind != "data_correction" ? target.Kind
        : target.Changes?.RootElement.TryGetProperty("corrects_kind", out var k) == true && k.ValueKind == JsonValueKind.String ? k.GetString()! : "data_correction";

    /// <summary>The employment fields of a correction (ruling Q2), checked and stored as snake_case keys.</summary>
    private static void EmploymentDelta(EmploymentFieldsInput input, Employment employment, JsonObject delta, Errors errors)
    {
        var clear = (input.Clear ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var name in clear.Where(n => n != "expectedEndDate"))
        {
            errors.Add("employment." + name, CannotClear);
        }

        if (input.EmployeeType is { } type)
        {
            if (!Catalogue.IsEmployeeType(type))
            {
                errors.Add("employment.employeeType", UnknownCode);
            }

            delta["employee_type"] = type;
        }

        if (input.ServiceStartDate is { } start)
        {
            delta["service_start_date"] = Iso(start);
        }

        if (input.ExpectedEndDate is { } end)
        {
            if (end < employment.JoiningDate)
            {
                errors.Add("employment.expectedEndDate", DateOrder);
            }

            delta["expected_end_date"] = Iso(end);
        }
        else if (clear.Contains("expectedEndDate"))
        {
            delta["expected_end_date"] = null;
        }
    }

    /// <summary>
    /// Problems of the planned rows the database would refuse less clearly: a manager who is the employee, or an employment that is
    /// not current. The cost allocation, the company of every reference and reporting cycles are checked by the database too.
    /// </summary>
    private async Task<PeopleResult?> PlannedProblemAsync(Employment employment, IReadOnlyList<PlannedRow> planned, JsonObject changed, CancellationToken ct)
    {
        if (planned.Any(p => p.Values[AssignmentKeys.Manager] is { } m && Guid.Parse(m.GetValue<string>()) == employment.Id))
        {
            return Field("assignment.managerEmploymentId", OwnManager);
        }

        if (changed[AssignmentKeys.Manager] is { } node)
        {
            var manager = Guid.Parse(node.GetValue<string>());
            if (!await db.Set<Employment>().AsNoTracking().AnyAsync(m => m.Id == manager && m.CompanyId == employment.CompanyId && CurrentStatuses.Contains(m.Status), ct).ConfigureAwait(false))
            {
                return Field("assignment.managerEmploymentId", ManagerNotCurrent);
            }
        }

        return null;
    }

    /// <summary>An assignment row as a change would put it in force, for the data-scope check (never saved).</summary>
    private static EmploymentAssignment Transient(Employment employment, PlannedRow row) => new()
    {
        CompanyId = employment.CompanyId,
        EmploymentId = employment.Id,
        EventId = row.EventId,
        OrgUnitId = Guid.Parse(row.Values[AssignmentKeys.OrgUnit]!.GetValue<string>()),
        BranchId = Guid.Parse(row.Values[AssignmentKeys.Branch]!.GetValue<string>()),
        Title = row.Values[AssignmentKeys.Title]!.GetValue<string>(),
        CostAllocation = JsonDocument.Parse("[]"),
        ValidFrom = row.ValidFrom,
        ValidTo = row.ValidTo,
    };

    /// <summary>Whether the company's setting lists the kind as needing approval (ruling Q11).</summary>
    private async Task<bool> NeedsApprovalAsync(string kind, Guid companyId, CancellationToken ct)
    {
        var value = (await settings.GetAsync(ApprovalKindsSetting, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
        return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == kind);
    }

    /// <summary>The values the route's conditions may test (document type <c>hrm.employment_event</c> in workflow.json).</summary>
    private static Dictionary<string, JsonElement> RouteValues(Employment employment, EmploymentEvent e, PlannedRow? after)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["company"] = JsonSerializer.SerializeToElement(employment.CompanyId.ToString()),
            ["kind"] = JsonSerializer.SerializeToElement(e.Kind),
            ["effective_date"] = JsonSerializer.SerializeToElement(Iso(e.EffectiveDate)),
            ["employee_no"] = JsonSerializer.SerializeToElement(employment.EmployeeNo),
        };
        if (employment.UserRef is { } user)
        {
            values["employee"] = JsonSerializer.SerializeToElement(user.ToString());
        }

        if (after?.Values[AssignmentKeys.OrgUnit] is { } unit)
        {
            values["unit"] = JsonSerializer.SerializeToElement(unit.GetValue<string>());
        }

        return values;
    }
}
