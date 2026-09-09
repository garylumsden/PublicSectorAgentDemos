namespace GovernanceCouncil.Web;

using GovernanceCouncil.Core.Models;

/// <summary>
/// UI-facing branding, projected from the active <see cref="Scenario"/> (organisation, app name,
/// tagline, emblem). The emblem is either an emoji (rendered as text) or a path under
/// <c>wwwroot/branding/</c> such as <c>branding/logo.svg</c> (rendered as an image).
/// </summary>
public static class Brand
{
    private static BrandingConfig Cfg => Scenario.Current.Branding;

    public static string Organisation => Cfg.Organisation;
    public static string AppName => Cfg.AppName;
    public static string Tagline => Cfg.Tagline;
    public static string Emblem => Cfg.Emblem;

    /// <summary>True when the emblem is an image path (e.g. an SVG under wwwroot/branding/) rather than an emoji.</summary>
    public static bool EmblemIsImage =>
        Emblem.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
        Emblem.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        Emblem.Contains('/');

    /// <summary>True when an organisation eyebrow should be shown (non-empty).</summary>
    public static bool HasOrganisation => !string.IsNullOrWhiteSpace(Organisation);
}
