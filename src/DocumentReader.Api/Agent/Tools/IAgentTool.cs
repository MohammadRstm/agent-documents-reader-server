using System.Text.Json;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Api.Agent.Tools;

/// <summary>
/// One approved, read-only operation the model may request. Tools only see the documents of the
/// session they are given, validate their own arguments strictly, and never touch the file system,
/// the network or configuration.
/// </summary>
public interface IAgentTool
{
    string Name { get; }

    /// <summary>Description shown to the model.</summary>
    string Description { get; }

    /// <summary>JSON Schema of the input object shown to the model. Validation does not rely on it.</summary>
    JsonElement InputSchema { get; }

    /// <summary>Runs the tool. Throws <see cref="ToolArgumentException"/> for invalid arguments.</summary>
    ToolResult Execute(Session session, JsonElement input, CancellationToken cancellationToken);
}

/// <summary>Invalid tool arguments. The message is returned to the model, so it must be safe.</summary>
public sealed class ToolArgumentException(string message) : Exception(message);
