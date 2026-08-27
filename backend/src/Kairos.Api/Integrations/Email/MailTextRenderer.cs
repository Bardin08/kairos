using System.Text;

namespace Kairos.Api.Integrations.Email;

/// <summary>
/// <see cref="MailBody"/> as the <c>text/plain</c> half.
/// </summary>
/// <remarks>
/// Blocks collected and then joined, never a template with holes in it. A template prints an empty
/// line where an absent block was and a heading over nothing where its label was, and both are
/// things the invitee reads. This construction is inherited from the plaintext body it replaces
/// and is the one piece of that implementation that was prose rather than plumbing.
/// </remarks>
public static class MailTextRenderer
{
    public const string ReasonHeading = "Why:";

    /// <summary>One blank line between blocks, which is what makes an absent block leave no mark.</summary>
    private const string BlockSeparator = "\n\n";

    /// <summary>What the time this meeting has moved from is printed under.</summary>
    private const string WasLabel = "Was: ";

    public static string Render(MailBody body)
    {
        var blocks = new List<string> { body.Headline, Fact(body.Fact), body.Sentence };

        if (body.Reason is { } reason)
        {
            blocks.Add($"{ReasonHeading}\n{reason}");
        }

        if (body.Action is { } action)
        {
            blocks.Add($"{action.Label}:\n{action.Url}");
        }

        if (body.Secondary is { } secondary)
        {
            blocks.Add($"{secondary.Label}:\n{secondary.Url}");
        }

        if (body.Details.Count > 0)
        {
            blocks.Add(string.Join('\n', body.Details.Select(Detail)));
        }

        if (body.Footer is { } footer)
        {
            blocks.Add(footer);
        }

        return string.Join(BlockSeparator, blocks);
    }

    private static string Fact(MailFact fact)
    {
        var text = new StringBuilder();

        if (fact.Was is { } was)
        {
            text.Append(WasLabel).Append(was).Append('\n');
        }

        return text.Append(fact.Date).Append('\n')
            .Append(fact.Time).Append(" (").Append(fact.Zone).Append(')')
            .ToString();
    }

    /// <summary>
    /// A URL in a detail goes on its own line under its label rather than inline after it: mail
    /// clients linkify a URL that ends a line and routinely swallow the punctuation after one that
    /// does not.
    /// </summary>
    private static string Detail(MailDetail detail) =>
        detail.Url is { } url
            ? $"{detail.Label}: {detail.Value}\n{url}"
            : $"{detail.Label}: {detail.Value}";
}
