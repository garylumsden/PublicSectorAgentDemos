using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Defra.Contracts.FloodSupport;

namespace Defra.Tools.Mcp.Contracts;

public sealed record FarmRegisterInput(
    [property: Description("Fictional area reference in AREA-0000 format.")]
    [property: StringLength(9, MinimumLength = 9)]
    [property: JsonPropertyName("areaReference")] string FarmReference);

public sealed record ComplaintHistoryInput(
    [property: Description("Fictional area reference returned by getSituationReports.")]
    [property: StringLength(9, MinimumLength = 9)]
    [property: JsonPropertyName("areaReference")] string FarmReference);

public sealed record WeatherDataInput(
    [property: Description("Fictional area reference returned by getSituationReports.")]
    [property: StringLength(9, MinimumLength = 9)]
    [property: JsonPropertyName("areaReference")] string FarmReference);

public sealed record AccommodationOutput(
    string AreaReference,
    IReadOnlyList<SupportPackageDefinition> Packages,
    string CapacityBoundary);

public sealed record TransportCapacityOutput(
    string AreaReference,
    int Vehicles,
    int TransportSeats,
    int AccessibleTransportPlaces,
    DateTimeOffset ReportedAt,
    IReadOnlyList<string> Evidence,
    string CapacityBoundary);
