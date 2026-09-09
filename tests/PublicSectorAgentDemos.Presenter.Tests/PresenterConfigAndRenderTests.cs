using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PublicSectorAgentDemos.Presenter;

namespace PublicSectorAgentDemos.Presenter.Tests;

public sealed class PresenterConfigAndRenderTests
{
    [Theory]
    [InlineData("http://localhost:5082/")]
    [InlineData("https://act.azurewebsites.net/health")]
    public void LocalAppWarmUp_AcceptsRootAndHealthEndpoints(string endpoint)
    {
        PresenterWarmUpTarget target = new("local-app", endpoint, null);

        target.Validate("act");
    }

    [Fact]
    public void LocalAppWarmUp_RejectsOtherPaths()
    {
        PresenterWarmUpTarget target =
            new("local-app", "https://act.azurewebsites.net/admin", null);

        Assert.Throws<InvalidDataException>(() => target.Validate("act"));
    }

    [Fact]
    public void Configuration_HasRequiredOrderAndContent()
    {
        PresenterSessionCatalog catalog = LoadCatalog();

        Assert.Equal(
            [
                "Foundation",
                "Ground",
                "Act",
                "Cross-Government Coordinate",
                "Patriots Coordinate",
                "Hosted"
            ],
            catalog.Sessions.Select(session => session.Title));
        Assert.All(
            catalog.Sessions,
            session =>
            {
                Assert.NotEmpty(session.Proof);
                if (session.Configured)
                {
                    Assert.NotEmpty(session.LaunchUrl);
                }
                Assert.NotEmpty(session.Input);
                Assert.NotEmpty(session.TabNote);
                Assert.NotEmpty(session.SavedResultFallback);
            });
        Assert.All(
            catalog.Sessions.Where(session => session.Id != "patriots-coordinate"),
            session => Assert.DoesNotContain(
                "replace-",
                session.WarmUp.Endpoint,
                StringComparison.Ordinal));
        Assert.All(
            catalog.Sessions,
            session =>
            {
                Assert.DoesNotContain("synthetic", session.Proof, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("synthetic", session.Input, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("synthetic", session.TabNote, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "synthetic",
                    session.SavedResultFallback,
                    StringComparison.OrdinalIgnoreCase);
            });
        Assert.True(catalog.Sessions[^1].Optional);
        PresenterSession patriots = catalog.Sessions[4];
        Assert.True(patriots.Optional);
        Assert.False(patriots.Configured);
        Assert.Empty(patriots.LaunchUrl);
    }

    [Fact]
    public void FoundationAndGroundUseTheSameEvidenceDependentRehearsal()
    {
        PresenterSessionCatalog catalog = LoadCatalog();
        PresenterSession foundation = catalog.Sessions.Single(session => session.Id == "foundation");
        PresenterSession ground = catalog.Sessions.Single(session => session.Id == "ground");
        using JsonDocument fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "data", "ground", "v1", "fixture-set.json")));
        string prompt = fixtures.RootElement.GetProperty("fixtures").EnumerateArray()
            .Single(item => item.GetProperty("scenarioId").GetString() == "MPM-005")
            .GetProperty("prompt").GetString()!;

        Assert.Equal(prompt, foundation.Input);
        Assert.Equal(prompt, ground.Input);
        Assert.Equal("http://localhost:5090/", foundation.LaunchUrl);
        Assert.Equal(foundation.LaunchUrl, ground.LaunchUrl);
        foreach (PresenterSession session in new[] { foundation, ground })
        {
            Assert.Contains("shared prompt", session.TabNote, StringComparison.Ordinal);
            Assert.Contains("Compare both agents", session.TabNote, StringComparison.Ordinal);
            Assert.Contains("independently as each completes", session.TabNote, StringComparison.Ordinal);
            Assert.Contains("Foundry link only as an alternative", session.TabNote, StringComparison.Ordinal);
            Assert.DoesNotContain("fresh", session.TabNote, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("before opening Ground", session.TabNote, StringComparison.Ordinal);
        }
        Assert.Contains("blockquotes", ground.Proof, StringComparison.Ordinal);
        Assert.Contains("does not guarantee correctness", ground.Proof, StringComparison.Ordinal);
        Assert.Contains("ground-comparison.md", foundation.SavedResultFallback, StringComparison.Ordinal);
        Assert.Contains("ground-comparison.md", ground.SavedResultFallback, StringComparison.Ordinal);
    }

