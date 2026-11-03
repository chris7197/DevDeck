using chris7197.DevDeck.Core;

/// <summary>Offline contract tests: no Visual Studio, UI, Git changes or hardware required.</summary>
internal static class SolutionSelectionSelfTest
{
    public static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "DevDeck-SolutionSelection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var instances = Enumerable.Range(1, 12).Select(i =>
            {
                string path = Path.Combine(root, $"Solution{i:00}.sln");
                File.WriteAllText(path, "");
                return new VisualStudioSolutionInstance(path, (uint)(3000 + i), IntPtr.Zero);
            }).ToArray();

            // Focused VS must be chosen immediately without opening the overlay or enumerating.
            var focused = new FakeDiscovery(instances) { Focused = instances[3] };
            var noPrompt = new FakePrompt();
            var focusedResult = await new SolutionSelectionResolver(focused, noPrompt)
                .ResolveAsync("Sync Main", saveAllBeforeExecute: true);
            Check(focusedResult?.SolutionPath == instances[3].SolutionPath, "Focus path");
            Check(focusedResult?.VisualStudioProcessId == instances[3].ProcessId, "Focus process");
            Check(noPrompt.Requests.Count == 0 && focused.ListCalls == 0, "Focused path must bypass prompt");
            Check(focused.Saved.Count == 1 && focused.Saved[0].Path == instances[3].SolutionPath,
                "Save All must target focused instance");

            // Without focus, sort alphabetically and choose by digit (2 selects the second).
            var notFocused = new FakeDiscovery(instances[..3]);
            var selectSecond = new FakePrompt("1"); // zero-based choice IDs, number '2' on screen
            var chosen = await new SolutionSelectionResolver(notFocused, selectSecond)
                .ResolveAsync("New Migration");
            Check(chosen?.SolutionPath == instances[1].SolutionPath && notFocused.Saved.Count == 0,
                "Numbered solution selection should resolve without saving");
            Check(selectSecond.Requests.Single().Choices[1].Shortcut == '2', "Number shortcut mapping");

            // Esc should prevent Save All and any subsequent action.
            var cancelDiscovery = new FakeDiscovery(instances[..3]);
            var cancel = new FakePrompt((string?)null);
            var result = await new SolutionSelectionResolver(cancelDiscovery, cancel)
                .ResolveAsync("Sync Main", true);
            Check(result is null && cancelDiscovery.Saved.Count == 0, "Escape cancellation");

            // Pagination must expose all 9 numeric choices and navigate to the next page.
            var paged = new FakePrompt("next", "0");
            var pageResult = await new SolutionSelectionResolver(new FakeDiscovery(instances), paged)
                .ResolveAsync("Latest Log");
            Check(paged.Requests.Count == 2, "Multiple prompt pages");
            Check(paged.Requests[0].Choices.Take(9).Select(x => x.Shortcut)
                .SequenceEqual("123456789"), "First nine shortcuts");
            Check(paged.Requests[0].Choices.Last().Shortcut == 'N', "Next shortcut");
            Check(pageResult?.SolutionPath == instances[9].SolutionPath, "Page two selection");

            var empty = new FakeDiscovery([]);
            bool failedWithMessage = false;
            try { await new SolutionSelectionResolver(empty, new FakePrompt()).ResolveAsync("Sync Main"); }
            catch (InvalidOperationException e) { failedWithMessage = e.Message.Contains("No saved Visual Studio"); }
            Check(failedWithMessage, "No open solution error");

            // Max eleven prompt choices includes nine solutions + Previous and Next navigation.
            DevDeckPromptValidation.Validate(new DevDeckPromptRequest("Select", "Page 2", [
                .. Enumerable.Range(1, 9).Select(i => new DevDeckPromptChoice(i.ToString(), "Solution " + i, (char)('0' + i))),
                new("prev", "Previous", 'B'), new("next", "Next", 'N')
            ]));

            Console.WriteLine("Focused VS, numbered selection, cancellation, paging, SaveAll: PASS");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Solution selection self-test failed: " + e);
            return 1;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Failed: " + description);
    }

    private sealed class FakeDiscovery(IReadOnlyList<VisualStudioSolutionInstance> open) : IVisualStudioSolutionDiscovery
    {
        public VisualStudioSolutionInstance? Focused { get; set; }
        public int ListCalls { get; private set; }
        public List<(uint Pid, string Path)> Saved { get; } = [];
        public Task<VisualStudioSolutionInstance?> GetFocusedSolutionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Focused);
        public Task<IReadOnlyList<VisualStudioSolutionInstance>> GetOpenSolutionsAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(open);
        }
        public Task SaveAllAsync(uint processId, string expectedSolutionPath, CancellationToken cancellationToken = default)
        {
            Saved.Add((processId, expectedSolutionPath));
            return Task.CompletedTask;
        }
    }

    private sealed class FakePrompt(params string?[] selections) : IDevDeckPromptService
    {
        private readonly Queue<string?> _responses = new(selections);
        public List<DevDeckPromptRequest> Requests { get; } = [];
        public Task<string?> ChooseAsync(DevDeckPromptRequest request, CancellationToken cancellationToken = default)
        {
            DevDeckPromptValidation.Validate(request);
            Requests.Add(request);
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : null);
        }
    }
}
