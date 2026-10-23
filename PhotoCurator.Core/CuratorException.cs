namespace PhotoCurator.Core;

/// <summary>
/// A problem the person can act on (bad name, missing folder, name clash). The message is shown in the UI as-is,
/// so it is written for people, not developers.
/// </summary>
public sealed class CuratorException : Exception
{
    public int StatusCode { get; }

    public CuratorException(string message, int statusCode = 400) : base(message)
    {
        StatusCode = statusCode;
    }
}
