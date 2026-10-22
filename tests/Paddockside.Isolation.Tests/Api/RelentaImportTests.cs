using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Bringing clients across from Relenta: everyone in, nobody doubled, groups sorted for staff to choose lists from,
/// people in a horse's group linked to it with their share to confirm and nothing sent. Shares the sign-in tests'
/// seed: Bel Esprit, owned 50/50 by Ann and Bob. The file is made up, in Relenta's own shape.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record GroupView(string Name, int Contacts, string Kind, Guid? HorseId);

    private sealed record ImportPreview(int Contacts, int New, int Existing, int WithoutEmail, int Unsubscribed, List<GroupView> Groups, List<object> Couples);

    private sealed record ImportResult(int Added, int Updated, int Unsubscribed, int ListsMade, int OnLists, int LinkedToHorses);

    private sealed record ClientDetails(string? Title, string? Company, string? Address, string? Notes, string? OtherPhones, string? Website, string? Social, string? SourceGroups);

    private sealed record ImportedClient(string Name, string? Email, string? Mobile, string? HowWeKnowThem, string Status, List<ClientHorse> CurrentHorses, ClientDetails Details);

    private sealed record ConfirmLine(Guid PartyId, string Name, string Percent, bool ToConfirm);

    private sealed record ConfirmList(List<ConfirmLine> Owners, string Allocated);

    private static string RelentaFile(Seed seed, string suffix) => string.Join("\n",
        "First,Last,Creation date,Email,Title,Company,Comment,Phone,Website,Fax,Messenger,Twitter,LinkedIn,Facebook,Text,Address,Do not mass-email,Groups,Tags,\"Current Group\",\"Unsubscribed\"",
        $"\"Gus\",\"Grady\",\"05-22-12\",\"gus-{suffix}@relenta.test\",\"Mr\",\"Grady Holdings\",\"Likes the Spring carnival\",\"8883 1066 (Work);0410 340 321\",\"\",\"\",\"\",\"\",\"linkedin.com/in/gus\",\"\",\"\",\"(Address)\n46 Somersby Cct, Acacia Gardens 2763\n\",\"\",\"All contacts;Sydney Social;Bel Esprit;Kendalia;2023 Royal Ascot\",,\"Test group\",",
        $"\"Nicole & Craig\",\"Mackay\",\"09-18-13\",\"mackay-{suffix}@relenta.test\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"All contacts;Sydney Social;Bel Esprit;Zazel \",,\"Test group\",",
        $"\"Una\",\"Gone\",\"01-02-20\",\"una-{suffix}@relenta.test\",\"\",\"\",\"\",\"0412 555 666\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"1\",\"All contacts;Kendalia\",,\"Test group\",\"\"",
        $"\"Ann\",\"Owner\",\"03-04-15\",\"{seed.AnnEmail.ToUpperInvariant()}\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"All contacts;Bel Esprit;Sydney Social\",,\"Test group\",",
        "");

    private static MultipartFormDataContent ImportForm(string csv, object? choices = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "relenta.csv");
        if (choices is not null) form.Add(new StringContent(JsonSerializer.Serialize(choices)), "choices");
        return form;
    }

    [Fact]
    public async Task Relenta_contacts_all_come_in_once_with_their_details_lists_and_horses_share_to_confirm()
    {
        var seed = await SeedAsync();
        using var manager = await Api.SignedInAsync(seed.TenantId, MemberRole.Manager);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var csv = RelentaFile(seed, suffix);
        var before = (await manager.GetFromJsonAsync<List<ClientRow>>("/api/clients"))!.Count;

        // The preview reads and sorts, and saves nothing.
        var preview = (await (await manager.PostFormApiAsync("/api/imports/relenta/preview", ImportForm(csv))).Content.ReadFromJsonAsync<ImportPreview>())!;
        Assert.Equal((4, 3, 1, 1), (preview.Contacts, preview.New, preview.Existing, preview.Unsubscribed));
        Assert.Single(preview.Couples);
        Assert.Equal(("Horse", seed.HorseId), (preview.Groups.Single(g => g.Name == "Bel Esprit").Kind, preview.Groups.Single(g => g.Name == "Bel Esprit").HorseId));
        Assert.Equal(["List", "List", "HorseName", "HorseName"],
            new[] { "Sydney Social", "2023 Royal Ascot", "Kendalia", "Zazel" }.Select(n => preview.Groups.Single(g => g.Name == n).Kind));
        Assert.DoesNotContain(preview.Groups, g => g.Name == "All contacts");
        Assert.Equal(3, preview.Groups.Single(g => g.Name == "Sydney Social").Contacts);
        Assert.Equal(before, (await manager.GetFromJsonAsync<List<ClientRow>>("/api/clients"))!.Count);

        // Imported: one list kept, the horse linked. Ann is already here: filled in, not doubled, and her share stays.
        var choices = new { lists = new[] { "Sydney Social" }, horses = new[] { "Bel Esprit" } };
        var imported = await manager.PostFormApiAsync("/api/imports/relenta", ImportForm(csv, choices));
        Assert.True(imported.IsSuccessStatusCode, await imported.Content.ReadAsStringAsync());
        var result = (await imported.Content.ReadFromJsonAsync<ImportResult>())!;
        Assert.Equal((3, 1, 1, 1, 3, 2), (result.Added, result.Updated, result.Unsubscribed, result.ListsMade, result.OnLists, result.LinkedToHorses));
        Assert.Equal(before + 3, (await manager.GetFromJsonAsync<List<ClientRow>>("/api/clients"))!.Count);

        // Gus: every field kept; his mobile picked out of Relenta's phone field; his groups remembered.
        var gusRow = (await manager.GetFromJsonAsync<List<ClientRow>>($"/api/clients?q=gus-{suffix}"))!.Single();
        var gus = (await manager.GetFromJsonAsync<ImportedClient>($"/api/clients/{gusRow.Id}"))!;
        Assert.Equal(("+61410340321", "8883 1066 (Work)", "46 Somersby Cct, Acacia Gardens 2763"), (gus.Mobile, gus.Details.OtherPhones, gus.Details.Address));
        Assert.Equal(("Mr", "Grady Holdings", "Likes the Spring carnival", "LinkedIn: linkedin.com/in/gus"), (gus.Details.Title, gus.Details.Company, gus.Details.Notes, gus.Details.Social));
        Assert.Contains("Kendalia", gus.Details.SourceGroups);
        Assert.Equal("Relenta contact since May 2012", gus.HowWeKnowThem);
        Assert.Equal(("Current owner", "(share to confirm)"), (gus.Status, gus.CurrentHorses.Single().Share));

        // On the horse: listed, share to confirm, not counted; nothing sent to them.
        await DispatchAsync();
        Assert.DoesNotContain(Api.Emails.Sent, e => e.Email.ToAddress == $"gus-{suffix}@relenta.test");
        var owners = (await manager.GetFromJsonAsync<ConfirmList>($"/api/horses/{seed.HorseId}/owners"))!;
        Assert.Equal("100% of 100% allocated", owners.Allocated);
        Assert.Equal(2, owners.Owners.Count(o => o.ToConfirm));

        // Confirmed: within the whole horse only; then they own it and are invited.
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostApiAsync($"/api/horses/{seed.HorseId}/owners/{gusRow.Id}/confirm", new { percent = 5m })).StatusCode);
        await manager.PostApiAsync($"/api/horses/{seed.HorseId}/owners/{seed.BobId}/exit", new { toHouse = false, on = (string?)null, access = "LockedOut" });
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostApiAsync($"/api/horses/{seed.HorseId}/owners/{gusRow.Id}/confirm", new { percent = 5m })).StatusCode);
        owners = (await manager.GetFromJsonAsync<ConfirmList>($"/api/horses/{seed.HorseId}/owners"))!;
        Assert.Equal(("5%", false), (owners.Owners.Single(o => o.PartyId == gusRow.Id).Percent, owners.Owners.Single(o => o.PartyId == gusRow.Id).ToConfirm));
        await DispatchAsync();
        Assert.Contains(Api.Emails.Sent, e => e.Email.ToAddress == $"gus-{suffix}@relenta.test");

        // The same file again doubles nobody and links nobody twice.
        var again = (await (await manager.PostFormApiAsync("/api/imports/relenta", ImportForm(csv, choices))).Content.ReadFromJsonAsync<ImportResult>())!;
        Assert.Equal((0, 4, 0, 0), (again.Added, again.Updated, again.ListsMade, again.LinkedToHorses));

        // Not a Relenta file; not for a coordinator.
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostFormApiAsync("/api/imports/relenta/preview", ImportForm("Name,Phone\nX,1\n"))).StatusCode);
        using var coordinator = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.PostFormApiAsync("/api/imports/relenta/preview", ImportForm(csv))).StatusCode);
    }
}
