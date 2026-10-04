using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    /// <summary>The last problem, in a sentence for the user; <c>null</c> when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>The exception behind <see cref="ErrorMessage"/>, kept for diagnostics.</summary>
    public Exception? LastException { get; private set; }

    protected void ReportError(Exception exception)
    {
        LastException = exception;
        ErrorMessage = UserFacingError.Describe(exception);
    }

    protected void ReportError(string message)
    {
        LastException = null;
        ErrorMessage = message;
    }

    /// <summary>A message for the user, with the exception behind it kept for diagnostics.</summary>
    protected void ReportError(string message, Exception exception)
    {
        LastException = exception;
        ErrorMessage = message;
    }

    protected void ClearError()
    {
        LastException = null;
        ErrorMessage = null;
    }
}
