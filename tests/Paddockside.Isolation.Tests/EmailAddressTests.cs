using Paddockside.Application.Messaging;

namespace Paddockside.Isolation.Tests;

/// <summary>Reply addresses that can never exist are left off outgoing mail (filters read them as forgery).</summary>
public sealed class ReplyAddressTests
{
    [Theory]
    [InlineData("kate@laurel-oak.test", false)]
    [InlineData("someone@example.com", false)]
    [InlineData("a@mail.example.org", false)]
    [InlineData("x@thing.invalid", false)]
    [InlineData("not-an-address", false)]
    [InlineData("", false)]
    [InlineData("david@bluebloods.com.au", true)]
    [InlineData("bel-esprit@laureloak.in.paddockside.com.au", true)]
    [InlineData("someone@testing.com", true)] // "test" only as the whole top-level domain
    public void Only_real_domains_can_receive(string address, bool expected) =>
        Assert.Equal(expected, EmailAddresses.CanReceive(address));
}
