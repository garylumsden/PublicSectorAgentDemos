namespace GovernanceCouncil.Core.Models;

/// <summary>
/// A web-grounding citation surfaced from an agent's Foundry IQ knowledge base
/// annotations (UriCitationMessageAnnotation). Displayed in the live debate UI so the
/// council's reasoning is evidence-linked and auditable.
/// </summary>
public record Citation(string Title, string Url);
