using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Barebone.Platform.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kyc.CaseReviewer;

internal static partial class KycEndpoints
{
    public const int MaxTextLength = 2000;
    private const string ResourceType = "kyc-case";

    private static readonly JsonSerializerOptions DocumentJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] Recommendations = ["approve", "reject", "escalate"];
    private static readonly string[] Decisions = ["approve", "reject"];
    private static readonly string[] OpenStatuses = [KycStatus.New, KycStatus.InReview];

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex CaseIdPattern();

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/cases", ListCases).RequireAction("case.list");
        group.MapGet("/cases/{id}", GetCase).RequireAction("case.view", LoadCase);
        group.MapPost("/cases/{id}/reveal", RevealPii).RequireAction("case.reveal-pii", LoadCase);
        group.MapPost("/cases/{id}/assign", Assign).RequireAction("case.assign", LoadCase);
        group.MapPost("/cases/{id}/notes", AddNote).RequireAction("case.note.add", LoadCase);
        group.MapPost("/cases/{id}/recommendation", Recommend).RequireAction("case.recommend", LoadCase);
        group.MapPost("/cases/{id}/decision", Decide).RequireAction("case.decide", LoadCase);
    }

    private static async ValueTask<ResourceRef?> LoadCase(HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (httpContext.Request.RouteValues["id"] is not string id || !CaseIdPattern().IsMatch(id))
        {
            return null;
        }

        var data = httpContext.RequestServices.GetRequiredService<IAppData>();
        var kycCase = await data.Query<KycCase>().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        return kycCase is null ? null : ToResource(kycCase);
    }

    private static ResourceRef ToResource(KycCase c) =>
        ResourceRef.Of(ResourceType, c.Id, new Dictionary<string, object?>
        {
            ["assignedAnalystId"] = c.AssignedAnalystId,
            ["preparedBy"] = c.PreparedBy,
            ["status"] = c.Status,
        });

    private static async Task<IResult> ListCases(IAppData data, ICurrentUser user, IAuthorizer authorizer, CancellationToken ct)
    {
        var cases = await data.Query<KycCase>().OrderBy(c => c.CaseRef).ToListAsync(ct);
        var rows = cases
            .Where(c => authorizer.Authorize(user.Subject, KycApp.AppSlug, "case.view", ToResource(c)).Allowed)
            .Select(c => new CaseSummary(
                c.Id,
                c.CaseRef,
                PiiMask.Name(c.CustomerName),
                c.RiskScore,
                c.Status,
                c.AssignedAnalystId,
                c.PreparedBy,
                authorizer.Authorize(user.Subject, KycApp.AppSlug, "case.assign", ToResource(c)).Allowed))
            .ToList();
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetCase(string id, IAppData data, ICurrentUser user, IAuthorizer authorizer, CancellationToken ct)
    {
        var kycCase = await data.Query<KycCase>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (kycCase is null)
        {
            return NotFound();
        }

        var notes = await data.Query<ReviewNote>().Where(n => n.CaseId == id).ToListAsync(ct);
        var decisions = await data.Query<KycDecision>().Where(d => d.CaseId == id).ToListAsync(ct);
        return Results.Ok(ToDetail(kycCase, notes, decisions, Permissions(kycCase, user, authorizer)));
    }

    private static async Task<IResult> RevealPii(string id, IUnitOfWork uow, CancellationToken ct)
    {
        var revealed = await uow.ExecuteAsync(
            new UnitOfWorkContext("case.reveal-pii", ResourceType, id),
            async (scope, token) =>
            {
                scope.MarkAsSensitiveRead("pii-reveal");
                var kycCase = await scope.FindAsync<KycCase>(id, token) ?? throw new CaseConflictException("Case not found");
                return new RevealedPii(
                    kycCase.CustomerName,
                    kycCase.DateOfBirth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    kycCase.SsnLast4);
            },
            ct);
        return Results.Ok(revealed);
    }

    private static Task<IResult> Assign(string id, IUnitOfWork uow, ICurrentUser user, CancellationToken ct) =>
        Mutate(id, "case.assign", uow, (scope, kycCase) =>
        {
            if (kycCase.AssignedAnalystId is not null)
            {
                throw new CaseConflictException("Case is already assigned");
            }

            kycCase.AssignedAnalystId = user.RequiredUser.Id;
            if (kycCase.Status == KycStatus.New)
            {
                kycCase.Status = KycStatus.InReview;
            }

            scope.Publish("kyc.case.assigned", new { caseId = kycCase.Id, assignedAnalystId = kycCase.AssignedAnalystId });
        }, ct);

    private static async Task<IResult> AddNote(string id, NoteRequest? body, IUnitOfWork uow, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        var text = body?.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
        {
            return BadRequest("text is required (1..2000 characters)");
        }

        var now = Now(http);
        return await Mutate(id, "case.note.add", uow, (scope, kycCase) =>
        {
            var note = new ReviewNote
            {
                Id = "note-" + Guid.NewGuid().ToString("N"),
                CaseId = kycCase.Id,
                AuthorId = user.RequiredUser.Id,
                Text = text,
                CreatedAt = now,
            };
            scope.Add(note);
            scope.Publish("kyc.case.note-added", new { caseId = kycCase.Id, noteId = note.Id, authorId = note.AuthorId });
            return note;
        }, ct);
    }

    private static async Task<IResult> Recommend(string id, RecommendationRequest? body, IUnitOfWork uow, ICurrentUser user, CancellationToken ct)
    {
        var recommendation = body?.Recommendation;
        var rationale = body?.Rationale?.Trim();
        if (recommendation is null || !Recommendations.Contains(recommendation, StringComparer.Ordinal))
        {
            return BadRequest("recommendation must be one of: approve, reject, escalate");
        }

        if (string.IsNullOrEmpty(rationale) || rationale.Length > MaxTextLength)
        {
            return BadRequest("rationale is required (1..2000 characters)");
        }

        return await Mutate(id, "case.recommend", uow, (scope, kycCase) =>
        {
            var userId = user.RequiredUser.Id;
            if (!OpenStatuses.Contains(kycCase.Status, StringComparer.Ordinal))
            {
                throw new CaseConflictException("Case is not open for a recommendation");
            }

            if (kycCase.AssignedAnalystId is not null && kycCase.AssignedAnalystId != userId)
            {
                throw new CaseConflictException("Case is assigned to another analyst");
            }

            kycCase.AssignedAnalystId = userId;
            kycCase.Recommendation = recommendation;
            kycCase.RecommendationRationale = rationale;
            kycCase.PreparedBy = userId;
            kycCase.Status = KycStatus.PendingSeniorReview;
            scope.Publish("kyc.case.recommended", new { caseId = kycCase.Id, recommendation, preparedBy = userId });
        }, ct);
    }

    private static async Task<IResult> Decide(string id, DecisionRequest? body, IUnitOfWork uow, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        var decision = body?.Decision;
        var rationale = body?.Rationale?.Trim();
        if (decision is null || !Decisions.Contains(decision, StringComparer.Ordinal))
        {
            return BadRequest("decision must be one of: approve, reject");
        }

        if (string.IsNullOrEmpty(rationale) || rationale.Length > MaxTextLength)
        {
            return BadRequest("rationale is required (1..2000 characters)");
        }

        var now = Now(http);
        return await Mutate(id, "case.decide", uow, (scope, kycCase) =>
        {
            var userId = user.RequiredUser.Id;

            // Defence in depth: the policy already enforces both; re-checked inside the transaction (no TOCTOU).
            if (kycCase.Status != KycStatus.PendingSeniorReview)
            {
                throw new CaseConflictException("Case is not pending senior review");
            }

            if (kycCase.PreparedBy == userId)
            {
                throw new CaseConflictException("maker-checker: the preparer cannot decide");
            }

            scope.Add(new KycDecision
            {
                Id = "dec-" + Guid.NewGuid().ToString("N"),
                CaseId = kycCase.Id,
                DeciderId = userId,
                Decision = decision,
                Rationale = rationale,
                CreatedAt = now,
            });
            kycCase.Status = decision == "approve" ? KycStatus.Approved : KycStatus.Rejected;
            scope.Publish("kyc.case.decided", new { caseId = kycCase.Id, decision, deciderId = userId });
        }, ct);
    }

    private static Task<IResult> Mutate(string id, string action, IUnitOfWork uow, Action<IWriteScope, KycCase> change, CancellationToken ct) =>
        Mutate(id, action, uow, (scope, kycCase) =>
        {
            change(scope, kycCase);
            return (ReviewNote?)null;
        }, ct);

    /// <summary>Runs one UnitOfWork (business change + audit + outbox + idempotency) and returns the masked case detail.</summary>
    private static async Task<IResult> Mutate<TAdded>(
        string id,
        string action,
        IUnitOfWork uow,
        Func<IWriteScope, KycCase, TAdded?> change,
        CancellationToken ct)
        where TAdded : class
    {
        try
        {
            var detail = await uow.ExecuteAsync(
                new UnitOfWorkContext(action, ResourceType, id),
                async (scope, token) =>
                {
                    var kycCase = await scope.FindAsync<KycCase>(id, token) ?? throw new CaseConflictException("Case not found");
                    var added = change(scope, kycCase);
                    var notes = await scope.Query<ReviewNote>().Where(n => n.CaseId == id).ToListAsync(token);
                    if (added is ReviewNote note && notes.TrueForAll(n => n.Id != note.Id))
                    {
                        notes.Add(note);
                    }

                    var decisions = await scope.Query<KycDecision>().Where(d => d.CaseId == id).ToListAsync(token);
                    return ToDetail(kycCase, notes, decisions, permissions: null);
                },
                ct);
            return Results.Ok(detail);
        }
        catch (CaseConflictException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
    }

    private static CasePermissions Permissions(KycCase c, ICurrentUser user, IAuthorizer authorizer)
    {
        var resource = ToResource(c);
        PermissionHint Hint(string action)
        {
            var d = authorizer.Authorize(user.Subject, KycApp.AppSlug, action, resource);
            return new PermissionHint(d.Allowed, d.Reason);
        }

        return new CasePermissions(
            Hint("case.reveal-pii"),
            Hint("case.assign"),
            Hint("case.note.add"),
            Hint("case.recommend"),
            Hint("case.decide"));
    }

    private static CaseDetail ToDetail(KycCase c, IEnumerable<ReviewNote> notes, IEnumerable<KycDecision> decisions, CasePermissions? permissions) =>
        new(
            c.Id,
            c.CaseRef,
            PiiMask.Name(c.CustomerName),
            PiiMask.Date(c.DateOfBirth),
            PiiMask.Last4(c.SsnLast4),
            c.RiskScore,
            c.Status,
            c.AssignedAnalystId,
            c.PreparedBy,
            c.Recommendation,
            c.RecommendationRationale,
            ParseDocuments(c.DocumentsJson),
            [.. notes.OrderBy(n => n.CreatedAt).Select(n => new NoteDto(n.Id, n.AuthorId, n.Text, n.CreatedAt))],
            [.. decisions.OrderBy(d => d.CreatedAt).Select(d => new DecisionDto(d.Id, d.DeciderId, d.Decision, d.Rationale, d.CreatedAt))],
            permissions);

    internal static string SerializeDocuments(IReadOnlyList<DocumentMeta> documents) =>
        JsonSerializer.Serialize(documents, DocumentJson);

    private static List<DocumentMeta> ParseDocuments(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<DocumentMeta>>(json, DocumentJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DateTimeOffset Now(HttpContext http) =>
        (http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

    private static IResult BadRequest(string title) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Case not found");

    private sealed class CaseConflictException(string message) : Exception(message);
}
