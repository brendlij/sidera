using System;
using System.Linq;
using Sidera.Core.Guiding;
using Sidera.Runtime.Coordination;

namespace Sidera.Desktop.ViewModels;

/// <summary>Turns exceptions from the runtime into one short sentence for the UI. The exception itself stays available to the caller.</summary>
public static class UserFacingError
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            RigTrackFailedException track => $"{track.TrackName}: {Describe(track.InnerException ?? track)}",
            CoordinationAbortedException => "Cancelled: another branch failed before the coordinated step could run.",
            OperationCanceledException => "Cancelled.",
            GuidingSettleTimeoutException => "Guiding did not settle within the time limit.",
            AggregateException aggregate when aggregate.InnerExceptions.Count > 0 => DescribeAggregate(aggregate),
            ArgumentOutOfRangeException range => FirstLine(range.Message),
            FormatException format => format.Message,
            InvalidOperationException invalid => invalid.Message,
            _ => $"Unexpected error: {FirstLine(exception.Message)}",
        };
    }

    private static string DescribeAggregate(AggregateException aggregate)
    {
        var first = Describe(aggregate.InnerExceptions[0]);
        return aggregate.InnerExceptions.Count == 1 ? first : $"{first} (and {aggregate.InnerExceptions.Count - 1} more)";
    }

    // Domain messages end with "(Parameter 'x')" and "Actual value was ..."; the first sentence is enough for a person.
    private static string FirstLine(string message)
    {
        var line = message.Split('\n', '\r').FirstOrDefault(l => l.Length > 0) ?? message;
        var parameter = line.IndexOf(" (Parameter", StringComparison.Ordinal);
        return parameter > 0 ? line[..parameter] : line;
    }
}
