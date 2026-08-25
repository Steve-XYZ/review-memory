using ReviewMemory.Core;
using ReviewMemory.Core.Decisions;

namespace ReviewMemory.Core.Tests;

public sealed class DecisionInferrerTests
{
    private static DateTimeOffset T(int minutes) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private static ReviewThreadData Thread(string finding, params (string Author, string Body)[] replies) =>
        new(
            Id: 42,
            Path: "src/LottoPendingTransactionProcessor.cs",
            Line: 88,
            Resolved: false,
            Finding: new ReviewCommentData(1, "reviewer", finding, T(0)),
            Replies: replies.Select((r, i) => new ReviewCommentData(i + 2, r.Author, r.Body, T(i + 1))).ToList());

    [Fact]
    public void Fixed_reply_is_accepted()
    {
        var thread = Thread(
            "This retry can process the same provider transaction twice.",
            ("dev", "Good catch, fixed by adding an idempotency check."));

        var decision = DecisionInferrer.Infer(thread);

        Assert.Equal(DecisionOutcome.Accepted, decision.Outcome);
        Assert.Equal(DecisionConfidence.Inferred, decision.Confidence);
    }

    [Fact]
    public void Wont_fix_reply_is_rejected()
    {
        var thread = Thread(
            "Consumer may process the message twice.",
            ("dev", "Not an issue: providerRequestId already enforced by a unique constraint."));

        var decision = DecisionInferrer.Infer(thread);

        Assert.Equal(DecisionOutcome.Rejected, decision.Outcome);
    }

    [Fact]
    public void Partial_reply_is_partially_accepted()
    {
        var thread = Thread(
            "The error path swallows exceptions.",
            ("dev", "Partially addressed; full retry policy lands in a follow-up."));

        var decision = DecisionInferrer.Infer(thread);

        Assert.Equal(DecisionOutcome.PartiallyAccepted, decision.Outcome);
    }

    [Fact]
    public void Contradictory_signals_produce_unknown_with_reason()
    {
        var thread = Thread(
            "This can double-charge the payout.",
            ("dev", "Fixed, moved the check."),
            ("reviewer", "Hmm, actually not an issue, the provider dedupes."));

        var decision = DecisionInferrer.Infer(thread);

        Assert.Equal(DecisionOutcome.Unknown, decision.Outcome);
        Assert.NotNull(decision.Reason);
        Assert.Contains("contradictory", decision.Reason);
    }

    [Fact]
    public void No_signals_stay_unknown_without_reason()
    {
        var thread = Thread(
            "Consider extracting this to a service.",
            ("dev", "Sure, will look into it next sprint."));

        var decision = DecisionInferrer.Infer(thread);

        Assert.Equal(DecisionOutcome.Unknown, decision.Outcome);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Rejection_inside_finding_itself_counts()
    {
        var thread = Thread(
            "False positive from my own tooling, ignore this comment.",
            ("dev", "ok"));

        var decision = DecisionInferrer.Infer(thread);

        Assert.Equal(DecisionOutcome.Rejected, decision.Outcome);
    }

    [Fact]
    public void Reason_quote_truncates_decisive_body_at_120_visible_chars()
    {
        var body = "Fixed. " + new string('x', 200);
        var thread = Thread(
            "This retry can process the same provider transaction twice.",
            ("dev", body));

        var decision = DecisionInferrer.Infer(thread);

        Assert.Equal(DecisionOutcome.Accepted, decision.Outcome);
        Assert.NotNull(decision.Reason);
        Assert.Contains($"response from dev: \"{body[..119]}…\"", decision.Reason);
    }
}
