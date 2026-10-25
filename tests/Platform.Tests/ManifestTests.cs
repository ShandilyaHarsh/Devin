using Barebone.Platform.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Platform.Tests.Infrastructure;

namespace Platform.Tests;

public sealed class ManifestTests(PlatformFactory factory) : IClassFixture<PlatformFactory>
{
    private IManifestService Service => factory.Services.GetRequiredService<IManifestService>();

    public static TheoryData<string, string, string?> InvalidMutations => new()
    {
        { "missing owner", "owner:", null },
        { "missing name", "name:", null },
        { "missing businessOwner", "businessOwner:", null },
        { "bad slug uppercase", "name:", "name: KYC_Case_Reviewer" },
        { "bad slug too short", "name:", "name: ab" },
        { "bad slug leading digit", "name:", "name: 1kyc-reviewer" },
        { "unknown classification", "dataClassification:", "dataClassification: top-secret" },
        { "unknown access", "access:", "access: admin" },
        { "unknown stage", "stage:", "stage: production" },
        { "card data present", "cardData:", "cardData: pan" },
        { "card data tokenized", "cardData:", "cardData: tokenized" },
        { "pii on internal app", "dataClassification:", "dataClassification: internal" },
        { "pii on public app", "dataClassification:", "dataClassification: public" },
        { "wildcard access list on restricted", "accessList:", "accessList: [\"*\"]" },
    };

    [Fact]
    public void ExampleManifestFromPlanIsValid()
    {
        var result = Service.Validate(Manifests.ValidKyc);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);

        var manifest = Service.Parse(Manifests.ValidKyc);
        Assert.Equal("kyc-case-reviewer", manifest.Name);
        Assert.Equal(DataClassification.Restricted, manifest.DataClassification);
        Assert.Equal(AccessMode.Write, manifest.Access);
        Assert.Equal(AppStage.BuilderOnly, manifest.Stage);
        Assert.Equal(["customerName", "dateOfBirth", "ssnLast4"], manifest.PiiFields);
        Assert.Equal("none", manifest.CardData);
    }

    [Theory]
    [MemberData(nameof(InvalidMutations))]
    public void InvalidManifestsAreRejected(string description, string linePrefix, string? replacement)
    {
        var yaml = Manifests.Replace(Manifests.ValidKyc, linePrefix, replacement);
        var result = Service.Validate(yaml);
        Assert.False(result.IsValid, "should be invalid: " + description);
        Assert.NotEmpty(result.Errors);

        var ex = Assert.Throws<ManifestValidationException>(() => Service.Parse(yaml));
        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public void WildcardAccessListIsAllowedOnlyForInternalOrPublicWithoutPii()
    {
        var yaml = Manifests.Replace(Manifests.ValidKyc, "dataClassification:", "dataClassification: internal");
        yaml = Manifests.Replace(yaml, "piiFields:", "piiFields: []");
        yaml = Manifests.Replace(yaml, "accessList:", "accessList: [\"*\"]");
        var result = Service.Validate(yaml);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));

        var confidential = Manifests.Replace(yaml, "dataClassification:", "dataClassification: confidential");
        Assert.False(Service.Validate(confidential).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not: [valid yaml")]
    [InlineData("just a string")]
    public void GarbageIsRejectedWithoutThrowingFromValidate(string yaml)
    {
        var result = Service.Validate(yaml);
        Assert.False(result.IsValid);
        Assert.Throws<ManifestValidationException>(() => Service.Parse(yaml));
    }

    [Fact]
    public void ToYamlRoundTrips()
    {
        var manifest = Service.Parse(Manifests.ValidKyc);
        var yaml = Service.ToYaml(manifest);
        Assert.True(Service.Validate(yaml).IsValid, yaml);
        var again = Service.Parse(yaml);
        Assert.Equal(manifest.Name, again.Name);
        Assert.Equal(manifest.DataClassification, again.DataClassification);
        Assert.Equal(manifest.Access, again.Access);
        Assert.Equal(manifest.PiiFields, again.PiiFields);
        Assert.Equal(manifest.Owner, again.Owner);
    }
}

public sealed class ManifestFailClosedStartupTests
{
    private const string ValidPolicy = "app: bad-app\nrules:\n  - action: thing.list\n    effect: allow\n    roles: [Requester]\n";

    public static TheoryData<string, string, string?> BrokenApps => new()
    {
        { "card data", "cardData:", "cardData: pan" },
        { "missing owner", "owner:", null },
        { "slug differs from folder", "name:", "name: other-app" },
        { "pii on internal", "dataClassification:", "dataClassification: internal" },
    };

    [Theory]
    [MemberData(nameof(BrokenApps))]
    public void HostRefusesToStartWithInvalidManifest(string description, string linePrefix, string? replacement)
    {
        var appsDir = Path.Combine(Path.GetTempPath(), "barebone-apps-" + Guid.NewGuid().ToString("N"));
        var appDir = Path.Combine(appsDir, "bad-app");
        Directory.CreateDirectory(appDir);
        try
        {
            var yaml = Manifests.Replace(Manifests.ValidKyc, "name:", "name: bad-app");
            yaml = Manifests.Replace(yaml, "backend:", null);
            yaml = Manifests.Replace(yaml, linePrefix, replacement);
            File.WriteAllText(Path.Combine(appDir, "platform.yaml"), yaml);
            File.WriteAllText(Path.Combine(appDir, "policy.yaml"), ValidPolicy);

            using var factory = new PlatformFactory().WithSetting("Platform:AppsDirectory", appsDir);
            Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            _ = description;
        }
        finally
        {
            Directory.Delete(appsDir, recursive: true);
        }
    }

    [Fact]
    public void HostRefusesToStartWithMissingPolicy()
    {
        var appsDir = Path.Combine(Path.GetTempPath(), "barebone-apps-" + Guid.NewGuid().ToString("N"));
        var appDir = Path.Combine(appsDir, "bad-app");
        Directory.CreateDirectory(appDir);
        try
        {
            var yaml = Manifests.Replace(Manifests.ValidKyc, "name:", "name: bad-app");
            File.WriteAllText(Path.Combine(appDir, "platform.yaml"), Manifests.Replace(yaml, "backend:", null));
            using var factory = new PlatformFactory().WithSetting("Platform:AppsDirectory", appsDir);
            Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        }
        finally
        {
            Directory.Delete(appsDir, recursive: true);
        }
    }
}
