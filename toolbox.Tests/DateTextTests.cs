using toolbox.Services;

namespace toolbox.Tests
{
    /// <summary>購入、報廢、盤點日期的輸入：各種常見寫法、民國年、未來日期。</summary>
    public class DateTextTests
    {
        private static readonly DateTime Today = new(2026, 10, 8);

        [Theory]
        [InlineData("2026/10/8", 2026, 10, 8)]
        [InlineData("2026-10-08", 2026, 10, 8)]
        [InlineData("2026.1.2", 2026, 1, 2)]
        [InlineData(" 2026 / 3 / 5 ", 2026, 3, 5)]
        [InlineData("20261008", 2026, 10, 8)]
        [InlineData("115/10/8", 2026, 10, 8)]
        [InlineData("民國115年10月8日", 2026, 10, 8)]
        [InlineData("2026年3月5日", 2026, 3, 5)]
        [InlineData("２０２６／１０／８", 2026, 10, 8)]
        [InlineData("3/5", 2026, 3, 5)]
        [InlineData("2024/2/29", 2024, 2, 29)]
        public void TryParse_AcceptsCommonFormats(string text, int y, int m, int d)
        {
            Assert.True(DateText.TryParse(text, Today, out var value));
            Assert.Equal(new DateTime(y, m, d), value);
        }

        [Theory]
        [InlineData("")]
        [InlineData("昨天")]
        [InlineData("2026/13/1")]
        [InlineData("2025/2/29")]
        [InlineData("2026/10")]
        [InlineData("1/2/3/4")]
        [InlineData("2026//8")]
        [InlineData("1800/1/1")]
        public void TryParse_RejectsInvalid(string text) => Assert.False(DateText.TryParse(text, Today, out _));

        [Fact]
        public void Check_RequiresDateAndRejectsFuture()
        {
            Assert.Equal(DateError.Empty, DateText.Check(" ", Today, out _));
            Assert.Equal(DateError.Invalid, DateText.Check("abc", Today, out _));
            Assert.Equal(DateError.Future, DateText.Check("2026/10/9", Today, out _));
            Assert.Equal(DateError.None, DateText.Check("2026/10/8", Today, out var d));
            Assert.Equal(Today, d);
        }

        [Fact]
        public void Format_And_At()
        {
            Assert.Equal("2026/01/02", DateText.Format(new DateTime(2026, 1, 2, 13, 0, 0)));
            Assert.Equal("", DateText.Format((DateTime?)null));
            Assert.Equal(new DateTime(2026, 9, 1, 14, 30, 0), DateText.At(new DateTime(2026, 9, 1), new DateTime(2026, 10, 8, 14, 30, 0)));
        }
    }
}
