using Defra.Contracts.FloodSupport;
using Defra.Tools.Mcp.Contracts;

namespace Defra.Tools.Mcp;

public sealed record ToolValidationFailure(string Code, string SafeDetail);

public static class McpValidation
{
    public static ToolValidationFailure? FarmRegister(FarmRegisterInput? input) => ValidateArea(input?.FarmReference);
    public static ToolValidationFailure? ComplaintHistory(ComplaintHistoryInput? input) => ValidateArea(input?.FarmReference);
    public static ToolValidationFailure? WeatherData(WeatherDataInput? input) => ValidateArea(input?.FarmReference);
    public static ToolValidationFailure? VetDispatch(SupportPackageRequest? input) =>
        input is not null && FloodSupportCatalogue.ValidateRequest(input, input.AreaReference, out _)
            ? null
            : new("validation.support_package", "Supply the exact six fields of a valid, versioned flood-support package request.");

    private static ToolValidationFailure? ValidateArea(string? value) =>
        value is { Length: 9 } && value.StartsWith("AREA-", StringComparison.Ordinal) &&
        !value.AsSpan(5).ContainsAnyExcept("0123456789")
            ? null
            : new("validation.area_reference", "areaReference must use AREA-0000 format.");
}
