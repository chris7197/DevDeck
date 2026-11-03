using System.Text.Json;
using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Overlay;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            // A WinExe can still use stdin/stdout when they are redirected by the plugin.
            string? json = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(json) || json.Length > 20_000)
                throw new ArgumentException("Missing or excessively long prompt request.");
            DevDeckPromptRequest request = JsonSerializer.Deserialize<DevDeckPromptRequest>(json)
                ?? throw new ArgumentException("Invalid prompt request.");
            DevDeckPromptValidation.Validate(request);

            ApplicationConfiguration.Initialize();
            using var form = new PromptForm(request);
            Application.Run(form);
            Console.WriteLine(JsonSerializer.Serialize(new DevDeckPromptResponse(form.SelectedChoiceId)));
            Console.Out.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            // Never echo the request/clipboard content to the plugin log.
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
