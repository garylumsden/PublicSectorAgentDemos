namespace PublicSectorAgentDemos.ArchitectureTests;

/// <summary>
/// Locks the exact Demo 4 authentication contract.
/// App Service EasyAuth v2 is the browser gate for every route except the health probe.
/// Microsoft Identity Web validates bearer tokens for the assessment and memory APIs.
/// </summary>
public sealed class UnifiedInfrastructureTests
{
    [Fact]
    public void Demo4EasyAuthRequiresAuthenticationAndRedirectsBrowsers()
    {
        string auth = ReadAuthSettings();

        Assert.Contains("requireAuthentication: true", auth, StringComparison.Ordinal);
        Assert.Contains(
            "unauthenticatedClientAction: 'RedirectToLoginPage'",
            auth,
            StringComparison.Ordinal);
        Assert.Contains(
            "redirectToProvider: 'azureactivedirectory'",
            auth,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AllowAnonymous", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("requireAuthentication: false", auth, StringComparison.Ordinal);
    }

    [Fact]
    public void Demo4EasyAuthLeavesOnlyTheHealthProbeAnonymous()
    {
        string resources = ReadResources();
        string anonymousPaths = Slice(
            resources,
            "var applicationAnonymousPaths",
            "var applicationAuthSettings");

        Assert.Contains("'/health'", anonymousPaths, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(anonymousPaths, "'/"));

        string auth = ReadAuthSettings();
        Assert.Contains(
            "excludedPaths: applicationAnonymousPaths",
            auth,
            StringComparison.Ordinal);
        foreach (string protectedPath in new[]
        {
            "'/'",
            "'/Memory'",
            "'/api",
            "'/css",
            "'/js",
            "'/.auth'"
        })
        {
            Assert.DoesNotContain(protectedPath, anonymousPaths, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Demo4EasyAuthUsesTheTenantSpecificVersionTwoIssuer()
    {
        string auth = ReadAuthSettings();

        Assert.Contains(
            "openIdIssuer: '${environment().authentication.loginEndpoint}" +
            "${tenant().tenantId}/v2.0'",
            auth,
            StringComparison.Ordinal);
        Assert.DoesNotContain("/common", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("/organizations", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("sts.windows.net", auth, StringComparison.Ordinal);
    }

    [Fact]
    public void Demo4EasyAuthAllowsOnlyTheApplicationAudiences()
    {
        string resources = ReadResources();
        string auth = ReadAuthSettings();

        Assert.Contains(
            "var applicationAudience = 'api://${applicationApiClientId}'",
            resources,
            StringComparison.Ordinal);
        Assert.Contains("clientId: applicationApiClientId", auth, StringComparison.Ordinal);

        string audiences = Slice(auth, "allowedAudiences: [", "]");
        Assert.Contains("applicationAudience", audiences, StringComparison.Ordinal);
        Assert.Contains("applicationApiClientId", audiences, StringComparison.Ordinal);
    }

    [Fact]
    public void Demo4EasyAuthRestrictsSignInToConfiguredPrincipals()
    {
        string auth = ReadAuthSettings();
        string main = Normalise(ReadMain());
        string resources = Normalise(ReadResources());

        Assert.Contains(
            "identities: allowedUserPrincipalIds",
            auth,
            StringComparison.Ordinal);
        Assert.Contains(
            "@minLength(1)\nparam allowedUserPrincipalIds array",
            resources,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "param allowedUserPrincipalIds array = []",
            resources,
            StringComparison.Ordinal);
        Assert.Contains(
            "deployerPrincipalId is required so Demo 4 sign-in stays restricted",
            main,
            StringComparison.Ordinal);
        Assert.Contains(
            "allowedUserPrincipalIds: [\n      validatedDeployerPrincipalId\n    ]",
            main,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Demo4EasyAuthRequiresHttpsTokenStoreAndNonceValidation()
    {
        string auth = ReadAuthSettings();
        string resources = ReadResources();

        Assert.Contains("requireHttps: true", auth, StringComparison.Ordinal);
        Assert.Contains(
            "enabled: true",
            Slice(auth, "tokenStore: {", "}"),
            StringComparison.Ordinal);
        Assert.Contains("validateNonce: true", auth, StringComparison.Ordinal);
        Assert.Contains("apiPrefix: '/.auth'", auth, StringComparison.Ordinal);
        Assert.Contains("httpsOnly: true", resources, StringComparison.Ordinal);
        Assert.Contains("minTlsVersion: '1.2'", resources, StringComparison.Ordinal);
        Assert.Contains("ftpsState: 'Disabled'", resources, StringComparison.Ordinal);
    }

    [Fact]
    public void Demo4EasyAuthBindsTheAuthSettingsToTheApplication()
    {
        string resources = ReadResources();

        Assert.Contains(
            "resource applicationAuth 'Microsoft.Web/sites/config@2024-11-01'",
            resources,
            StringComparison.Ordinal);
        Assert.Contains("parent: application", resources, StringComparison.Ordinal);
        Assert.Contains("name: 'authsettingsV2'", resources, StringComparison.Ordinal);
        Assert.Contains(
            "properties: applicationAuthSettings",
            resources,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Demo4ClientSecretStaysSecureAndIsNeverExported()
    {
        string resources = Normalise(ReadResources());
        string main = Normalise(ReadMain());

        Assert.Contains(
            "@secure()\n@minLength(1)\n@description('Client secret",
            resources,
            StringComparison.Ordinal);
        Assert.Contains(
            "clientSecretSettingName: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'",
            resources,
            StringComparison.Ordinal);
        Assert.Contains(
            "@secure()\n@description('Client secret",
            main,
            StringComparison.Ordinal);
        Assert.Contains(
            "applicationWebClientSecret is required for Demo 4 EasyAuth.",
            main,
            StringComparison.Ordinal);
        Assert.Contains(
            "applicationApiClientId is required for Demo 4 EasyAuth.",
            main,
            StringComparison.Ordinal);

        foreach (string line in resources.Split('\n')
            .Concat(main.Split('\n'))
            .Where(candidate =>
                candidate.TrimStart().StartsWith("output ", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("secret", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Demo4ApplicationReceivesTheBearerValidationSettings()
    {
        string resources = ReadResources();

        foreach (string setting in new[]
        {
            "AzureAd__Audience",
            "AzureAd__ClientId",
            "AzureAd__Instance",
            "AzureAd__TenantId"
        })
        {
            Assert.Contains(setting, resources, StringComparison.Ordinal);
        }

        Assert.Contains(
            "value: environment().authentication.loginEndpoint",
            resources,
            StringComparison.Ordinal);
        Assert.Contains("value: tenant().tenantId", resources, StringComparison.Ordinal);
    }

    [Fact]
    public void Demo4ApplicationValidatesEveryBearerTokenProperty()
    {
        string program = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "PublicSectorAgentDemos.Demo4.Application",
            "Program.cs"));

        Assert.Contains("AddMicrosoftIdentityWebApi", program, StringComparison.Ordinal);
        foreach (string requirement in new[]
        {
            "options.RequireHttpsMetadata = true;",
            "options.TokenValidationParameters.ValidateIssuer = true;",
            "options.TokenValidationParameters.ValidateIssuerSigningKey = true;",
            "options.TokenValidationParameters.ValidateAudience = true;",
            "options.TokenValidationParameters.ValidateLifetime = true;",
            "options.TokenValidationParameters.RequireSignedTokens = true;",
            "options.TokenValidationParameters.RequireExpirationTime = true;"
        })
        {
            Assert.Contains(requirement, program, StringComparison.Ordinal);
        }

        Assert.Contains(
            "\"/api/case-pattern-assessments\"",
            program,
            StringComparison.Ordinal);
        Assert.Contains(".RequireAuthorization();", program, StringComparison.Ordinal);
        Assert.Contains("app.UseAuthentication();", program, StringComparison.Ordinal);
        Assert.Contains("app.UseAuthorization();", program, StringComparison.Ordinal);
        Assert.Contains("app.UseHttpsRedirection();", program, StringComparison.Ordinal);
        Assert.Contains(".AllowAnonymous();", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Demo4MemoryNotebookStaysReadOnly()
    {
        string page = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "PublicSectorAgentDemos.Demo4.Application",
            "Pages",
            "Memory.cshtml.cs"));

        Assert.Contains("public async Task OnGetAsync(", page, StringComparison.Ordinal);
        Assert.DoesNotContain("OnPost", page, StringComparison.Ordinal);
        Assert.DoesNotContain("OnPut", page, StringComparison.Ordinal);
        Assert.DoesNotContain("OnDelete", page, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateAndVerifyAsync", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Demo4EntraLifecycleKeepsTheExactCallbackAndBoundedCredential()
    {
        string entra = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "DemoReady",
            "Entra.ps1"));

        Assert.Contains("requestedAccessTokenVersion = 2", entra, StringComparison.Ordinal);
        Assert.Contains(
            "'--enable-id-token-issuance', 'true'",
            entra,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://$ApplicationName.azurewebsites.net/.auth/login/aad/callback",
            entra,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ValidateRange(1, 90)][int]$LifetimeDays = 28",
            entra,
            StringComparison.Ordinal);
        Assert.Contains("Test-DemoReadyCredentialExpiry", entra, StringComparison.Ordinal);
        Assert.DoesNotContain("-LogPath", entra, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host", entra, StringComparison.Ordinal);
    }

    [Fact]
    public void Demo4InfrastructureUsesNoSyntheticLabel()
    {
        string root = FindRepositoryRoot();
        foreach (string path in Directory.EnumerateFiles(
            Path.Combine(root, "infra", "demo4"),
            "*",
            SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(
                "synthetic",
                File.ReadAllText(path),
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(
            "synthetic",
            File.ReadAllText(Path.Combine(root, "scripts", "DemoReady", "Entra.ps1")),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadResources() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "infra",
        "demo4",
        "resources.bicep"));

    private static string ReadMain() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "infra",
        "demo4",
        "main.bicep"));

    private static string ReadAuthSettings() => Slice(
        ReadResources(),
        "var applicationAuthSettings",
        "resource foundryAccount");

    private static string Normalise(string source) =>
        source.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Slice(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"'{start}' was not found.");
        int endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"'{end}' was not found after '{start}'.");
        return source[startIndex..endIndex];
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

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
}
