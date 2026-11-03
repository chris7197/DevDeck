namespace chris7197.DevDeck.Core;

/// <summary>Small, reusable keyboard-first choice request. Never includes the complete clipboard.</summary>
public sealed record DevDeckPromptRequest(string Title, string Description, IReadOnlyList<DevDeckPromptChoice> Choices);
public sealed record DevDeckPromptChoice(string Id, string Label, char Shortcut, string? Description = null);
public sealed record DevDeckPromptResponse(string? ChoiceId);

public interface IDevDeckPromptService
{
    /// <returns>The selected choice ID; null if dismissed with Escape or the close button.</returns>
    Task<string?> ChooseAsync(DevDeckPromptRequest request, CancellationToken cancellationToken = default);
}

public static class DevDeckPromptValidation
{
    public static void Validate(DevDeckPromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Title.Length is < 1 or > 100 || request.Description.Length > 400 ||
            request.Choices.Count is < 1 or > 11)
            throw new ArgumentException("Prompt needs a title and one to eleven short choices.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shortcuts = new HashSet<char>();
        foreach (var choice in request.Choices)
        {
            if (string.IsNullOrWhiteSpace(choice.Id) || choice.Id.Length > 40 ||
                string.IsNullOrWhiteSpace(choice.Label) || choice.Label.Length > 70 ||
                !char.IsLetterOrDigit(choice.Shortcut) || !ids.Add(choice.Id) ||
                !shortcuts.Add(char.ToUpperInvariant(choice.Shortcut)))
                throw new ArgumentException("Prompt choices must have unique IDs and keyboard shortcuts.");
        }
    }
}
