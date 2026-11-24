using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace IntuneWrapper.Core
{
    /// <summary>
    /// Builds Windows command lines that parse back exactly (CommandLineToArgvW rules). Paths are
    /// always quoted (prototype issue), including trailing backslashes, which would otherwise
    /// escape the closing quote ("C:\out\" -> C:\out").
    /// </summary>
    public static class CommandLine
    {
        public static string Quote(string argument, bool force = true)
        {
            if (argument == null)
            {
                argument = "";
            }
            if (!force && argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return argument;
            }

            var sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    // Escape the preceding backslashes and the quote itself.
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            // Backslashes before the closing quote must be doubled.
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Joins switch/value pairs; values are always quoted, switches never.</summary>
        public static string Join(IEnumerable<KeyValuePair<string, string>> switches)
        {
            return string.Join(" ", switches.Select(kv => kv.Value == null ? kv.Key : kv.Key + " " + Quote(kv.Value)));
        }
    }
}
