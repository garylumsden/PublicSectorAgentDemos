using System.Collections.Frozen;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Defra.Contracts.FloodSupport;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SupportPackageRequest(
    [property: JsonRequired, Description("Exact generated actionRequestId from the issued assessment actionContext; also the idempotency key.")] string ActionRequestId,
    [property: JsonRequired, Description("Verified area reference in AREA-0000 format. Must match the incident and package.")] string AreaReference,
    [property: JsonRequired, Description("Exact incidentReference from the issued assessment actionContext.")] string IncidentReference,
    [property: JsonRequired, Description("Immutable catalogue package ID returned by findAccommodation. It binds all quantities and the price.")] string PackageId,
    [property: JsonRequired, Description("Exact catalogue package version. A resource, duration or price change requires a new version and approval.")] int PackageVersion,
    [property: JsonRequired, Description("Explain why this exact bounded package is needed. Use 1 to 2000 characters and preserve unmet needs and source conflicts.")] string Justification)
{
    [System.ComponentModel.DataAnnotations.Required]
    [Description("Required for execution. Copy reservationGeneration exactly from the issued actionContext; reset invalidates older generations.")]
    public string ReservationGeneration { get; init; } = "initial";
}

public sealed record ReservationInventoryState(string Generation, int ReservationCount);

public sealed record SupportPackageDefinition(
    string PackageId,
    int Version,
    string AreaReference,
    string AccommodationSite,
    int Rooms,
    int AccessibleRooms,
    int Vehicles,
    int TransportSeats,
    int AccessibleTransportPlaces,
    int DurationHours,
    decimal EstimatedCostGbp,
    string Why,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Risks,
    int UnmetHouseholds);

public static class FloodSupportCatalogue
{
    private static readonly string[] ArgumentKeys =
        ["actionRequestId", "areaReference", "incidentReference", "packageId", "packageVersion", "justification", "reservationGeneration"];

    private static readonly FrozenDictionary<string, SupportPackageDefinition> Packages =
        new[]
        {
            CreateRivertonPackage("PKG-RIV-060", 60, 12, 2, 120, 4, 8100m, 120),
            CreateRivertonPackage("PKG-RIV-030", 30, 6, 1, 60, 2, 4050m, 150),
            new SupportPackageDefinition(
                "PKG-MEA-020", 1, "AREA-1002", "Meadowfield Civic Training Centre",
                20, 4, 1, 40, 2, 24, 3100m,
                "Provide temporary accommodation and transport for 20 displaced Meadowfield households.",
                Array.AsReadOnly(FloodSupportSources.Meadowfield.Select(fact => fact.Description).ToArray()),
                Array.AsReadOnly(new[] { "Confirm that the accessible rooms and transport places meet the reported needs." }), 0)
        }.ToFrozenDictionary(package => package.PackageId, StringComparer.Ordinal);

    public static SupportPackageDefinition GetPackage(string packageId) =>
        Packages.TryGetValue(packageId, out SupportPackageDefinition? package)
            ? package
            : throw new ArgumentException("Unknown flood-support package.", nameof(packageId));

    public static bool TryParseApprovalArguments(
        IReadOnlyDictionary<string, object?> arguments,
        out SupportPackageRequest? request)
    {
        request = null;
        if (arguments is null)
        {
            return false;
        }

        if (arguments.Count == 1 && arguments.Keys.Single() == "request" && arguments.TryGetValue("request", out object? nested))
        {
            if (!TryReadObject(nested, out arguments))
            {
                return false;
            }
        }

        if (arguments.Count != ArgumentKeys.Length ||
            arguments.Keys.Any(key => !ArgumentKeys.Contains(key, StringComparer.Ordinal)) ||
            !TryReadString(arguments, "actionRequestId", out string actionRequestId) ||
            !TryReadString(arguments, "areaReference", out string areaReference) ||
            !TryReadString(arguments, "incidentReference", out string incidentReference) ||
            !TryReadString(arguments, "packageId", out string packageId) ||
            !TryReadString(arguments, "justification", out string justification) ||
            !TryReadString(arguments, "reservationGeneration", out string generation) ||
            !arguments.TryGetValue("packageVersion", out object? versionValue))
        {
            return false;
        }

        int version;
        if (versionValue is int typedVersion)
        {
            version = typedVersion;
        }
        else if (versionValue is JsonElement { ValueKind: JsonValueKind.Number } jsonVersion &&
            jsonVersion.TryGetInt32(out int parsedVersion))
        {
            version = parsedVersion;
        }
        else
        {
            return false;
        }

        SupportPackageRequest parsed = new(actionRequestId, areaReference, incidentReference, packageId, version, justification)
        {
            ReservationGeneration = generation
        };
        if (!ValidateRequest(parsed, areaReference, out _))
        {
            return false;
        }

        request = parsed;
        return true;
    }

