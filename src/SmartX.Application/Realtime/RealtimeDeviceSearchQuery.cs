using System.Globalization;

using SmartX.Application.Sensors;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Exceptions;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Realtime;

/// <summary>
/// Raw device-search input from query-string values. Keeping the strings until
/// validation ensures malformed numbers, booleans and enum names are rejected
/// explicitly instead of silently broadening a search.
/// </summary>
public sealed record RealtimeDeviceSearchQuery(
    string? DeviceIdentifier = null,
    string? IdentifierKind = null,
    string? Text = null,
    string? Category = null,
    string? Status = null,
    string? HasActiveCondition = null,
    string? ConditionType = null,
    string? Limit = null,
    string? Offset = null)
{
    public const int DefaultLimit = 25;
    public const int MaximumLimit = 200;
    public const int MaximumOffset = 100_000;
    public const int MaximumTextLength = 120;

    /// <summary>
    /// Validates all filters before querying the registry or touching the
    /// active issue collection. Exact identifier parsing reuses the original
    /// P2-02 Domain factories, including case-sensitive custom identifiers.
    /// </summary>
    public ValidatedRealtimeDeviceSearchQuery Validate()
    {
        SmartX.Domain.Sensors.DeviceIdentifier? canonicalIdentifier = null;

        if (DeviceIdentifier is not null || IdentifierKind is not null)
        {
            if (string.IsNullOrWhiteSpace(DeviceIdentifier) ||
                string.IsNullOrWhiteSpace(IdentifierKind))
            {
                throw new InvalidRealtimeDeviceSearchQueryException(
                    "Exact lookup requires both deviceIdentifier and identifierKind.");
            }

            try
            {
                canonicalIdentifier = SensorContractMapper.MapDeviceIdentifier(
                    DeviceIdentifier,
                    IdentifierKind);
            }
            catch (Exception error) when (
                error is DomainValidationException or SensorRegistrationValidationException)
            {
                throw new InvalidRealtimeDeviceSearchQueryException(error.Message);
            }
        }

        string? normalizedText = null;

        if (Text is not null)
        {
            normalizedText = Text.Trim();

            if (normalizedText.Length is 0 or > MaximumTextLength)
            {
                throw new InvalidRealtimeDeviceSearchQueryException(
                    $"Search text must contain 1 to {MaximumTextLength} characters.");
            }
        }

        SensorCategory? category = ParseSymbolicEnum<SensorCategory>(Category, "category");
        SensorStatus? status = ParseSymbolicEnum<SensorStatus>(Status, "status");
        DashboardIssueType? conditionType =
            ParseSymbolicEnum<DashboardIssueType>(ConditionType, "conditionType");

        bool? active = null;

        if (HasActiveCondition is not null)
        {
            if (!bool.TryParse(HasActiveCondition.Trim(), out bool parsed))
            {
                throw new InvalidRealtimeDeviceSearchQueryException(
                    "hasActiveCondition must be either true or false.");
            }

            active = parsed;
        }

        if (active == false && conditionType is not null)
        {
            throw new InvalidRealtimeDeviceSearchQueryException(
                "conditionType cannot be combined with hasActiveCondition=false.");
        }

        int limit = ParseBoundedInteger(Limit, DefaultLimit, 1, MaximumLimit, "limit");
        int offset = ParseBoundedInteger(Offset, 0, 0, MaximumOffset, "offset");

        return new ValidatedRealtimeDeviceSearchQuery(
            canonicalIdentifier,
            normalizedText,
            category,
            status,
            active,
            conditionType,
            limit,
            offset);
    }

    private static TEnum? ParseSymbolicEnum<TEnum>(string? input, string field)
        where TEnum : struct, Enum
    {
        if (input is null)
        {
            return null;
        }

        string value = input.Trim();

        // Enum.TryParse would also accept numeric strings such as "1".
        // Discovery filters intentionally accept supported symbolic names only.
        string? name = Enum.GetNames<TEnum>().FirstOrDefault(
            candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            throw new InvalidRealtimeDeviceSearchQueryException(
                $"'{value}' is not a supported {field} value.");
        }

        return Enum.Parse<TEnum>(name);
    }

    private static int ParseBoundedInteger(
        string? input,
        int defaultValue,
        int minimum,
        int maximum,
        string field)
    {
        if (input is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(
                input.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed) ||
            parsed < minimum ||
            parsed > maximum)
        {
            throw new InvalidRealtimeDeviceSearchQueryException(
                $"{field} must be a whole number between {minimum} and {maximum}.");
        }

        return parsed;
    }
}

/// <summary>
/// Parsed and immutable criteria. Query services only accept this shape after
/// validation, preventing invalid filters from causing repository reads.
/// </summary>
public sealed record ValidatedRealtimeDeviceSearchQuery(
    DeviceIdentifier? Identifier,
    string? Text,
    SensorCategory? Category,
    SensorStatus? Status,
    bool? HasActiveCondition,
    DashboardIssueType? ConditionType,
    int Limit,
    int Offset);