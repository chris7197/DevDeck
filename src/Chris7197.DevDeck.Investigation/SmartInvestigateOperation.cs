using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Investigation;

/// <summary>Smart Clipboard action; captures context before displaying an external choice overlay.</summary>
public sealed class SmartInvestigateOperation(
    SmartInvestigationService service,
    VisualStudioSourceNavigator navigator,
    IDevDeckPromptService prompts,
    string codePattern) : IDevDeckOperation, IDevDeckInvestigationCommands, IDevDeckCliCommandSource
{
    public Task<InvestigationResult> InvestigateRawAsync(string text, DeveloperContext context,
        InvestigationKind? selectedKind = null, CancellationToken cancellationToken = default) =>
        service.InvestigateAsync(text, context, openReport: false,
            cancellationToken: cancellationToken, selectedKind: selectedKind);

    private readonly Regex _ambiguousCode = new(codePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    public IReadOnlyList<DevDeckCliCommand> GetCliCommands() =>
    [
        new("--investigate-text", "--investigate-text <text> [solution.sln]", InvestigateCliAsync),
        new("--investigate-prompt-text", "--investigate-prompt-text <text> [solution.sln]", InvestigateCliAsync),
        new("--investigate-clipboard", "--investigate-clipboard [solution.sln]", InvestigateCliAsync)
    ];

    private async Task<int> InvestigateCliAsync(DevDeckCliContext cli)
    {
        string command = cli.Arguments[0];
        bool clipboard = command == "--investigate-clipboard";
        bool invalidArguments = clipboard
            ? cli.Arguments.Count < 1 || cli.Arguments.Count > 2
            : cli.Arguments.Count < 2 || cli.Arguments.Count > 3;
        if (invalidArguments)
            return await cli.InvalidArgumentsAsync(clipboard ? "--investigate-clipboard [solution.sln]" :
                command + " <text> [solution.sln]");
        string? path = cli.Arguments.Count == (clipboard ? 2 : 3) ? cli.Arguments[^1] : null;
        DeveloperContext developer;
        if (command == "--investigate-prompt-text" && path is null)
            developer = new DeveloperContext(Environment.CurrentDirectory, null, null);
        else
            developer = await cli.Runtime.ContextProvider.ResolveAsync(path);
        if (clipboard) return await cli.RunOperationAsync(this, developer);
        string text = cli.Arguments[1];
        if (command == "--investigate-prompt-text")
        {
            DevDeckOperationResult prompted = await InvestigateTextAsync(text, new DevDeckOperationContext(developer));
            cli.Output.WriteLine(prompted.Message);
            return prompted.Success || prompted.Cancelled ? 0 : 1;
        }
        InvestigationResult result = await InvestigateRawAsync(text, developer);
        cli.Output.WriteLine($"{result.Kind}: {result.Message}");
        if (result.ReportPath is not null) cli.Output.WriteLine("Report: " + result.ReportPath);
        return result.ReportPath is null ? 1 : 0;
    }

    public string Name => "Investigate";
    public string PluginActionId => "com.chris7197.devdeck.investigate";
    public string? IconName => DevDeckIcons.Investigate;
    public bool RequiresSolutionContext => false;
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
