using System.Text.RegularExpressions;

namespace chris7197.DevDeck.Core
{
    public static class StringEx
    {
        /// <summary>
        /// Checks if the string matches a given pattern with wildcards (*). The pattern can contain '*' to match any sequence of characters.
        /// </summary>
        /// <param name="value">The string to check.</param>
        /// <param name="pattern">The pattern to match, which can include '*' as a wildcard.</param>
        /// <param name="ignoreCase">Indicates whether the match should be case-insensitive. Default is true.</param>
        /// <returns>True if the string matches the pattern; otherwise, false.</returns>
        public static bool MatchesPattern(this string value, string pattern, bool ignoreCase = true)
        {
            if (value == null || pattern == null)
                return false;

            var regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";

            var options = ignoreCase ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant;
            return Regex.IsMatch(value, regexPattern, options);
        }
    }
}
