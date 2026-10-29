#nullable enable

namespace FikaServer.Services.Headless;

internal static class HeadlessReservationToken
{
    // Headless room creation must prove ownership of the exact reservation.
    // A missing token cannot distinguish a delayed create from an earlier raid.
    public static bool Matches(string? expected, string? received) =>
        !string.IsNullOrEmpty(expected) &&
        !string.IsNullOrEmpty(received) &&
        string.Equals(expected, received, StringComparison.Ordinal);
}
