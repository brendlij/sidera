using System;
using System.Collections.Generic;

namespace Sidera.Desktop.Help;

/// <summary>
/// Which article a "?" opens, from where it is pressed: one place for the whole application. A context is the page ("session"), or the page and what is open on it ("settings:platesolving",
/// "equipment:dome"). A context that is not listed falls back to the page, and a page that is not listed to the first article.
/// </summary>
public static class HelpContexts
{
    public static IReadOnlyDictionary<string, string> Articles { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["dashboard"] = "dashboard",
        ["session"] = "sequencer-basics",
        ["imaging"] = "manual-capture",
        ["framing"] = "framing",
        ["platesolve"] = "plate-solving",
        ["equipment"] = "equipment-setup",
        ["equipment:dome"] = "domes",
        ["equipment:setups"] = "multi-camera",
        ["diagnostics"] = "logs-and-diagnostics",
        ["settings"] = "file-locations",
        ["settings:general"] = "file-locations",
        ["settings:appearance"] = "themes",
        ["settings:observatory"] = "observing-site",
        ["settings:imaging"] = "manual-capture",
        ["settings:autofocus"] = "autofocus",
        ["settings:guiding"] = "guiding",
        ["settings:platesolving"] = "plate-solving",
        ["settings:framing"] = "framing",
        ["settings:meridianflip"] = "meridian-flip",
        ["settings:sequencer"] = "sequencer-basics",
        ["settings:advanced"] = "file-locations",
        ["help"] = HelpCatalog.StartArticle,
    };

    /// <summary>The id of the article for a context.</summary>
    public static string ArticleFor(string? context)
    {
        if (context is not null)
        {
            if (Articles.TryGetValue(context, out var exact))
            {
                return exact;
            }

            var colon = context.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && Articles.TryGetValue(context[..colon], out var page))
            {
                return page;
            }
        }

        return HelpCatalog.StartArticle;
    }
}
