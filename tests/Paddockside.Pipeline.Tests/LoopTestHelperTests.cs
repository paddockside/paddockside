namespace Paddockside.Pipeline.Tests;

/// <summary>The parts of the loop test that need no live systems, so CI still runs something here.</summary>
public sealed class LoopTestHelperTests
{
    [Fact]
    public void Common_mail_providers_need_only_an_address_and_a_password()
    {
        Assert.Equal(("imap.gmail.com", "smtp.gmail.com"), LoopTestSettings.KnownHosts("someone@gmail.com"));
        Assert.Equal(("outlook.office365.com", "smtp-mail.outlook.com"), LoopTestSettings.KnownHosts("someone@Outlook.com"));
        Assert.Equal(((string?)null, (string?)null), LoopTestSettings.KnownHosts("someone@laurel-oak.com.au"));
    }

    [Fact]
    public void Authenticator_codes_match_the_rfc_6238_test_vector()
    {
        // RFC 6238 appendix B, SHA-1, T = 59 s: 94287082, of which an authenticator shows the last six digits.
        var key = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"; // "12345678901234567890" in base32
        Assert.Equal("287082", Totp.Code(key, DateTimeOffset.FromUnixTimeSeconds(59)));
    }
}
