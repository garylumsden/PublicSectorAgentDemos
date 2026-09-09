using System.Text;
using System.Text.Encodings.Web;

namespace PublicSectorAgentDemos.Demo1.Web;

public static class SafeMarkdown
{
    public static string Render(string text)
    {
        StringBuilder html = new();
        foreach (string line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            (string tag, string value) = line switch
            {
                _ when line.StartsWith("> ", StringComparison.Ordinal) => ("blockquote", line[2..]),
                _ when line.StartsWith("### ", StringComparison.Ordinal) => ("h4", line[4..]),
                _ when line.StartsWith("## ", StringComparison.Ordinal) => ("h3", line[3..]),
                _ when line.StartsWith("# ", StringComparison.Ordinal) => ("h3", line[2..]),
                _ => ("p", line)
            };
            html.Append('<').Append(tag).Append('>')
                .Append(HtmlEncoder.Default.Encode(value))
                .Append("</").Append(tag).Append('>');
        }

        return html.ToString();
    }
}
