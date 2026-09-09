using Defra.Contracts.FloodSupport;
using Defra.Contracts.V1;
using Defra.Tools.Mcp.Contracts;

namespace Defra.Tools.Mcp.Providers;

public interface IWelfareDataProvider
{
    ReservationInventoryState GetInventoryState();
    ReservationInventoryState ResetReservations();
    ValueTask<ProviderResult<SituationReport>> LookupFarmRegisterAsync(FarmRegisterInput input, CancellationToken cancellationToken);
    ValueTask<ProviderResult<AccommodationOutput>> GetComplaintHistoryAsync(ComplaintHistoryInput input, CancellationToken cancellationToken);
    ValueTask<ProviderResult<TransportCapacityOutput>> GetWeatherDataAsync(WeatherDataInput input, CancellationToken cancellationToken);
    ValueTask<ProviderResult<SupportReservationReceipt>> DispatchVetAsync(SupportPackageRequest input, CancellationToken cancellationToken);
}

public sealed class InMemoryWelfareDataProvider(FloodSupportReservationSimulator? simulator = null) : IWelfareDataProvider
{
    private readonly FloodSupportReservationSimulator _simulator = simulator ?? new();
    public IReadOnlyList<SupportReservationReceipt> RecordedDispatches => _simulator.Reservations;
    public ReservationInventoryState GetInventoryState() => _simulator.GetInventoryState();
    public ReservationInventoryState ResetReservations() => _simulator.ResetReservations();

    public ValueTask<ProviderResult<SituationReport>> LookupFarmRegisterAsync(FarmRegisterInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureArea(input.FarmReference);
        SituationReport report = FloodSupportFixtures.GetSituation(input.FarmReference);
        return ValueTask.FromResult(new ProviderResult<SituationReport>(report,
            Evidence(input.FarmReference)));
    }

    public ValueTask<ProviderResult<AccommodationOutput>> GetComplaintHistoryAsync(ComplaintHistoryInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureArea(input.FarmReference);
        IReadOnlyList<SupportPackageDefinition> packages = FloodSupportFixtures.GetPackages(input.FarmReference);
        AccommodationOutput result = new(input.FarmReference, packages,
            input.FarmReference == "AREA-1001"
                ? "Alternative packages share the same capacity; do not add them together. The older 90-room and latest 60-room Riverton reports conflict until source verification."
                : "The package is bounded by the Meadowfield capacity report.");
        return ValueTask.FromResult(new ProviderResult<AccommodationOutput>(result,
            Evidence(input.FarmReference)));
    }

    public ValueTask<ProviderResult<TransportCapacityOutput>> GetWeatherDataAsync(WeatherDataInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureArea(input.FarmReference);
        SupportPackageDefinition capacity = FloodSupportFixtures.GetPackages(input.FarmReference)[0];
        TransportCapacityOutput result = new(input.FarmReference, capacity.Vehicles, capacity.TransportSeats,
            capacity.AccessibleTransportPlaces,
            DateTimeOffset.Parse("2026-09-06T15:35:00Z", System.Globalization.CultureInfo.InvariantCulture), capacity.Evidence,
            "Transport seats are not household capacity. A human must verify individual accessibility needs and routes.");
        return ValueTask.FromResult(new ProviderResult<TransportCapacityOutput>(result,
            Evidence(input.FarmReference)));
    }

    public ValueTask<ProviderResult<SupportReservationReceipt>> DispatchVetAsync(SupportPackageRequest input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            SupportReservationReceipt receipt = _simulator.Reserve(input, DateTimeOffset.UtcNow);
            return ValueTask.FromResult(new ProviderResult<SupportReservationReceipt>(receipt,
                [SyntheticEvidence.Create($"ev-{receipt.ReservationReference}", "simulated-reservations-v1",
                    "Fictional reservation simulator", receipt.ApprovalBoundary, receipt.RecordedAt)]));
        }
        catch (SupportReservationException exception)
        {
            throw new SyntheticProviderException(exception.Code, exception.Message,
                isProtocolError: exception.Code is not ("capacity.support_unavailable" or "reservation.generation_changed"));
        }
    }

    private static void EnsureArea(string areaReference)
    {
        if (areaReference is not ("AREA-1001" or "AREA-1002"))
        {
            throw new SyntheticProviderException("data.area_not_found", "No situation report matched the area.");
        }
    }

    private static IReadOnlyList<EvidenceReference> Evidence(string areaReference) =>
        FloodSupportSources.GetForArea(areaReference).Select(fact => SyntheticEvidence.Create(
            $"ev-{fact.SourceId}", fact.SourceId, "Fictional flood-support fixture", fact.Description, fact.ReportedAt)).ToArray();
}
