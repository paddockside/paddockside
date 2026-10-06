using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace Paddockside.Pipeline.Tests;

/// <summary>
/// Settings for the end-to-end loop test, read from environment variables first (<c>Test__ExternalMailbox</c>) and
/// then from the dev Key Vault (<c>Test--ExternalMailbox</c>), signed in as you through <c>az login</c>.
/// Nothing here is ever written to the repository.
/// </summary>
public sealed record LoopTestSettings(
    string BaseUrl,
    string ConnectionString,
    string Mailbox,
    string MailboxUsername,
    string MailboxPassword,
    string ImapHost,
    int ImapPort,
    string SmtpHost,
    int SmtpPort)
{
    public const string DefaultVault = "https://kv-paddockside-dev-cd63.vault.azure.net/";
    public const string DefaultBaseUrl = "https://app-paddockside-dev-cd63cr.azurewebsites.net";

    public static LoopTestSettings Load()
    {
        // Locally, sign in to Azure as the developer (az login); never probe for a managed identity.
        Environment.SetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS", "dev");

        var builder = new ConfigurationBuilder().AddEnvironmentVariables();
        var vault = Environment.GetEnvironmentVariable("PADDOCKSIDE_KEYVAULT") ?? DefaultVault;
        if (vault != "none") builder.AddAzureKeyVault(new Uri(vault), new DefaultAzureCredential());
        var config = builder.Build();

        string Required(string key, string hint) =>
            config[key] is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException(
                    $"The loop test needs {key}: set Key Vault secret '{key.Replace(":", "--")}' or environment variable '{key.Replace(":", "__")}'. {hint}");

        var mailbox = Required("Test:ExternalMailbox", "The address of a real mailbox you control.");
        var (imap, smtp) = KnownHosts(mailbox);
        return new LoopTestSettings(
            (config["Test:BaseUrl"] ?? DefaultBaseUrl).TrimEnd('/'),
            Required("ConnectionStrings:Paddockside", "Already in the dev Key Vault; your IP must be allowed in the SQL firewall."),
            mailbox,
            config["Test:ExternalMailboxUsername"] ?? mailbox,
            Required("Test:ExternalMailboxPassword", "An app password for that mailbox (IMAP and SMTP)."),
            config["Test:ImapHost"] ?? imap ?? Required("Test:ImapHost", "The mailbox's IMAP server."),
            int.TryParse(config["Test:ImapPort"], out var imapPort) ? imapPort : 993,
            config["Test:SmtpHost"] ?? smtp ?? Required("Test:SmtpHost", "The mailbox's SMTP server."),
            int.TryParse(config["Test:SmtpPort"], out var smtpPort) ? smtpPort : 587);
    }

    /// <summary>IMAP and SMTP servers for the common providers, so only the address and password are needed.</summary>
    public static (string? Imap, string? Smtp) KnownHosts(string mailbox) =>
        mailbox.Split('@').LastOrDefault()?.ToLowerInvariant() switch
        {
            "gmail.com" or "googlemail.com" => ("imap.gmail.com", "smtp.gmail.com"),
            "outlook.com" or "hotmail.com" or "live.com" => ("outlook.office365.com", "smtp-mail.outlook.com"),
            "icloud.com" or "me.com" => ("imap.mail.me.com", "smtp.mail.me.com"),
            "yahoo.com" => ("imap.mail.yahoo.com", "smtp.mail.yahoo.com"),
            _ => (null, null),
        };
}
