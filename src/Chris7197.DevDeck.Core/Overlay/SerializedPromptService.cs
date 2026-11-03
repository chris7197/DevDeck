namespace chris7197.DevDeck.Core;

/// <summary>Prevents two controller actions from opening overlapping keyboard-choice popups.</summary>
public sealed class SerializedPromptService(IDevDeckPromptService inner) : IDevDeckPromptService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> ChooseAsync(DevDeckPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await inner.ChooseAsync(request, cancellationToken); }
        finally { _gate.Release(); }
    }
}
