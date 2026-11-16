namespace SdlVulkan.Renderer.Inspector.Client;

/// <summary>
/// An inspector call that failed on the app's side: it answered with an error, it closed the connection
/// with no reply, or it refused the token. <see cref="ServerMessage"/> is the server's own text, never a
/// generic one, so a caller sees why (an unknown key name, a modifier it refused, a missing frame).
/// </summary>
public sealed class InspectorException : Exception
{
    /// <summary>The wire method that failed, e.g. <c>click</c>.</summary>
    public string Method { get; }

    /// <summary>What the server said (or, with no reply, what the client saw).</summary>
    public string ServerMessage { get; }

    /// <summary>True when the server refused the token.</summary>
    public bool Unauthorized { get; }

    public InspectorException(string method, string serverMessage, bool unauthorized = false)
        : base($"inspector {method}: {serverMessage}")
    {
        Method = method;
        ServerMessage = serverMessage;
        Unauthorized = unauthorized;
    }
}
