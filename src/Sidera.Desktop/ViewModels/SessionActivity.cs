using System;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// Whether a sequence is running right now, shared by the view models of the manual controls so they can offer
/// no conflicting buttons. This is only a courtesy to the user: the runtime coordinates all access itself.
/// </summary>
public sealed class SessionActivity
{
    private bool _isSequenceRunning;

    public bool IsSequenceRunning
    {
        get => _isSequenceRunning;
        set
        {
            if (_isSequenceRunning != value)
            {
                _isSequenceRunning = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? Changed;
}