    public static bool ValidateRequest(
        SupportPackageRequest request,
        string expectedAreaReference,
        out SupportPackageDefinition? package)
    {
        package = null;
        if (request is null || request.PackageId is null ||
            !Packages.TryGetValue(request.PackageId, out SupportPackageDefinition? candidate) ||
            !string.Equals(request.AreaReference, expectedAreaReference, StringComparison.Ordinal) ||
            !string.Equals(request.AreaReference, candidate.AreaReference, StringComparison.Ordinal) ||
            request.PackageVersion != candidate.Version ||
            request.ActionRequestId is not { Length: 41 } actionId ||
            !actionId.StartsWith("approval-", StringComparison.Ordinal) ||
            actionId.AsSpan(9).ContainsAnyExcept("0123456789abcdef") ||
            request.IncidentReference is not { Length: >= 8 and <= 12 } incident ||
            !incident.StartsWith("INC-", StringComparison.Ordinal) ||
            incident.AsSpan(4).ContainsAnyExcept("0123456789") ||
            (request.ReservationGeneration != "initial" &&
                (request.ReservationGeneration is not { Length: 32 } generation ||
                 generation.AsSpan().ContainsAnyExcept("0123456789abcdef"))) ||
            string.IsNullOrWhiteSpace(request.Justification) || request.Justification.Length > 2000 ||
            request.Justification.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            return false;
        }

        package = candidate;
        return true;
    }

    public static IReadOnlyDictionary<string, object?> ToArguments(SupportPackageRequest request) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actionRequestId"] = request.ActionRequestId,
            ["areaReference"] = request.AreaReference,
            ["incidentReference"] = request.IncidentReference,
            ["packageId"] = request.PackageId,
            ["packageVersion"] = request.PackageVersion,
            ["justification"] = request.Justification,
            ["reservationGeneration"] = request.ReservationGeneration
        };

    public static bool MatchesSnapshot(SupportPackageDefinition snapshot, SupportPackageDefinition canonical) =>
        snapshot is not null && snapshot with { Evidence = canonical.Evidence, Risks = canonical.Risks } == canonical &&
        snapshot.Evidence is not null && snapshot.Risks is not null &&
        snapshot.Evidence.SequenceEqual(canonical.Evidence, StringComparer.Ordinal) &&
        snapshot.Risks.SequenceEqual(canonical.Risks, StringComparer.Ordinal);

    private static SupportPackageDefinition CreateRivertonPackage(
        string id, int rooms, int accessibleRooms, int vehicles, int seats, int accessiblePlaces, decimal cost, int unmet) =>
        new(id, 1, "AREA-1001", "Riverton Government Training Centre",
            rooms, accessibleRooms, vehicles, seats, accessiblePlaces, 24, cost,
            "Riverton flooding displaced 180 households. Existing accommodation reaches capacity at 18:00. Reserve bounded temporary support while coordinating unmet needs.",
            Array.AsReadOnly(FloodSupportSources.Riverton.Select(fact => fact.Description).ToArray()),
            Array.AsReadOnly(new[]
            {
                "The older 90-room report conflicts with the latest verified 60-room report. Keep this risk open until source verification.",
                $"UNMET: {unmet} households still require accommodation. Transport seats do not imply household coverage.",
                "Accessible rooms and accessible transport places are different limits. A human must coordinate individual needs."
            }), unmet);

    private static bool TryReadString(IReadOnlyDictionary<string, object?> arguments, string key, out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetValue(key, out object? raw))
        {
            return false;
        }

        if (raw is string text)
        {
            value = text;
            return true;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.String } element)
        {
            value = element.GetString()!;
            return true;
        }

        return false;
    }

    private static bool TryReadObject(object? value, out IReadOnlyDictionary<string, object?> result)
    {
        if (value is IReadOnlyDictionary<string, object?> dictionary)
        {
            result = dictionary;
            return true;
        }

        Dictionary<string, object?> parsed = new(StringComparer.Ordinal);
        result = parsed;
        if (value is IReadOnlyDictionary<string, JsonElement> jsonDictionary)
        {
            foreach ((string key, JsonElement element) in jsonDictionary)
            {
                parsed.Add(key, element);
            }
            return true;
        }

        if (value is not JsonElement { ValueKind: JsonValueKind.Object } json)
        {
            return false;
        }

        foreach (JsonProperty property in json.EnumerateObject())
        {
            if (!parsed.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        return true;
    }
}
