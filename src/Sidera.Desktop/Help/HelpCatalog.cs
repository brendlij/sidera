using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Help;

/// <summary>
/// The Help of the application: its sections and its articles, as typed data (no XAML, no markup to parse), so that a test can check every id, every button and every picture. The articles are in the
/// other parts of this class, one for each section.
/// </summary>
public static partial class HelpCatalog
{
    public const string StartSection = "start";
    public const string SequencingSection = "sequencing";
    public const string ImagingSection = "imaging";
    public const string AstrometrySection = "astrometry";
    public const string MountSection = "mount";
    public const string ObservatorySection = "observatory";
    public const string TroubleshootingSection = "troubleshooting";
    public const string ReferenceSection = "reference";

    /// <summary>The first article: where Help opens when it is asked for nothing in particular.</summary>
    public const string StartArticle = "getting-started";

    /// <summary>The sections in the order the tree shows them.</summary>
    public static IReadOnlyList<HelpSection> Sections { get; } =
    [
        new(StartSection, "Start Here"),
        new(SequencingSection, "Sequencing"),
        new(ImagingSection, "Imaging"),
        new(AstrometrySection, "Astrometry"),
        new(MountSection, "Mount & Guiding"),
        new(ObservatorySection, "Observatory"),
        new(TroubleshootingSection, "Troubleshooting"),
        new(ReferenceSection, "Reference"),
    ];

    /// <summary>Every article, in the order of the tree: by section, then as written.</summary>
    public static IReadOnlyList<HelpArticle> All { get; } = Order([.. StartArticles(), .. SequencingArticles(), .. ImagingArticles(), .. AstrometryArticles(), .. MountArticles(), .. ObservatoryArticles(), .. TroubleshootingArticles(), .. ReferenceArticles()]);

    public static HelpArticle? Find(string? id) => id is null ? null : All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));

    public static IEnumerable<HelpArticle> In(string sectionId) => All.Where(a => a.SectionId == sectionId);

    /// <summary>Everything an article says, as one text to search in (lower case).</summary>
    public static string SearchText(HelpArticle article) => string.Join(
        '\n', new[] { article.Title, article.Intro }.Concat(article.Steps).Concat(article.Notes).Concat(article.Keywords)
            .Concat(article.Problem is { } p ? new[] { p.Problem, p.Reason }.Concat(p.Checks) : [])
            .Concat(article.Actions.Select(a => a.Label))).ToLowerInvariant();

    /// <summary>The articles that match every word of <paramref name="query"/> (anywhere in the title or the text); all of them for an empty query.</summary>
    public static IReadOnlyList<HelpArticle> Search(string? query)
    {
        var words = (query ?? string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant()).ToList();
        return words.Count == 0 ? All : [.. All.Where(a => { var text = SearchText(a); return words.All(text.Contains); })];
    }

    private static IReadOnlyList<HelpArticle> Order(IReadOnlyList<HelpArticle> articles) =>
        [.. Sections.SelectMany(section => articles.Where(a => a.SectionId == section.Id))];

    // ---- what the parts of this class use to write articles

    private static HelpScreenshot Shot(string file, string caption, params (int Number, string Label, double X, double Y)[] marks) =>
        new(file, caption, [.. marks.Select(m => new HelpCallout(m.Number, m.Label, m.X, m.Y))]);

    private static HelpAction Page(string label, AppPage page) => new(label, new HelpPageTarget(page));

    private static HelpAction Setting(string label, string tab) => new(label, new HelpSettingsTarget(tab));

    private static HelpAction Equipment(string label, EquipmentSection section) => new(label, new HelpEquipmentTarget(section));
}
