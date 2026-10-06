using Paddockside.Domain;

namespace Paddockside.Domain.Tests;

/// <summary>The domain side of owner sign-in (identity-access.md §3): mobile numbers and linking a party to a person.</summary>
public sealed class OwnerIdentityTests
{
    [Theory]
    [InlineData("0412 345 678", "+61412345678")]
    [InlineData("+61 412 345 678", "+61412345678")]
    [InlineData("61412345678", "+61412345678")]
    [InlineData("412345678", "+61412345678")]
    [InlineData("(04) 1234-5678", "+61412345678")]
    [InlineData("0061 412 345 678", "+61412345678")]
    [InlineData("+64 21 123 4567", "+64211234567")]
    public void Mobile_numbers_are_kept_in_one_form(string typed, string e164) => Assert.Equal(e164, PhoneNumbers.Normalise(typed));

    [Theory]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("123")]
    public void Things_that_are_not_mobile_numbers_are_refused(string typed) => Assert.Null(PhoneNumbers.Normalise(typed));

    [Fact]
    public void A_party_matches_a_text_however_staff_typed_the_number()
    {
        var party = new Party(Guid.NewGuid(), "Ann Owner");
        party.AddMobile("0412 345 678");

        Assert.Equal("+61412345678", party.PrimaryMobile!.Value);
        Assert.Throws<DomainException>(() => party.AddMobile("+61412345678")); // the same number, written differently
    }

    [Fact]
    public void A_party_is_one_person()
    {
        var party = new Party(Guid.NewGuid(), "Ann Owner");
        var ann = Guid.NewGuid();
        party.LinkPerson(ann);
        party.LinkPerson(ann); // again: fine

        Assert.Throws<DomainException>(() => party.LinkPerson(Guid.NewGuid()));
        Assert.Equal(ann, party.PersonId);
    }
}
