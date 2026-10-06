namespace Paddockside.Domain;

/// <summary>
/// Mobile numbers in one form (E.164, "+61412345678") so an SMS sign-in matches the number staff typed on the
/// party however either was written ("0412 345 678", "+61 412 345 678", "61412345678"). Australian numbers by
/// default; anything already international is kept as given.
/// </summary>
public static class PhoneNumbers
{
    /// <summary>The number in E.164 form, or null when it is not a plausible mobile number.</summary>
    public static string? Normalise(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;
        var trimmed = number.Trim();
        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());

        string e164;
        if (trimmed.StartsWith('+')) e164 = "+" + digits;
        else if (digits.StartsWith("00", StringComparison.Ordinal)) e164 = "+" + digits[2..];
        else if (digits.StartsWith("61", StringComparison.Ordinal) && digits.Length == 11) e164 = "+" + digits;
        else if (digits.StartsWith('0') && digits.Length == 10) e164 = "+61" + digits[1..];
        else if (digits.StartsWith('4') && digits.Length == 9) e164 = "+61" + digits;
        else return null;

        return e164.Length is >= 9 and <= 16 ? e164 : null;
    }
}
