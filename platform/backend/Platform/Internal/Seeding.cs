using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Barebone.Platform.Abstractions;
using Bogus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Barebone.Platform.Internal;

/// <summary>Deterministic synthetic data. Never real PII; emails use example.test; last-4 values are from the 9xxx range.</summary>
internal sealed class SyntheticDataGenerator : ISyntheticDataGenerator
{
    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly Faker faker;
    private int emailCounter;

    public SyntheticDataGenerator(string appSlug, int seedBase)
    {
        var seed = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(appSlug + ":" + seedBase.ToString(CultureInfo.InvariantCulture))), 0);
        faker = new Faker("en") { Random = new Randomizer(seed) };
        Random = new Random(seed);
    }

    public Random Random { get; }

    public IReadOnlyList<T> Generate<T>(int count) where T : class, new()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var props = typeof(T).GetProperties()
            .Select(p => (Prop: p, Attr: (SyntheticAttribute?)Attribute.GetCustomAttribute(p, typeof(SyntheticAttribute))))
            .Where(x => x.Attr is not null && x.Prop.CanWrite)
            .ToList();
        var list = new List<T>(count);
        for (var i = 0; i < count; i++)
        {
            var item = new T();
            string? first = null;
            string? last = null;
            foreach (var (prop, attr) in props)
            {
                object? value = attr!.Kind switch
                {
                    SyntheticKind.FullName => $"{first ??= faker.Name.FirstName()} {last ??= faker.Name.LastName()}",
                    SyntheticKind.FirstName => first ??= faker.Name.FirstName(),
                    SyntheticKind.LastName => last ??= faker.Name.LastName(),
                    SyntheticKind.Email => Email(first ??= faker.Name.FirstName(), last ??= faker.Name.LastName()),
                    SyntheticKind.DateOfBirth => faker.Date.Between(Anchor.AddYears(-80), Anchor.AddYears(-21)),
                    SyntheticKind.Last4Digits => "9" + faker.Random.Int(0, 999).ToString("D3", CultureInfo.InvariantCulture),
                    SyntheticKind.RiskScore => faker.Random.Int(1, 100),
                    SyntheticKind.Company => faker.Company.CompanyName(),
                    SyntheticKind.Sentence => faker.Lorem.Sentence(),
                    SyntheticKind.Paragraph => faker.Lorem.Paragraph(),
                    SyntheticKind.PastDate => faker.Date.Between(Anchor.AddDays(-90), Anchor),
                    SyntheticKind.FileName => faker.System.FileName("pdf"),
                    SyntheticKind.UniqueId => new Guid(faker.Random.Bytes(16)),
                    SyntheticKind.OneOf => attr.Values.Count == 0 ? null : attr.Values[faker.Random.Int(0, attr.Values.Count - 1)],
                    _ => null,
                };
                prop.SetValue(item, Convert(value, prop.PropertyType));
            }

            list.Add(item);
        }

        return list;
    }

    private string Email(string first, string last) =>
        $"{new string([.. first.ToLowerInvariant().Where(char.IsAsciiLetter)])}.{new string([.. last.ToLowerInvariant().Where(char.IsAsciiLetter)])}{++emailCounter}@example.test";

    private static object? Convert(object? value, Type target)
    {
        if (value is null)
        {
            return null;
        }

        var t = Nullable.GetUnderlyingType(target) ?? target;
        return value switch
        {
            _ when t.IsInstanceOfType(value) => value,
            DateTime d when t == typeof(DateOnly) => DateOnly.FromDateTime(d),
            DateTime d when t == typeof(DateTimeOffset) => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
            DateTime d when t == typeof(string) => t == typeof(string) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            Guid g when t == typeof(string) => g.ToString(),
            int n when t == typeof(double) => (double)n,
            int n when t == typeof(decimal) => (decimal)n,
            int n when t == typeof(long) => (long)n,
            int n when t == typeof(string) => n.ToString(CultureInfo.InvariantCulture),
            _ => System.Convert.ChangeType(value, t, CultureInfo.InvariantCulture),
        };
    }
}

internal sealed class SeedContext(string appSlug, DataClassification classification, IWriteScope scope, ISyntheticDataGenerator generator) : ISeedContext
{
    public string AppSlug { get; } = appSlug;
    public DataClassification DataClassification { get; } = classification;
    public IWriteScope Scope { get; } = scope;
    public ISyntheticDataGenerator Generator { get; } = generator;
}

internal sealed class SyntheticSeeder(AppRegistry registry, IConfiguration configuration) : ISyntheticSeeder
{
    public async Task<bool> SeedAppAsync(IWriteScope scope, string slug, CancellationToken cancellationToken)
    {
        if (registry.Get(slug) is not { Code: { } code } app || scope is not WriteScope ws)
        {
            return false;
        }

        var tables = ws.Db.Model.GetEntityTypes()
            .Where(e => e.FindAnnotation(PlatformDbContext.OwnerAnnotation)?.Value as string == slug)
            .Select(e => e.GetTableName())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var table in tables)
        {
            var sql = "SELECT EXISTS(SELECT 1 FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\") AS \"Value\"";
#pragma warning disable EF1002 // table name comes from the EF model, not user input
            var hasRows = await ws.Db.Database.SqlQueryRaw<int>(sql).SingleAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
            if (hasRows == 1)
            {
                return false;
            }
        }

        var generator = new SyntheticDataGenerator(slug, configuration.GetValue("Platform:SeedBase", 4242));
        await code.SeedAsync(new SeedContext(slug, app.Manifest.DataClassification, scope, generator), cancellationToken).ConfigureAwait(false);
        return true;
    }
}
