namespace IDMChat.Utils
{
    /// <summary>
    /// Результат построения сниппета вокруг первого совпадения.
    /// </summary>
    public sealed record SearchSnippet(string Content, int HighlightStart, int HighlightLength, bool Truncated);

    /// <summary>
    /// Строит сниппет с контекстом вокруг первого совпадения (как в Telegram/WhatsApp/Slack).
    /// Чистый helper без I/O — полностью покрывается юнит-тестами.
    /// </summary>
    public static class SearchSnippetBuilder
    {
        public const int ContextBefore = 60;
        public const int ContextAfter = 180;
        public const int RawWindowLength = ContextBefore + ContextAfter; // 240
        private const string Ellipsis = "\u2026"; // "…"

        /// <summary>
        /// Строит сниппет.
        /// </summary>
        /// <param name="rawWindow">Ограниченное окно текста вокруг совпадения (из SQL SUBSTRING).</param>
        /// <param name="query">Поисковая фраза (без FTS-синтаксиса).</param>
        /// <param name="rawWindowStartOffset">0-based смещение <paramref name="rawWindow"/>[0] в полном тексте.</param>
        /// <param name="fullLength">Полная длина исходного текста (LEN).</param>
        public static SearchSnippet Build(string rawWindow, string query, int rawWindowStartOffset, int fullLength)
        {
            if (string.IsNullOrWhiteSpace(rawWindow))
                return new SearchSnippet(string.Empty, 0, 0, false);

            bool windowAtStartOfFull = rawWindowStartOffset <= 0;
            bool windowAtEndOfFull = rawWindowStartOffset + rawWindow.Length >= fullLength;

            // Короткое сообщение целиком в окне — возвращаем полный текст как раньше (без нормализации).
            bool isFullText = windowAtStartOfFull && windowAtEndOfFull;

            if (string.IsNullOrEmpty(query))
                return isFullText
                    ? new SearchSnippet(rawWindow, 0, 0, false)
                    : BuildPrefix(Normalize(rawWindow), windowAtStartOfFull, windowAtEndOfFull);

            if (isFullText)
            {
                int fullMatch = rawWindow.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (fullMatch == -1 && query.Length > 4)
                {
                    var stem = Stem(query);
                    if (stem.Length > 0)
                    {
                        fullMatch = rawWindow.IndexOf(stem, StringComparison.OrdinalIgnoreCase);
                        if (fullMatch != -1)
                            return new SearchSnippet(rawWindow, fullMatch, MatchWordLength(rawWindow, fullMatch), false);
                    }
                }
                if (fullMatch == -1)
                    return new SearchSnippet(rawWindow, 0, 0, false);
                return new SearchSnippet(rawWindow, fullMatch, query.Length, false);
            }

            // Обрезка — нормализуем переносы строк и строим окно вокруг совпадения.
            var text = Normalize(rawWindow);

            int match = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);

            // Фолбэк со стеммингом (переиспользована логика усечённой формы слова).
            if (match == -1 && query.Length > 4)
            {
                var stem = Stem(query);
                if (stem.Length > 0)
                {
                    match = text.IndexOf(stem, StringComparison.OrdinalIgnoreCase);
                    if (match != -1)
                        return BuildAround(text, match, MatchWordLength(text, match), windowAtStartOfFull, windowAtEndOfFull);
                }
            }

            if (match == -1)
                return BuildPrefix(text, windowAtStartOfFull, windowAtEndOfFull);

            return BuildAround(text, match, query.Length, windowAtStartOfFull, windowAtEndOfFull);
        }

        private static SearchSnippet BuildAround(string text, int match, int matchLength, bool windowAtStartOfFull, bool windowAtEndOfFull)
        {
            int start = Math.Max(0, match - ContextBefore);
            int end = Math.Min(text.Length, match + matchLength + ContextAfter);

            // Выравнивание границ по словам, не рвём слова.
            start = AlignStartForward(text, start, match);
            end = AlignEndBackward(text, end, match + matchLength);

            // Не режем суррогатную пару (эмодзи) пополам.
            start = AvoidCuttingSurrogateAtStart(text, start);
            end = AvoidCuttingSurrogateAtEnd(text, end);

            bool truncatedLeft = !(windowAtStartOfFull && start == 0);
            bool truncatedRight = !(windowAtEndOfFull && end >= text.Length);

            string body = text.Substring(start, end - start);

            int highlightStart = match - start;
            int highlightLength = Math.Min(matchLength, end - match);

            if (truncatedLeft) highlightStart += 1; // один символ "…" в начале

            string content = (truncatedLeft ? Ellipsis : "") + body + (truncatedRight ? Ellipsis : "");

            // Страховка от выхода подсветки за границы content.
            highlightStart = Math.Max(0, Math.Min(highlightStart, content.Length));
            highlightLength = Math.Max(0, Math.Min(highlightLength, content.Length - highlightStart));

            return new SearchSnippet(content, highlightStart, highlightLength, truncatedLeft || truncatedRight);
        }

        private static SearchSnippet BuildPrefix(string text, bool windowAtStartOfFull, bool windowAtEndOfFull)
        {
            bool truncatedLeft = !windowAtStartOfFull;
            bool truncatedRight = !windowAtEndOfFull;
            string content = (truncatedLeft ? Ellipsis : "") + text + (truncatedRight ? Ellipsis : "");
            return new SearchSnippet(content, 0, 0, truncatedLeft || truncatedRight);
        }

        private static string Normalize(string s)
            => s.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");

        private static string Stem(string query)
        {
            bool endsWithSingle = query.EndsWith("ы") || query.EndsWith("и") || query.EndsWith("а");
            int cut = endsWithSingle ? 1 : 2;
            return query.Length > cut ? query.Substring(0, query.Length - cut) : string.Empty;
        }

        private static int MatchWordLength(string text, int match)
        {
            int space = text.IndexOf(' ', match);
            return (space != -1 ? space : text.Length) - match;
        }

        private static int AlignStartForward(string text, int start, int match)
        {
            if (start <= 0) return 0;
            // Если граница режет слово — сдвигаемся к началу следующего слова.
            if (!char.IsWhiteSpace(text[start - 1]))
            {
                for (int i = start; i < match; i++)
                {
                    if (char.IsWhiteSpace(text[i]))
                        return i + 1;
                }
            }
            return start;
        }

        private static int AlignEndBackward(string text, int end, int matchEnd)
        {
            if (end >= text.Length) return text.Length;
            // Если граница режет слово — отступаем к концу предыдущего слова.
            if (!char.IsWhiteSpace(text[end - 1]) && !char.IsWhiteSpace(text[end]))
            {
                for (int i = end - 1; i >= matchEnd; i--)
                {
                    if (char.IsWhiteSpace(text[i]))
                        return i;
                }
            }
            return end;
        }

        private static int AvoidCuttingSurrogateAtStart(string text, int start)
        {
            // start указывает на низкий суррогат, чей высокий остался слева — сдвигаем вправо.
            while (start > 0 && start < text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
                start++;
            return start;
        }

        private static int AvoidCuttingSurrogateAtEnd(string text, int end)
        {
            // end отсекает низкий суррогат от высокого — расширяем вправо на пару.
            while (end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
                end++;
            return end;
        }
    }
}
