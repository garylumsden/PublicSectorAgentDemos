using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using PublicSectorAgentDemos.Demo1.FoundationAndGround;

namespace PublicSectorAgentDemos.ContractTests;

public sealed class Demo1FoundationGroundTests
{
    [Fact]
    public void DefinitionsUseOneCanonicalBaseWithExactOfflineParity()
    {
        string root = FindRepositoryRoot();
        Demo1AgentCatalogDocument catalog = Demo1AgentCatalog.Load(root);
        ResolvedAgentDefinition foundation = Demo1AgentCatalog.Resolve(root, catalog, "foundation");
        ResolvedAgentDefinition ground = Demo1AgentCatalog.Resolve(root, catalog, "ground");

        Assert.Equal(foundation.CanonicalBaseInstructions, ground.CanonicalBaseInstructions);
        Assert.Equal(foundation.Definition.Model, ground.Definition.Model);
        Assert.Equal("low", foundation.Definition.ReasoningEffort);
        Assert.Equal("low", ground.Definition.ReasoningEffort);
        Assert.Equal(foundation.CanonicalBaseInstructions, foundation.Instructions);
        Assert.StartsWith(
            foundation.CanonicalBaseInstructions.TrimEnd(),
            ground.Instructions,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FoundationHasNoKnowledgeOrRetrievalClauses()
    {
        string root = FindRepositoryRoot();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "config",
            "ground",
            "agent-definitions.v1.json")));
        JsonElement foundation = document.RootElement.GetProperty("definitions")[0];
        string[] forbiddenProperties = ["foundryIq", "knowledge", "retrieval", "tools", "citations"];
        Assert.All(forbiddenProperties, property => Assert.False(foundation.TryGetProperty(property, out _)));

        Demo1AgentCatalogDocument catalog = Demo1AgentCatalog.Load(root);
        string instructions = Demo1AgentCatalog.Resolve(root, catalog, "foundation").Instructions;
        Assert.DoesNotContain("knowledge", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("retriev", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("citation", instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GroundPinsFoundryIqAndFailsClosedWithoutApprovedCitations()
    {
        Demo1AgentCatalogDocument catalog = Demo1AgentCatalog.Load(FindRepositoryRoot());
        Demo1FoundryIqDefinition iq = catalog.Definitions
            .Single(item => item.Stage == "ground")
            .FoundryIq!;

        Assert.Equal("managing-public-money-kb-v1", iq.KnowledgeBaseName);
        Assert.Equal("managing-public-money-govuk-v1", iq.KnowledgeSourceName);
        Assert.Equal("2026-05-01-preview", iq.ApiVersion);
        Assert.Equal("minimal", iq.RetrievalReasoningEffort);
        Assert.True(iq.Required);
        Assert.True(iq.FailClosed);
        Assert.True(iq.CitationsRequired);
        string groundInstructions =
            Demo1AgentCatalog.Resolve(FindRepositoryRoot(), catalog, "ground").Instructions;
        Assert.Contains(
            $"one unmodified `output_text` part containing: `{GroundedResponseGate.FailureMessage}`",
            groundInstructions,
            StringComparison.Ordinal);
        Assert.Contains("copied unchanged from the retrieval output", groundInstructions, StringComparison.Ordinal);
        Assert.Contains("Markdown blockquotes", groundInstructions, StringComparison.Ordinal);
        Assert.Contains("4 to 20 consecutive source words", groundInstructions, StringComparison.Ordinal);
        Assert.Contains("Never use the opening fragment of a longer rule", groundInstructions, StringComparison.Ordinal);
        Assert.Contains("Include the qualifier and its condition", groundInstructions, StringComparison.Ordinal);
        Assert.Contains("`Application`", groundInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Then use `RisksAndUnknowns`",
            groundInstructions,
            StringComparison.Ordinal);

        GroundedResponseDecision noRetrieval = GroundedResponseGate.Evaluate(
            new(false, false, "An unsupported answer.", []));
        GroundedResponseDecision wrongSource = GroundedResponseGate.Evaluate(
            new(true, true, "An unsupported answer.", [new("other-source", "[1]")]));
        GroundedResponseDecision valid = GroundedResponseGate.Evaluate(
            new(
                true,
                true,
                "A grounded answer.",
                [new(Demo1AgentCatalog.ApprovedSourceId, "[1]")]));
        GroundedResponseDecision refusal = GroundedResponseGate.Evaluate(
            new(
                false,
                false,
                GroundedResponseGate.UnsupportedRefusalMessage,
                [],
                true));
        GroundedResponseDecision citedRefusal = GroundedResponseGate.Evaluate(
            new(
                false,
                false,
                GroundedResponseGate.UnsupportedRefusalMessage,
                [new(Demo1AgentCatalog.ApprovedSourceId, "[1]")],
                true));

        Assert.False(noRetrieval.CanAnswer);
        Assert.Equal(GroundedResponseGate.FailureMessage, noRetrieval.Answer);
        Assert.False(wrongSource.CanAnswer);
        Assert.True(valid.CanAnswer);
        Assert.False(refusal.CanAnswer);
        Assert.True(refusal.IsExplicitRefusal);
        Assert.Equal(GroundedResponseGate.UnsupportedRefusalMessage, refusal.Answer);
        Assert.False(citedRefusal.IsExplicitRefusal);
        Assert.Equal(GroundedResponseGate.FailureMessage, citedRefusal.Answer);
    }

    [Fact]
    public void ManagingPublicMoneyCorpusMatchesPinnedGovUkSha256()
    {
        string root = FindRepositoryRoot();
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "data",
            "ground",
            "v1",
            "source-manifest.json")));
        JsonElement source = Assert.Single(manifest.RootElement.GetProperty("sources").EnumerateArray());
        string filename = source.GetProperty("expectedLocalFilename").GetString()!;
        string path = Path.Combine(root, "data", "ground", "v1", "knowledge", filename);
        string checksum = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

        Assert.Equal("www.gov.uk", new Uri(source.GetProperty("officialUrl").GetString()!).Host);
        Assert.Equal("assets.publishing.service.gov.uk", new Uri(source.GetProperty("downloadUrl").GetString()!).Host);
        Assert.Equal("SHA-256", source.GetProperty("checksumAlgorithm").GetString());
        Assert.Equal("managing-public-money-v1", source.GetProperty("blobContainerName").GetString());
        Assert.Equal(
            "managing-public-money-govuk-v1",
            source.GetProperty("knowledgeSourceName").GetString());
        Assert.Equal(Demo1AgentCatalog.PinnedChecksum, checksum);
        Assert.Equal(source.GetProperty("expectedChecksum").GetString(), checksum);
    }

