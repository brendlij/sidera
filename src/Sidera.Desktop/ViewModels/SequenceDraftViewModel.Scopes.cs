using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;

namespace Sidera.Desktop.ViewModels;

/// <summary>One view of the session: everything, one rig, or what is shared. A view is a filter over the steps of the one sequence, never a copy of them.</summary>
public sealed partial class SessionScopeTab(string key, string title, Action<string> select) : ObservableObject
{
    public const string OverviewKey = "overview";
    public const string SharedKey = "shared";

    public string Key { get; } = key;
    public string Title { get; } = title;

    /// <summary>How many steps the view shows (not counting the containers that only hold them).</summary>
    [ObservableProperty]
    public partial int Count { get; internal set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string CountText => Key == OverviewKey ? string.Empty : Count.ToString(CultureInfo.InvariantCulture);

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            select(Key);
        }
    }
}

/// <summary>
/// The session by rig. The tabs (Overview, each rig, Shared) are projections of the same steps: a step belongs to the rig it names (or the track it is in), steps of the mount and the
/// guider are shared, and a container is shown while something it holds is. Nothing is stored for a tab; selecting one only decides which rows of the list are shown.
/// </summary>
public sealed partial class SequenceDraftViewModel
{
    private string _scopeKey = SessionScopeTab.OverviewKey;
    private bool _refreshingScopes;

    /// <summary>The views of the session; only offered when more than one rig exists, because with one rig the tabs would only repeat the list.</summary>
    public ObservableCollection<SessionScopeTab> ScopeTabs { get; } = [];

    public bool HasScopeTabs => ScopeTabs.Count > 0;

    /// <summary>A view other than the Overview is shown: some rows are hidden, and steps cannot be moved (see <c>JudgeMove</c>).</summary>
    public bool IsScopeFiltered => _scopeKey != SessionScopeTab.OverviewKey;

    /// <summary>The key of the view that is shown: "overview", "shared" or a rig id.</summary>
    public string SelectedScopeKey => _scopeKey;

    /// <summary>Shows a view. A view that does not exist (a rig that is gone) shows the overview.</summary>
    public void SelectScope(string key)
    {
        _scopeKey = ScopeTabs.Any(t => t.Key == key) ? key : SessionScopeTab.OverviewKey;
        RefreshScopeView();
    }

    internal string? RigKeyOf(StepDraftViewModel step)
    {
        if (step.IsMultiRig)
        {
            return null;
        }

        if (step is RigTrackDraftViewModel track)
        {
            return track.Rig.SelectedId?.Value;
        }

        if (step.RigPickers.FirstOrDefault()?.SelectedId is { } own)
        {
            return own.Value;
        }

        for (var parent = step.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is RigTrackDraftViewModel owner)
            {
                return owner.Rig.SelectedId?.Value;
            }
        }

        return null;
    }

    private string? ScopeKeyOf(StepDraftViewModel step) => step.IsContainer ? RigKeyOf(step) : RigKeyOf(step) ?? SessionScopeTab.SharedKey;

    private string ScopeLabelOf(StepDraftViewModel step)
    {
        if (step.IsMultiRig)
        {
            return "Parallel";
        }

        if (RigKeyOf(step) is { } key)
        {
            return _rigs is not null && _rigs.TryGet(new RigId(key), out var rig) && rig is not null ? rig.Name : key;
        }

        if (step.IsContainer)
        {
            return string.Empty;
        }

        return step.Kind is SequenceStepKind.Exposure or SequenceStepKind.MoveFocuser or SequenceStepKind.ChangeFilter or SequenceStepKind.DeviceOperation ? "Device" : "Shared";
    }

    /// <summary>Reads which views exist and which rows each shows. Called whenever the steps or the rigs change.</summary>
    internal void RefreshScopeView()
    {
        if (_refreshingScopes)
        {
            return;
        }

        _refreshingScopes = true;
        try
        {
            var rigs = (_rigs?.GetAll() ?? []).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var wanted = new List<(string Key, string Title)>();
            if (rigs.Count >= 2)
            {
                wanted.Add((SessionScopeTab.OverviewKey, "Overview"));
                wanted.AddRange(rigs.Select(r => (r.Id.Value, r.Name)));
                wanted.Add((SessionScopeTab.SharedKey, "Shared"));
            }

            if (!ScopeTabs.Select(t => (t.Key, t.Title)).SequenceEqual(wanted))
            {
                ScopeTabs.Clear();
                foreach (var (key, title) in wanted)
                {
                    ScopeTabs.Add(new SessionScopeTab(key, title, SelectScope));
                }

                OnPropertyChanged(nameof(HasScopeTabs));
            }

            if (ScopeTabs.All(t => t.Key != _scopeKey))
            {
                _scopeKey = SessionScopeTab.OverviewKey;
            }

            var keys = Rows.ToDictionary(row => row, ScopeKeyOf);
            foreach (var tab in ScopeTabs)
            {
                tab.IsSelected = tab.Key == _scopeKey;
                tab.Count = tab.Key == SessionScopeTab.OverviewKey ? 0 : Rows.Count(row => !row.IsContainer && keys[row] == tab.Key);
            }

            bool Shown(StepDraftViewModel row) =>
                _scopeKey == SessionScopeTab.OverviewKey || keys[row] == _scopeKey
                || (row is ContainerStepDraftViewModel container && container.Children.Any(Shown));
            foreach (var row in Rows)
            {
                row.IsInScopeView = Shown(row);
                row.ScopeLabel = ScopeLabelOf(row);
            }

            OnPropertyChanged(nameof(SelectedScopeKey));
            OnPropertyChanged(nameof(IsScopeFiltered));
        }
        finally
        {
            _refreshingScopes = false;
        }

        NotifyCommands();
    }
}
