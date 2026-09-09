using System.Net;
using System.Text;

namespace PublicSectorAgentDemos.Presenter;

public static class PresenterPage
{
    public static string Render(PresenterSessionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        StringBuilder html = new(
            """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Public Sector Agent Demos | Presenter</title>
              <link rel="stylesheet" href="/presenter.css">
              <script src="/presenter.js" defer></script>
            </head>
            <body>
              <main>
                <header>
                  <p class="eyebrow">Public Sector Agent Demos</p>
                  <h1>Presenter mode</h1>
                  <p>Use these sessions in order. All inputs and fallback notes are available without Azure or network access.</p>
                  <button id="warm-up" type="button">Warm up demos</button>
                  <p id="warm-up-summary" role="status">Warm-up has not started.</p>
                  <ul id="warm-up-results" aria-live="polite"></ul>
                </header>
                <section class="sessions" aria-label="Demonstration sessions">
            """);

        foreach (PresenterSession session in catalog.Sessions)
        {
            AppendSession(html, session);
        }

        html.Append("</section>");
        if (catalog.Extras is { Count: > 0 })
        {
            html.Append(
                """
                <section aria-labelledby="extras-title">
                  <h2 id="extras-title">Optional extras</h2>
                  <p>These applications are separate from the six main sessions. Their status does not determine main readiness.</p>
                  <ul id="warm-up-extra-results" aria-live="polite"></ul>
                  <div class="sessions">
                """);
            foreach (PresenterSession extra in catalog.Extras)
            {
                AppendSession(html, extra);
            }
            html.Append("</div></section>");
        }
        return html.Append(
            """
              </main>
            </body>
            </html>
            """).ToString();
    }

    private static void AppendSession(StringBuilder html, PresenterSession session)
    {
        string id = Encode(session.Id);
        html.Append(
            $"""
                  <article class="session-card" id="{id}">
                    <div class="session-title">
                      <h2>{Encode(session.Title)}</h2>
                      {(session.Optional ? "<span class=\"optional\">Optional</span>" : string.Empty)}
                    </div>
                    <p class="proof">{Encode(session.Proof)}</p>
                    <p class="session-links">{RenderLinks(session)}</p>
                    <h3>Exact input</h3>
                    <pre id="input-{id}"><code>{Encode(session.Input)}</code></pre>
                    <button class="copy-input" type="button" data-copy-target="input-{id}">Copy input</button>
                    <p><strong>Tab note:</strong> {Encode(session.TabNote)}</p>
                    <aside class="fallback"><strong>Saved-result fallback:</strong> {Encode(session.SavedResultFallback)}</aside>
                  </article>
            """);
    }

    // Renders one action group: the primary launch link plus any optional auxiliary links
    // (for example, the Hosted session's agent playground and VS Code source links). This
    // keeps the markup generic instead of adding session-specific HTML per link.
    private static string RenderLinks(PresenterSession session)
    {
        if (!session.Configured)
        {
            if (session.Id == "tokens-and-credits")
            {
                return session.StartupStatus == "failed"
                    ? "Startup failed. Review the masked Tokens and Credits logs. The main sessions remain available."
                    : "Not configured. The optional Tokens and Credits checkout was not discovered. The main sessions remain available.";
            }
            return "Not configured. This optional application is managed outside this repository.";
        }

        StringBuilder links = new();
        links.Append(RenderLink(session.LaunchUrl, session.LaunchLabel));
        foreach (PresenterSessionLink link in session.Links ?? [])
        {
            links.Append(RenderLink(link.Url, link.Label));
        }

        return links.ToString();
    }

    private static string RenderLink(string url, string label) =>
        $"""<a href="{Encode(url)}" target="_blank" rel="noopener noreferrer">{Encode(label)}</a> """;

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
