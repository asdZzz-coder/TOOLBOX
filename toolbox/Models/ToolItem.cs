using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using toolbox.Services;

namespace toolbox.Models
{
    /// <summary>
    /// 一項工具：名稱、分類、規格 / 型號、在庫數量、單位、購入日期、最後盤點日期、備註。
    /// 借出、報廢的數量由紀錄算出（不存檔）。會通知畫面更新，動作之後清單能即時看到。
    /// </summary>
    public class ToolItem : INotifyPropertyChanged
    {
        private string _name = "";
        private string _category = "";
        private string _spec = "";
        private decimal _quantity;
        private string _unit = "";
        private string _note = "";
        private DateTime? _purchasedAt;
        private DateTime? _lastCheckedAt;
        private DateTime _updatedAt = DateTime.Now;
        private decimal _lentQuantity;
        private decimal _scrappedQuantity;
        private DateTime? _lastScrappedAt;
        private string _lastScrapReason = "";

        /// <summary>固定識別碼，紀錄與借出單靠它對應工具。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get => _name; set => Set(ref _name, value, nameof(Initial), nameof(AvatarColor)); }

        /// <summary>所屬分類；空字串表示「未分類」。</summary>
        public string Category { get => _category; set => Set(ref _category, value); }

        /// <summary>規格 / 型號。</summary>
        public string Spec { get => _spec; set => Set(ref _spec, value, nameof(Subtitle)); }

        /// <summary>在庫（可以用、沒借出）的數量。</summary>
        public decimal Quantity { get => _quantity; set => Set(ref _quantity, value, nameof(QuantityDisplay), nameof(IsEmpty), nameof(IsScrapped)); }

        public string Unit { get => _unit; set => Set(ref _unit, value, nameof(QuantityDisplay), nameof(LentDisplay)); }

        public string Note { get => _note; set => Set(ref _note, value, nameof(Subtitle)); }

        /// <summary>購入日期（第一次買進來的日期）；不知道時為 null。</summary>
        public DateTime? PurchasedAt { get => _purchasedAt; set => Set(ref _purchasedAt, value?.Date, nameof(Subtitle)); }

        /// <summary>最後一次盤點的日期；還沒盤點過為 null。</summary>
        public DateTime? LastCheckedAt { get => _lastCheckedAt; set => Set(ref _lastCheckedAt, value?.Date); }

        /// <summary>最後修改時間（新增、儲存、動作時更新）。</summary>
        public DateTime UpdatedAt { get => _updatedAt; set => Set(ref _updatedAt, value); }

        // ---------- 由紀錄算出，不存檔（StockService.ApplyDerived） ----------

        /// <summary>借出未還的數量；不算在 Quantity（在庫數量）裡。</summary>
        [JsonIgnore]
        public decimal LentQuantity { get => _lentQuantity; set => Set(ref _lentQuantity, value, nameof(LentDisplay), nameof(HasLent), nameof(IsScrapped)); }

        /// <summary>歷來報廢的總數量。</summary>
        [JsonIgnore]
        public decimal ScrappedQuantity { get => _scrappedQuantity; set => Set(ref _scrappedQuantity, value, nameof(IsScrapped)); }

        /// <summary>最後一次報廢的日期與原因。</summary>
        [JsonIgnore]
        public DateTime? LastScrappedAt { get => _lastScrappedAt; set => Set(ref _lastScrappedAt, value, nameof(Subtitle)); }

        [JsonIgnore]
        public string LastScrapReason { get => _lastScrapReason; set => Set(ref _lastScrapReason, value, nameof(Subtitle)); }

        // 螢幕閱讀器與 UI 自動化讀到的名稱（不影響存檔）
        public override string ToString() => Name;

        // ---------- 僅供畫面使用，不存檔 ----------

        /// <summary>全部都報廢了：在庫 0、沒有借出、而且有報廢紀錄。清單上改放到「已報廢」。</summary>
        [JsonIgnore]
        public bool IsScrapped => Quantity <= 0 && LentQuantity <= 0 && ScrappedQuantity > 0;

        /// <summary>清單右側的數量徽章，例如「3 支」。</summary>
        [JsonIgnore]
        public string QuantityDisplay => (QuantityText.Format(Quantity) + " " + Unit.Trim()).Trim();

        /// <summary>清單上的借出徽章，例如「借出 2 支」。</summary>
        [JsonIgnore]
        public string LentDisplay => ("借出 " + QuantityText.Format(LentQuantity) + " " + Unit.Trim()).Trim();

        [JsonIgnore]
        public bool HasLent => LentQuantity > 0;

        /// <summary>數量為 0 時徽章改成紅色提醒。</summary>
        [JsonIgnore]
        public bool IsEmpty => Quantity <= 0;

        /// <summary>
        /// 清單第二行：規格 · 購入日期；全部報廢的改顯示報廢日期與原因。都沒有時顯示備註。
        /// </summary>
        [JsonIgnore]
        public string Subtitle
        {
            get
            {
                var parts = new List<string>();
                if (Spec.Trim().Length > 0) parts.Add(Spec.Trim());
                if (IsScrapped && LastScrappedAt is { } s)
                    parts.Add($"{DateText.Format(s)} 報廢" + (LastScrapReason.Length > 0 ? $"（{LastScrapReason}）" : ""));
                else if (PurchasedAt is { } p)
                    parts.Add($"{DateText.Format(p)} 購入");
                return parts.Count > 0 ? string.Join(" · ", parts) : Note.Trim().ReplaceLineEndings(" ");
            }
        }

        /// <summary>清單頭像上顯示的第一個字。</summary>
        [JsonIgnore]
        public string Initial => string.IsNullOrWhiteSpace(Name)
            ? "?"
            : Name.Trim().EnumerateRunes().First().ToString().ToUpperInvariant();

        private static readonly string[] AvatarColors =
        {
            "#4F46E5", "#0EA5E9", "#10B981", "#F59E0B", "#EF4444",
            "#EC4899", "#8B5CF6", "#14B8A6", "#F97316", "#6366F1",
        };

        /// <summary>依名稱固定挑一個頭像底色（同名永遠同色）。</summary>
        [JsonIgnore]
        public string AvatarColor
        {
            get
            {
                uint h = 2166136261; // FNV-1a，跨次啟動結果一致（string.GetHashCode 每次啟動都不同）
                foreach (var c in Name.Trim().ToUpperInvariant()) { h ^= c; h *= 16777619; }
                return AvatarColors[h % (uint)AvatarColors.Length];
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>設定欄位並通知畫面；also 是跟著改變的顯示用屬性。</summary>
        private void Set<T>(ref T field, T value, string? also1 = null, string? also2 = null, string? also3 = null,
            [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            foreach (var n in new[] { name, also1, also2, also3 })
                if (n != null) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
            if (name is nameof(Quantity) or nameof(LentQuantity) or nameof(ScrappedQuantity))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle))); // 報廢與否會換第二行
        }
    }
}
