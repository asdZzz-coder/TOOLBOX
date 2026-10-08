using System.Globalization;
using System.Text.RegularExpressions;

namespace toolbox.Services
{
    public enum DateError { None, Empty, Invalid, Future }

    /// <summary>
    /// 日期的文字轉換：購入、報廢、盤點日期都用文字框輸入（WPF 的日曆在深色主題下看不清楚）。
    /// 可輸入 2026/10/8、2026-10-08、20261008、民國 115/10/8、2026年10月8日，只打 10/8 時當作今年。
    /// </summary>
    public static partial class DateText
    {
        public static string Format(DateTime value) => value.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);

        public static string Format(DateTime? value) => value is { } v ? Format(v) : "";

        /// <summary>解析日期（只取日期，不含時間）；空白或看不懂時回傳 false。</summary>
        public static bool TryParse(string? text, DateTime today, out DateTime value)
        {
            value = default;
            var s = (text ?? "").Trim().Normalize(System.Text.NormalizationForm.FormKC);
            if (s.StartsWith("民國")) s = s[2..].Trim();
            if (s.EndsWith('日')) s = s[..^1];

            int y, m, d;
            var digits = EightDigits().Match(s);
            if (digits.Success)
            {
                y = int.Parse(digits.Groups[1].Value);
                m = int.Parse(digits.Groups[2].Value);
                d = int.Parse(digits.Groups[3].Value);
            }
            else
            {
                var parts = Separators().Split(s);
                if (parts.Any(p => p.Length == 0 || p.Length > 4 || !p.All(char.IsAsciiDigit))) return false;
                switch (parts.Length)
                {
                    case 3:
                        (y, m, d) = (int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
                        if (parts[0].Length <= 3) y += 1911; // 民國年
                        break;
                    case 2:
                        (y, m, d) = (today.Year, int.Parse(parts[0]), int.Parse(parts[1]));
                        break;
                    default:
                        return false;
                }
            }
            if (y < 1900 || y > 9999 || m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
            value = new DateTime(y, m, d);
            return true;
        }

        /// <summary>檢查必填、不能是未來的日期（購入、報廢、盤點都是已經發生的事）。</summary>
        public static DateError Check(string? text, DateTime today, out DateTime value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(text)) return DateError.Empty;
            if (!TryParse(text, today, out value)) return DateError.Invalid;
            return value > today.Date ? DateError.Future : DateError.None;
        }

        /// <summary>選的日期 + 現在的時刻：同一天做的多筆紀錄仍能依先後排列。</summary>
        public static DateTime At(DateTime date, DateTime now) => date.Date + now.TimeOfDay;

        public const string Hint = "例如 2026/10/8 或 115/10/8";

        [GeneratedRegex(@"^(\d{4})(\d{2})(\d{2})$")]
        private static partial Regex EightDigits();

        [GeneratedRegex(@"\s*[/\-.年月]\s*")]
        private static partial Regex Separators();
    }
}
