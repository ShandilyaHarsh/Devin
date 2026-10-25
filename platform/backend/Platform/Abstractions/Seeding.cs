namespace Barebone.Platform.Abstractions;

public enum SyntheticKind
{
    FullName, FirstName, LastName, Email, DateOfBirth, Last4Digits, RiskScore, Company,
    Sentence, Paragraph, PastDate, FileName, UniqueId, OneOf,
}

/// <summary>Marks how the synthetic data generator fills a property. For OneOf, pass the allowed values.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SyntheticAttribute(SyntheticKind kind, params string[] values) : Attribute
{
    public SyntheticKind Kind { get; } = kind;
    public IReadOnlyList<string> Values { get; } = values;
}

public interface ISyntheticDataGenerator
{
    /// <summary>Deterministic for a given app slug and seed base. Properties without [Synthetic] keep defaults.</summary>
    IReadOnlyList<T> Generate<T>(int count) where T : class, new();

    /// <summary>Deterministic random source for app-specific choices.</summary>
    Random Random { get; }
}

public interface ISeedContext
{
    string AppSlug { get; }
    DataClassification DataClassification { get; }
    IWriteScope Scope { get; }
    ISyntheticDataGenerator Generator { get; }
}

public interface ISyntheticSeeder
{
    /// <summary>Seeds the app if its code is loaded; returns false if no code or already seeded.</summary>
    Task<bool> SeedAppAsync(IWriteScope scope, string slug, CancellationToken cancellationToken);
}
