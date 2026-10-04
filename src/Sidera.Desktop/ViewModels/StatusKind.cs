namespace Sidera.Desktop.ViewModels;

/// <summary>
/// How a status is shown, not what it says: the dot of a status indicator takes its colour from this. Colour is kept
/// for meaning: <see cref="Ok"/> for what works, <see cref="Active"/> for what is going on, <see cref="Warning"/> for
/// what needs a look (paused, cancelled), <see cref="Error"/> for what failed.
/// </summary>
public enum StatusKind
{
    Neutral,
    Ok,
    Active,
    Warning,
    Error,
}
