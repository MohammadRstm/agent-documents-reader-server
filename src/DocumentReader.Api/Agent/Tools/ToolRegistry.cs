using System.Text.Json;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Api.Agent.Tools;

/// <summary>
/// The fixed allowlist of tools. Unknown tool names, invalid arguments and unexpected tool failures
/// all become error results for the model instead of exceptions, so one bad call cannot crash a request.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools;
    private readonly ILogger<ToolRegistry> _logger;

    public ToolRegistry(IEnumerable<IAgentTool> tools, ILogger<ToolRegistry> logger)
    {
        _tools = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        _logger = logger;
        Tools = [.. _tools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal)];
    }

    public IReadOnlyList<IAgentTool> Tools { get; }

    public ToolResult Execute(Session session, string toolName, JsonElement input, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(toolName, out var tool))
        {
            var shownName = toolName.Length > 64 ? toolName[..64] : toolName;
            return ToolResult.Error($"Unknown tool \"{shownName}\". Available tools: {string.Join(", ", _tools.Keys.Order())}.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return tool.Execute(session, input, cancellationToken);
        }
        catch (ToolArgumentException exception)
        {
            return ToolResult.Error($"Invalid arguments for {tool.Name}: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Tool {ToolName} failed.", tool.Name);
            return ToolResult.Error($"The tool {tool.Name} failed unexpectedly.");
        }
    }
}
