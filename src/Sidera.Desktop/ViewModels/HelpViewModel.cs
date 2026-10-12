using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Desktop.Help;

namespace Sidera.Desktop.ViewModels;

/// <summary>One entry of the tree of the Help page: an article, and whether it is the one that is open.</summary>
public sealed partial class HelpLinkViewModel(HelpArticle article, Action<HelpArticle> open) : ObservableObject
{
    public HelpArticle Article { get; } = article;

    public string Title => Article.Title;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Open() => open(Article);
}

/// <summary>A group of the tree: a section and its articles.</summary>
public sealed record HelpGroupViewModel(string Title, IReadOnlyList<HelpLinkViewModel> Links);

/// <summary>One numbered step of an article.</summary>
public sealed record HelpStepLine(int Number, string Text);

/// <summary>A button of an article: it goes to a page of the application.</summary>
public sealed class HelpActionViewModel(HelpAction action, Func<HelpTarget, bool> go, Action<string> say)
{
    public string Label => action.Label;

    public HelpTarget Target => action.Target;

    public ICommand GoCommand { get; } = new RelayCommand(() =>
    {
        if (!go(action.Target))
        {
            say("That page is not there yet: " + action.Label.Replace("Open ", string.Empty, StringComparison.Ordinal) + " appears when it has something to show.");
        }
    });
}

/// <summary>A link to another article, under "Related".</summary>
public sealed class HelpRelatedViewModel(HelpArticle article, Action<HelpArticle> open)
{
    public string Title => article.Title;

    public ICommand OpenCommand { get; } = new RelayCommand(() => open(article));
}

/// <summary>An article as the page shows it: the words, the numbered steps, the picture with its markers, the buttons and the related articles.</summary>
public sealed class HelpArticleViewModel
{
    public HelpArticleViewModel(HelpArticle article, string sectionTitle, IReadOnlyList<HelpActionViewModel> actions, IReadOnlyList<HelpRelatedViewModel> related)
    {
        Article = article;
        SectionTitle = sectionTitle;
        Actions = actions;
        Related = related;
        Steps = [.. article.Steps.Select((text, index) => new HelpStepLine(index + 1, text))];
    }

    public HelpArticle Article { get; }

    public string SectionTitle { get; }

    public string Title => Article.Title;

    public string Intro => Article.Intro;

    public IReadOnlyList<HelpStepLine> Steps { get; }

    public bool HasSteps => Steps.Count > 0;

    public IReadOnlyList<string> Notes => Article.Notes;

    public bool HasNotes => Notes.Count > 0;

    public HelpScreenshot? Screenshot => Article.Screenshot;

    public bool HasScreenshot => Screenshot is not null;

    public IReadOnlyList<HelpCallout> Callouts => Screenshot?.Callouts ?? [];

    public bool HasCallouts => Callouts.Count > 0;

    public HelpProblem? Problem => Article.Problem;

    public bool HasProblem => Problem is not null;

    public IReadOnlyList<string> Checks => Problem?.Checks ?? [];

    public IReadOnlyList<HelpActionViewModel> Actions { get; }

    public bool HasActions => Actions.Count > 0;

    public IReadOnlyList<HelpRelatedViewModel> Related { get; }

    public bool HasRelated => Related.Count > 0;
}

/// <summary>
/// The Help page: a tree of sections and articles with a search on the left, the article on the right. Every article is short and says what to do; its buttons go to the page it is about, and a "?" in
/// the header of a page opens the article of that page (see <see cref="HelpContexts"/>). The content is data (<see cref="HelpCatalog"/>); this only shows it and navigates.
/// </summary>
public sealed partial class HelpViewModel : ViewModelBase
{
    private readonly Func<HelpTarget, bool> _go;
    private readonly Action _back;
    private readonly Stack<string> _history = new();
    private IReadOnlyList<HelpLinkViewModel> _all = [];

