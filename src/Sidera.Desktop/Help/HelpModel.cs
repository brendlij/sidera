using System.Collections.Generic;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Help;

/// <summary>A group of articles in the tree of the Help page ("Sequencing", "Troubleshooting").</summary>
public sealed record HelpSection(string Id, string Title);

/// <summary>A numbered marker on a screenshot: where it is (<see cref="X"/> and <see cref="Y"/> are fractions of the picture, 0 to 1) and what it points at. The markers are drawn over the picture, never in it.</summary>
public sealed record HelpCallout(int Number, string Label, double X, double Y);

/// <summary>A picture in the application (a file of <c>Assets/Help</c>, taken in the default theme) with the numbered markers that say what to look at.</summary>
public sealed record HelpScreenshot(string Resource, string Caption, IReadOnlyList<HelpCallout> Callouts);

/// <summary>Where a button of an article goes. Typed, so that a test can check that every one of them leads somewhere that exists.</summary>
public abstract record HelpTarget;

/// <summary>A page of the application.</summary>
public sealed record HelpPageTarget(AppPage Page) : HelpTarget;

/// <summary>A tab of the settings (see <c>SettingsViewModel</c>: "platesolving", "meridianflip").</summary>
public sealed record HelpSettingsTarget(string Tab) : HelpTarget;

/// <summary>A section of the equipment page (Cameras, Domes, Imaging Setups).</summary>
public sealed record HelpEquipmentTarget(EquipmentSection Section) : HelpTarget;

/// <summary>A button of an article: "Open Equipment", "Open Plate Solving settings".</summary>
public sealed record HelpAction(string Label, HelpTarget Target);

/// <summary>A troubleshooting entry: what is wrong, why it usually is, and what to look at.</summary>
public sealed record HelpProblem(string Problem, string Reason, IReadOnlyList<string> Checks);

/// <summary>
/// One article of the Help page, short and to do something with: what it is for, the steps, a picture with numbered markers, the things to know, buttons that go to the page, and the articles
/// that belong with it. Everything an article says was checked against what the application does; what it cannot do yet is said as "not yet", and never left out silently.
/// </summary>
public sealed record HelpArticle(string Id, string SectionId, string Title, string Intro)
{
    public IReadOnlyList<string> Steps { get; init; } = [];

    /// <summary>Things to know, one sentence each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public HelpScreenshot? Screenshot { get; init; }

    public IReadOnlyList<HelpAction> Actions { get; init; } = [];

    /// <summary>The ids of the articles that belong with this one.</summary>
    public IReadOnlyList<string> Related { get; init; } = [];

    /// <summary>Words that find the article besides its title and text ("flip", "roof").</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>For a troubleshooting article: the problem, the likely reason and what to check.</summary>
    public HelpProblem? Problem { get; init; }
}
