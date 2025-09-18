using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace POSM_MR3_2
{
    public static class TextHelper
    {
    public static TextBlock CreateHyperlinkedText(string inputText)
    {
        // Create a TextBlock to display our text (with wrapping).
        var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };

        // Regex pattern: capture any http:// or https:// until next whitespace.
        // Simplistic pattern: (https?://[^\s]+)
        string urlPattern = @"(https?://[^\s]+)";

        // Split the text by the URL pattern, keeping delimiters in the result.
        // Regex.Split will remove them by default, but we can parse carefully.
        // Instead, let's do a "split but capture the delimiter" approach:
        var parts = Regex.Split(inputText, urlPattern, RegexOptions.IgnoreCase);

        foreach (string part in parts)
        {
            // Check if this segment is a URL:
            if (Regex.IsMatch(part, urlPattern, RegexOptions.IgnoreCase))
            {
                // Create a Hyperlink that points to this URL
                var link = new Hyperlink(new Run(part))
                {
                    NavigateUri = new Uri(part)
                };

                // Handle the click: open default browser
                link.RequestNavigate += (s, e) =>
                {
                    // Use ShellExecute to launch the system browser.
                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
                    {
                        UseShellExecute = true
                    });
                };

                // Add the hyperlink inline
                textBlock.Inlines.Add(link);
            }
            else
            {
                // Just normal text (no URL).
                textBlock.Inlines.Add(part);
            }
        }

        return textBlock;
    }
    }
}
