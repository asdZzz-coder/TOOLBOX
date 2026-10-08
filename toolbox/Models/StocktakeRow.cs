using System.ComponentModel;
using toolbox.Services;

namespace toolbox.Models
{
    /// <summary>
    /// 盤點視窗的一列：帳面數量（在庫）與實際點到的數量。
    /// 實點欄空白表示還沒點，完成盤點時不會更動這項工具。
    /// </summary>
    public class StocktakeRow : INotifyPropertyChanged
    {
        private string _countText = "";

        public StocktakeRow(ToolItem item)
        {
            Item = item;
            Book = item.Quantity;
        }

        public ToolItem Item { get; }

        /// <summary>帳面數量：打開盤點視窗時的在庫數量。</summary>
        public decimal Book { get; }

        public string CountText
        {
            get => _countText;
            set
            {
                if (_countText == value) return;
                _countText = value;
                foreach (var n in new[] { nameof(CountText), nameof(IsCounted), nameof(IsValid), nameof(Counted), nameof(DiffDisplay), nameof(State) })
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
            }
        }

        public bool IsCounted => CountText.Trim().Length > 0;

        /// <summary>沒點或填的是正確的數字。</summary>
        public bool IsValid => !IsCounted || QuantityText.TryParse(CountText, out _);

        /// <summary>實點數量；沒點或填錯時為 null。</summary>
        public decimal? Counted => IsCounted && QuantityText.TryParse(CountText, out var v) ? v : null;

        public decimal? Difference => Counted is { } c ? c - Book : null;

        /// <summary>未點 / 相符 / 多 / 少 / 填錯：決定差異欄的顏色。</summary>
        public StocktakeState State => !IsCounted ? StocktakeState.NotCounted
            : !IsValid ? StocktakeState.Invalid
            : Difference switch { 0 => StocktakeState.Match, > 0 => StocktakeState.More, _ => StocktakeState.Less };

        public string DiffDisplay => State switch
        {
            StocktakeState.NotCounted => "未點",
            StocktakeState.Invalid => "數字有誤",
            StocktakeState.Match => "相符",
            StocktakeState.More => "多 " + QuantityText.Format(Difference!.Value),
            _ => "少 " + QuantityText.Format(-Difference!.Value),
        };

        public string ItemDisplay => StockService.ItemDisplay(Item.Name, Item.Spec);

        public string BookDisplay => (QuantityText.Format(Book) + " " + Item.Unit.Trim()).Trim();

        /// <summary>第二行：上次盤點日期、借出中的數量（借出的不在架上，不用點）。</summary>
        public string Detail
        {
            get
            {
                var parts = new List<string> { Item.LastCheckedAt is { } d ? $"上次盤點 {DateText.Format(d)}" : "還沒盤點過" };
                if (Item.HasLent) parts.Add($"另有{Item.LentDisplay}（不用點）");
                return string.Join(" · ", parts);
            }
        }

        public override string ToString() => Item.Name;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public enum StocktakeState { NotCounted, Invalid, Match, More, Less }
}
