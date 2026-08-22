using System.Text.RegularExpressions;

namespace ReviewMemory.Core.Decisions;

public enum DecisionOutcome
{
    Accepted,
    Rejected,
    PartiallyAccepted,
    Unknown,
}

public sealed record Decision(
    DecisionOutcome Outcome,
    string? Reason,
    DecisionConfidence Confidence);

public enum DecisionConfidence
{
    Inferred,
    Manual,
}

/// <summary>
/// Infiere el desenlace de una discusión de review a partir de las respuestas.
/// Etapa 1: señales léxicas deterministas, sin IA. Las respuestas del autor
/// del PR pesan más que las del reviewer (el autor es quien actúa).
/// </summary>
public static partial class DecisionInferrer
{
    [GeneratedRegex(
        @"won'?t ?fix|wontfix|not an issue|non-?issue|by design|as designed|works as intended|intentional|false positive|already (?:enforced|handled|covered)|no (?:aplica|hace falta)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex RejectionPattern();

    [GeneratedRegex(
        @"partially|partial fix|parcialmente",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PartialPattern();

    [GeneratedRegex(
        @"fixed|addressed|good catch|done in|changed to|refactored|added (?:a )?(?:check|guard|test)|arreglado|corregido",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AcceptancePattern();

    public static Decision Infer(ReviewThreadData thread)
    {
        var candidates = thread.Replies
            .Append(thread.Finding)
            .OrderBy(c => c.Author == thread.Finding.Author ? 1 : 0)
            .ToList();

        var rejection = FirstMatch(candidates, RejectionPattern());
        var partial = FirstMatch(candidates, PartialPattern());
        var acceptance = FirstMatch(candidates, AcceptancePattern());

        if (rejection is not null && (acceptance is not null || partial is not null))
        {
            var other = acceptance ?? partial!;
            return new Decision(
                DecisionOutcome.Unknown,
                $"señales contradictorias — {Describe(rejection)} / {Describe(other)}",
                DecisionConfidence.Inferred);
        }

        if (rejection is not null)
        {
            return new Decision(DecisionOutcome.Rejected, Describe(rejection), DecisionConfidence.Inferred);
        }

        if (partial is not null)
        {
            return new Decision(DecisionOutcome.PartiallyAccepted, Describe(partial), DecisionConfidence.Inferred);
        }

        if (acceptance is not null)
        {
            return new Decision(DecisionOutcome.Accepted, Describe(acceptance), DecisionConfidence.Inferred);
        }

        return new Decision(DecisionOutcome.Unknown, null, DecisionConfidence.Inferred);
    }

    private static ReviewCommentData? FirstMatch(IEnumerable<ReviewCommentData> comments, Regex pattern) =>
        comments.FirstOrDefault(c => pattern.IsMatch(c.Body));

    private static string Describe(ReviewCommentData comment) =>
        $"respuesta de {comment.Author}: \"{Truncate(comment.Body.Trim(), 120)}\"";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}
