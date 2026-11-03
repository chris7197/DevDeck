using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace chris7197.DevDeck.Core;

/// <summary>Smart Clipboard action; captures context before displaying an external choice overlay.</summary>
public sealed class SmartInvestigateOperation(
    SmartInvestigationService service,
    VisualStudioSourceNavigator navigator,
    IDevDeckPromptService prompts,
    string codePattern) : IDevDeckOperation
{
    private readonly Regex _ambiguousCode = new(codePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    public string Name => "Investigate";
    public string PluginActionId => "com.chris7197.devdeck.investigate";
    public string? IconName => DevDeckIcons.Investigate;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool RequiresSolutionContext => false;
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target)
    { target = null; return false; }
    public Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context, CancellationToken ct = default)
        => Task.FromResult(new DevDeckButtonState("Investigate", "Clipboard", DeckStatus.Neutral,
            "Reads clipboard when pressed. Ambiguous codes prompt for Product or Accessory.", IconName: IconName));

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        string text = await SmartInvestigationService.ReadClipboardAsync();
        return await InvestigateTextAsync(text, context);
    }

    /// <summary>Used by the CLI to exercise precisely the same overlay and handler without touching the clipboard.</summary>
    public async Task<DevDeckOperationResult> InvestigateTextAsync(string text, DevDeckOperationContext context)
    {
        InvestigationKind? chosenKind = null;
        // Only this configured pattern triggers the prompt. Other known formats follow
        // the existing no-dialog path; unrecognised input is never saved.
        if (!string.IsNullOrWhiteSpace(text) && text.Length <= 256 && _ambiguousCode.IsMatch(text.Trim()))
        {
            string code = text.Trim();
            string? selection = await prompts.ChooseAsync(new DevDeckPromptRequest(
                "Investigate clipboard", code,
                [
                    new DevDeckPromptChoice("product", "Product", 'P', "Search product"),
                    new DevDeckPromptChoice("accessory", "Accessory", 'A', "Search accessory")
                ]), context.CancellationToken);
            if (selection is null) return new DevDeckOperationResult(false, "Investigation cancelled", Cancelled: true);
            chosenKind = selection switch
            {
                "product" => InvestigationKind.ProductCode,
                "accessory" => InvestigationKind.AccessoryCode,
                _ => throw new InvalidOperationException("Unexpected investigation option: " + selection)
            };
        }

        InvestigationResult result = await service.InvestigateAsync(text, context.Developer,
            openReport: false, cancellationToken: context.CancellationToken, selectedKind: chosenKind);
        if (result.ReportPath is null) return new(false, result.Message);
        if (result.SourcePath is not null && result.SourceLine is > 0 && context.Developer.SolutionPath is not null)
        {
            try
            {
                await navigator.OpenAtLineAndOffsetAsync(context.Developer.SolutionPath,
                    result.SourcePath, result.SourceLine.Value, 1);
            }
            catch { DeveloperReportStore.Open(result.ReportPath); }
        }
        else DeveloperReportStore.Open(result.ReportPath);
        return new(true, result.Message + ": " + result.ReportPath);
    }
}
