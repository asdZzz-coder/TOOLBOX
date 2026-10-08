using System.Windows;
using System.Windows.Controls;
using toolbox.Models;
using toolbox.Services;

namespace toolbox
{
    /// <summary>購入 / 報廢 / 借出 對話框填的內容；Date 是使用者填的日期（不含時間）。</summary>
    public record StockInput(decimal Quantity, DateTime Date, string Purpose, string Borrower);

    public partial class StockDialog : Window
    {
        private readonly RecordKind _kind;
        private readonly ToolItem _item;
        private StockInput? _result;

        private StockDialog(RecordKind kind, ToolItem item, IReadOnlyList<string> purposeChoices, IReadOnlyList<string> recentBorrowers)
        {
            InitializeComponent();
            _kind = kind;
            _item = item;
            var name = StockService.KindName(kind);
            var unit = item.Unit.Trim();
            Title = $"{name}工具";
            ItemText.Text = StockService.ItemDisplay(item.Name, item.Spec);
            ItemText.ToolTip = ItemText.Text;
            StockText.Text = $"目前在庫 {item.QuantityDisplay}" + (item.HasLent ? $" · {item.LentDisplay}" : "");
            KindText.Text = name;
            var (soft, strong) = StockStyles.Brushes(kind);
            KindBadge.SetResourceReference(Border.BackgroundProperty, soft);
            KindText.SetResourceReference(TextBlock.ForegroundProperty, strong);
            QtyLabel.Text = unit.Length > 0 ? $"{name}數量（{unit}）*" : $"{name}數量 *";
            DateLabel.Text = $"{name}日期 *";
            DateBox.Text = DateText.Format(DateTime.Today);
            DateBox.ToolTip = DateText.Hint;
            PurposeLabel.Text = kind switch
            {
                RecordKind.Scrap => "報廢原因 *",
                RecordKind.Lend => "用途（選填）",
                _ => "說明（選填，例如：在哪裡買的、價格）",
            };
            BorrowerPanel.Visibility = kind == RecordKind.Lend ? Visibility.Visible : Visibility.Collapsed;
            OkButton.Content = name;

            // 預設 1；要拿走的數量在庫不到 1 就預設全部
            bool removes = !StockService.Adds(kind);
            QtyBox.Text = removes && item.Quantity < 1 ? QuantityText.Format(item.Quantity) : "1";

            Chips.Fill(PurposeChips, purposeChoices, v => Pick(PurposeBox, v),
                caption: kind == RecordKind.Scrap ? null : "最近：", small: true);
            Chips.Fill(BorrowerChips, recentBorrowers, v => Pick(BorrowerBox, v), caption: "最近：", small: true);
            UpdatePreview();
            Loaded += (_, _) => { QtyBox.Focus(); QtyBox.SelectAll(); };
        }

        /// <summary>顯示對話框；按確定且通過檢查時回傳填的內容，取消則回傳 null。</summary>
        /// <param name="purposeChoices">原因 / 說明的快速選項（報廢原因、最近用過的說明或用途）。</param>
        public static StockInput? Ask(Window owner, RecordKind kind, ToolItem item,
            IReadOnlyList<string> purposeChoices, IReadOnlyList<string> recentBorrowers)
        {
            var dlg = new StockDialog(kind, item, purposeChoices, recentBorrowers) { Owner = owner };
            return dlg.ShowDialog() == true ? dlg._result : null;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            WindowTheme.ApplyTitleBar(this, "CardBrush");
        }

        private static void Pick(TextBox box, string value)
        {
            box.Text = value;
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }

        private void Step(decimal delta)
        {
            if (QuantityText.TryParse(QtyBox.Text, out var q))
                QtyBox.Text = QuantityText.Format(QuantityText.Adjust(q, delta));
        }

        private void QtyPlus_Click(object sender, RoutedEventArgs e) => Step(1);

        private void QtyMinus_Click(object sender, RoutedEventArgs e) => Step(-1);

        private void Today_Click(object sender, RoutedEventArgs e) => DateBox.Text = DateText.Format(DateTime.Today);

        private void Field_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ErrorText == null) return; // InitializeComponent 期間就會觸發 TextChanged
            ErrorText.Visibility = Visibility.Collapsed;
            if (sender == QtyBox) UpdatePreview();
        }

        /// <summary>數量下方預告做完之後的在庫，例如「報廢後在庫剩 2 支」。</summary>
        private void UpdatePreview()
        {
            if (PreviewText == null) return;
            if (!QuantityText.TryParse(QtyBox.Text, out var q) || q <= 0) { PreviewText.Text = ""; return; }
            bool adds = StockService.Adds(_kind);
            if (!adds && q > _item.Quantity)
            {
                PreviewText.Text = $"超過目前在庫（{_item.QuantityDisplay}）";
                return;
            }
            var after = QuantityText.Adjust(_item.Quantity, adds ? q : -q);
            var text = $"{StockService.KindName(_kind)}後在庫 {(QuantityText.Format(after) + " " + _item.Unit.Trim()).Trim()}";
            if (_kind == RecordKind.Scrap && after == 0 && !_item.HasLent) text += "，這項工具會移到「已報廢」";
            PreviewText.Text = text;
        }

        private void ShowError(string message, Control focus)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
            focus.Focus();
            if (focus is TextBox t) t.SelectAll();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!QuantityText.TryParse(QtyBox.Text, out var qty))
            {
                ShowError("數量請輸入正數（可以有小數，例如 0.5）。", QtyBox);
                return;
            }
            var name = StockService.KindName(_kind);
            switch (DateText.Check(DateBox.Text, DateTime.Today, out var date))
            {
                case DateError.Empty: ShowError($"請填{name}日期。", DateBox); return;
                case DateError.Invalid: ShowError($"看不懂這個日期，{DateText.Hint}。", DateBox); return;
                case DateError.Future: ShowError($"{name}日期不能是未來的日子。", DateBox); return;
            }
            switch (StockService.Validate(_kind, _item.Quantity, qty, PurposeBox.Text, BorrowerBox.Text))
            {
                case StockError.QuantityNotPositive: ShowError("請輸入大於 0 的數量。", QtyBox); return;
                case StockError.NotEnoughStock:
                    ShowError($"目前在庫只有 {_item.QuantityDisplay}，不夠{name}這麼多。" + (_item.HasLent ? "借出中的要先按「歸還」。" : ""), QtyBox);
                    return;
                case StockError.TooMuchStock: ShowError("數量太大了。", QtyBox); return;
                case StockError.ReasonRequired: ShowError("請寫上報廢原因（例如：壞掉、遺失）。", PurposeBox); return;
                case StockError.BorrowerRequired: ShowError("請寫上借給誰。", BorrowerBox); return;
                case StockError.TextTooLong: ShowError($"文字最多 {StockService.MaxTextLength} 個字。", PurposeBox); return;
            }
            _result = new StockInput(qty, date, CategoryService.Normalize(PurposeBox.Text), CategoryService.Normalize(BorrowerBox.Text));
            DialogResult = true;
        }
    }

    /// <summary>動作的代表色：購入、歸還綠色，報廢紅色，借出琥珀色，盤點主色。</summary>
    internal static class StockStyles
    {
        public static (string Soft, string Strong) Brushes(RecordKind kind) => kind switch
        {
            RecordKind.Buy or RecordKind.Return => ("SuccessSoftBrush", "SuccessBrush"),
            RecordKind.Scrap => ("DangerSoftBrush", "DangerBrush"),
            RecordKind.Lend => ("WarnSoftBrush", "WarnBrush"),
            _ => ("AccentSoftBrush", "AccentBrush"),
        };
    }
}