    [Fact]
    public void ActSession_ExplainsFloodSupportAndTheRequestedApproval()
    {
        PresenterSession act = LoadCatalog().Sessions.Single(session => session.Id == "act");

        Assert.Contains("180 households", act.Input, StringComparison.Ordinal);
        Assert.Contains("90 available rooms yesterday", act.Input, StringComparison.Ordinal);
        Assert.Contains("latest report lists 60", act.Input, StringComparison.Ordinal);
        Assert.Contains("AREA-1001", act.TabNote, StringComparison.Ordinal);
        Assert.Contains("approval modal", act.TabNote, StringComparison.Ordinal);
        Assert.Contains("estimated cost, justification, evidence, and unmet needs", act.TabNote, StringComparison.Ordinal);
        Assert.Contains("reserveSupportPackage", act.TabNote, StringComparison.Ordinal);
        Assert.Contains("/admin", act.TabNote, StringComparison.Ordinal);
        Assert.Contains("only when one exists", act.SavedResultFallback, StringComparison.Ordinal);
        Assert.Contains("Do not present an old welfare result", act.SavedResultFallback, StringComparison.Ordinal);
        Assert.DoesNotContain("FRM-", act.Input, StringComparison.Ordinal);
        Assert.DoesNotContain("dispatchVet", act.TabNote, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossGovernmentCoordinateSession_ExplainsTheAssuranceWorkflow()
    {
        PresenterSession coordinate = LoadCatalog().Sessions
            .Single(session => session.Id == "cross-government-coordinate");

        Assert.Contains("Dossier", coordinate.TabNote, StringComparison.Ordinal);
        Assert.Contains("Deliberation", coordinate.TabNote, StringComparison.Ordinal);
        Assert.Contains("Assessment", coordinate.TabNote, StringComparison.Ordinal);
        Assert.Contains("dissent", coordinate.TabNote, StringComparison.Ordinal);
        Assert.Contains(
            "dossier-shared-ai-service.md",
            coordinate.Input,
            StringComparison.Ordinal);
        Assert.Contains(@"demo-dossiers\cross-government\", coordinate.Input, StringComparison.Ordinal);
    }

    [Fact]
    public void PatriotsCoordinateSession_UsesARepositorySpecificDossierNotTheAssuranceFile()
    {
        PresenterSessionCatalog catalog = LoadCatalog();
        PresenterSession coordinate = catalog.Sessions
            .Single(session => session.Id == "cross-government-coordinate");
        PresenterSession patriots = catalog.Sessions
            .Single(session => session.Id == "patriots-coordinate");

        Assert.NotEqual(coordinate.Input, patriots.Input);
        Assert.DoesNotContain(
            "dossier-shared-ai-service.md",
            patriots.Input,
            StringComparison.Ordinal);
        Assert.Contains(@"demo-dossiers\patriots\", patriots.Input, StringComparison.Ordinal);
        Assert.DoesNotContain("the same dossier", patriots.Proof, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostedSession_UsesTheFourCaseCrossGovernmentSequence()
    {
        PresenterSession hosted = LoadCatalog().Sessions.Single(session => session.Id == "hosted");

        Assert.Contains("CG-8101", hosted.Input, StringComparison.Ordinal);
        Assert.Contains("CG-8202", hosted.Input, StringComparison.Ordinal);
        Assert.Contains("CG-8303", hosted.Input, StringComparison.Ordinal);
        Assert.Contains("CG-8404", hosted.Input, StringComparison.Ordinal);
        Assert.Contains("Memory notebook", hosted.Input, StringComparison.Ordinal);
        Assert.Contains("Memory notebook", hosted.TabNote, StringComparison.Ordinal);
        Assert.Contains("matched", hosted.TabNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ruled-out", hosted.TabNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skipped", hosted.TabNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CG-8101", hosted.SavedResultFallback, StringComparison.Ordinal);
        Assert.Contains("CG-8202", hosted.SavedResultFallback, StringComparison.Ordinal);
        Assert.Contains("CG-8303", hosted.SavedResultFallback, StringComparison.Ordinal);
        Assert.Contains("CG-8404", hosted.SavedResultFallback, StringComparison.Ordinal);
        Assert.Contains("notebook", hosted.SavedResultFallback, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CP-4202", hosted.Input, StringComparison.Ordinal);
        Assert.DoesNotContain("CP-4202", hosted.SavedResultFallback, StringComparison.Ordinal);
        Assert.DoesNotContain("receipt", hosted.SavedResultFallback, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostedSession_ExposesThreeDistinctLinks()
    {
        PresenterSession hosted = LoadCatalog().Sessions.Single(session => session.Id == "hosted");

        Assert.Equal("Open application", hosted.LaunchLabel);
        Assert.NotNull(hosted.Links);
        Assert.Equal(2, hosted.Links!.Count);

        PresenterSessionLink playground = hosted.Links.Single(link => link.Kind == "playground");
        Assert.Equal("Open agent playground", playground.Label);
        Uri playgroundUri = new(playground.Url);
        Assert.Equal(Uri.UriSchemeHttps, playgroundUri.Scheme);
        Assert.Equal("ai.azure.com", playgroundUri.Host);

        PresenterSessionLink vsCode = hosted.Links.Single(link => link.Kind == "vscode");
        Assert.Equal("Open hosted-agent code in VS Code", vsCode.Label);
        Uri vsCodeUri = new(vsCode.Url);
        Assert.Equal("vscode", vsCodeUri.Scheme);
        Assert.Equal("file", vsCodeUri.Host);
        Assert.Matches("^/[A-Za-z]:/", vsCodeUri.AbsolutePath);
    }

    [Theory]
    [InlineData("info", "https://example.com/")]
    [InlineData("playground", "https://ai.azure.com/build/agents/x")]
    public void PresenterSessionLink_AcceptsSafeHttpsLinks(string kind, string url)
    {
        PresenterSessionLink link = new("Label", url, kind);

        link.Validate("hosted");
    }

    [Fact]
    public void PresenterSessionLink_RejectsNonHttpsForGenericAndPlaygroundKinds()
    {
        PresenterSessionLink generic = new("Label", "http://example.com/", "info");
        PresenterSessionLink playground = new("Label", "https://not-ai-azure.example/", "playground");

        Assert.Throws<InvalidDataException>(() => generic.Validate("hosted"));
        Assert.Throws<InvalidDataException>(() => playground.Validate("hosted"));
    }

    [Fact]
    public void PresenterSessionLink_AcceptsAWellFormedVsCodeUri()
    {
        PresenterSessionLink link = new(
            "Open hosted-agent code in VS Code",
            "vscode://file/C:/repository-root/src/PublicSectorAgentDemos.Demo4.HostedAgents",
            "vscode");

        link.Validate("hosted");
    }

    [Theory]
    [InlineData("vscode://folder/C:/repo")]
    [InlineData("vscode://file/repo")]
    [InlineData("vscode://user@file/C:/repo")]
    [InlineData("vscode://file/C:/repo?x=1")]
    [InlineData("vscode://file/C:/repo#top")]
    [InlineData("https://ai.azure.com/")]
    public void PresenterSessionLink_RejectsMalformedVsCodeUris(string url)
    {
        PresenterSessionLink link = new("Label", url, "vscode");

        Assert.Throws<InvalidDataException>(() => link.Validate("hosted"));
    }

    [Fact]
    public void PresenterSession_RejectsAVsCodeSchemeAsANormalLaunchUrl()
    {
        PresenterSession session = new(
            "hosted",
            "Hosted",
            "Proof.",
            "vscode://file/C:/repo",
            "Input.",
            "Tab note.",
            "Saved fallback.",
            true,
            new("demo4", "https://hosted.example/health", null));

        Assert.Throws<InvalidDataException>(session.Validate);
    }

    [Fact]
    public async Task Home_RendersOfflineWithCopyAndFallbackContent()
    {
        using WebApplicationFactory<Program> factory = new();
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new("http://localhost")
            });

        using HttpResponseMessage response = await client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(7, Count(html, "class=\"session-card\""));
        Assert.Equal(7, Count(html, "class=\"copy-input\""));
        Assert.Equal(7, Count(html, "class=\"fallback\""));
        Assert.Contains("data-copy-target=\"input-foundation\"", html, StringComparison.Ordinal);
        Assert.Contains(
            WebUtility.HtmlEncode(LoadCatalog().Sessions[0].Input),
            html,
            StringComparison.Ordinal);
        Assert.Contains("Saved-result fallback:", html, StringComparison.Ordinal);
        Assert.Equal(4 + 3 + 2, Count(html, "<a href="));
        Assert.Contains("Not configured.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:7081", html, StringComparison.Ordinal);
        Assert.Contains(">Open application<", html, StringComparison.Ordinal);
        Assert.Contains(">Open agent playground<", html, StringComparison.Ordinal);
        Assert.Contains(">Open hosted-agent code in VS Code<", html, StringComparison.Ordinal);
        Assert.Contains("href=\"vscode://file/", html, StringComparison.Ordinal);
        Assert.Contains("Optional extras", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("id=\"hosted\"", StringComparison.Ordinal) <
            html.IndexOf("id=\"extras-title\"", StringComparison.Ordinal));
        Assert.True(html.IndexOf("id=\"extras-title\"", StringComparison.Ordinal) <
            html.IndexOf("id=\"tokens-and-credits\"", StringComparison.Ordinal));
        Assert.Contains("id=\"warm-up-extra-results\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"http://localhost:5041", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TokensExtra_IsDistinctAndCannotReplaceAMainSession()
    {
        PresenterSessionCatalog catalog = LoadCatalog();
        PresenterSession tokens = Assert.Single(catalog.Extras!);
        Assert.Equal("tokens-and-credits", tokens.Id);
        Assert.True(tokens.Optional);
        Assert.False(tokens.Configured);
        Assert.Equal("not-configured", tokens.StartupStatus);
        Assert.Equal(6, catalog.Sessions.Count);
        Assert.Throws<InvalidDataException>(() =>
            (catalog with { Sessions = [.. catalog.Sessions, tokens], Extras = [] }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (catalog with { Extras = [tokens with { Optional = false }] }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (catalog with { Extras = [tokens, tokens] }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (catalog with { Extras = [catalog.Sessions[4]] }).Validate());
    }

    [Fact]
    public void TokensExtra_RendersStartupFailureWithoutClaimingReady()
    {
        PresenterSessionCatalog catalog = LoadCatalog();
        PresenterSession tokens = Assert.Single(catalog.Extras!) with { StartupStatus = "failed" };
        catalog = catalog with { Extras = [tokens] };
        catalog.Validate();

        string html = PresenterPage.Render(catalog);

        Assert.Contains("Startup failed.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"http://localhost:5041", html, StringComparison.Ordinal);
        Assert.Contains("id=\"foundation\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"hosted\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("foundry", "https://demo.services.ai.azure.com/openai/v1/responses", "model")]
    [InlineData("local-app", "http://localhost:5041/health", null)]
    [InlineData("tokens-local", "http://localhost:5041/api/models", null)]
    [InlineData("tokens-local", "http://localhost:5041/api/embeddings/manifest?cloud=true", null)]
    [InlineData("tokens-local", "https://example.com/api/embeddings/manifest", null)]
    [InlineData("tokens-local", "http://localhost:5041/api/embeddings/manifest", "model")]
    public void TokensExtra_RejectsModelRequestsAndOtherEndpoints(string kind, string endpoint, string? model)
    {
        PresenterWarmUpTarget target = new(kind, endpoint, model);

        Assert.Throws<InvalidDataException>(() => target.Validate("tokens-and-credits"));
    }

    [Fact]
    public void TokensExtra_ConfiguredTileRequiresReadyStatusAndTheLocalLaunchUrl()
    {
        PresenterSession tokens = Assert.Single(LoadCatalog().Extras!) with
        {
            Configured = true,
            StartupStatus = "ready",
            LaunchUrl = "http://localhost:5041/",
            WarmUp = new("tokens-local", "http://localhost:5041/api/embeddings/manifest", null)
        };
        tokens.Validate();
        Assert.Throws<InvalidDataException>(() => (tokens with { StartupStatus = "failed" }).Validate());
        Assert.Throws<InvalidDataException>(() => (tokens with { LaunchUrl = "https://example.com/" }).Validate());
        Assert.Throws<InvalidDataException>(() => (tokens with { Configured = false }).Validate());
    }

    private static PresenterSessionCatalog LoadCatalog() =>
        PresenterSessionCatalog.Load(
            Path.Combine(
                AppContext.BaseDirectory,
                "config",
                "presenter",
                "sessions.v1.json"));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PublicSectorAgentDemos.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;
}
