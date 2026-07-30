using System.Text;

namespace SpeedEmulator.Services;

internal static class SystemFlowNumberGenerator
{
    private const string Digits = "0123456789";
    private const string LowercaseLetters = "abcdefghijklmnopqrstuvwxyz";

    public static string CreateEverbrightCorporateSerialNumber(string? accountNumber)
    {
        var builder = new StringBuilder(12);
        builder.Append(!string.IsNullOrEmpty(accountNumber) && accountNumber.Length >= 6
            ? accountNumber.AsSpan(4, 2)
            : "00");

        for (var index = 0; index < 10; index++)
        {
            // The reference statement uses an 80% digit / 20% lowercase-letter suffix.
            var characters = Random.Shared.Next(5) == 0 ? LowercaseLetters : Digits;
            builder.Append(characters[Random.Shared.Next(characters.Length)]);
        }

        return builder.ToString();
    }
}
