using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CouchDeck.Games
{
    /// <summary>
    /// Turns folder and file names into game titles, and compares titles fuzzily.
    ///   "Elden.Ring.v1.02.3-CODEX"              -> "Elden Ring"
    ///   "Hollow_Knight_v1.5.78.11833_(64bit)"   -> "Hollow Knight"
    ///   "Cyberpunk 2077 [FitGirl Repack]"       -> "Cyberpunk 2077"
    ///   "The Witcher 3 Wild Hunt GOTY Edition"  -> display keeps "GOTY Edition", search term drops it
    /// </summary>
    public static class TitleCleaner
    {
        // Release-group and repacker tags that end up glued to folder names.
        private static readonly HashSet<string> ReleaseTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "codex", "plaza", "skidrow", "reloaded", "razor1911", "razor", "cpy", "hoodlum", "flt", "fairlight",
            "darksiders", "simplex", "tinyiso", "doge", "rune", "tenoke", "empress", "goldberg", "p2p", "elamigos",
            "fitgirl", "dodi", "kaos", "xatab", "mechanics", "corepack", "gog", "steam", "repack", "anomaly",
            "prophet", "postmortem", "hi2u", "dinobytes", "ali213", "3dm", "chronos", "activated", "bat", "voksi",
            "skidrow-reloaded", "steamrip", "gogrip", "igg", "igggames", "kapital", "gamesdrive", "onlinefix",
            "online-fix", "portable", "multi", "rip", "proper", "repack-fitgirl", "unleashed", "delight", "i_know",
            "ikk", "venom", "vrex", "rg", "torrent", "pc", "win", "win64", "x64", "x86", "dlc", "dlcs", "update",
            "build", "crack", "cracked", "fix", "nosteam", "drmfree", "drm-free", "launcher", "setup", "installer"
        };

        // Words dropped from the search term only (kept in the displayed title).
        private static readonly Regex EditionWords = new(
            @"\b(game of the year|goty|deluxe|digital deluxe|gold|ultimate|complete|definitive|premium|collector'?s|standard|enhanced|anniversary|special|legendary|royal|director'?s cut|bundle|edition|remaster(ed)? edition)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Bracketed = new(@"[\[\(\{][^\]\)\}]*[\]\)\}]", RegexOptions.Compiled);
        private static readonly Regex Version = new(@"\b(v|ver|version)\s?\d+([._]\d+)*[a-z]?\b|\b\d+(\.\d+){2,}\b|\bbuild\s?\d+\b|\bb\d{4,}\b|\bupdate\s?\d+(\.\d+)*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex JunkTail = new(@"(\bincl(uding)?\b|\+\s*\d*\s*dlcs?\b|\ball dlcs?\b|\bwith dlcs?\b).*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex JunkWords = new(@"\b(multi\d*|repack|portable|x64|x86|win64|win32|64\s?bit|32\s?bit)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CamelSplit = new(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z]{2})|(?<=[A-Za-z])(?=\d)(?!\d*(st|nd|rd|th)\b)", RegexOptions.Compiled);
        private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);

        /// <summary>A readable title for display.</summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }

            bool hadSpaces = raw.Trim().Contains(' ');
            string text = raw.Trim().Replace('_', ' ');
            text = Bracketed.Replace(text, " ");

            // Versions first, while their dots are still intact ("v1.02.3").
            text = Version.Replace(text, " ");

            // Scene style: dots instead of spaces. Keep dotted acronyms like S.T.A.L.K.E.R.
            if ((!hadSpaces || text.Count(c => c == '.') >= 2) && !Regex.IsMatch(text, @"^\s*([A-Za-z]\.){2,}"))
            {
                text = text.Replace('.', ' ');
            }

            // "Game Name-CODEX" / "Game Name - FitGirl Repack"
            text = StripTrailingTags(text);
            text = JunkTail.Replace(text, " ");
            text = JunkWords.Replace(text, " ");

            // "HollowKnight" -> "Hollow Knight" (only when there are no spaces at all)
            if (!text.Trim().Contains(' ') && text.Any(char.IsLower))
            {
                text = CamelSplit.Replace(text, " ");
            }

            text = Spaces.Replace(text, " ").Trim(' ', '-', '_', '.', ':', '+', ',');
            text = StripTrailingTags(text); // tags exposed after removing the version

            return text.Length == 0 ? raw.Trim() : text;
        }

        /// <summary>A title tuned for store search: no edition words, no subtitles noise.</summary>
        public static string SearchTerm(string title)
        {
            string text = Clean(title);
            string withoutEdition = Spaces.Replace(EditionWords.Replace(text, " "), " ").Trim(' ', '-', ':');
            text = withoutEdition.Length >= 2 ? withoutEdition : text;

            // Symbols the store search chokes on
            text = text.Replace("™", "").Replace("®", "").Replace("©", "");
            return Spaces.Replace(text, " ").Trim();
        }

        /// <summary>Main title without subtitle: "Batman: Arkham Knight" -> "Batman". Used as a second search.</summary>
        public static string? ShortSearchTerm(string term)
        {
            int cut = term.IndexOfAny(new[] { ':', '-', '–' });
            if (cut <= 2)
            {
                return null;
            }

            string shortTerm = term[..cut].Trim();
            return shortTerm.Length >= 3 && shortTerm != term ? shortTerm : null;
        }

        private static string StripTrailingTags(string text)
        {
            while (true)
            {
                string trimmed = text.TrimEnd();
                int dash = trimmed.LastIndexOf('-');
                if (dash < 0)
                {
                    return trimmed;
                }

                string tail = trimmed[(dash + 1)..].Trim();
                string tailFirstWord = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

                bool knownTag = ReleaseTags.Contains(tail) || ReleaseTags.Contains(tail.Replace(" ", ""))
                    || (ReleaseTags.Contains(tailFirstWord) && tail.Split(' ').Length <= 3);
                // Unknown group glued on with no space ("Name-GRP"). dash >= 4 keeps "X-COM" intact.
                bool sceneTag = !tail.Contains(' ') && tail.Length is >= 3 and <= 12
                    && dash >= 4 && trimmed[dash - 1] != ' ' && IsShouty(tail);

                if (!knownTag && !sceneTag)
                {
                    return trimmed;
                }

                text = trimmed[..dash];
            }
        }

        // "CODEX", "TiNYiSO", "DARKSiDERS": scene tags are mostly upper case.
        private static bool IsShouty(string word) =>
            word.Count(char.IsUpper) >= Math.Max(2, word.Count(char.IsLetter) * 0.6);

        // =====================================================================
        // Fuzzy matching
        // =====================================================================

        /// <summary>Lowercase letters and digits only, roman numerals 2-10 turned into digits.</summary>
        public static string Normalize(string title)
        {
            string decomposed = title.Normalize(NormalizationForm.FormD);
            StringBuilder builder = new(decomposed.Length);
            foreach (string word in Regex.Split(decomposed.ToLowerInvariant(), @"[^\p{L}\p{N}]+"))
            {
                if (word.Length == 0 || word is "the" or "a" or "an" or "and")
                {
                    continue;
                }

                builder.Append(word switch
                {
                    "ii" => "2", "iii" => "3", "iv" => "4", "v" => "5", "vi" => "6",
                    "vii" => "7", "viii" => "8", "ix" => "9", "x" => "10",
                    _ => RemoveDiacritics(word)
                });
            }

            return builder.ToString();
        }

        /// <summary>0..1, 1 = same title. Mix of edit distance and word overlap so word order matters less.</summary>
        public static double Similarity(string a, string b)
        {
            string na = Normalize(a);
            string nb = Normalize(b);
            if (na.Length == 0 || nb.Length == 0)
            {
                return 0;
            }

            if (na == nb)
            {
                return 1;
            }

            double edit = 1.0 - (double)Levenshtein(na, nb) / Math.Max(na.Length, nb.Length);

            HashSet<string> wa = Words(a);
            HashSet<string> wb = Words(b);
            double overlap = wa.Count == 0 || wb.Count == 0 ? 0 : (double)wa.Intersect(wb).Count() / Math.Max(wa.Count, wb.Count);

            // "elden ring" vs "elden ring nightreign": prefix match is likely the base game
            double prefix = nb.StartsWith(na) || na.StartsWith(nb) ? 0.1 : 0;

            return Math.Min(0.99, Math.Max(edit, overlap) * 0.85 + Math.Min(edit, overlap) * 0.15 + prefix);
        }

        private static HashSet<string> Words(string text) =>
            Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
                .Where(w => w.Length > 0 && w is not ("the" or "a" or "an" or "and" or "of"))
                .ToHashSet();

        private static string RemoveDiacritics(string word)
        {
            StringBuilder builder = new(word.Length);
            foreach (char c in word)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        private static int Levenshtein(string a, string b)
        {
            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }
    }
}
