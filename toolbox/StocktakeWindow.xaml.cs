using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using toolbox.Models;
using toolbox.Services;

namespace toolbox
{
    /// <summary>盤點的結果：盤點日期與有點到的工具（實點數量）。</summary>
    public record StocktakeResult(DateTime Date, IReadOnlyList<(ToolItem Item, decimal Counted)> Counts);

    public partial class StocktakeWindow : Window
    {
        private readonly List<StocktakeRow> _rows;
        private StocktakeResult? _result;
        private bool _finishing;

        private StocktakeWindow(IEnumerable<ToolItem> items, string scope)
        {
            InitializeComponent();
            _rows = items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).Select(i => new StocktakeRow(i)).ToList();
            foreach (var r in _rows) r.PropertyChanged += (_, _) => UpdateSummary();
            Title = $"盤點 - {scope}";
            ScopeText.Text = $"範圍：{scope}，共 {_rows.Count} 項。在「實點」欄填上架上實際點到的數量（借出中的不用點）；"
                + "空白的表示還沒點，完成盤點時不會更動。數量不符的會改成實點數量並留下紀錄。";
            DateBox.Text = DateText.Format(DateTime.Today);
            DateBox.ToolTip = DateText.Hint;
            ApplySearch();
            Closing += OnClosing;
        }

        /// <summary>打開盤點視窗；完成盤點時回傳結果，取消則回傳 null。</summary>
        /// <param name="scope">盤點範圍的名稱，例如「全部工具」或分類名稱。</param>
        public static StocktakeResult? Open(Window owner, IEnumerable<ToolItem> items, string scope)
        {
            var w = new StocktakeWindow(items, scope) { Owner = owner };
            return w.ShowDialog() == true ? w._result : null;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            WindowTheme.ApplyTitleBar(this);
        }

        private void Today_Click(object sender, RoutedEventArgs e) => DateBox.Text = DateText.Format(DateTime.Today);

        private void DateBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSummary();

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();

        private void ApplySearch()
        {
            if (RowList == null) return;
            var keyword = SearchBox.Text.Trim();
            var shown = _rows.Where(r => keyword.Length == 0 ||
                r.Item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                r.Item.Spec.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            RowList.ItemsSource = shown;
            EmptyText.Text = _rows.Count == 0 ? "這個範圍裡沒有工具可以盤點" : "沒有符合的工具";
            EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (SummaryText == null) return;
            int counted = _rows.Count(r => r.IsCounted);
            int diff = _rows.Count(r => r.State is StocktakeState.More or StocktakeState.Less);
            int invalid = _rows.Count(r => r.State == StocktakeState.Invalid);
            var text = $"已點 {counted} / {_rows.Count} 項";
            if (counted > 0) text += diff > 0 ? $" · 不符 {diff} 項" : " · 目前都相符";
            if (invalid > 0) text += $" · {invalid} 項數字有誤";
            SummaryText.Text = text;
        }

        /// <summary>在實點欄按 Enter 或 ↓ 跳到下一項，↑ 回上一項。</summary>
        private void CountBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box) return;
            var direction = e.Key switch
            {
                Key.Enter or Key.Down => FocusNavigationDirection.Next,
                Key.Up => FocusNavigationDirection.Previous,
                _ => (FocusNavigationDirection?)null,
            };
            if (direction == null) return;
            box.MoveFocus(new TraversalRequest(direction.Value));
            e.Handled = true;
        }

        /// <summary>跳到某一格時全選，直接打數字就會取代原本的內容。</summary>
        private void CountBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox box)
            {
                box.SelectAll();
                box.BringIntoView();
            }
        }

        private void FillBook_Click(object sender, RoutedEventArgs e)
        {
            var blanks = _rows.Where(r => !r.IsCounted).ToList();
            if (blanks.Count == 0)
            {
                MessageBox.Show(this, "每一項都已經填了實點數量。", "未點的照帳面");
                return;
            }
            var ok = MessageBox.Show(this, $"還沒點的 {blanks.Count} 項，全部填上帳面數量（當作相符）嗎？", "未點的照帳面",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes) return;
            foreach (var r in blanks) r.CountText = QuantityText.Format(r.Book);
        }

        private void Finish_Click(object sender, RoutedEventArgs e)
        {
            const string title = "完成盤點";
            switch (DateText.Check(DateBox.Text, DateTime.Today, out var date))
            {
                case DateError.Empty: Warn("請填盤點日期。", DateBox); return;
                case DateError.Invalid: Warn($"看不懂這個盤點日期，{DateText.Hint}。", DateBox); return;
                case DateError.Future: Warn("盤點日期不能是未來的日子。", DateBox); return;
            }
            if (_rows.FirstOrDefault(r => r.State == StocktakeState.Invalid) is { } bad)
            {
                SearchBox.Clear();
                MessageBox.Show(this, $"「{bad.ItemDisplay}」的實點數量看不懂，請輸入 0 或正數。", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var counted = _rows.Where(r => r.IsCounted).ToList();
            if (counted.Count == 0)
            {
                MessageBox.Show(this, "還沒有填任何實點數量。\n\n在每項工具的「實點」欄填上點到的數量後再按「完成盤點」。", title);
                return;
            }

            var diffs = counted.Where(r => r.Difference != 0).ToList();
            var lines = new List<string>
            {
                $"盤點日期：{DateText.Format(date)}",
                $"已點 {counted.Count} 項：相符 {counted.Count - diffs.Count} 項、不符 {diffs.Count} 項。",
            };
            int notCounted = _rows.Count - counted.Count;
            if (notCounted > 0) lines.Add($"還沒點的 {notCounted} 項不會更動。");
            if (diffs.Count > 0)
            {
                lines.Add("");
                lines.Add("以下工具的在庫會改成實點數量：");
                lines.AddRange(diffs.Take(10).Select(r => $"・{r.ItemDisplay}：{r.BookDisplay} → {QuantityText.Format(r.Counted!.Value)}（{r.DiffDisplay}）"));
                if (diffs.Count > 10) lines.Add($"…另外還有 {diffs.Count - 10} 項");
            }
            var ok = MessageBox.Show(this, string.Join("\n", lines), title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
            if (ok != MessageBoxResult.OK) return;

            _result = new StocktakeResult(date, counted.Select(r => (r.Item, r.Counted!.Value)).ToList());
            _finishing = true;
            DialogResult = true;
        }

        private void Warn(string message, Control focus)
        {
            MessageBox.Show(this, message, "盤點日期", MessageBoxButton.OK, MessageBoxImage.Warning);
            focus.Focus();
            if (focus is TextBox t) t.SelectAll();
        }

        /// <summary>已經點了一些卻要關掉：先確認，避免辛苦點的數字不見。</summary>
        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_finishing || !_rows.Any(r => r.IsCounted)) return;
            var ok = MessageBox.Show(this, "已經填的實點數量不會儲存，確定要離開盤點嗎？", "離開盤點",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes) e.Cancel = true;
        }
    }
}
