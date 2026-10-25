using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Barebone.Platform.Abstractions;
using Barebone.Platform.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Barebone.Platform;

public static partial class PlatformExtensions
{
    /// <summary>Registers all platform services (data, audit, policy, catalog, sessions, telemetry, rate limits).</summary>
    public static WebApplicationBuilder AddBarebonePlatform(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.FormatterName = RedactingJsonFormatter.Name)
            .AddConsoleFormatter<RedactingJsonFormatter, ConsoleFormatterOptions>();

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("barebone-platform"));
        otel.WithTracing(t =>
        {
            t.AddAspNetCoreInstrumentation(o => o.RecordException = false);
            if (builder.Configuration.GetValue("Platform:Telemetry:ConsoleTraces", true))
            {
                t.AddConsoleExporter();
            }
        });

        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        });

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<Internal.SessionOptions>().BindConfiguration("Platform:Session");
        services.AddSingleton<IManifestService, ManifestService>();
        services.AddSingleton<AppRegistry>();
        services.AddSingleton<IAuthorizer, Authorizer>();
        services.AddDbContextFactory<PlatformDbContext>((sp, o) =>
            o.UseSqlite(ResolveConnectionString(sp))
                .ReplaceService<IModelCacheKeyFactory, RegistryModelCacheKeyFactory>());
        services.AddSingleton<WriteLock>();
        services.AddSingleton<AuditChain>();
        services.AddSingleton<AuditWriter>();
        services.AddSingleton<AuditVerifier>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAppData, AppData>();
        services.AddSingleton<CatalogService>();
        services.AddSingleton<ICatalog>(sp => sp.GetRequiredService<CatalogService>());
        services.AddSingleton<ICatalogAdmin>(sp => sp.GetRequiredService<CatalogService>());
        services.AddSingleton<ISyntheticSeeder, SyntheticSeeder>();
        services.AddSingleton<IOutboxRelay, OutboxRelay>();
        services.TryAddSingleton<IOutboxPublisher, LogOutboxPublisher>();
        services.AddHostedService<OutboxRelayService>();
        services.AddSingleton<ISessionStore, InMemorySessionStore>();
        services.TryAddSingleton<IIdentityProvider, DevIdentityProvider>();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddSingleton<StageGate>();
        services.AddSingleton<IdempotencyInFlight>();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, _) => new ValueTask(Problems.WriteAsync(ctx.HttpContext, 429, "Too many requests", "rate-limited"));
            o.AddPolicy(PlatformEndpoints.LoginRateLimitPolicy, http =>
                Limiter("login:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"), Limit(http, "LoginPerMinute", 20)));
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                Methods.IsSafe(http.Request.Method)
                    ? RateLimitPartition.GetNoLimiter("safe")
                    : Limiter("mut:" + ((http.Items[HttpItems.User] as PlatformUser)?.Id ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown"), Limit(http, "MutationsPerMinute", 120)));
        });

        return builder;
    }

    /// <summary>
    /// Validates secrets/manifests/policies (fail closed), creates the database, registers bootstrap apps, builds the
    /// request pipeline and maps platform + app endpoints. Call before mapping any SPA fallback.
    /// </summary>
    public static WebApplication UseBarebonePlatform(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var sp = app.Services;
        _ = sp.GetRequiredService<AuditChain>();
        var registry = sp.GetRequiredService<AppRegistry>();
        var idp = sp.GetRequiredService<IIdentityProvider>();
        if (idp.Name == "dev" && app.Environment.IsProduction() && !app.Configuration.GetValue("Platform:Identity:AllowDevProvider", false))
        {
            throw new InvalidOperationException("The dev identity provider is not allowed in Production.");
        }

        InitializeDatabase(sp);
        BootstrapAppsAsync(sp, registry).GetAwaiter().GetResult();

        app.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = PlatformMiddleware.HandleException });
        app.Use(PlatformMiddleware.SecurityHeaders);
        app.Use(PlatformMiddleware.CorrelationId);
        app.UseStatusCodePages(async ctx =>
        {
            var http = ctx.HttpContext;
            if (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/health"))
            {
                await Problems.WriteAsync(http, http.Response.StatusCode, Problems.TitleFor(http.Response.StatusCode));
            }
        });
        if (Directory.Exists(app.Environment.WebRootPath))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }

        app.UseRouting();
        app.Use(PlatformMiddleware.Session);
        app.UseRateLimiter();
        app.Use(PlatformMiddleware.Csrf);
        app.Use(PlatformMiddleware.AppFilters);

        PlatformEndpoints.Map(app);
        foreach (var loaded in registry.Apps.Where(a => a.Code is not null))
        {
            var group = app.MapGroup("/api/apps/" + loaded.Manifest.Name);
            group.WithMetadata(new AppEndpointMetadata(loaded.Manifest.Name));
            loaded.Code!.MapEndpoints(group);
        }

        return app;
    }

    private static RateLimitPartition<string> Limiter(string key, int permits) =>
        permits <= 0
            ? RateLimitPartition.GetNoLimiter(key)
            : RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });

    private static int Limit(HttpContext http, string name, int fallback) =>
        http.RequestServices.GetRequiredService<IConfiguration>().GetValue("Platform:RateLimits:" + name, fallback);

    private static string ResolveConnectionString(IServiceProvider sp)
    {
        var configured = sp.GetRequiredService<IConfiguration>().GetConnectionString("Platform") ?? "Data Source=App_Data/platform.db";
        var csb = new SqliteConnectionStringBuilder(configured);
        if (csb.DataSource != ":memory:" && csb.Mode != SqliteOpenMode.Memory && !Path.IsPathRooted(csb.DataSource))
        {
            csb.DataSource = Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, csb.DataSource);
        }

        if (csb.Mode != SqliteOpenMode.Memory && Path.GetDirectoryName(csb.DataSource) is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
        }

        return csb.ToString();
    }

    [GeneratedRegex(@"(?:CREATE TABLE|ON) ""([^""]+)""")]
    private static partial Regex TableRefRegex();

    private static void InitializeDatabase(IServiceProvider sp)
    {
        using var db = sp.GetRequiredService<IDbContextFactory<PlatformDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        var existing = db.Database.SqlQueryRaw<string>("SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'table'").ToHashSet(StringComparer.Ordinal);
        var missing = db.Model.GetEntityTypes().Select(e => e.GetTableName()).OfType<string>().Where(t => !existing.Contains(t)).ToHashSet(StringComparer.Ordinal);
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var statement in Regex.Split(db.Database.GenerateCreateScript(), @";\s*\r?\n"))
        {
            var match = TableRefRegex().Match(statement);
            if (match.Success && missing.Contains(match.Groups[1].Value))
            {
#pragma warning disable EF1002 // statements come from EF's own generated DDL for the model
                db.Database.ExecuteSqlRaw(statement.Trim());
#pragma warning restore EF1002
            }
        }
    }

    private static async Task BootstrapAppsAsync(IServiceProvider sp, AppRegistry registry)
    {
        var catalog = sp.GetRequiredService<ICatalog>();
        foreach (var app in registry.Apps.Where(a => a.Manifest.Registration == RegistrationMode.Bootstrap))
        {
            if (await catalog.GetAsync(app.Manifest.Name, CancellationToken.None) is not null)
            {
                continue;
            }

            using var scope = sp.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var admin = scope.ServiceProvider.GetRequiredService<ICatalogAdmin>();
            var seeder = scope.ServiceProvider.GetRequiredService<ISyntheticSeeder>();
            AppSlugOverride.Current.Value = app.Manifest.Name;
            try
            {
                await uow.ExecuteAsync(new UnitOfWorkContext("catalog.register", "app", app.Manifest.Name), async (ws, ct) =>
                {
                    var entry = await admin.RegisterAsync(ws, new RegisterAppRequest(app.Manifest, "system", "system"), ct);
                    await seeder.SeedAppAsync(ws, app.Manifest.Name, ct);
                    return entry;
                });
            }
            finally
            {
                AppSlugOverride.Current.Value = null;
            }
        }
    }
}
