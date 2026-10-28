using System;
using System.Collections.Generic;
using System.Text;

namespace CouchDeck.Games
{
    /// <summary>
    /// Minimal parser for Valve's text KeyValues format (.vdf / .acf):
    ///
    ///   "AppState"
    ///   {
    ///       "appid"   "1245620"
    ///       "name"    "ELDEN RING"
    ///   }
    ///
    /// Produces nested dictionaries: values are either string or Dictionary&lt;string, object&gt;.
    /// Keys are case-insensitive because Valve isn't consistent about casing.
    /// </summary>
    public static class VdfParser
    {
        public static Dictionary<string, object> Parse(string text)
        {
            int position = 0;
            return ParseObject(text, ref position);
        }

        public static string? GetString(Dictionary<string, object> node, string key)
        {
            return node.TryGetValue(key, out object? value) ? value as string : null;
        }

        public static Dictionary<string, object>? GetObject(Dictionary<string, object> node, string key)
        {
            return node.TryGetValue(key, out object? value) ? value as Dictionary<string, object> : null;
        }

        private static Dictionary<string, object> ParseObject(string text, ref int position)
        {
            Dictionary<string, object> result = new(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                string? key = NextToken(text, ref position);
                if (key == null || key == "}")
                {
                    return result;
                }

                string? value = NextToken(text, ref position);
                if (value == null)
                {
                    return result;
                }

                result[key] = value == "{" ? ParseObject(text, ref position) : value;
            }
        }

        /// <summary>Returns a quoted string's content, "{" or "}", or null at end of text.</summary>
        private static string? NextToken(string text, ref int position)
        {
            while (position < text.Length)
            {
                char c = text[position];

                if (char.IsWhiteSpace(c))
                {
                    position++;
                    continue;
                }

                // Line comments
                if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
                {
                    while (position < text.Length && text[position] != '\n')
                    {
                        position++;
                    }

                    continue;
                }

                if (c == '{' || c == '}')
                {
                    position++;
                    return c.ToString();
                }

                if (c == '"')
                {
                    position++;
                    StringBuilder builder = new();
                    while (position < text.Length && text[position] != '"')
                    {
                        // Paths are stored as C:\\Program Files (x86)\\Steam
                        if (text[position] == '\\' && position + 1 < text.Length)
                        {
                            position++;
                        }

                        builder.Append(text[position]);
                        position++;
                    }

                    position++; // closing quote
                    return builder.ToString();
                }

                // Unquoted token (rare, but legal)
                int start = position;
                while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '{' && text[position] != '}')
                {
                    position++;
                }

                return text.Substring(start, position - start);
            }

            return null;
        }
    }
}