    [Fact]
    public void ResponseFixturesMatchInstalledFileAndUrlCitationSchemas()
    {
        string fixtureRoot = Path.Combine(
            FindRepositoryRoot(),
            "data",
            "ground",
            "v1",
            "response-fixtures");
        ApprovedCitationSource source = CreateApprovedCitationSource();

        FoundryResponseCitationResult fileResult = FoundryResponseCitationValidator.Inspect(
            File.ReadAllText(Path.Combine(fixtureRoot, "file-citation.json")),
            source);
        FoundryCitationAnnotation fileCitation = Assert.Single(fileResult.Citations);
        Assert.Equal("file_citation", fileCitation.Type);
        Assert.Equal(0, fileCitation.FileListIndex);
        Assert.Null(fileCitation.StartIndex);
        Assert.Null(fileCitation.EndIndex);
        Assert.Equal(1, Assert.Single(fileResult.MaterialUnits).CitationMarkerNumber);
        Assert.True(fileResult.AllCitationsAreValid);
        Assert.True(fileResult.AllMaterialUnitsHaveValidCitations);

        FoundryResponseCitationResult urlResult = FoundryResponseCitationValidator.Inspect(
            File.ReadAllText(Path.Combine(fixtureRoot, "url-citation.json")),
            source);
        FoundryCitationAnnotation urlCitation = Assert.Single(urlResult.Citations);
        Assert.Equal("url_citation", urlCitation.Type);
        Assert.Equal(38, urlCitation.StartIndex);
        Assert.Equal(41, urlCitation.EndIndex);
        Assert.Null(urlCitation.FileListIndex);
        Assert.Equal(1, Assert.Single(urlResult.MaterialUnits).CitationMarkerNumber);
        Assert.True(urlResult.AllCitationsAreValid);
        Assert.True(urlResult.AllMaterialUnitsHaveValidCitations);
    }

