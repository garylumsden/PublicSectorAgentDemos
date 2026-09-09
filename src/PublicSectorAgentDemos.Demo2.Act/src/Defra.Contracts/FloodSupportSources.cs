namespace Defra.Contracts.FloodSupport;

public sealed record FloodSupportFact(string SourceId, DateTimeOffset ReportedAt, string Description);

public static class FloodSupportSources
{
    public static IReadOnlyList<FloodSupportFact> Riverton { get; } = Array.AsReadOnly(new[]
    {
        new FloodSupportFact("riverton-situation-v1", new(2026, 9, 6, 15, 0, 0, TimeSpan.Zero),
            "Situation report, 2026-09-06T15:00:00Z: 180 displaced households; accommodation reaches capacity at 18:00."),
        new FloodSupportFact("riverton-accommodation-previous-v1", new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero),
            "Training centre report, 2026-09-06T12:00:00Z: 90 rooms reported previously."),
        new FloodSupportFact("riverton-accommodation-latest-v1", new(2026, 9, 6, 15, 30, 0, TimeSpan.Zero),
            "Latest verified training centre report, 2026-09-06T15:30:00Z: 60 rooms, including 12 accessible rooms."),
        new FloodSupportFact("riverton-transport-v1", new(2026, 9, 6, 15, 35, 0, TimeSpan.Zero),
            "Transport report, 2026-09-06T15:35:00Z: two coaches, 120 seats, including four accessible transport places."),
        new FloodSupportFact("flood-support-rate-card-v1", new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero),
            "Rate card v1: GBP 95 per room and GBP 1200 per coach for 24 hours.")
    });

    public static IReadOnlyList<FloodSupportFact> Meadowfield { get; } = Array.AsReadOnly(new[]
    {
        new FloodSupportFact("meadowfield-support-v1", new(2026, 9, 6, 15, 0, 0, TimeSpan.Zero),
            "Meadowfield report, 2026-09-06T15:00:00Z: 20 rooms and one 40-seat coach available.")
    });

    public static IReadOnlyList<FloodSupportFact> GetForArea(string areaReference) => areaReference switch
    {
        "AREA-1001" => Riverton,
        "AREA-1002" => Meadowfield,
        _ => throw new ArgumentException("Unknown evidence area.", nameof(areaReference))
    };
}
