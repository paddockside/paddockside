using static Paddockside.Domain.Tests.Stable;

namespace Paddockside.Domain.Tests;

/// <summary>Questions on messages: what counts as an answer, and reading one out of a reply in words.</summary>
public sealed class PromptTests
{
    private static readonly MessagePrompt Tickets = MessagePrompt.Count("How many owners tickets would you like?", 10);
    private static readonly MessagePrompt Coming = MessagePrompt.YesNo("Will you come to the presentation?");
    private static readonly MessagePrompt Share = MessagePrompt.Choice("Which share?", ["10%", "5%", "2.5%", "Tell me more", "Not this time"]);

    [Theory]
    [InlineData("Two please, for me and my sister. Can we see her in the mounting yard?", "2")]
    [InlineData("Just me this time, thanks.", "1")]
    [InlineData("None for us, we will watch from home. Good luck!", "0")]
    [InlineData("3", "3")]
    [InlineData("We'd love 4!", "4")]
    [InlineData("Two for me and 3 for my brother", null)] // two numbers: staff decide
    [InlineData("Can I let you know tomorrow?", null)]
    [InlineData("15 please", null)] // more than anyone may ask for
    public void A_count_is_read_from_plain_words(string reply, string? expected) => Assert.Equal(expected, Tickets.ReadFrom(reply));

    [Theory]
    [InlineData("Yes, count me in!", "Yes")]
    [InlineData("No, sorry, we're away", "No")]
    [InlineData("Yes but no dinner", null)]
    [InlineData("Maybe", null)]
    public void Yes_or_no_is_read_only_when_it_is_clear(string reply, string? expected) => Assert.Equal(expected, Coming.ReadFrom(reply));

    [Theory]
    [InlineData("10% please - can we visit?", "10%")]
    [InlineData("I'll take 2.5% thanks", "2.5%")] // not "5%", which it contains
    [InlineData("Not this time", "Not this time")]
    [InlineData("Either 5% or 10%", null)]
    public void A_choice_is_read_from_its_words(string reply, string? expected) => Assert.Equal(expected, Share.ReadFrom(reply));

    [Fact]
    public void Questions_and_answers_must_make_sense()
    {
        Assert.Throws<DomainException>(() => MessagePrompt.Count("", 10));
        Assert.Throws<DomainException>(() => MessagePrompt.Count("How many?", 0));
        Assert.Throws<DomainException>(() => MessagePrompt.Choice("Which?", ["Only one"]));
        Assert.Equal(Enumerable.Range(0, 11).Select(n => n.ToString()), Tickets.Answers);
        Assert.Equal("Yes", Coming.Normalise("yes"));
        Assert.Null(Tickets.Normalise("11"));

        var s = new Stable();
        var message = StreamItem.Message(s.Horse, null, null, "Tickets?", StreamItemScope.Owners, On(2026, 10, 1), "Kate", prompt: Tickets);
        var answer = new PromptAnswer(message, Guid.NewGuid(), "2", AnswerSource.Portal, "Ann", On(2026, 10, 1));
        Assert.Throws<DomainException>(() => answer.Change(message, "eleven", AnswerSource.Portal, "Ann", On(2026, 10, 2)));
        answer.Change(message, "3", AnswerSource.Staff, "kate@laureloak.test", On(2026, 10, 2));
        Assert.Equal(("3", AnswerSource.Staff), (answer.Value, answer.Source));

        var plain = StreamItem.Message(s.Horse, null, null, "No question", StreamItemScope.Owners, On(2026, 10, 1), "Kate");
        Assert.Throws<DomainException>(() => new PromptAnswer(plain, Guid.NewGuid(), "2", AnswerSource.Portal, "Ann", On(2026, 10, 1)));
    }
}
