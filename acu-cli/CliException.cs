namespace acu_cli;

/// <summary>A user-facing error that should be printed without a stack trace.</summary>
internal sealed class CliException : Exception
{
    public CliException(string message) : base(message)
    {
    }
}