    [Fact]
    public void FoundryCitationInspectionAcceptsServiceUrlMarkerOnTheSameLine()
    {
        ApprovedCitationSource source = CreateApprovedCitationSource();
        string response = """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "Claim one source [1]",
                      "annotations": [
                        {
                          "type": "url_citation",
                          "start_index": 9,
                          "end_index": 16,
                          "url": "https://www.gov.uk/government/publications/managing-public-money",
                          "title": "Managing Public Money"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        FoundryResponseCitationResult result =
            FoundryResponseCitationValidator.Inspect(response, source);

        Assert.True(result.AllCitationsAreValid);
        Assert.True(Assert.Single(result.MaterialUnits).HasValidCitation);
        Assert.True(result.AllMaterialUnitsHaveValidCitations);
    }

    [Fact]
    public void FoundryCitationInspectionRejectsAnUnmarkedExtraClaim()
    {
        ApprovedCitationSource source = CreateApprovedCitationSource();
        string response = """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "Assessment\nClaim one [1]\nClaim two",
                      "annotations": [
                        {
                          "type": "url_citation",
                          "start_index": 21,
                          "end_index": 24,
                          "url": "https://www.gov.uk/government/publications/managing-public-money",
                          "title": "Managing Public Money"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        FoundryResponseCitationResult result =
            FoundryResponseCitationValidator.Inspect(response, source);

        Assert.True(result.AllCitationsAreValid);
        Assert.Equal(2, result.MaterialUnits.Count);
        Assert.Single(result.MaterialUnits, unit => unit.HasValidCitation);
        Assert.Single(result.MaterialUnits, unit => !unit.HasValidCitation);
        Assert.False(result.AllMaterialUnitsHaveValidCitations);
    }

    [Theory]
    [InlineData("https://www.gov.uk:444/government/publications/managing-public-money")]
    [InlineData("https://www.gov.uk/government/publications/managing-public-money?view=full")]
    [InlineData("https://www.gov.uk/government/publications/managing-public-money#details")]
    [InlineData("https://www.gov.uk/government/publications/not-managing-public-money")]
    public void FoundryCitationInspectionRequiresTheExactFullApprovedUri(string uri)
    {
        ApprovedCitationSource source = CreateApprovedCitationSource();
        string response = $$"""
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "Assessment: Claim [1]",
                      "annotations": [
                        {
                          "type": "url_citation",
                          "start_index": 18,
                          "end_index": 21,
                          "url": "{{uri}}",
                          "title": "Wrong publication"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        FoundryResponseCitationResult result =
            FoundryResponseCitationValidator.Inspect(response, source);

        FoundryCitationAnnotation citation = Assert.Single(result.Citations);
        Assert.False(citation.ResolvesToApprovedSource);
        Assert.False(result.AllCitationsAreValid);
        Assert.False(result.AllMaterialUnitsHaveValidCitations);
    }

    [Fact]
    public void FoundryCitationInspectionRejectsFileIdOrFilenameOutsideAllowlist()
    {
        ApprovedCitationSource source = CreateApprovedCitationSource();
        string response = """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "Claim one [1]",
                      "annotations": [
                        {
                          "type": "file_citation",
                          "file_id": "wrong-file",
                          "index": 0,
                          "filename": "managing-public-money-april-2026.pdf"
                        }
                      ]
                    },
                    {
                      "type": "output_text",
                      "text": "Claim two [1]",
                      "annotations": [
                        {
                          "type": "file_citation",
                          "file_id": "file-mpm-001",
                          "index": 0,
                          "filename": "different.pdf"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        FoundryResponseCitationResult result =
            FoundryResponseCitationValidator.Inspect(response, source);

        Assert.Equal(2, result.Citations.Count);
        Assert.All(result.Citations, citation => Assert.False(citation.ResolvesToApprovedSource));
        Assert.False(result.AllCitationsAreValid);
        Assert.False(result.AllMaterialUnitsHaveValidCitations);
    }

    [Fact]
    public void FileCitationIndexSelectsTheFileListEntryNotACharacterOffset()
    {
        ApprovedCitationSource source = CreateApprovedCitationSource();
        string response = """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "Claim [2]",
                      "annotations": [
                        {
                          "type": "file_citation",
                          "file_id": "file-mpm-001",
                          "index": 0,
                          "filename": "managing-public-money-april-2026.pdf"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        FoundryResponseCitationResult result =
            FoundryResponseCitationValidator.Inspect(response, source);

        FoundryCitationAnnotation citation = Assert.Single(result.Citations);
        Assert.Equal(0, citation.FileListIndex);
        Assert.Null(citation.StartIndex);
        Assert.Null(citation.EndIndex);
        Assert.True(result.AllCitationsAreValid);
        Assert.False(result.AllMaterialUnitsHaveValidCitations);
    }

    [Fact]
    public void RetrievalFailureIsExemptOnlyAsTheExactSingleMessage()
    {
        string fixtureRoot = Path.Combine(
            FindRepositoryRoot(),
            "data",
            "ground",
            "v1",
            "response-fixtures");
        ApprovedCitationSource source = CreateApprovedCitationSource();
        FoundryResponseCitationResult exact = FoundryResponseCitationValidator.Inspect(
            File.ReadAllText(Path.Combine(fixtureRoot, "retrieval-failure.json")),
            source);

        Assert.Equal(GroundedResponseGate.FailureMessage, exact.Text);
        Assert.True(exact.IsExactExemptMessage);
        Assert.Empty(exact.MaterialUnits);
        Assert.Empty(exact.Citations);

        string[] invalidResponses =
        [
            CreateOutputResponse($"{GroundedResponseGate.FailureMessage} "),
            $$"""
              {
                "output": [
                  {
                    "type": "message",
                    "content": [
                      {
                        "type": "output_text",
                        "text": "{{GroundedResponseGate.FailureMessage}}",
                        "annotations": []
                      },
                      {
                        "type": "output_text",
                        "text": "",
                        "annotations": []
                      }
                    ]
                  }
                ]
              }
              """,
            $$"""
              {
                "output": [
                  {
                    "type": "message",
                    "content": [
                      {
                        "type": "output_text",
                        "text": "{{GroundedResponseGate.FailureMessage}}",
                        "annotations": [
                          {
                            "type": "file_citation",
                            "file_id": "file-mpm-001",
                            "index": 0,
                            "filename": "managing-public-money-april-2026.pdf"
                          }
                        ]
                      }
                    ]
                  }
                ]
              }
              """,
            $$"""
              {
                "output": [
                  {
                    "type": "message",
                    "content": [
                      {
                        "type": "output_text",
                        "text": "{{GroundedResponseGate.FailureMessage}}",
                        "annotations": []
                      }
                    ]
                  },
                  {
                    "type": "mcp_call",
                    "name": "knowledge_base_retrieve"
                  }
                ]
              }
              """
        ];

        Assert.All(
            invalidResponses,
            response =>
            {
                FoundryResponseCitationResult result =
                    FoundryResponseCitationValidator.Inspect(response, source);
                Assert.False(result.IsExactExemptMessage);
                GroundedResponseDecision decision =
                    GroundedResponseGate.EvaluateLiveResponse(false, result);
                Assert.Equal(GroundedResponseGate.FailureMessage, decision.Answer);
                Assert.False(decision.CanAnswer);
            });
    }

    [Fact]
    public void ReasoningMayPrecedeAnExactRefusal()
    {
        ApprovedCitationSource source = CreateApprovedCitationSource();
        string response = $$"""
            {
              "output": [
                {
                  "type": "reasoning",
                  "id": "reasoning-1",
                  "summary": []
                },
                {
                  "type": "message",
                  "role": "assistant",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "{{GroundedResponseGate.UnsupportedRefusalMessage}}",
                      "annotations": []
                    }
                  ]
                }
              ]
            }
            """;

        FoundryResponseCitationResult result =
            FoundryResponseCitationValidator.Inspect(response, source);
        GroundedResponseDecision decision =
            GroundedResponseGate.EvaluateLiveResponse(false, result);

        Assert.True(result.IsExactExemptMessage);
        Assert.Empty(result.MaterialUnits);
        Assert.True(decision.IsExplicitRefusal);
        Assert.Equal(GroundedResponseGate.UnsupportedRefusalMessage, decision.Answer);
    }

    [Fact]
    public void McpToolDiscoveryMayPrecedeAnExactRefusal()
    {
        string response = JsonSerializer.Serialize(new
        {
            output = new object[]
            {
                new { type = "mcp_list_tools", tools = Array.Empty<object>() },
                new { type = "reasoning", summary = Array.Empty<object>() },
                new
                {
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = GroundedResponseGate.UnsupportedRefusalMessage,
                            annotations = Array.Empty<object>()
                        }
                    }
                }
            }
        });

        FoundryResponseCitationResult result =
            FoundryResponseCitationValidator.Inspect(response, CreateApprovedCitationSource());
        GroundedResponseDecision decision =
            GroundedResponseGate.EvaluateLiveResponse(false, result);

        Assert.True(result.IsExactExemptMessage);
        Assert.True(decision.IsExplicitRefusal);
    }

    [Theory]
    [InlineData("extra-message")]
    [InlineData("tool-call")]
    [InlineData("extra-content")]
    public void ReasoningDoesNotExemptAdditionalOutput(string extraKind)
    {
        object extra = extraKind switch
        {
            "extra-message" => new
            {
                type = "message",
                role = "assistant",
                content = new[] { new { type = "output_text", text = "Extra.", annotations = Array.Empty<object>() } }
            },
            "tool-call" => new
            {
                type = "mcp_call",
                name = "knowledge_base_retrieve",
                arguments = "{}"
            },
            _ => new
            {
                type = "message",
                role = "assistant",
                content = new object[]
                {
                    new
                    {
                        type = "output_text",
                        text = GroundedResponseGate.UnsupportedRefusalMessage,
                        annotations = Array.Empty<object>()
                    },
                    new { type = "refusal", refusal = "Extra." }
                }
            }
        };
        object[] output = extraKind == "extra-content"
            ? [new { type = "reasoning", summary = Array.Empty<object>() }, extra]
            :
            [
                new { type = "reasoning", summary = Array.Empty<object>() },
                new
                {
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = GroundedResponseGate.UnsupportedRefusalMessage,
                            annotations = Array.Empty<object>()
                        }
                    }
                },
                extra
            ];
        string response = JsonSerializer.Serialize(new { output });

        FoundryResponseCitationResult result =
            FoundryResponseCitationValidator.Inspect(response, CreateApprovedCitationSource());
        GroundedResponseDecision decision =
            GroundedResponseGate.EvaluateLiveResponse(false, result);

        Assert.False(result.IsExactExemptMessage);
        Assert.False(decision.IsExplicitRefusal);
        Assert.Equal(GroundedResponseGate.FailureMessage, decision.Answer);
    }

    [Theory]
    [InlineData("first-create")]
    [InlineData("rerun-update-version")]
    [InlineData("partial-recovery")]
    [InlineData("transient-probe-retry")]
    public async Task AgentPublisherPassesMockedRestScenarios(string testCase)
    {
        string root = FindRepositoryRoot();
        string script = Path.Combine(
            root,
            "tests",
            "PublicSectorAgentDemos.ContractTests",
            "PowerShell",
            "Demo1AgentPublisher.Tests.ps1");
        ProcessStartInfo startInfo = new()
        {
            FileName = "pwsh",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-Case");
        startInfo.ArgumentList.Add(testCase);
        startInfo.ArgumentList.Add("-RepositoryRoot");
        startInfo.ArgumentList.Add(root);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell did not start.");
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(
            process.ExitCode == 0,
            $"Mocked REST case '{testCase}' failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        Assert.Contains($"Passed {testCase}.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveResponseGateReturnsOnlyGroundedOrExactFailClosedOutput()
    {
        ApprovedCitationSource source = CreateApprovedCitationSource();
        FoundryResponseCitationResult valid = FoundryResponseCitationValidator.Inspect(
            CreateQuotedResponse(),
            source);

        GroundedResponseDecision accepted =
            GroundedResponseGate.EvaluateLiveResponse(true, valid);
        GroundedResponseDecision rejected =
            GroundedResponseGate.EvaluateLiveResponse(false, valid);

        Assert.True(accepted.CanAnswer);
        Assert.Equal(valid.Text, accepted.Answer);
        Assert.False(rejected.CanAnswer);
        Assert.Equal(GroundedResponseGate.FailureMessage, rejected.Answer);
    }

    [Theory]
    [InlineData("Treasury approval is not essential.")]
    [InlineData("\"Treasury approval is essential.\"")]
    [InlineData("Treasury approval ... is essential.")]
    public void GroundRejectsEditedQuotationsEvenWithApprovedCitations(string editedQuote)
    {
        FoundryResponseCitationResult result = FoundryResponseCitationValidator.Inspect(
            CreateQuotedResponse(firstQuote: editedQuote), CreateApprovedCitationSource());
        Assert.True(result.AllCitationsAreValid);
        Assert.False(result.HasVerifiedQuotations);
        Assert.False(GroundedResponseGate.EvaluateLiveResponse(true, result).CanAnswer);
    }

    [Fact]
    public void GroundRejectsMalformedRetrievalMetadataWithoutThrowing()
    {
        string response = CreateQuotedResponse().Replace(
            "\"status\":\"completed\"", "\"status\":42", StringComparison.Ordinal);
        FoundryResponseCitationResult result =
            FoundryResponseCitationValidator.Inspect(response, CreateApprovedCitationSource());
        Assert.False(GroundedResponseGate.EvaluateLiveResponse(true, result).CanAnswer);
    }

    [Fact]
    public void GroundRejectsAQuotationCutBeforeItsQualifier()
    {
        FoundryResponseCitationResult result = FoundryResponseCitationValidator.Inspect(
            CreateQuotedResponse(firstQuote: "Letters of comfort, however vague, give rise"),
            CreateApprovedCitationSource());
        SourceQuotation shortened = result.Quotations[0];
        Assert.True(shortened.IsVerbatimRetrievedExcerpt);
        Assert.False(shortened.HasSourceBoundary);
        Assert.False(GroundedResponseGate.EvaluateLiveResponse(true, result).CanAnswer);
    }

    [Fact]
    public void GroundRequiresBothRetrievedQuoteIdentityAndApplication()
    {
        foreach (string response in new[]
        {
            CreateQuotedResponse(retrievalUrl: "https://example.org/other.pdf"),
            CreateQuotedResponse(includeApplication: false),
            CreateQuotedResponse().Replace("\"completed\"", "\"failed\"", StringComparison.Ordinal),
            CreateQuotedResponse().Replace("\\u003E ", "", StringComparison.Ordinal)
        })
        {
            FoundryResponseCitationResult result = FoundryResponseCitationValidator.Inspect(
                response, CreateApprovedCitationSource());
            Assert.False(GroundedResponseGate.EvaluateLiveResponse(true, result).CanAnswer);
        }
    }

    [Fact]
    public void FoundryCitationInspectionRejectsUnsupportedOutputAnnotations()
    {
        ApprovedCitationSource source = CreateApprovedCitationSource();
        string response = """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "Unsupported claim [1]",
                      "annotations": [
                        {
                          "type": "file_citation",
                          "file_id": "file-mpm-001",
                          "index": 0,
                          "filename": "managing-public-money-april-2026.pdf"
                        },
                        {
                          "type": "highlight"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        FoundryResponseCitationResult result =
            FoundryResponseCitationValidator.Inspect(response, source);

        FoundryCitationAnnotation citation = Assert.Single(result.Citations);
        Assert.True(citation.IsAttachedToOutputText);
        Assert.True(citation.ResolvesToApprovedSource);
        Assert.True(result.HasUnsupportedAnnotations);
        Assert.False(result.AllCitationsAreValid);
        Assert.True(result.AllMaterialUnitsHaveValidCitations);
        Assert.Equal(
            GroundedResponseGate.FailureMessage,
            GroundedResponseGate.EvaluateLiveResponse(true, result).Answer);
    }

    [Fact]
    public void FixturesContainControlScenariosAndEvidenceDependentComparison()
    {
        string root = FindRepositoryRoot();
        using JsonDocument fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "data",
            "ground",
            "v1",
            "fixture-set.json")));
        using JsonDocument schedule = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "data",
            "ground",
            "v1",
            "delegation-schedule.json")));

        Assert.Equal("v1", fixtures.RootElement.GetProperty("dataVersion").GetString());
        Assert.Equal(5, fixtures.RootElement.GetProperty("fixtures").GetArrayLength());
        Assert.Equal(
            5,
            fixtures.RootElement.GetProperty("fixtures").EnumerateArray()
                .Select(item => item.GetProperty("scenarioId").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            ["delegated", "treasury-consent", "accounting-officer-escalation", "refuse-unsupported", "treasury-consent"],
            fixtures.RootElement.GetProperty("fixtures").EnumerateArray()
                .Select(item => item.GetProperty("expectedRoute").GetString()));
        JsonElement refusal = fixtures.RootElement.GetProperty("fixtures")[3];
        Assert.Equal("refusal", refusal.GetProperty("expectedResponseKind").GetString());
        Assert.False(refusal.GetProperty("requiresGrounding").GetBoolean());
        Assert.False(refusal.GetProperty("requiresCitations").GetBoolean());
        Assert.Equal(
            GroundedResponseGate.UnsupportedRefusalMessage,
            refusal.GetProperty("expectedRouteMarker").GetString());
        Assert.Equal("v1", schedule.RootElement.GetProperty("dataVersion").GetString());
        Assert.NotEmpty(schedule.RootElement.GetProperty("delegations").EnumerateArray());
        Assert.Contains("demonstration data", schedule.RootElement.GetProperty("notice").GetString()!, StringComparison.OrdinalIgnoreCase);
        JsonElement comparison = fixtures.RootElement.GetProperty("fixtures")[4];
        Assert.Equal("MPM-005", comparison.GetProperty("scenarioId").GetString());
        Assert.Equal(5, comparison.GetProperty("reviewRubric").GetArrayLength());
    }

    [Fact]
    public void InfrastructureIsOptInAndPinsFoundryIqWithoutLocalKeys()
    {
        string root = FindRepositoryRoot();
        string module = File.ReadAllText(Path.Combine(root, "infra", "demo1-foundation-ground.bicep"));

        Assert.Contains("knowledgeRetrieval: 'standard'", module, StringComparison.Ordinal);
        Assert.Contains("managing-public-money-kb-v1", module, StringComparison.Ordinal);
        Assert.Contains("managing-public-money-govuk-v1", module, StringComparison.Ordinal);
        Assert.Contains("authType: 'ProjectManagedIdentity'", module, StringComparison.Ordinal);
        Assert.Contains("disableLocalAuth: true", module, StringComparison.Ordinal);
        Assert.Contains("allowSharedKeyAccess: false", module, StringComparison.Ordinal);
        Assert.Contains("storageBlobDataReaderRoleId", module, StringComparison.Ordinal);
        Assert.Contains("scope: corpusContainer", module, StringComparison.Ordinal);
        Assert.Contains("principalId: search.identity.principalId", module, StringComparison.Ordinal);
        Assert.Contains("output storageAccountBlobHost string", module, StringComparison.Ordinal);
        Assert.Contains("output knowledgeSourceName string", module, StringComparison.Ordinal);

        string standalone = File.ReadAllText(Path.Combine(root, "infra", "demo1-main.bicep"));
        string standaloneResources = File.ReadAllText(Path.Combine(
            root,
            "infra",
            "demo1-standalone-resources.bicep"));
        string standaloneAzd = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "demo1",
            "azure.yaml"));
        Assert.Contains("demo1-standalone-resources.bicep", standalone, StringComparison.Ordinal);
        Assert.DoesNotContain("module resources 'resources.bicep'", standalone, StringComparison.Ordinal);
        Assert.DoesNotContain("Coordinate", standalone, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kind: 'AIServices'", standaloneResources, StringComparison.Ordinal);
        Assert.Contains("demo1-foundation-ground.bicep", standaloneResources, StringComparison.Ordinal);
        Assert.Contains("module: demo1-main", standaloneAzd, StringComparison.Ordinal);
        Assert.Contains("DEMO1_STORAGE_ACCOUNT_BLOB_HOST", standalone, StringComparison.Ordinal);
        Assert.Contains("DEMO1_KNOWLEDGE_SOURCE_NAME", standalone, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvisioningAndRehearsalScriptsAreExplicitlyOptIn()
    {
        string root = FindRepositoryRoot();
        string provisioning = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "ground",
            "Publish-Demo1Agents.ps1"));
        string rehearsal = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "ground",
            "Invoke-Rehearsal.ps1"));
        string liveClient = File.ReadAllText(Path.Combine(
            root,
            "tests",
            "PublicSectorAgentDemos.Demo1.CloudIntegrationTests",
            "LiveAgentParityTests.cs"));

        Assert.Contains("RUN_DEMO1_CLOUD_INTEGRATION", provisioning, StringComparison.Ordinal);
        Assert.Contains("Authorization = \"Bearer $searchToken\"", provisioning, StringComparison.Ordinal);
        Assert.Contains("Authorization = \"Bearer $foundryToken\"", provisioning, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization = \"******\"", provisioning, StringComparison.Ordinal);
        Assert.Contains("knowledgesources('$escapedName')/status", provisioning, StringComparison.Ordinal);
        Assert.Contains("itemsUpdatesFailed", provisioning, StringComparison.Ordinal);
        Assert.Contains("currentSynchronizationState", provisioning, StringComparison.Ordinal);
        Assert.Contains("lastSynchronizationState", provisioning, StringComparison.Ordinal);
        Assert.Contains("timed out after $TimeoutSeconds", provisioning, StringComparison.Ordinal);
        Assert.Contains("DEMO1_STORAGE_ACCOUNT_BLOB_HOST", provisioning, StringComparison.Ordinal);
        Assert.Contains("DEMO1_KNOWLEDGE_SOURCE_NAME", provisioning, StringComparison.Ordinal);
        Assert.Contains("$deployedKnowledgeSourceName", provisioning, StringComparison.Ordinal);
        Assert.Contains("Get-PinnedFileCitationIds", provisioning, StringComparison.Ordinal);
        Assert.Contains("DEMO1_CITATION_IDENTITY_PATH", provisioning, StringComparison.Ordinal);
        Assert.Contains(".azure\\demo1-citation-identity.json", provisioning, StringComparison.Ordinal);
        Assert.Contains("file_citation", provisioning, StringComparison.Ordinal);
        Assert.Contains("file_id", provisioning, StringComparison.Ordinal);
        Assert.Contains("filename", provisioning, StringComparison.Ordinal);
        Assert.Contains("knowledge_base_retrieve", provisioning, StringComparison.Ordinal);
        Assert.Contains("retrievalReasoningEffort = @{ kind = [string]$iq.retrievalReasoningEffort }", provisioning, StringComparison.Ordinal);
        Assert.Contains("reasoning = @{", provisioning, StringComparison.Ordinal);
        Assert.Contains(
            "effort = [string]$definition.reasoningEffort",
            provisioning,
            StringComparison.Ordinal);
        Assert.Contains("tool_choice = 'required'", provisioning, StringComparison.Ordinal);
        Assert.DoesNotContain("$bodyDefinition['tool_choice']", provisioning, StringComparison.Ordinal);
        Assert.Contains(
            "Search service returned an unexpected knowledge-source identifier",
            provisioning,
            StringComparison.Ordinal);
        int sourcePut = provisioning.IndexOf(
            "-Uri \"$searchUri/knowledgesources/$($iq.knowledgeSourceName)",
            StringComparison.Ordinal);
        int ingestionWait = provisioning.LastIndexOf(
            "Wait-KnowledgeSourceIngestion `",
            StringComparison.Ordinal);
        int knowledgeBase = provisioning.IndexOf("$knowledgeBasePayload", StringComparison.Ordinal);
        int agents = provisioning.IndexOf("foreach ($definition", StringComparison.Ordinal);
        Assert.True(sourcePut >= 0);
        Assert.True(sourcePut < ingestionWait);
        Assert.True(ingestionWait < knowledgeBase);
        Assert.True(knowledgeBase < agents);
        Assert.Contains("knowledgebases", provisioning, StringComparison.Ordinal);
        Assert.Contains("knowledgesources", provisioning, StringComparison.Ordinal);
        Assert.Contains("--auth-mode login", provisioning, StringComparison.Ordinal);
        Assert.Contains("successRatePercent", rehearsal, StringComparison.Ordinal);
        Assert.Contains("[switch]$AgentsOnly", provisioning, StringComparison.Ordinal);
        Assert.Contains("if (-not $AgentsOnly)", provisioning, StringComparison.Ordinal);
        Assert.Contains("attemptsPerAgentPerScenario = 1", rehearsal, StringComparison.Ordinal);
        Assert.Contains("Category=CloudIntegration", rehearsal, StringComparison.Ordinal);
        Assert.Contains("DEMO1_CITATION_IDENTITY_PATH", rehearsal, StringComparison.Ordinal);
        Assert.Contains("payload[\"tool_choice\"] = \"required\"", liveClient, StringComparison.Ordinal);
        Assert.Contains("GroundedResponseGate.EvaluateLiveResponse", liveClient, StringComparison.Ordinal);
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

    private static ApprovedCitationSource CreateApprovedCitationSource() => new(
        new HashSet<string>(["file-mpm-001"], StringComparer.Ordinal),
        "managing-public-money-april-2026.pdf",
        new HashSet<string>(
            [
                "https://www.gov.uk/government/publications/managing-public-money",
                "https://assets.publishing.service.gov.uk/media/69e0b17861d2e8e9b9e42e13/Managing_Public_Money_-_April_2026.pdf"
            ],
            StringComparer.Ordinal));

    private static string CreateQuotedResponse(
        string? firstQuote = null,
        string? retrievalUrl = null,
        bool includeApplication = true)
    {
        const string source = "https://www.gov.uk/government/publications/managing-public-money";
        const string first = "Treasury approval is essential.";
        const string second = "Letters of comfort, however vague, give rise to moral and sometimes legal obligations.";
        const string marker = "\u30107:0\u2020source\u3011";
        string text = $"Assessment\nRoute: treasury-consent. Provisional conclusion. {marker}\n" +
            $"Evidence\n> {firstQuote ?? first} {marker}\n> {second} {marker}\n";
        if (includeApplication)
        {
            text += $"Application\nThe stated letter of comfort needs Treasury approval despite its wording. {marker}";
        }
        List<object> annotations = [];
        for (int offset = text.IndexOf(marker, StringComparison.Ordinal); offset >= 0;
            offset = text.IndexOf(marker, offset + marker.Length, StringComparison.Ordinal))
        {
            annotations.Add(new { type = "url_citation", url = source, start_index = offset, end_index = offset + marker.Length });
        }
        string retrieval = marker + "\n" + JsonSerializer.Serialize(new
        {
            blob_url = retrievalUrl ?? source,
            snippet = second + "\n\n" + first
        });
        return JsonSerializer.Serialize(new
        {
            output = new object[]
            {
                new { type = "mcp_call", name = "knowledge_base_retrieve", status = "completed", output = retrieval },
                new { type = "message", role = "assistant", content = new[]
                {
                    new { type = "output_text", text, annotations }
                }}
            }
        });
    }

    private static string CreateOutputResponse(string text) => JsonSerializer.Serialize(new
    {
        output = new[]
        {
            new
            {
                type = "message",
                role = "assistant",
                content = new[]
                {
                    new
                    {
                        type = "output_text",
                        text,
                        annotations = Array.Empty<object>()
                    }
                }
            }
        }
    });
}
