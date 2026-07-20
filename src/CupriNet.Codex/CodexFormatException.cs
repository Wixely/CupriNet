namespace CupriNet.Codex;

/// <summary>Thrown when a Codex document or frame is malformed or exceeds a safety bound.</summary>
public sealed class CodexFormatException(string message) : Exception(message);
