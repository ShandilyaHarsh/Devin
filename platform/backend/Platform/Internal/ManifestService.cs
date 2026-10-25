using System.Text.RegularExpressions;
using Barebone.Platform.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Barebone.Platform.Internal;

internal sealed partial class ManifestService : IManifestService
{
    internal const string ReservedSlug = "platform";

    [GeneratedRegex("^[a-z][a-z0-9-]{2,40}$")]
    internal static partial Regex SlugRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public ManifestValidationResult Validate(string yaml)
    {
        var (_, errors) = TryParse(yaml);
        return new ManifestValidationResult(errors.Count == 0, errors);
    }

    public AppManifest Parse(string yaml)
    {
        var (manifest, errors) = TryParse(yaml);
        if (errors.Count > 0 || manifest is null)
        {
            throw new ManifestValidationException(errors.Count > 0 ? errors : ["manifest could not be parsed"]);
        }

        return manifest;
    }

    public string ToYaml(AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var dto = new ManifestDto
        {
            SchemaVersion = manifest.SchemaVersion,
            Name = manifest.Name,
            DisplayName = manifest.DisplayName,
            Description = manifest.Description,
            Owner = new OwnerDto { Team = manifest.Owner.Team, Email = manifest.Owner.Email },
            BusinessOwner = new BusinessOwnerDto { Name = manifest.BusinessOwner.Name, Email = manifest.BusinessOwner.Email },
            DataClassification = manifest.DataClassification.ToWire(),
            Access = manifest.Access.ToWire(),
            Stage = manifest.Stage.ToWire(),
            Registration = manifest.Registration.ToWire(),
            Roles = [.. manifest.Roles],
            AccessList = [.. manifest.AccessList],
            AuditedFields = [.. manifest.AuditedFields],
            PiiFields = [.. manifest.PiiFields],
            CardData = manifest.CardData,
            Backend = manifest.BackendAssembly is null ? null : new BackendDto { Assembly = manifest.BackendAssembly },
        };
        return Serializer.Serialize(dto);
    }

    private static (AppManifest? Manifest, List<string> Errors) TryParse(string yaml)
    {
        var errors = new List<string>();
        ManifestDto? dto;
        try
        {
            dto = string.IsNullOrWhiteSpace(yaml) ? null : Deserializer.Deserialize<ManifestDto>(yaml);
        }
        catch (YamlException ex)
        {
            errors.Add("invalid YAML: " + (ex.InnerException?.Message ?? ex.Message));
            return (null, errors);
        }

        if (dto is null)
        {
            errors.Add("manifest is empty");
            return (null, errors);
        }

        if (dto.SchemaVersion != 1)
        {
            errors.Add("schemaVersion must be 1");
        }

        Required(dto.Name, "name", errors);
        if (dto.Name is not null && (!SlugRegex().IsMatch(dto.Name) || dto.Name == ReservedSlug))
        {
            errors.Add("name must match ^[a-z][a-z0-9-]{2,40}$ and not be reserved");
        }

        Required(dto.DisplayName, "displayName", errors);
        Required(dto.Description, "description", errors);
        if (dto.Owner is null)
        {
            errors.Add("missing required field: owner");
        }
        else
        {
            Required(dto.Owner.Team, "owner.team", errors);
            Email(dto.Owner.Email, "owner.email", errors);
        }

        if (dto.BusinessOwner is null)
        {
            errors.Add("missing required field: businessOwner");
        }
        else
        {
            Required(dto.BusinessOwner.Name, "businessOwner.name", errors);
            Email(dto.BusinessOwner.Email, "businessOwner.email", errors);
        }

        var classificationOk = EnumNames.TryParseClassification(dto.DataClassification, out var classification);
        if (!classificationOk)
        {
            errors.Add("dataClassification must be one of public|internal|confidential|restricted");
        }

        if (!EnumNames.TryParseAccess(dto.Access, out var access))
        {
            errors.Add("access must be one of read-only|write");
        }

        if (!EnumNames.TryParseStage(dto.Stage, out var stage))
        {
            errors.Add("stage must be one of builder-only|demo|live");
        }

        if (!EnumNames.TryParseRegistration(dto.Registration, out var registration))
        {
            errors.Add("registration must be one of intake|bootstrap");
        }

        if (dto.CardData != "none")
        {
            errors.Add("cardData must be \"none\" (zero-PAN rule)");
        }

        var roles = dto.Roles ?? [];
        var accessList = dto.AccessList ?? [];
        var audited = dto.AuditedFields ?? [];
        var pii = dto.PiiFields ?? [];
        if (roles.Concat(accessList).Concat(audited).Concat(pii).Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("list entries must be non-empty strings");
        }

        if (pii.Count > 0 && classificationOk && classification < DataClassification.Confidential)
        {
            errors.Add("piiFields require dataClassification confidential or restricted");
        }

        if (accessList.Contains("*") && (!classificationOk || classification >= DataClassification.Confidential))
        {
            errors.Add("accessList \"*\" is only allowed for public or internal apps");
        }

        if (dto.Backend is not null && string.IsNullOrWhiteSpace(dto.Backend.Assembly))
        {
            errors.Add("backend.assembly must be non-empty when backend is set");
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        return (new AppManifest
        {
            SchemaVersion = dto.SchemaVersion,
            Name = dto.Name!,
            DisplayName = dto.DisplayName!,
            Description = dto.Description!,
            Owner = new OwnerRef(dto.Owner!.Team!, dto.Owner.Email!),
            BusinessOwner = new BusinessOwnerRef(dto.BusinessOwner!.Name!, dto.BusinessOwner.Email!),
            DataClassification = classification,
            Access = access,
            Stage = stage,
            Registration = registration,
            Roles = roles,
            AccessList = accessList,
            AuditedFields = audited,
            PiiFields = pii,
            CardData = dto.CardData!,
            BackendAssembly = dto.Backend?.Assembly,
        }, errors);
    }

    private static void Required(string? value, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add("missing required field: " + field);
        }
    }

    private static void Email(string? value, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add("missing required field: " + field);
        }
        else if (!EmailRegex().IsMatch(value))
        {
            errors.Add(field + " must be an email address");
        }
    }

    internal sealed class ManifestDto
    {
        public int SchemaVersion { get; set; }
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public OwnerDto? Owner { get; set; }
        public BusinessOwnerDto? BusinessOwner { get; set; }
        public string? DataClassification { get; set; }
        public string? Access { get; set; }
        public string? Stage { get; set; }
        public string? Registration { get; set; }
        public List<string>? Roles { get; set; }
        public List<string>? AccessList { get; set; }
        public List<string>? AuditedFields { get; set; }
        public List<string>? PiiFields { get; set; }
        public string? CardData { get; set; }
        public BackendDto? Backend { get; set; }
    }

    internal sealed class OwnerDto
    {
        public string? Team { get; set; }
        public string? Email { get; set; }
    }

    internal sealed class BusinessOwnerDto
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
    }

    internal sealed class BackendDto
    {
        public string? Assembly { get; set; }
    }
}
