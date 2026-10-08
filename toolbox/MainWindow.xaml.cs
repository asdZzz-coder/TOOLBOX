using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using toolbox.Models;
using toolbox.Services;

namespace toolbox
{
    public partial class MainWindow : Window
    {
        private const string AppTitle = "工具盤點";

        private readonly ObservableCollection<ToolItem> _items;
        private readonly ICollectionView _view;
        private readonly UpdateService _updater = new();

        // 分類：清單（含空分類）與左側目前選的篩選
        private List<string> _categories;
        private CategoryFilter _categoryFilter = CategoryFilter.All;
        private bool _rebuildingCategories;

        // 預設單位：單位欄下拉面板的選項（使用者排的順序）
        private List<string> _units;

        // 紀錄與借出單（直接就是存檔裡的那兩份清單）
        private readonly List<StockRecord> _records;
        private readonly List<Loan> _loans;

        public MainWindow()
        {
            InitializeComponent();
            // 小螢幕（例如筆電）上不要讓視窗超出可用範圍
            Height = Math.Min(Height, SystemParameters.WorkArea.Height - 20);
            Width = Math.Min(Width, SystemParameters.WorkArea.Width - 20);
            var data = DataStore.Load();
            _items = new ObservableCollection<ToolItem>(data.Items);
            _categories = CategoryService.Merge(data.Categories, _items);
            _units = UnitService.Clean(data.Units);
            _records = data.Records;
            _loans = data.Loans;
            RelinkStock();
            _view = CollectionViewSource.GetDefaultView(_items);
            _view.SortDescriptions.Add(new SortDescription(nameof(ToolItem.Name), ListSortDirection.Ascending));
            ToolList.ItemsSource = _view;
            RebuildCategories();
            ApplyFilter();
            ClearForm();
            UpdateStatus();
            ThemeService.ThemeChanged += OnThemeChanged; // 切換主題（或系統深淺色改變）時更新標題列與按鈕
            UpdateThemeButton();
        }

        // ---------- 主題：跟隨系統 / 淺色 / 深色 ----------

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            ThemeService.Cycle();
            StatusText.Text = $"主題：{ThemeName(ThemeService.Mode)}";
        }

        private static string ThemeName(AppTheme mode) => mode switch
        {
            AppTheme.Light => "淺色",
            AppTheme.Dark => "深色",
            _ => "跟隨系統",
        };

        private void OnThemeChanged()
        {
            UpdateThemeButton();
            ApplyTitleBar();
        }

        private void UpdateThemeButton()
        {
            ThemeIcon.Text = ThemeService.Mode switch
            {
                AppTheme.Light => "", // 太陽
                AppTheme.Dark => "",  // 月亮
                _ => "",              // 電腦（跟隨系統）
            };
            ThemeButton.ToolTip = $"主題：{ThemeName(ThemeService.Mode)}（按一下切換）";
        }

