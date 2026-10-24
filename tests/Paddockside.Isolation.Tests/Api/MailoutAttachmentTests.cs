using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// A mailout's files and sign-off (David, 24 Oct 2026): files attached while writing go to every recipient as real
/// attachments, up to 7 MB in all; each staff member keeps their own signature, which signs off the email above the
/// business's details. Shares the sign-in tests' seed: Bel Esprit, owned by Ann and Bob.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record AttachmentLine(Guid Id, string FileName, string Size, long Bytes);

    private sealed record SignatureLine(string Text);

    private sealed record MailoutWithFiles(string? Signature, List<AttachmentLine> Attachments);

    private static MultipartFormDataContent AttachmentForm(string name, byte[] bytes, string type = "application/pdf")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new MultipartFormDataContent { { content, "file", name } };
    }

    [Fact]
    public async Task A_mailout_carries_its_files_and_the_senders_own_signature()
    {
        var seed = await SeedAsync();
        using var kate = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);

        // Kate saves her sign-off once; it's hers, and comes back on her next mailout.
        Assert.Equal("", (await kate.GetFromJsonAsync<SignatureLine>("/api/mailouts/signature"))!.Text);
        Assert.Equal(HttpStatusCode.NoContent, (await kate.PutApiAsync("/api/mailouts/signature", new { text = "Kind regards,\nKate Wells\n0412 345 678" })).StatusCode);
        Assert.Equal("Kind regards,\nKate Wells\n0412 345 678", (await kate.GetFromJsonAsync<SignatureLine>("/api/mailouts/signature"))!.Text);
        using var someoneElse = await Api.SignedInAsync(seed.TenantId, MemberRole.Manager);
        Assert.Equal("", (await someoneElse.GetFromJsonAsync<SignatureLine>("/api/mailouts/signature"))!.Text);

        // A PDF attaches; a program never does.
        var pdf = "%PDF-1.4 share offer"u8.ToArray();
        var uploaded = await kate.PostFormApiAsync("/api/mailouts/attachments", AttachmentForm("Colt share offer.pdf", pdf));
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var offer = (await uploaded.Content.ReadFromJsonAsync<AttachmentLine>())!;
        Assert.Equal(("Colt share offer.pdf", "1 KB"), (offer.FileName, offer.Size));
        Assert.Equal(HttpStatusCode.BadRequest, (await kate.PostFormApiAsync("/api/mailouts/attachments", AttachmentForm("setup.exe", [1, 2, 3], "application/octet-stream"))).StatusCode);

        var mailout = new
        {
            subject = "Colt shares", body = "We've bought a colt at Easter.", listIds = Array.Empty<Guid>(), statuses = Array.Empty<string>(),
            clientIds = new[] { seed.AnnId }, horseId = (Guid?)null, signature = "Kind regards,\nKate Wells", attachmentIds = new[] { offer.Id },
        };
        var created = (await (await kate.PostApiAsync("/api/mailouts", mailout)).Content.ReadFromJsonAsync<MailoutCreated>())!;
        await DispatchAsync();

        var email = Api.Emails.Sent.Single(e => e.Email.Subject == "Colt shares").Email;
        var file = Assert.Single(email.Attachments!);
        Assert.Equal(("Colt share offer.pdf", "application/pdf"), (file.Name, file.ContentType));
        Assert.Equal(pdf, file.Content);
        Assert.Contains("Kind regards,<br>Kate Wells", email.HtmlBody);
        Assert.True(email.TextBody.IndexOf("Kate Wells", StringComparison.Ordinal) < email.TextBody.IndexOf("--", StringComparison.Ordinal)); // above the business's details

        var page = (await kate.GetFromJsonAsync<MailoutWithFiles>($"/api/mailouts/{created.Id}"))!;
        Assert.Equal("Kind regards,\nKate Wells", page.Signature);
        Assert.Equal(offer.Id, Assert.Single(page.Attachments).Id);
        Assert.Equal(pdf, await kate.GetByteArrayAsync($"/api/mailouts/attachments/{offer.Id}"));

        // A file goes with one mailout only; another business can't see it.
        Assert.Equal(HttpStatusCode.BadRequest, (await kate.PostApiAsync("/api/mailouts", mailout)).StatusCode);
        using var otherStaff = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.NotFound, (await otherStaff.GetAsync($"/api/mailouts/attachments/{offer.Id}")).StatusCode);
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostFormApiAsync("/api/mailouts/attachments", AttachmentForm("a.pdf", pdf))).StatusCode);
    }
}
