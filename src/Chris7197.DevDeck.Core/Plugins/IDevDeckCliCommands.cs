namespace chris7197.DevDeck.Core;

/// <summary>
/// Optional capability of an action DLL. The CLI discovers commands from the loaded action,
/// without referencing its implementation or knowing what its arguments mean.
/// </summary>
public interface IDevDeckCliCommandSource
{
    IReadOnlyList<DevDeckCliCommand> GetCliCommands();
}

/// <summary>Action-owned CLI command, including its help text and execution delegate.</summary>
public sealed record DevDeckCliCommand(
    string Name, string Usage, Func<DevDeckCliContext, Task<int>> ExecuteAsync);

/// <summary>Host conveniences; interpretation and presentation of action-specific results remain in plugins.</summary>
public sealed class DevDeckCliContext(DevDeckRuntime runtime, IReadOnlyList<string> arguments,
    TextWriter output, TextWriter error)
{
    public DevDeckRuntime Runtime { get; } = runtime;
    public IReadOnlyList<string> Arguments { get; } = arguments;
    public TextWriter Output { get; } = output;
    public TextWriter Error { get; } = error;

    public async Task<DeveloperContext?> ResolveContextAsync(string? path, string title,
        bool saveAll = false, bool promptWhenMissing = true)
        => path is null && promptWhenMissing
            ? await Runtime.SolutionSelector.ResolveAsync(title, saveAll)
            : await Runtime.ContextProvider.ResolveAsync(path, saveAll);

    public async Task<int> RunOperationAsync(IDevDeckOperation operation, DeveloperContext context,
        DeckActionSettings? settings = null)
    {
        DevDeckOperationResult result = await operation.ExecuteAsync(new DevDeckOperationContext(
            context,
            stage => { Output.WriteLine(" > " + stage.Replace('\n', ' ')); return Task.CompletedTask; },
            ActionSettings: settings));
        Output.WriteLine(result.Message);
        return result.Success || result.Cancelled ? 0 : result.HasConflicts ? 2 : 1;
    }

    public Task<int> InvalidArgumentsAsync(string usage)
    {
        Error.WriteLine("Usage: DevDeck.Cli " + usage);
        return Task.FromResult(64);
    }
}

/// <summary>Single dispatch point for all plugin-owned commands. A duplicate alias is an error.</summary>
public sealed class DevDeckCliCommandRegistry
{
    private readonly Dictionary<string, DevDeckCliCommand> _commands = new(StringComparer.Ordinal);

    public DevDeckCliCommandRegistry(IEnumerable<IDevDeckOperation> operations)
    {
        foreach (IDevDeckOperation operation in operations)
        {
            if (operation is IDevDeckCliCommandSource source)
            {
                foreach (DevDeckCliCommand command in source.GetCliCommands()) Add(command);
            }
            else
            {
                // Compatibility for third-party actions written against the original action contract.
                foreach (string alias in operation.CliCommands)
                    Add(new DevDeckCliCommand(alias, operation.CliUsage, context => RunLegacyAsync(operation, context)));
            }
        }
    }

    public IEnumerable<DevDeckCliCommand> Commands => _commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal);

    public bool TryGet(string name, out DevDeckCliCommand? command) => _commands.TryGetValue(name, out command);

    private void Add(DevDeckCliCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Name) || !command.Name.StartsWith("--", StringComparison.Ordinal) ||
            command.Name is "--help" or "--run" or "--run-slot" ||
            !_commands.TryAdd(command.Name, command))
            throw new InvalidOperationException($"Invalid or duplicate plugin CLI command: '{command.Name}'.");
    }

    private static async Task<int> RunLegacyAsync(IDevDeckOperation action, DevDeckCliContext context)
    {
        if (!action.TryParseCliArguments(context.Arguments, out string? target))
            return await context.InvalidArgumentsAsync(action.CliUsage);
        DeveloperContext? developer = await context.ResolveContextAsync(target, action.Name,
            action.SaveAllBeforeExecute, action.RequiresSolutionContext);
        if (developer is null) { context.Output.WriteLine("Cancelled."); return 0; }
        return await context.RunOperationAsync(action, developer);
    }
}