        // ---------- Windows 11：標題列底色與視窗背景同色，看起來是一整片 ----------

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyTitleBar();
        }

        private void ApplyTitleBar() => WindowTheme.ApplyTitleBar(this);

        // ---------- 啟動時檢查更新（詢問使用者） ----------

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CleanupService.RunInBackground(); // 清掉更新後遺留的舊檔
            DesktopShortcutService.TidyUp(_updater.IsInstalled);     // 更新後：重複捷徑只留最新的、工作列釘選改指向新版
            DesktopShortcutService.EnsureOnce(_updater.IsInstalled); // 安裝版第一次開啟時補上桌面捷徑

            await CheckForUpdateAsync(manual: false);
        }

        private void Shortcut_Click(object sender, RoutedEventArgs e)
        {
            const string title = "桌面捷徑";
            try
            {
                if (DesktopShortcutService.Create(_updater.IsInstalled) == ShortcutResult.SourceNotFound)
                {
                    MessageBox.Show("找不到安裝版的開始功能表捷徑，請重新執行「安裝.cmd」後再試。", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                StatusText.Text = "已在桌面建立捷徑";
                MessageBox.Show("已在桌面建立捷徑", title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or COMException)
            {
                MessageBox.Show($"建立桌面捷徑失敗：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdateAsync(manual: true);
        }

        private async Task CheckForUpdateAsync(bool manual)
        {
            const string title = "檢查更新";
            if (!_updater.IsInstalled)
            {
                if (manual)
                    MessageBox.Show("目前是開發版（非安裝版），無法線上更新。", title);
                return;
            }

            try
            {
                var info = await _updater.CheckAsync();
                if (info == null)
                {
                    if (manual) MessageBox.Show($"目前已是最新版本（{_updater.CurrentVersion}）。", title);
                    return;
                }

                var answer = MessageBox.Show(
                    $"發現新版本 {info.Version}（目前 {_updater.CurrentVersion}）。\n\n是否現在更新？更新完成後程式會自動重新啟動。",
                    "有新版本", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;

                StatusText.Text = "下載更新中…";
                await _updater.DownloadAndLaunchAsync(info, p => Dispatcher.Invoke(() => StatusText.Text = $"下載更新中… {p}%"));
                // 安裝程式已啟動，結束本程式讓它能覆蓋檔案；安裝完成後會自動重新開啟
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                UpdateStatus();
                // 啟動時的自動檢查失敗（例如沒網路）不打擾使用者
                if (manual) MessageBox.Show($"檢查更新失敗：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------- 搜尋 / 選取 ----------

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        /// <summary>清單只顯示左側選的分類裡、符合搜尋文字的工具。</summary>
        private void ApplyFilter()
        {
            var keyword = SearchBox.Text.Trim();
            var category = _categoryFilter;
            _view.Filter = o => o is ToolItem m && category.Matches(m) &&
                (keyword.Length == 0 ||
                 m.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                 m.Spec.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                 m.Unit.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                 m.Note.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            UpdateEmptyHint();
        }

        private void UpdateEmptyHint() =>
            EmptyText.Text = _items.Count == 0
                ? "還沒有任何工具\n在右邊填寫資料後按「新增」"
                : _categoryFilter.Kind == CategoryFilterKind.Scrapped && SearchBox.Text.Trim().Length == 0
                    ? "沒有報廢的工具"
                    : "沒有符合的工具";

        // ---------- 分類 ----------

        private const string AllDisplay = "全部工具";
        private const string UncategorizedDisplay = "未分類";
        private const string ScrappedDisplay = "已報廢";

        /// <summary>依目前資料重建左側分類清單（含數量）與表單的分類下拉選單，並保留原本的選取。</summary>
        private void RebuildCategories()
        {
            var list = new List<CategoryItem> { new(CategoryFilter.All, AllDisplay, _items.Count(CategoryFilter.All.Matches)) };
            list.AddRange(_categories.Select(c => new CategoryItem(CategoryFilter.Of(c), c, _items.Count(CategoryFilter.Of(c).Matches))));
            list.Add(new CategoryItem(CategoryFilter.Uncategorized, UncategorizedDisplay, _items.Count(CategoryFilter.Uncategorized.Matches)));
            int scrapped = _items.Count(CategoryFilter.Scrapped.Matches);
            if (scrapped > 0) list.Add(new CategoryItem(CategoryFilter.Scrapped, ScrappedDisplay, scrapped));

            var current = list.FirstOrDefault(i => i.Filter.SameAs(_categoryFilter)) ?? list[0];
            _rebuildingCategories = true;
            CategoryList.ItemsSource = list;
            CategoryList.SelectedItem = current;
            _rebuildingCategories = false;
            if (current.Filter != _categoryFilter) // 原本選的分類被刪掉了（或已報廢清空了）→ 回到「全部工具」
            {
                _categoryFilter = current.Filter;
                ApplyFilter();
            }

            var formCategory = CategoryService.Canonical(_categories, CategoryBox.SelectedValue as string);
            CategoryBox.ItemsSource = CategoryOptions();
            CategoryBox.SelectedValue = formCategory;
        }

        private List<CategoryOption> CategoryOptions() =>
            _categories.Select(c => new CategoryOption(c, c)).Prepend(new CategoryOption("", UncategorizedDisplay)).ToList();

        /// <summary>新增工具時預設放進左側目前選的分類。</summary>
        private string DefaultCategory => _categoryFilter.Kind == CategoryFilterKind.Category ? _categoryFilter.Name : "";

        private void SetFormCategory(string category) => CategoryBox.SelectedValue = CategoryService.Canonical(_categories, category);

        private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_rebuildingCategories || CategoryList.SelectedItem is not CategoryItem item) return;
            bool hadSelection = ToolList.SelectedItem != null;
            _categoryFilter = item.Filter;
            ApplyFilter();
            if (ToolList.SelectedItem != null) return;
            // 原本選的工具不在這個分類 → 清空表單，準備在這個分類新增
            if (hadSelection) ClearForm();
            else SetFormCategory(DefaultCategory);
        }

        private static string? CategoryNameMessage(string name, IEnumerable<string> existing, string? renaming = null) =>
            CategoryService.Validate(name, existing, renaming) switch
            {
                CategoryNameError.Empty => "請輸入分類名稱。",
                CategoryNameError.TooLong => $"分類名稱最多 {CategoryService.MaxNameLength} 個字。",
                CategoryNameError.Duplicate => "已經有同名的分類。",
                _ => null,
            };

        /// <summary>詢問名稱並建立分類；取消則回傳 null。</summary>
        private string? CreateCategory()
        {
            var name = InputDialog.Ask(this, "新增分類", "分類名稱", "", n => CategoryNameMessage(n, _categories));
            if (name == null) return null;
            _categories = CategoryService.Merge(_categories.Append(name), []);
            return name;
        }

        private void NewCategory_Click(object sender, RoutedEventArgs e)
        {
            var name = CreateCategory();
            if (name == null) return;
            _categoryFilter = CategoryFilter.Of(name); // 建好就切過去
            PersistAndRefresh();
            if (ToolList.SelectedItem == null) SetFormCategory(DefaultCategory); // 接著新增的工具直接放進這個分類
            StatusText.Text = $"已新增分類「{name}」";
        }

        private void RenameCategory(string oldName)
        {
            var name = InputDialog.Ask(this, "重新命名分類", "分類名稱", oldName, n => CategoryNameMessage(n, _categories, oldName));
            if (name == null || name == oldName) return;
            bool viewing = _categoryFilter.SameAs(CategoryFilter.Of(oldName));
            var formCategory = CategoryBox.SelectedValue as string;
            (_categories, _) = CategoryService.Rename(_categories, _items, oldName, name);
            if (viewing) _categoryFilter = CategoryFilter.Of(name);
            if (CategoryService.SameName(formCategory, oldName)) _pendingFormCategory = name;
            PersistAndRefresh();
            StatusText.Text = $"分類已改名為「{name}」";
        }

        private void DeleteCategory(string name)
        {
            int count = _items.Count(CategoryFilter.Of(name).Contains);
            var ok = MessageBox.Show(this,
                $"確定要刪除分類「{name}」嗎？\n\n裡面的 {count} 項工具不會被刪除，會移到「{UncategorizedDisplay}」。",
                "刪除分類", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes) return;
            (_categories, _) = CategoryService.Delete(_categories, _items, name);
            PersistAndRefresh();
            StatusText.Text = $"已刪除分類「{name}」";
        }

        // 重新整理後下拉選單才有新名稱，所以先記下來，PersistAndRefresh 重建選單後再選
        private string? _pendingFormCategory;

        private void RenameCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: CategoryItem { IsUserCategory: true } item })
                RenameCategory(item.Filter.Name);
        }

        private void DeleteCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: CategoryItem { IsUserCategory: true } item })
                DeleteCategory(item.Filter.Name);
        }

        /// <summary>從右鍵點到的位置往上找出清單項目（點在空白處則為 null）。</summary>
        private static T? ItemUnderMouse<T>(ContextMenuEventArgs e) where T : class
        {
            for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is ListBoxItem { DataContext: T item }) return item;
            return null;
        }

        private static MenuItem MenuEntry(string header, Action onClick, bool isChecked = false, Brush? foreground = null)
        {
            var mi = new MenuItem { Header = header, IsChecked = isChecked };
            if (foreground != null) mi.Foreground = foreground;
            mi.Click += (_, _) => onClick();
            return mi;
        }

        // 分類右鍵：重新命名 / 刪除（「全部工具」「未分類」「已報廢」沒有選單）
        private void CategoryList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (ItemUnderMouse<CategoryItem>(e) is not { IsUserCategory: true } item) { e.Handled = true; return; }
            var menu = CategoryList.ContextMenu;
            menu.Items.Clear();
            menu.Items.Add(MenuEntry("重新命名分類", () => RenameCategory(item.Filter.Name)));
            menu.Items.Add(MenuEntry("刪除分類", () => DeleteCategory(item.Filter.Name),
                foreground: (Brush)FindResource("DangerBrush")));
        }

        // 工具右鍵：購入、報廢、借出、歸還、查看紀錄、移到分類（目前所在的分類打勾）
        private void ToolList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (ItemUnderMouse<ToolItem>(e) is not { } item) { e.Handled = true; return; }
            ToolList.SelectedItem = item;
            var menu = ToolList.ContextMenu;
            menu.Items.Clear();
            menu.Items.Add(MenuEntry("購入…", () => DoStock(RecordKind.Buy, item)));
            if (item.Quantity > 0)
            {
                menu.Items.Add(MenuEntry("報廢…", () => DoStock(RecordKind.Scrap, item), foreground: (Brush)FindResource("DangerBrush")));
                menu.Items.Add(MenuEntry("借出…", () => DoStock(RecordKind.Lend, item)));
            }
            if (item.HasLent) menu.Items.Add(MenuEntry("歸還…", () => ReturnLoan(item)));
            menu.Items.Add(MenuEntry("查看紀錄", () => OpenRecords(item, loansTab: false)));
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "移到分類", IsEnabled = false, FontSize = 12 });
            foreach (var option in CategoryOptions())
                menu.Items.Add(MenuEntry(option.Display, () => MoveItem(item, option.Name),
                    isChecked: CategoryService.SameName(item.Category, option.Name)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("新分類…", () =>
            {
                var name = CreateCategory();
                if (name != null) MoveItem(item, name);
            }));
        }

        private void MoveItem(ToolItem item, string category)
        {
            item.Category = category;
            item.UpdatedAt = DateTime.Now;
            if (ToolList.SelectedItem == item) _pendingFormCategory = category;
            PersistAndRefresh();
            StatusText.Text = $"「{item.Name}」已移到「{(category.Length == 0 ? UncategorizedDisplay : category)}」";
        }

        private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ToolList.SelectedItem is ToolItem m)
                FillForm(m);
            UpdateItemInfo();
        }

        // ---------- 表單 ----------

        private void FillForm(ToolItem m)
        {
            NameBox.Text = m.Name;
            SetFormCategory(m.Category);
            SpecBox.Text = m.Spec;
            QtyBox.Text = QuantityText.Format(m.Quantity);
            UnitBox.Text = m.Unit;
            PurchasedBox.Text = DateText.Format(m.PurchasedAt);
            ShowChecked(m);
            NoteBox.Text = m.Note;
            ShowUpdated(m);
        }

        private void ShowUpdated(ToolItem m) => UpdatedText.Text = $"最後修改：{m.UpdatedAt:yyyy/MM/dd HH:mm}";

        private void ShowChecked(ToolItem? m) =>
            CheckedBox.Text = m?.LastCheckedAt is { } d ? $"{DateText.Format(d)}（{StockService.DaysAgo(d, DateTime.Now)}）" : m == null ? "" : "還沒盤點過";

        private void ClearForm()
        {
            NameBox.Clear();
            SetFormCategory(DefaultCategory);
            SpecBox.Clear();
            QtyBox.Text = "1";
            UnitBox.Clear();
            PurchasedBox.Text = DateText.Format(DateTime.Today);
            ShowChecked(null);
            NoteBox.Clear();
            UpdatedText.Text = "";
            LoanInfoText.Visibility = Visibility.Collapsed;
            ScrapInfoText.Visibility = Visibility.Collapsed;
        }

        private void ClearForm_Click(object sender, RoutedEventArgs e)
        {
            ToolList.SelectedItem = null;
            ClearForm();
            NameBox.Focus();
        }

        private void PurchasedToday_Click(object sender, RoutedEventArgs e) => PurchasedBox.Text = DateText.Format(DateTime.Today);

        private void QtyPlus_Click(object sender, RoutedEventArgs e) => StepFormQuantity(1);

        private void QtyMinus_Click(object sender, RoutedEventArgs e) => StepFormQuantity(-1);

        /// <summary>表單上的 ＋／－：只改輸入框，按「新增」或「儲存修改」才會寫入。</summary>
        private void StepFormQuantity(decimal delta)
        {
            if (!QuantityText.TryParse(QtyBox.Text, out var qty))
            {
                MessageBox.Show(QuantityError, "提示");
                return;
            }
            QtyBox.Text = QuantityText.Format(QuantityText.Adjust(qty, delta));
        }

        // ---------- 動作：購入 / 報廢 / 借出 / 歸還 ----------

        /// <summary>紀錄與借出單重新對上工具，並更新每項工具的借出中、報廢數量。</summary>
        private void RelinkStock()
        {
            StockService.Relink(_items, _loans.Concat<IItemRef>(_records).ToList());
            StockService.ApplyDerived(_items, _records, _loans);
        }

        private void Buy_Click(object sender, RoutedEventArgs e) => DoStock(RecordKind.Buy);

        private void Scrap_Click(object sender, RoutedEventArgs e) => DoStock(RecordKind.Scrap);

        private void Lend_Click(object sender, RoutedEventArgs e) => DoStock(RecordKind.Lend);

        private void Return_Click(object sender, RoutedEventArgs e) => ReturnLoan(ToolList.SelectedItem as ToolItem);

        /// <summary>購入、報廢、借出：問數量、日期（與原因 / 借用人），改在庫、記一筆紀錄並存檔。</summary>
        private void DoStock(RecordKind kind, ToolItem? item = null)
        {
            var name = StockService.KindName(kind);
            item ??= ToolList.SelectedItem as ToolItem;
            if (item == null)
            {
                MessageBox.Show($"請先在清單中選取要{name}的工具。", name);
                return;
            }
            if (!StockService.Adds(kind) && item.Quantity <= 0)
            {
                var hint = item.HasLent ? $"（{item.LentDisplay}，要先按「歸還」）" : "";
                MessageBox.Show($"「{item.Name}」目前在庫是 0，沒有可以{name}的數量{hint}。", name);
                return;
            }

            var choices = kind == RecordKind.Scrap
                ? StockService.ReasonChoices(_records)
                : StockService.Recent(_records, r => r.Kind == kind, r => r.Purpose);
            var borrowers = StockService.Recent(_records, r => r.Kind == RecordKind.Lend, r => r.Borrower);
            var input = StockDialog.Ask(this, kind, item, choices, borrowers);
            if (input == null) return;

            var time = DateText.At(input.Date, DateTime.Now);
            switch (kind)
            {
                case RecordKind.Buy:
                    _records.Add(StockService.Buy(item, input.Quantity, input.Purpose, time));
                    break;
                case RecordKind.Scrap:
                    _records.Add(StockService.Scrap(item, input.Quantity, input.Purpose, time));
                    break;
                case RecordKind.Lend:
                    var (loan, record) = StockService.Lend(item, input.Quantity, input.Borrower, input.Purpose, time);
                    _loans.Add(loan);
                    _records.Add(record);
                    break;
            }
            AfterStockChange(item);
            var amount = (QuantityText.Format(input.Quantity) + " " + item.Unit.Trim()).Trim();
            StatusText.Text = kind switch
            {
                RecordKind.Lend => $"已借出「{item.Name}」{amount} 給 {input.Borrower}，在庫剩 {item.QuantityDisplay}",
                RecordKind.Scrap when item.IsScrapped => $"已報廢「{item.Name}」{amount}（{input.Purpose}），全部報廢，已移到「{ScrappedDisplay}」",
                RecordKind.Scrap => $"已報廢「{item.Name}」{amount}（{input.Purpose}），在庫剩 {item.QuantityDisplay}",
                _ => $"已購入「{item.Name}」{amount}（{DateText.Format(input.Date)}），在庫 {item.QuantityDisplay}",
            };
        }

        /// <summary>
        /// 歸還：item 有值時只列這項工具的借出，否則列出全部借出未還的；preselect 是預先選好的那筆。
        /// 有歸還時回傳 true。
        /// </summary>
        private bool ReturnLoan(ToolItem? item, Loan? preselect = null)
        {
            const string title = "歸還";
            var open = _loans.Where(l => l.IsOpen && (item == null || l.ItemId == item.Id)).OrderBy(l => l.LentAt).ToList();
            if (open.Count == 0)
            {
                MessageBox.Show(item == null ? "目前沒有借出未還的工具。" : $"「{item.Name}」目前沒有借出未還。", title);
                return false;
            }
            // 從「紀錄」視窗按歸還時，對話框要蓋在那個視窗上面
            var owner = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
            var input = ReturnDialog.Ask(owner, open, preselect);
            if (input == null) return false;

            var target = StockService.FindItem(_items, input.Loan);
            _records.Add(StockService.Return(input.Loan, target, input.Quantity, input.Note, DateTime.Now));
            AfterStockChange(target);
            var amount = (QuantityText.Format(input.Quantity) + " " + input.Loan.Unit.Trim()).Trim();
            StatusText.Text = target == null
                ? $"{input.Loan.Borrower} 已歸還「{input.Loan.ItemName}」{amount}（這項工具已被刪除，不加回在庫）"
                : $"{input.Loan.Borrower} 已歸還「{target.Name}」{amount}，在庫 {target.QuantityDisplay}";
            return true;
        }

        /// <summary>在庫變動後：表單上的數量、購入日期跟著更新（其他還沒儲存的修改不動），然後存檔。</summary>
        private void AfterStockChange(ToolItem? item)
        {
            if (item != null && ToolList.SelectedItem == item)
            {
                QtyBox.Text = QuantityText.Format(item.Quantity);
                PurchasedBox.Text = DateText.Format(item.PurchasedAt);
                ShowChecked(item);
                ShowUpdated(item);
            }
            PersistAndRefresh();
        }

        /// <summary>表單上「動作」下方的小字：借給了誰、還有多少沒還；報廢過幾次、為什麼。</summary>
        private void UpdateItemInfo()
        {
            var item = ToolList.SelectedItem as ToolItem;

            var open = item == null ? [] : _loans.Where(l => l.IsOpen && l.ItemId == item.Id).ToList();
            if (open.Count == 0)
                LoanInfoText.Visibility = Visibility.Collapsed;
            else
            {
                var who = string.Join("、", open.GroupBy(l => l.Borrower, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => $"{g.Key} {QuantityText.Format(g.Sum(l => l.Outstanding))}"));
                LoanInfoText.Text = $"借出中 {(QuantityText.Format(item!.LentQuantity) + " " + item.Unit.Trim()).Trim()}：{who}";
                LoanInfoText.ToolTip = LoanInfoText.Text;
                LoanInfoText.Visibility = Visibility.Visible;
            }

            var scraps = item == null ? [] : _records.Where(r => r.Kind == RecordKind.Scrap && r.ItemId == item.Id).OrderBy(r => r.Time).ToList();
            if (scraps.Count == 0)
                ScrapInfoText.Visibility = Visibility.Collapsed;
            else
            {
                var each = scraps.Select(r => $"{DateText.Format(r.Time)} {r.Purpose} {QuantityText.Format(r.Quantity)}");
                ScrapInfoText.Text = $"已報廢 {(QuantityText.Format(item!.ScrappedQuantity) + " " + item.Unit.Trim()).Trim()}：{string.Join("、", each)}";
                ScrapInfoText.ToolTip = string.Join("\n", scraps.Select(r => $"{DateText.Format(r.Time)}　報廢 {QuantityText.Format(r.Quantity)}　{r.Purpose}"));
                ScrapInfoText.Visibility = Visibility.Visible;
            }
        }

        private void OpenRecords(ToolItem? item, bool loansTab) =>
            RecordsWindow.Open(this, _records, _loans, item, loansTab, loan => ReturnLoan(null, loan));

        private void Records_Click(object sender, RoutedEventArgs e) =>
            OpenRecords(null, loansTab: _loans.Any(l => l.IsOpen));

        private void ItemRecords_Click(object sender, RoutedEventArgs e) =>
            OpenRecords(ToolList.SelectedItem as ToolItem, loansTab: false);

        // ---------- 盤點 ----------

        /// <summary>盤點左側目前選的分類（全部工具 / 某個分類 / 未分類）裡的工具，已報廢的不用盤點。</summary>
        private void Stocktake_Click(object sender, RoutedEventArgs e)
        {
            const string title = "盤點";
            var filter = _categoryFilter.Kind == CategoryFilterKind.Scrapped ? CategoryFilter.All : _categoryFilter;
            var scope = filter.Kind switch
            {
                CategoryFilterKind.All => AllDisplay,
                CategoryFilterKind.Uncategorized => UncategorizedDisplay,
                _ => $"分類「{filter.Name}」",
            };
            var items = _items.Where(filter.Matches).ToList();
            if (items.Count == 0)
            {
                MessageBox.Show(_items.Count == 0 ? "還沒有任何工具可以盤點。" : $"{scope}裡沒有工具可以盤點。", title);
                return;
            }

            var result = StocktakeWindow.Open(this, items, scope);
            if (result == null) return;

            var now = DateTime.Now;
            int changed = 0;
            foreach (var (item, counted) in result.Counts)
            {
                if (StockService.Count(item, counted, DateText.At(result.Date, now)) is { } record)
                {
                    _records.Add(record);
                    changed++;
                }
            }
            if (ToolList.SelectedItem is ToolItem selected && result.Counts.Any(c => c.Item == selected))
            {
                QtyBox.Text = QuantityText.Format(selected.Quantity);
                ShowChecked(selected);
                ShowUpdated(selected);
            }
            PersistAndRefresh();
            StatusText.Text = $"盤點完成（{DateText.Format(result.Date)}）：點了 {result.Counts.Count} 項，"
                + (changed == 0 ? "全部相符" : $"{changed} 項數量不符，已改成實點數量");
        }

        // ---------- 預設單位 ----------

        public const double UnitPopupWidth = 268;

        // 面板開著時按箭頭：滑鼠一按下面板就先關了，接著的 Click 不要又把它打開
        private DateTime _unitPopupClosedAt;

        private void UnitPick_Click(object sender, RoutedEventArgs e)
        {
            if ((DateTime.Now - _unitPopupClosedAt).TotalMilliseconds < 250) return;
            BuildUnitChips();
            UnitPopup.HorizontalOffset = UnitBox.ActualWidth - UnitPopupWidth; // 面板右緣對齊單位欄
            UnitPopup.IsOpen = true;
        }

        private void UnitPopup_Closed(object? sender, EventArgs e) => _unitPopupClosedAt = DateTime.Now;

        /// <summary>每次打開面板時依目前清單產生單位方塊；單位欄現在填的那個以強調色標示。</summary>
        private void BuildUnitChips()
        {
            Chips.Fill(UnitChips, _units, PickUnit, highlighted: UnitBox.Text.Trim());
            UnitChipsEmpty.Visibility = _units.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>只填入表單，跟其他欄位一樣要按「新增」或「儲存修改」才會寫入。</summary>
        private void PickUnit(string unit)
        {
            UnitBox.Text = unit;
            UnitPopup.IsOpen = false;
            UnitBox.Focus();
            UnitBox.CaretIndex = UnitBox.Text.Length;
        }

        private void ManageUnits_Click(object sender, RoutedEventArgs e)
        {
            UnitPopup.IsOpen = false;
            var units = UnitsDialog.Edit(this, _units);
            if (units == null) return;
            _units = units;
            PersistAndRefresh();
            StatusText.Text = $"預設單位已更新（共 {_units.Count} 個）";
        }

        private const string QuantityError = "數量請輸入 0 或正數（可以有小數，例如 0.5）。";

        private ToolItem? ReadForm()
        {
            if (NameBox.Text.Trim().Length == 0)
            {
                MessageBox.Show("請輸入工具名稱。", "提示");
                NameBox.Focus();
                return null;
            }
            if (!QuantityText.TryParse(QtyBox.Text, out var qty))
            {
                MessageBox.Show(QuantityError, "提示");
                QtyBox.Focus();
                QtyBox.SelectAll();
                return null;
            }
            DateTime? purchased = null;
            if (PurchasedBox.Text.Trim().Length > 0)
            {
                var error = DateText.Check(PurchasedBox.Text, DateTime.Today, out var d);
                if (error != DateError.None)
                {
                    MessageBox.Show(error == DateError.Future
                        ? "購入日期不能是未來的日子。"
                        : $"看不懂這個購入日期，{DateText.Hint}；不知道可以空白。", "提示");
                    PurchasedBox.Focus();
                    PurchasedBox.SelectAll();
                    return null;
                }
                purchased = d;
            }
            return new ToolItem
            {
                Name = NameBox.Text.Trim(),
                Category = CategoryBox.SelectedValue as string ?? "",
                Spec = SpecBox.Text.Trim(),
                Quantity = qty,
                Unit = UnitBox.Text.Trim(),
                PurchasedAt = purchased,
                Note = NoteBox.Text,
                UpdatedAt = DateTime.Now,
            };
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var item = ReadForm();
            if (item == null) return;
            if (_items.FirstOrDefault(x => ExcelService.SameTool(x, item)) is { } existing)
            {
                var ok = MessageBox.Show(
                    $"已經有「{existing.Name}」{(existing.Spec.Length > 0 ? $"（{existing.Spec}）" : "")}，目前在庫 {existing.QuantityDisplay}。\n\n仍要再新增一筆嗎？\n（又買了同樣的工具，請選取原本那筆後按「購入」）",
                    "重複的工具", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (ok != MessageBoxResult.Yes) return;
            }
            _items.Add(item);
            // 存到別的分類時，左側切到那個分類，才看得到剛新增的這筆
            if (!_categoryFilter.Matches(item))
                _categoryFilter = item.Category.Length == 0 ? CategoryFilter.Uncategorized : CategoryFilter.Of(item.Category);
            PersistAndRefresh();
            ToolList.SelectedItem = item;
            ToolList.ScrollIntoView(item);
            StatusText.Text = $"已新增「{item.Name}」";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (ToolList.SelectedItem is not ToolItem selected)
            {
                MessageBox.Show("請先在清單中選取要修改的工具；要新增請按「新增」。", "提示");
                return;
            }
            var edited = ReadForm();
            if (edited == null) return;

            selected.Name = edited.Name;
            selected.Category = edited.Category;
            selected.Spec = edited.Spec;
            selected.Quantity = edited.Quantity;
            selected.Unit = edited.Unit;
            selected.PurchasedAt = edited.PurchasedAt;
            selected.Note = edited.Note;
            selected.UpdatedAt = edited.UpdatedAt;
            PersistAndRefresh();
            if (ToolList.SelectedItem == selected) ShowUpdated(selected);
            StatusText.Text = $"已儲存「{selected.Name}」";
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (ToolList.SelectedItem is not ToolItem selected) return;
            // 還有借出沒還：借出單會保留，之後仍可在「紀錄」按歸還（只是不會加回在庫）
            var lentNote = selected.HasLent ? $"\n\n注意：這項工具還有 {(QuantityText.Format(selected.LentQuantity) + " " + selected.Unit.Trim()).Trim()} 借出沒還，借出紀錄會保留。" : "";
            var ok = MessageBox.Show($"確定要刪除「{selected.Name}」嗎？\n（壞掉或遺失請用「報廢」，才會留下報廢日期與原因）{lentNote}", "刪除工具",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes) return;

            _items.Remove(selected);
            ClearForm();
            PersistAndRefresh();
            StatusText.Text = $"已刪除「{selected.Name}」";
        }

        private void DeleteAll_Click(object sender, RoutedEventArgs e)
        {
            int count = _items.Count;
            if (count == 0)
            {
                MessageBox.Show("目前沒有任何工具。", "全部刪除");
                return;
            }

            // 兩段確認，且預設按鈕都是「否」，避免手滑按 Enter 就刪掉
            var first = MessageBox.Show(
                $"確定要刪除全部 {count} 項工具嗎？\n購入、報廢、借出、盤點紀錄也會一起清除。\n\n建議先用「匯出 Excel」備份。",
                "全部刪除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (first != MessageBoxResult.Yes) return;

            var second = MessageBox.Show(
                $"再確認一次：{count} 項工具將被永久刪除，無法復原。\n\n真的要刪除嗎？",
                "最後確認", MessageBoxButton.YesNo, MessageBoxImage.Stop, MessageBoxResult.No);
            if (second != MessageBoxResult.Yes) return;

            _items.Clear();
            _records.Clear();
            _loans.Clear();
            SearchBox.Clear();
            ClearForm();
            PersistAndRefresh();
            StatusText.Text = $"已刪除全部 {count} 項工具";
        }

        private void PersistAndRefresh()
        {
            var selected = ToolList.SelectedItem as ToolItem;
            // 工具用到、但清單裡沒有的分類（例如匯入的）一併補進清單
            _categories = CategoryService.Merge(_categories, _items);
            RelinkStock();
            try
            {
                DataStore.Save(new ToolData
                {
                    Items = _items.ToList(), Categories = _categories, Units = _units, Records = _records, Loans = _loans,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"儲存失敗，資料尚未寫入硬碟：{ex.Message}", "儲存", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RebuildCategories();
            if (_pendingFormCategory != null)
            {
                SetFormCategory(_pendingFormCategory);
                _pendingFormCategory = null;
            }
            ApplyFilter();
            // 重新篩選會清掉選取，重新選回同一筆（改名後排序位置會變）
            if (selected != null && _items.Contains(selected))
            {
                ToolList.SelectedItem = selected;
                // 移到別的分類（或全部報廢）後不在目前的清單裡了 → 清空表單，避免誤按「新增」複製一筆
                if (ToolList.SelectedItem == null) ClearForm();
            }
            UpdateItemInfo();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            // 視窗標題列顯示版本：安裝版為「工具盤點 v1.0.1」，直接從 Visual Studio 執行則標示開發版
            Title = _updater.IsInstalled ? $"{AppTitle} v{_updater.CurrentVersion}" : $"{AppTitle}（開發版）";
            int active = _items.Count(CategoryFilter.All.Matches);
            int scrapped = _items.Count - active;
            int loans = _loans.Count(l => l.IsOpen);
            CountText.Text = $"共 {active} 項工具 · {_categories.Count} 個分類"
                + (loans > 0 ? $" · 借出中 {loans} 筆" : "")
                + (scrapped > 0 ? $" · 已報廢 {scrapped} 項" : "");
            StatusText.Text = $"版本 {_updater.CurrentVersion}";
        }

        // ---------- Excel 匯出 / 匯入 ----------

        private const string ExcelFilter = "Excel 檔案 (*.xlsx)|*.xlsx";

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            const string title = "匯出 Excel";
            var dlg = new SaveFileDialog
            {
                Filter = ExcelFilter,
                FileName = $"工具清單_{DateTime.Now:yyyyMMdd}",
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var sorted = _items.OrderBy(i => i.IsScrapped)
                                   .ThenBy(i => i.Category, StringComparer.CurrentCultureIgnoreCase)
                                   .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);
                ExcelService.Export(sorted, dlg.FileName, _records, _loans);
                MessageBox.Show($"已匯出 {_items.Count} 項工具、{_records.Count} 筆紀錄。", title);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"匯出失敗：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            const string title = "匯入 Excel";
            var dlg = new OpenFileDialog { Filter = ExcelFilter };
            if (dlg.ShowDialog() != true) return;

            List<ToolItem> imported;
            try
            {
                imported = ExcelService.Import(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"匯入失敗：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (imported.Count == 0)
            {
                MessageBox.Show($"檔案裡沒有可匯入的工具。\n\n第一列是標題，欄位順序：{string.Join("、", ExcelService.Headers.Take(ExcelService.ImportColumns))}。", title);
                return;
            }

            var mode = MessageBox.Show(
                $"讀到 {imported.Count} 項工具。\n\n是：加入到現有資料（名稱與規格相同的會更新數量等內容）\n否：清空現有資料後再匯入\n取消：不匯入",
                title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (mode == MessageBoxResult.Cancel) return;

            if (mode == MessageBoxResult.No)
            {
                var ok = MessageBox.Show($"現有的 {_items.Count} 項工具會被清空，確定嗎？", title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (ok != MessageBoxResult.Yes) return;
                _items.Clear();
            }

            int added = 0, updated = 0;
            foreach (var item in imported)
            {
                if (_items.FirstOrDefault(x => ExcelService.SameTool(x, item)) is { } existing)
                {
                    existing.Category = item.Category;
                    existing.Quantity = item.Quantity;
                    existing.Unit = item.Unit;
                    existing.PurchasedAt = item.PurchasedAt ?? existing.PurchasedAt;
                    existing.LastCheckedAt = item.LastCheckedAt ?? existing.LastCheckedAt;
                    existing.Note = item.Note;
                    existing.UpdatedAt = DateTime.Now;
                    updated++;
                    continue;
                }
                _items.Add(item);
                added++;
            }

            ToolList.SelectedItem = null;
            ClearForm();
            PersistAndRefresh();
            MessageBox.Show($"匯入完成：新增 {added} 項、更新 {updated} 項。", title);
        }
    }
}
