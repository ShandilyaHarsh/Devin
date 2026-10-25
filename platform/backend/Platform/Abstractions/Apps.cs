using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Barebone.Platform.Abstractions;

/// <summary>Implemented once per app assembly. Discovered via the app's platform.yaml (backend.assembly).</summary>
public interface IPlatformApp
{
    /// <summary>Must equal the manifest name and the folder name under apps/.</summary>
    string Slug { get; }

    /// <summary>Register the app's entities in the shared model. Table names should be prefixed with the app slug.</summary>
    void ConfigureModel(ModelBuilder modelBuilder);

    /// <summary>Map endpoints on the platform-created group /api/apps/{slug}. Every endpoint must call RequireAction.</summary>
    void MapEndpoints(RouteGroupBuilder group);

    /// <summary>Seed synthetic data. Called by the platform seeder inside a UnitOfWork.</summary>
    Task SeedAsync(ISeedContext context, CancellationToken cancellationToken);
}
