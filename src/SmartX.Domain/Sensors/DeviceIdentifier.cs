using System.Text.RegularExpressions;

using SmartX.Domain.Exceptions;

namespace SmartX.Domain.Sensors;

/// <summary>
/// Represents a validated identifier for a physical Smart-X device.
///
/// A sensor may either use a standard 48-bit MAC address or a custom
/// unique identifier. MAC addresses are normalised so that equivalent
/// representations can be compared consistently.
/// </summary>
public sealed record DeviceIdentifier
{
    public const int MaxUniqueIdentifierLength = 64;

    private static readonly Regex MacAddressPattern = new(
        @"^(?:[0-9A-Fa-f]{12}|(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UniqueIdentifierPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private DeviceIdentifier(
        string value,
        DeviceIdentifierKind kind)
    {
        Value = value;
        Kind = kind;
    }

    /// <summary>
    /// Normalised identifier value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Indicates whether this identifier is a MAC address or a custom ID.
    /// </summary>
    public DeviceIdentifierKind Kind { get; }

    /// <summary>
    /// Creates a device identifier from a 48-bit MAC address.
    ///
    /// Accepted examples:
    /// AA:BB:CC:DD:EE:FF
    /// AA-BB-CC-DD-EE-FF
    /// AABBCCDDEEFF
    ///
    /// All valid values are normalised to AA:BB:CC:DD:EE:FF.
    /// </summary>
    public static DeviceIdentifier FromMacAddress(string? macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            throw new InvalidDeviceIdentifierException(
                "A device MAC address is required.");
        }

        var candidate = macAddress.Trim();

        if (!MacAddressPattern.IsMatch(candidate))
        {
            throw new InvalidDeviceIdentifierException(
                $"'{candidate}' is not a valid 48-bit MAC address.");
        }

        // Remove supported separators before rebuilding one canonical format.
        var compact = candidate
            .Replace(":", string.Empty)
            .Replace("-", string.Empty);

        var octets = Enumerable
            .Range(0, 6)
            .Select(index => compact.Substring(index * 2, 2));

        var normalised = string
            .Join(":", octets)
            .ToUpperInvariant();

        return new DeviceIdentifier(
            normalised,
            DeviceIdentifierKind.MacAddress);
    }

    /// <summary>
    /// Creates a device identifier using a Smart-X custom node identifier.
    ///
    /// Identifiers may contain letters, digits, dots, hyphens and
    /// underscores and must not exceed 64 characters.
    /// </summary>
    public static DeviceIdentifier FromUniqueIdentifier(string? uniqueIdentifier)
    {
        if (string.IsNullOrWhiteSpace(uniqueIdentifier))
        {
            throw new InvalidDeviceIdentifierException(
                "A unique device identifier is required.");
        }

        var candidate = uniqueIdentifier.Trim();

        if (candidate.Length > MaxUniqueIdentifierLength)
        {
            throw new InvalidDeviceIdentifierException(
                $"A unique device identifier cannot exceed " +
                $"{MaxUniqueIdentifierLength} characters.");
        }

        if (!UniqueIdentifierPattern.IsMatch(candidate))
        {
            throw new InvalidDeviceIdentifierException(
                "A unique device identifier may only contain letters, " +
                "digits, dots, hyphens and underscores, and must begin " +
                "with a letter or digit.");
        }

        return new DeviceIdentifier(
            candidate,
            DeviceIdentifierKind.UniqueIdentifier);
    }

    /// <summary>
    /// Returns the normalised identifier value.
    /// </summary>
    public override string ToString()
    {
        return Value;
    }
}
