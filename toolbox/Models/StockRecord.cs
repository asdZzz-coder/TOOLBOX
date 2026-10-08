using System.Text.Json.Serialization;
using toolbox.Services;

namespace toolbox.Models
{
    /// <summary>工具的動作：購入、報廢（要寫原因）、借出（要寫借給誰）、歸還、盤點（數量不符時的調整）。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<RecordKind>))]
    public enum RecordKind { Buy, Scrap, Lend, Return, Check }

    /// <summary>紀錄與借出單共用：指向哪一項工具，並另存當時的名稱、規格、單位。</summary>
    public interface IItemRef
    {
        string ItemId { get; set; }
        string ItemName { get; set; }
        string ItemSpec { get; set; }
        string Unit { get; set; }
    }

    /// <summary>
    /// 一筆紀錄（只增不改）。工具之後改名或被刪掉，紀錄仍保留當時的名稱看得懂。
    /// </summary>
    public class StockRecord : IItemRef
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ItemId { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string ItemSpec { get; set; } = "";
        public string Unit { get; set; } = "";
        public RecordKind Kind { get; set; }

        /// <summary>這次動作的數量（一律是正數）；盤點時是差了多少。</summary>
        public decimal Quantity { get; set; }

        /// <summary>報廢：原因；購入：說明（例如在哪裡買的）；借出：用途；歸還：說明。</summary>
        public string Purpose { get; set; } = "";

        /// <summary>借出 / 歸還：借用人。</summary>
        public string Borrower { get; set; } = "";

        /// <summary>借出 / 歸還：對應的借出單。</summary>
        public string LoanId { get; set; } = "";

        /// <summary>發生的時間：購入、報廢、盤點是使用者填的日期（加上登錄當下的時刻），其他是登錄當下。</summary>
        public DateTime Time { get; set; } = DateTime.Now;

        /// <summary>這筆動作之後的在庫數量；盤點時就是實際點到的數量。</summary>
        public decimal Balance { get; set; }

        /// <summary>盤點：實際點到的比帳面多（true）還是少（false）。</summary>
        public bool CheckFoundMore { get; set; }

        // ---------- 僅供畫面使用，不存檔 ----------

        [JsonIgnore]
        public string KindDisplay => StockService.KindName(Kind);

        /// <summary>是增加在庫還是減少（盤點看多點到還是少點到）。</summary>
        [JsonIgnore]
        public bool Adds => Kind == RecordKind.Check ? CheckFoundMore : StockService.Adds(Kind);

        /// <summary>例如「+5 支」「−2 支」：購入、歸還是加，報廢、借出是減。</summary>
        [JsonIgnore]
        public string QuantityDisplay => (Adds ? "+" : "−") + (QuantityText.Format(Quantity) + " " + Unit.Trim()).Trim();

        [JsonIgnore]
        public string ItemDisplay => StockService.ItemDisplay(ItemName, ItemSpec);

        /// <summary>第二行說明，例如「借給 小王 · 工地用」「原因：壞掉」「盤點實點 3 支（帳面 5）」。</summary>
        [JsonIgnore]
        public string Detail
        {
            get
            {
                var parts = new List<string>();
                if (Kind == RecordKind.Check)
                {
                    var before = Adds ? Balance - Quantity : Balance + Quantity;
                    parts.Add($"實點 {(QuantityText.Format(Balance) + " " + Unit.Trim()).Trim()}（帳面 {QuantityText.Format(before)}）");
                }
                if (Borrower.Trim().Length > 0) parts.Add((Kind == RecordKind.Return ? "歸還人 " : "借給 ") + Borrower.Trim());
                if (Purpose.Trim().Length > 0) parts.Add((Kind == RecordKind.Scrap ? "原因：" : "") + Purpose.Trim().ReplaceLineEndings(" "));
                return string.Join(" · ", parts);
            }
        }

        /// <summary>購入、報廢、盤點只看日期；借出、歸還連時間一起顯示。</summary>
        [JsonIgnore]
        public string TimeDisplay => Kind is RecordKind.Lend or RecordKind.Return
            ? Time.ToString("yyyy/MM/dd HH:mm")
            : DateText.Format(Time);
    }

    /// <summary>借出單：借出時建立，歸還到數量全部還清為止（可以分次還）。</summary>
    public class Loan : IItemRef
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ItemId { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string ItemSpec { get; set; } = "";
        public string Unit { get; set; } = "";
        public string Borrower { get; set; } = "";
        public string Purpose { get; set; } = "";
        public decimal Quantity { get; set; }
        public decimal Returned { get; set; }
        public DateTime LentAt { get; set; } = DateTime.Now;

        /// <summary>全部還清的時間；還沒還清為 null。</summary>
        public DateTime? ReturnedAt { get; set; }

        // ---------- 僅供畫面使用，不存檔 ----------

        [JsonIgnore]
        public decimal Outstanding => Math.Max(0, Quantity - Returned);

        [JsonIgnore]
        public bool IsOpen => Outstanding > 0;

        [JsonIgnore]
        public string ItemDisplay => StockService.ItemDisplay(ItemName, ItemSpec);

        /// <summary>例如「未還 2 / 借 3 支」或「借 3 支」。</summary>
        [JsonIgnore]
        public string QuantityDisplay
        {
            get
            {
                var unit = Unit.Trim();
                var total = (QuantityText.Format(Quantity) + " " + unit).Trim();
                return Returned > 0 && IsOpen ? $"未還 {QuantityText.Format(Outstanding)} / 借 {total}" : $"借 {total}";
            }
        }

        [JsonIgnore]
        public string OutstandingDisplay => (QuantityText.Format(Outstanding) + " " + Unit.Trim()).Trim();

        [JsonIgnore]
        public string Detail
        {
            get
            {
                var s = $"借給 {Borrower.Trim()} · {LentAt:yyyy/MM/dd}（{StockService.DaysAgo(LentAt, DateTime.Now)}）";
                return Purpose.Trim().Length > 0 ? s + " · " + Purpose.Trim().ReplaceLineEndings(" ") : s;
            }
        }

        public override string ToString() => $"{ItemDisplay} {Borrower}";
    }
}