    /// <param name="go">Goes to a page of the application; <c>false</c> when there is nothing to go to (an imaging setups tab with no setup).</param>
    /// <param name="back">Goes back to the page that Help was opened from.</param>
    public HelpViewModel(Func<HelpTarget, bool> go, Action back)
    {
        _go = go;
        _back = back;
        Build();
        Open(HelpCatalog.Find(HelpCatalog.StartArticle)!, remember: false);
    }

    /// <summary>The sections with their articles, narrowed by the search.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HelpGroupViewModel> Groups { get; private set; } = [];

    /// <summary>The article that is open.</summary>
    [ObservableProperty]
    public partial HelpArticleViewModel Article { get; private set; } = null!;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>What the search found, or that it found nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultsText))]
    public partial string ResultsText { get; private set; } = string.Empty;

    public bool HasResultsText => ResultsText.Length > 0;

    /// <summary>Said when a button leads nowhere (yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string NoticeText { get; private set; } = string.Empty;

    public bool HasNotice => NoticeText.Length > 0;

    public bool CanGoBack => _history.Count > 0;

    partial void OnSearchTextChanged(string value) => ApplySearch();

    /// <summary>Opens an article by its id; an id that does not exist leaves the page as it is.</summary>
    public bool Open(string? id)
    {
        if (HelpCatalog.Find(id) is not { } article)
        {
            return false;
        }

        Open(article, remember: true);
        return true;
    }

    /// <summary>Opens the article that belongs to where the "?" was pressed (see <see cref="HelpContexts"/>), with the search cleared so that it can be seen in the tree.</summary>
    public void OpenFor(string? context)
    {
        SearchText = string.Empty;
        Open(HelpCatalog.Find(HelpContexts.ArticleFor(context)) ?? HelpCatalog.Find(HelpCatalog.StartArticle)!, remember: true);
    }

    private void Open(HelpArticle article, bool remember)
    {
        if (remember && Article is { } current && current.Article.Id != article.Id)
        {
            _history.Push(current.Article.Id);
            OnPropertyChanged(nameof(CanGoBack));
            BackCommand.NotifyCanExecuteChanged();
        }

        NoticeText = string.Empty;
        var section = HelpCatalog.Sections.First(s => s.Id == article.SectionId).Title;
        Article = new HelpArticleViewModel(
            article, section,
            [.. article.Actions.Select(a => new HelpActionViewModel(a, _go, text => NoticeText = text))],
            [.. article.Related.Select(HelpCatalog.Find).OfType<HelpArticle>().Select(r => new HelpRelatedViewModel(r, a => Open(a, remember: true)))]);
        foreach (var link in _all)
        {
            link.IsSelected = link.Article.Id == article.Id;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (_history.TryPop(out var id) && HelpCatalog.Find(id) is { } article)
        {
            Open(article, remember: false);
        }

        OnPropertyChanged(nameof(CanGoBack));
        BackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Leaves Help for the page it was opened from.</summary>
    [RelayCommand]
    private void Return() => _back();

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private void Build()
    {
        _all = [.. HelpCatalog.All.Select(a => new HelpLinkViewModel(a, article => Open(article, remember: true)))];
        ApplySearch();
    }

    private void ApplySearch()
    {
        var found = HelpCatalog.Search(SearchText);
        var ids = found.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        Groups =
        [
            .. HelpCatalog.Sections
                .Select(section => new HelpGroupViewModel(section.Title, [.. _all.Where(l => l.Article.SectionId == section.Id && ids.Contains(l.Article.Id))]))
                .Where(group => group.Links.Count > 0),
        ];
        ResultsText = string.IsNullOrWhiteSpace(SearchText) ? string.Empty : found.Count == 0 ? "No article matches." : found.Count == 1 ? "1 article" : $"{found.Count} articles";
        if (found.Count > 0 && string.IsNullOrWhiteSpace(SearchText) is false && Article is { } shown && !ids.Contains(shown.Article.Id))
        {
            // The article that is open is not among the results: the first result is shown, so that the page is never empty next to its list.
            Open(found[0], remember: false);
        }
    }
}
