using Barebone.Platform.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Barebone.Builder;

/// <summary>The Barebone Builder portal: intake, ROI, maker-checker approval, registration and staging of apps.</summary>
public sealed class BuilderApp : IPlatformApp
{
    public const string AppSlug = "barebone-builder";
    public const string FactoryTeam = "app-factory";
    public const string FactoryEmail = "app-factory@example.test";
    private const string IntakeResource = "intake";
    private const string AppResource = "app";
    private const int MaxManifestDescription = 300;

    public string Slug => AppSlug;

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Intake>(e =>
        {
            e.ToTable("builder_intakes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(120).IsRequired();
            e.Property(x => x.AppSlug).HasMaxLength(41).IsRequired();
            e.Property(x => x.Problem).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Users).HasMaxLength(500).IsRequired();
            e.Property(x => x.DataTouched).HasMaxLength(500).IsRequired();
            e.Property(x => x.DataClassification).HasMaxLength(16).IsRequired();
            e.Property(x => x.Access).HasMaxLength(16).IsRequired();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.RequesterId).HasMaxLength(64).IsRequired();
            e.Property(x => x.DecidedBy).HasMaxLength(64);
            e.Property(x => x.RejectReason).HasMaxLength(1000);
            e.HasIndex(x => x.AppSlug);
            e.HasIndex(x => x.RequesterId);
        });
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/intakes", ListIntakes).RequireAction("intake.list");
        group.MapGet("/intakes/{id}", GetIntake).RequireAction("intake.view", LoadIntake);
        group.MapGet("/roi", GetRoi).RequireAction("intake.create");
        group.MapPost("/intakes", CreateIntake).RequireAction("intake.create");
        group.MapPost("/intakes/{id}/approve", ApproveIntake).RequireAction("intake.approve", LoadIntake);
        group.MapPost("/intakes/{id}/reject", RejectIntake).RequireAction("intake.approve", LoadIntake);
        group.MapPost("/apps/{appSlug}/stage", SetStage).RequireAction("app.stage.set", LoadApp);
        group.MapPut("/apps/{appSlug}/access-list", SetAccessList).RequireAction("app.access.set", LoadApp);
        group.MapGet("/directory", ListDirectory).RequireAction("intake.list");
    }

    public Task SeedAsync(ISeedContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var created = new DateTimeOffset(2026, 1, 5, 14, 0, 0, TimeSpan.Zero);
        context.Scope.Add(new Intake
        {
            Id = "itk-sample-0001",
            Title = "Vendor spend dashboard",
            AppSlug = "vendor-spend-dashboard",
            Problem = "Finance ops rebuilds the monthly vendor spend view by hand from three exports.",
            Users = "Finance operations analysts",
            DataTouched = "Vendor names, invoice totals by month (no payment instruments)",
            DataClassification = Vocab.Confidential,
            Access = Vocab.ReadOnly,
            HoursSavedPerUserPerWeek = 3,
            NumberOfUsers = 10,
            RequesterId = "u-alice",
            CreatedAt = created,
        });
        context.Scope.Add(new Intake
        {
            Id = "itk-sample-0002",
            Title = "Office access request tracker",
            AppSlug = "office-access-tracker",
            Problem = "Badge access requests live in a shared spreadsheet with no approval trail.",
            Users = "Workplace team and people managers",
            DataTouched = "Employee ids, office location, requested access level",
            DataClassification = Vocab.Internal,
            Access = Vocab.Write,
            HoursSavedPerUserPerWeek = 1.5,
            NumberOfUsers = 25,
            RequesterId = "u-alice",
            CreatedAt = created.AddDays(2),
        });
        return Task.CompletedTask;
    }

    private static async Task<IResult> ListIntakes(
        [FromServices] IAppData data, [FromServices] IAuthorizer authorizer, [FromServices] ICurrentUser currentUser,
        CancellationToken ct)
    {
        var rows = await data.Query<Intake>().ToListAsync(ct);
        var visible = rows
            .Where(i => authorizer.Authorize(currentUser.Subject, AppSlug, "intake.view", IntakeRef(i)).Allowed)
            .OrderByDescending(i => i.CreatedAt)
            .Select(IntakeDto.From)
            .ToList();
        return Results.Ok(visible);
    }

    private static async Task<IResult> GetIntake(string id, [FromServices] IAppData data, CancellationToken ct)
    {
        var row = await data.Query<Intake>().FirstOrDefaultAsync(i => i.Id == id, ct);
        return row is null ? NotFound() : Results.Ok(IntakeDto.From(row));
    }

    private static IResult GetRoi(
        double? hoursSavedPerUserPerWeek, int? numberOfUsers, string? access, string? dataClassification)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Validation.ValidateRoiInputs(errors, hoursSavedPerUserPerWeek, numberOfUsers, access, dataClassification);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        return Results.Ok(RoiModel.Compute(hoursSavedPerUserPerWeek!.Value, numberOfUsers!.Value, access!, dataClassification!));
    }

    private static async Task<IResult> CreateIntake(
        IntakeCreate? body, HttpContext http, [FromServices] IUnitOfWork uow, [FromServices] IAppData data,
        [FromServices] ICatalog catalog, [FromServices] ICurrentUser currentUser, CancellationToken ct)
    {
        var errors = Validation.ValidateIntake(body);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var slug = body!.AppSlug!;
        if (await SlugTakenAsync(slug, data, catalog, ct))
        {
            return Conflict("App slug already registered or requested", $"'{slug}' is in the catalog or has an open intake.");
        }

        var intake = new Intake
        {
            Id = "itk-" + Guid.NewGuid().ToString("N")[..20],
            Title = body.Title!.Trim(),
            AppSlug = slug,
            Problem = body.Problem!.Trim(),
            Users = body.Users!.Trim(),
            DataTouched = body.DataTouched!.Trim(),
            DataClassification = body.DataClassification!,
            Access = body.Access!,
            HoursSavedPerUserPerWeek = body.HoursSavedPerUserPerWeek!.Value,
            NumberOfUsers = body.NumberOfUsers!.Value,
            Status = IntakeStatus.Submitted,
            RequesterId = currentUser.RequiredUser.Id,
            CreatedAt = Now(http),
        };

        var dto = await uow.ExecuteAsync(new UnitOfWorkContext("intake.create", IntakeResource, intake.Id), (scope, _) =>
        {
            scope.Add(intake);
            scope.SetResourceId(intake.Id);
            scope.Publish("intake.submitted", new { intakeId = intake.Id, appSlug = intake.AppSlug, requesterId = intake.RequesterId });
            return Task.FromResult(IntakeDto.From(intake));
        }, ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> ApproveIntake(
        string id, HttpContext http, [FromServices] IUnitOfWork uow, [FromServices] IAppData data,
        [FromServices] ICatalog catalog, [FromServices] ICatalogAdmin catalogAdmin,
        [FromServices] IManifestService manifests, [FromServices] ISyntheticSeeder seeder,
        [FromServices] IIdentityProvider identity, [FromServices] ICurrentUser currentUser, CancellationToken ct)
    {
        var current = await data.Query<Intake>().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (current is null)
        {
            return NotFound();
        }

        if (current.Status != IntakeStatus.Submitted)
        {
            return Conflict("Intake already decided", $"Intake status is '{current.Status}'.");
        }

        if (await catalog.GetAsync(current.AppSlug, ct) is not null)
        {
            return Conflict("App slug already registered", $"'{current.AppSlug}' is already in the catalog.");
        }

        var requester = await identity.FindUserAsync(current.RequesterId, ct);
        if (requester is null)
        {
            return Results.Problem(
                title: "Requester not found in directory", statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var approver = currentUser.RequiredUser.Id;
        var now = Now(http);
        try
        {
            var dto = await uow.ExecuteAsync(new UnitOfWorkContext("intake.approve", IntakeResource, id), async (scope, token) =>
            {
                var intake = await scope.FindAsync<Intake>(id, token)
                    ?? throw new BuilderConflictException("Intake not found");
                if (intake.Status != IntakeStatus.Submitted || intake.RequesterId == approver)
                {
                    throw new BuilderConflictException("Intake already decided");
                }

                var manifest = BuildManifest(intake, requester);
                intake.Status = IntakeStatus.Approved;
                intake.DecidedBy = approver;
                intake.DecidedAt = now;
                intake.GeneratedManifestYaml = manifests.ToYaml(manifest);

                await catalogAdmin.RegisterAsync(scope, new RegisterAppRequest(manifest, intake.RequesterId, approver), token);
                var seeded = await seeder.SeedAppAsync(scope, intake.AppSlug, token);
                scope.Publish("app.registered", new
                {
                    slug = intake.AppSlug,
                    intakeId = intake.Id,
                    requesterId = intake.RequesterId,
                    approvedBy = approver,
                    stage = Vocab.StageName(manifest.Stage),
                    seeded,
                });
                return IntakeDto.From(intake) with { SeededSyntheticData = seeded };
            }, ct);
            return Results.Ok(dto);
        }
        catch (BuilderConflictException ex)
        {
            return Conflict(ex.Message, null);
        }
    }

    private static async Task<IResult> RejectIntake(
        string id, RejectRequest? body, HttpContext http, [FromServices] IUnitOfWork uow, [FromServices] ICurrentUser currentUser,
        CancellationToken ct)
    {
        var reason = body?.Reason?.Trim();
        if (reason is null || reason.Length is < 3 or > 1000 || reason.Any(char.IsControl))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] = ["Must be 3-1000 characters without control characters."],
            });
        }

        var decider = currentUser.RequiredUser.Id;
        var now = Now(http);
        try
        {
            var dto = await uow.ExecuteAsync(new UnitOfWorkContext("intake.reject", IntakeResource, id), async (scope, token) =>
            {
                var intake = await scope.FindAsync<Intake>(id, token)
                    ?? throw new BuilderConflictException("Intake not found");
                if (intake.Status != IntakeStatus.Submitted || intake.RequesterId == decider)
                {
                    throw new BuilderConflictException("Intake already decided");
                }

                intake.Status = IntakeStatus.Rejected;
                intake.DecidedBy = decider;
                intake.DecidedAt = now;
                intake.RejectReason = reason;
                scope.Publish("intake.rejected", new { intakeId = intake.Id, appSlug = intake.AppSlug, decidedBy = decider });
                return IntakeDto.From(intake);
            }, ct);
            return Results.Ok(dto);
        }
        catch (BuilderConflictException ex)
        {
            return Conflict(ex.Message, null);
        }
    }

    private static async Task<IResult> SetStage(
        string appSlug, StageRequest? body, [FromServices] IUnitOfWork uow, [FromServices] ICatalogAdmin catalogAdmin,
        CancellationToken ct)
    {
        if (body?.Stage is not { } stageName || !Vocab.Stages.TryGetValue(stageName, out var stage))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["stage"] = ["Must be one of: builder-only, demo, live."],
            });
        }

        var entry = await uow.ExecuteAsync(new UnitOfWorkContext("app.stage.set", AppResource, appSlug), async (scope, token) =>
        {
            var updated = await catalogAdmin.SetStageAsync(scope, appSlug, stage, token);
            scope.Publish("app.stage-changed", new { slug = appSlug, stage = stageName });
            return updated;
        }, ct);
        return Results.Ok(entry);
    }

    private static async Task<IResult> SetAccessList(
        string appSlug, AccessListRequest? body, [FromServices] IUnitOfWork uow, [FromServices] ICatalog catalog,
        [FromServices] ICatalogAdmin catalogAdmin, [FromServices] IIdentityProvider identity, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var ids = body?.UserIds;
        if (ids is null || ids.Count > Validation.MaxAccessListSize)
        {
            errors["userIds"] = [$"Must be an array of at most {Validation.MaxAccessListSize} user ids."];
            return Results.ValidationProblem(errors);
        }

        var distinct = ids.Distinct(StringComparer.Ordinal).ToList();
        var known = (await identity.ListUsersAsync(ct)).Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = distinct.Where(i => i != Vocab.Everyone && !known.Contains(i)).ToList();
        if (unknown.Count > 0)
        {
            errors["userIds"] = ["Unknown user ids: " + string.Join(", ", unknown.Take(10).Select(Sanitize))];
            return Results.ValidationProblem(errors);
        }

        var entry = await catalog.GetAsync(appSlug, ct);
        if (entry is null)
        {
            return NotFound();
        }

        if (distinct.Contains(Vocab.Everyone)
            && entry.DataClassification is not (DataClassification.Public or DataClassification.Internal))
        {
            errors["userIds"] = ["\"*\" (everyone) is only allowed for public or internal apps."];
            return Results.ValidationProblem(errors);
        }

        var updated = await uow.ExecuteAsync(new UnitOfWorkContext("app.access.set", AppResource, appSlug), async (scope, token) =>
        {
            var result = await catalogAdmin.SetAccessListAsync(scope, appSlug, distinct, token);
            scope.Publish("app.access-list-changed", new { slug = appSlug, count = distinct.Count });
            return result;
        }, ct);
        return Results.Ok(updated);
    }

    private static async Task<IResult> ListDirectory([FromServices] IIdentityProvider identity, CancellationToken ct)
    {
        var users = await identity.ListUsersAsync(ct);
        return Results.Ok(users.Select(u => new DirectoryUser(u.Id, u.DisplayName, u.Roles)).ToList());
    }

    private static async ValueTask<ResourceRef?> LoadIntake(HttpContext http, CancellationToken ct)
    {
        if (http.Request.RouteValues["id"] is not string id || !Validation.IdRegex().IsMatch(id))
        {
            return null;
        }

        var data = http.RequestServices.GetRequiredService<IAppData>();
        var row = await data.Query<Intake>().FirstOrDefaultAsync(i => i.Id == id, ct);
        return row is null ? null : IntakeRef(row);
    }

    private static async ValueTask<ResourceRef?> LoadApp(HttpContext http, CancellationToken ct)
    {
        if (http.Request.RouteValues["appSlug"] is not string slug || !Validation.SlugRegex().IsMatch(slug))
        {
            return null;
        }

        var entry = await http.RequestServices.GetRequiredService<ICatalog>().GetAsync(slug, ct);
        return entry is null
            ? null
            : ResourceRef.Of(AppResource, entry.Slug, new Dictionary<string, object?>
            {
                ["slug"] = entry.Slug,
                ["stage"] = Vocab.StageName(entry.Stage),
                ["requesterId"] = entry.RequesterId,
                ["approvedBy"] = entry.ApprovedBy,
            });
    }

    private static ResourceRef IntakeRef(Intake i) =>
        ResourceRef.Of(IntakeResource, i.Id, new Dictionary<string, object?>
        {
            ["requesterId"] = i.RequesterId,
            ["status"] = i.Status,
            ["appSlug"] = i.AppSlug,
        });

    private static AppManifest BuildManifest(Intake intake, PlatformUser requester) => new()
    {
        Name = intake.AppSlug,
        DisplayName = intake.Title,
        Description = intake.Problem.Length > MaxManifestDescription ? intake.Problem[..MaxManifestDescription] : intake.Problem,
        Owner = new OwnerRef(FactoryTeam, FactoryEmail),
        BusinessOwner = new BusinessOwnerRef(requester.DisplayName, requester.Email),
        DataClassification = Vocab.Classifications[intake.DataClassification],
        Access = Vocab.AccessModes[intake.Access],
        Stage = AppStage.BuilderOnly,
        Registration = RegistrationMode.Intake,
        Roles = [],
        AccessList = [],
        AuditedFields = [],
        PiiFields = [],
        CardData = "none",
    };

    private static async Task<bool> SlugTakenAsync(string slug, IAppData data, ICatalog catalog, CancellationToken ct) =>
        slug == AppSlug
        || await catalog.GetAsync(slug, ct) is not null
        || await data.Query<Intake>().AnyAsync(i => i.AppSlug == slug && i.Status != IntakeStatus.Rejected, ct);

    private static DateTimeOffset Now(HttpContext http) =>
        (http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

    private static string Sanitize(string value) =>
        new string(value.Where(c => char.IsLetterOrDigit(c) || c == '-').Take(64).ToArray());

    private static IResult NotFound() =>
        Results.Problem(title: "Not found", statusCode: StatusCodes.Status404NotFound);

    private static IResult Conflict(string title, string? detail) =>
        Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status409Conflict);
}

internal sealed class BuilderConflictException(string message) : Exception(message);
