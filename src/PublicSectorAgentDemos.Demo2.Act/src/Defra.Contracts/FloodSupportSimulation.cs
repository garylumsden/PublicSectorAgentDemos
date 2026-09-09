namespace Defra.Contracts.FloodSupport;

public sealed record SituationReport(
    string AreaReference, string IncidentName, int DisplacedHouseholds,
    string AccommodationCapacityDeadline, int RepeatedUnmetNeedsReports,
    DateTimeOffset ReportedAt, IReadOnlyList<string> Evidence);

public static class FloodSupportFixtures
{
    public static SituationReport GetSituation(string areaReference) => areaReference switch
    {
        "AREA-1001" => new(areaReference, "Riverton flood", 180, "2026-09-06T18:00:00+01:00", 3,
            DateTimeOffset.Parse("2026-09-06T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            FloodSupportCatalogue.GetPackage("PKG-RIV-060").Evidence),
        "AREA-1002" => new(areaReference, "Meadowfield flood", 20, "2026-09-06T20:00:00+01:00", 0,
            DateTimeOffset.Parse("2026-09-06T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            FloodSupportCatalogue.GetPackage("PKG-MEA-020").Evidence),
        _ => throw new ArgumentException("No situation report matches the area.", nameof(areaReference))
    };

    public static IReadOnlyList<SupportPackageDefinition> GetPackages(string areaReference) => areaReference switch
    {
        "AREA-1001" => [FloodSupportCatalogue.GetPackage("PKG-RIV-060"), FloodSupportCatalogue.GetPackage("PKG-RIV-030")],
        "AREA-1002" => [FloodSupportCatalogue.GetPackage("PKG-MEA-020")],
        _ => throw new ArgumentException("No support package matches the area.", nameof(areaReference))
    };
}

public sealed record SupportReservationReceipt(
    string ReservationReference,
    SupportPackageRequest Request,
    SupportPackageDefinition Package,
    DateTimeOffset RecordedAt,
    string Status,
    string ApprovalBoundary);

public sealed class SupportReservationException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

// Capacity is process-local demo state. The lock commits accommodation and transport together.
public sealed class FloodSupportReservationSimulator(string initialGeneration = "initial")
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SupportReservationReceipt> _receipts = new(StringComparer.Ordinal);
    private string _generation = initialGeneration;

    public ReservationInventoryState GetInventoryState()
    {
        lock (_gate) { return new(_generation, _receipts.Count); }
    }

    public ReservationInventoryState ResetReservations()
    {
        lock (_gate)
        {
            _receipts.Clear();
            _generation = Guid.NewGuid().ToString("N");
            return new(_generation, 0);
        }
    }

    public IReadOnlyList<SupportReservationReceipt> Reservations
    {
        get { lock (_gate) { return _receipts.Values.ToArray(); } }
    }

    public SupportReservationReceipt? FindReservation(string actionRequestId)
    {
        lock (_gate)
        {
            return _receipts.GetValueOrDefault(actionRequestId);
        }
    }

    public SupportReservationReceipt Reserve(SupportPackageRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!FloodSupportCatalogue.ValidateRequest(request, request.AreaReference, out SupportPackageDefinition? package))
        {
            throw new SupportReservationException("validation.support_package", "The support package request is invalid.");
        }

        lock (_gate)
        {
            if (request.ReservationGeneration != _generation)
            {
                throw new SupportReservationException("reservation.generation_changed",
                    "Reservations were reset after this assessment started. No resources were reserved.");
            }
            if (_receipts.TryGetValue(request.ActionRequestId, out SupportReservationReceipt? existing))
            {
                return existing.Request == request
                    ? existing
                    : throw new SupportReservationException("toolbox.idempotency_conflict", "The action reference already binds a different support package request.");
            }

            SupportPackageDefinition capacity = FloodSupportFixtures.GetPackages(request.AreaReference)[0];
            SupportPackageDefinition[] allocated = _receipts.Values
                .Where(receipt => receipt.Request.AreaReference == request.AreaReference)
                .Select(receipt => receipt.Package).ToArray();
            if (allocated.Sum(item => item.Rooms) + package!.Rooms > capacity.Rooms ||
                allocated.Sum(item => item.AccessibleRooms) + package.AccessibleRooms > capacity.AccessibleRooms ||
                allocated.Sum(item => item.Vehicles) + package.Vehicles > capacity.Vehicles ||
                allocated.Sum(item => item.TransportSeats) + package.TransportSeats > capacity.TransportSeats ||
                allocated.Sum(item => item.AccessibleTransportPlaces) + package.AccessibleTransportPlaces > capacity.AccessibleTransportPlaces)
            {
                throw new SupportReservationException("capacity.support_unavailable", "The complete package exceeds the remaining capacity. No resources were reserved.");
            }

            SupportReservationReceipt receipt = new(
                $"SIM-RES-{_receipts.Count + 1:0000}", request, package, now, "simulated",
                "Approval applies only to this package.");
            _receipts.Add(request.ActionRequestId, receipt);
            return receipt;
        }
    }
}
