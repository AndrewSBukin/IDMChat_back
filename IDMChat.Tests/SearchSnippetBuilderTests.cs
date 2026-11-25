using IDMChat.Utils;
using System.Text;
using Xunit;

namespace IDMChat.Tests
{
    public class SearchSnippetBuilderTests
    {
        // Имитация SQL-фазы 2: CHARINDEX(@query, Text) + SUBSTRING окна, как в LoadSnippetsAsync.
        private static (string RawWindow, int WindowStartOffset, int FullLength) SimulateSql(string fullText, string query)
        {
            int fullLength = fullText.Length;
            int pos = fullText.IndexOf(query, StringComparison.OrdinalIgnoreCase); // -1 если не найдено
            int pos1 = pos + 1; // 1-based, 0 = не найдено (как CHARINDEX)

            int windowStart; // 1-based
            if (fullLength <= SearchSnippetBuilder.RawWindowLength)
                windowStart = 1;
            else if (pos1 <= SearchSnippetBuilder.ContextBefore)
                windowStart = 1;
            else
                windowStart = pos1 - SearchSnippetBuilder.ContextBefore;

            int windowStartOffset = windowStart - 1;
            int windowLen = Math.Min(SearchSnippetBuilder.RawWindowLength, fullLength - windowStartOffset);
            string rawWindow = windowLen > 0 ? fullText.Substring(windowStartOffset, windowLen) : string.Empty;
            return (rawWindow, windowStartOffset, fullLength);
        }

        private static SearchSnippet Build(string fullText, string query)
        {
            var (raw, off, len) = SimulateSql(fullText, query);
            return SearchSnippetBuilder.Build(raw, query, off, len);
        }

        [Fact]
        public void Match_at_position_0_short_text_returns_full_text()
        {
            string text = "2000 клуб отчёт";
            var s = Build(text, "2000");
            Assert.Equal(text, s.Content);
            Assert.Equal(0, s.HighlightStart);
            Assert.Equal(4, s.HighlightLength);
            Assert.False(s.Truncated);
        }

        [Fact]
        public void Match_within_500_chars_centers_snippet()
        {
            string query = "2000";
            int total = 1000;
            int pos = 300;
            string text = new string('x', pos) + query + new string('y', total - pos - query.Length);

            var s = Build(text, query);

            Assert.Contains(query, s.Content);
            Assert.True(s.Truncated);
            Assert.Equal(query, s.Content.Substring(s.HighlightStart, s.HighlightLength));
        }

        [Fact]
        public void Match_beyond_500_chars_is_still_contained()
        {
            string query = "2000";
            int total = 5000;
            int pos = 2000;
            string text = new string('x', pos) + query + new string('y', total - pos - query.Length);

            var s = Build(text, query);

            Assert.Contains(query, s.Content);
            Assert.True(s.Truncated);
            Assert.Equal(query, s.Content.Substring(s.HighlightStart, s.HighlightLength));
        }

        [Fact]
        public void Match_in_megabyte_text_is_contained()
        {
            string query = "2000";
            int total = 2_000_000;
            int pos = 1_000_000;
            string text = new string('x', pos) + query + new string('y', total - pos - query.Length);

            var s = Build(text, query);

            Assert.Contains(query, s.Content);
            Assert.True(s.Truncated);
            Assert.Equal(query, s.Content.Substring(s.HighlightStart, s.HighlightLength));
        }

        [Fact]
        public void Match_near_end_has_trailing_ellipsis_removed_or_kept_correctly()
        {
            string query = "конец";
            string text = new string('x', 500) + query; // query на самом конце

            var s = Build(text, query);

            Assert.Contains(query, s.Content);
            // Заканчивается полным словом (нет обрезки справа, т.к. это конец текста).
            Assert.EndsWith(query, s.Content);
            Assert.Equal(query, s.Content.Substring(s.HighlightStart, s.HighlightLength));
        }

        [Fact]
        public void Text_shorter_than_window_returns_full_text()
        {
            string text = "короткое сообщение 2000";
            var s = Build(text, "2000");
            Assert.Equal(text, s.Content);
            Assert.False(s.Truncated);
            Assert.Equal("2000", s.Content.Substring(s.HighlightStart, s.HighlightLength));
        }

        [Fact]
        public void Multiple_matches_use_first_occurrence()
        {
            string text = "первое 2000 второе 2000 третье 2000";
            var s = Build(text, "2000");
            int first = text.IndexOf("2000", StringComparison.Ordinal);
            Assert.Equal(first, s.HighlightStart);
            Assert.Equal(4, s.HighlightLength);
        }

        [Fact]
        public void Surrogate_pairs_are_not_split_at_boundaries()
        {
            string emoji = "\U0001F600"; // 😀 — суррогатная пара
            var sb = new StringBuilder();
            for (int i = 0; i < 120; i++) sb.Append(emoji); // 240 UTF-16 code units без пробелов
            sb.Append("2000");
            sb.Append(emoji);
            string text = sb.ToString();

            var s = Build(text, "2000");

            Assert.Contains("2000", s.Content);
            AssertNoLoneSurrogates(s.Content);
            Assert.Equal("2000", s.Content.Substring(s.HighlightStart, s.HighlightLength));
        }

        [Fact]
        public void Newlines_are_normalized_in_truncated_snippet()
        {
            string query = "2000";
            // Длинный многострочный текст: переносы должны превратиться в пробелы в сниппете.
            string text = new string('x', 200) + "line\r\nbreak\nhere " + query + " tail\r\n" + new string('y', 500);
            var s = Build(text, query);

            Assert.DoesNotContain('\r', s.Content);
            Assert.DoesNotContain('\n', s.Content);
            Assert.Contains(query, s.Content);
        }

        [Fact]
        public void Newlines_are_preserved_in_full_short_text()
        {
            string text = "первая строка\r\nвторая строка 2000";
            var s = Build(text, "2000");
            Assert.Equal(text, s.Content);
            Assert.False(s.Truncated);
        }

        [Fact]
        public void Yo_normalization_degraded_to_prefix()
        {
            // FTS нормализует ё→е и найдёт "ежик", но CHARINDEX/IndexOf по литералу "ежик" — нет.
            string text = new string('x', 60) + "ёжик в тумане " + new string('y', 500);
            var s = Build(text, "ежик");

            Assert.Equal(0, s.HighlightStart);
            Assert.Equal(0, s.HighlightLength);
            Assert.True(s.Truncated);
        }

        [Fact]
        public void Query_not_found_degrades_to_prefix_with_zero_highlight()
        {
            string text = new string('x', 60) + "сообщение без искомого слова " + new string('y', 500);
            var s = Build(text, "отсутствует");

            Assert.Equal(0, s.HighlightStart);
            Assert.Equal(0, s.HighlightLength);
            Assert.True(s.Truncated);
        }

        [Fact]
        public void Multiword_query_highlights_whole_phrase()
        {
            string query = "клуб 2000";
            string text = new string('x', 100) + query + " закрыт " + new string('y', 500);
            var s = Build(text, query);

            Assert.Contains(query, s.Content);
            Assert.Equal(query, s.Content.Substring(s.HighlightStart, s.HighlightLength));
        }

        [Fact]
        public void Stemming_fallback_finds_word_form()
        {
            string query = "клуба"; // окончание 'а' → стем "клуб"
            string text = new string('x', 100) + "клуб 2000 " + new string('y', 500);
            var s = Build(text, query);

            Assert.Contains("клуб", s.Content);
            Assert.True(s.HighlightLength >= 4);
        }

        private static void AssertNoLoneSurrogates(string content)
        {
            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (char.IsHighSurrogate(c))
                    Assert.True(i + 1 < content.Length && char.IsLowSurrogate(content[i + 1]),
                        $"High surrogate at {i} without low surrogate");
                if (char.IsLowSurrogate(c))
                    Assert.True(i > 0 && char.IsHighSurrogate(content[i - 1]),
                        $"Low surrogate at {i} without high surrogate");
            }
        }
    }
}
