using toolbox.Models;

namespace toolbox.Services
{
    public enum StockError { None, QuantityNotPositive, NotEnoughStock, TooMuchStock, ReasonRequired, BorrowerRequired, MoreThanOutstanding, TextTooLong }

    /// <summary>
    /// 工具的動作：購入、報廢、借出、歸還、盤點。每個動作都會改工具的在庫數量並留下一筆紀錄；
    /// 借出另外建立借出單，歸還時從借出單扣（可以分次還）。
    /// 這裡只放不碰畫面與檔案的邏輯，方便單元測試。
    /// </summary>
    public static class StockService
    {
        public const int MaxTextLength = 100;

        /// <summary>報廢原因的快速選項（另外會列出最近用過的）。</summary>
        public static readonly IReadOnlyList<string> ScrapReasons = ["壞掉", "遺失", "老舊汰換", "無法維修"];

        public static string KindName(RecordKind kind) => kind switch
        {
            RecordKind.Buy => "購入",
            RecordKind.Scrap => "報廢",
            RecordKind.Lend => "借出",
            RecordKind.Return => "歸還",
            _ => "盤點",
        };

        /// <summary>購入、歸還會增加在庫；報廢、借出會減少（盤點看差多少，另外處理）。</summary>
        public static bool Adds(RecordKind kind) => kind is RecordKind.Buy or RecordKind.Return;

        public static string ItemDisplay(string name, string spec) =>
            spec.Trim().Length > 0 ? $"{name.Trim()}（{spec.Trim()}）" : name.Trim();

        public static string DaysAgo(DateTime then, DateTime now) => (now.Date - then.Date).Days switch
        {
            <= 0 => "今天",
            1 => "昨天",
            var d => $"{d} 天前",
        };

        /// <summary>
        /// 檢查一個動作能不能做。onHand 是目前在庫；歸還時 outstanding 是借出單還沒還的數量。
        /// 報廢一定要寫原因，借出一定要寫借給誰。
        /// </summary>
        public static StockError Validate(RecordKind kind, decimal onHand, decimal quantity,
            string? purpose = null, string? borrower = null, decimal outstanding = 0)
        {
            purpose = CategoryService.Normalize(purpose);
            borrower = CategoryService.Normalize(borrower);
            if (quantity <= 0) return StockError.QuantityNotPositive;
            if (purpose.Length > MaxTextLength || borrower.Length > MaxTextLength) return StockError.TextTooLong;
            switch (kind)
            {
                case RecordKind.Buy:
                    if (onHand + quantity > QuantityText.Max) return StockError.TooMuchStock;
                    break;
                case RecordKind.Scrap:
                    if (quantity > onHand) return StockError.NotEnoughStock;
                    if (purpose.Length == 0) return StockError.ReasonRequired;
                    break;
                case RecordKind.Lend:
                    if (quantity > onHand) return StockError.NotEnoughStock;
                    if (borrower.Length == 0) return StockError.BorrowerRequired;
                    break;
                case RecordKind.Return:
                    if (quantity > outstanding) return StockError.MoreThanOutstanding;
                    if (onHand + quantity > QuantityText.Max) return StockError.TooMuchStock;
                    break;
            }
            return StockError.None;
        }

        private static void Ensure(StockError error)
        {
            if (error != StockError.None) throw new InvalidOperationException(error.ToString());
        }

        private static T Ref<T>(T target, ToolItem item) where T : IItemRef
        {
            target.ItemId = item.Id;
            target.ItemName = item.Name;
            target.ItemSpec = item.Spec;
            target.Unit = item.Unit;
            return target;
        }

        private static void Touch(ToolItem item, decimal delta, DateTime now)
        {
            item.Quantity = QuantityText.Adjust(item.Quantity, delta);
            item.UpdatedAt = now;
        }

        /// <summary>購入：加到在庫。工具還沒有購入日期、或這次比原本記的更早時，購入日期改成這次的日期。</summary>
        public static StockRecord Buy(ToolItem item, decimal quantity, string? note, DateTime time)
        {
            Ensure(Validate(RecordKind.Buy, item.Quantity, quantity, note));
            Touch(item, quantity, time);
            if (item.PurchasedAt == null || time.Date < item.PurchasedAt) item.PurchasedAt = time.Date;
            return Ref(new StockRecord
            {
                Kind = RecordKind.Buy, Quantity = quantity, Purpose = CategoryService.Normalize(note), Time = time, Balance = item.Quantity,
            }, item);
        }

        /// <summary>報廢（壞掉、遺失…）：從在庫扣掉，一定要寫原因。</summary>
        public static StockRecord Scrap(ToolItem item, decimal quantity, string reason, DateTime time)
        {
            Ensure(Validate(RecordKind.Scrap, item.Quantity, quantity, reason));
            Touch(item, -quantity, time);
            return Ref(new StockRecord
            {
                Kind = RecordKind.Scrap, Quantity = quantity, Purpose = CategoryService.Normalize(reason), Time = time, Balance = item.Quantity,
            }, item);
        }

        public static (Loan Loan, StockRecord Record) Lend(ToolItem item, decimal quantity, string borrower, string? purpose, DateTime now)
        {
            Ensure(Validate(RecordKind.Lend, item.Quantity, quantity, purpose, borrower));
            Touch(item, -quantity, now);
            var loan = Ref(new Loan
            {
                Borrower = CategoryService.Normalize(borrower), Purpose = CategoryService.Normalize(purpose), Quantity = quantity, LentAt = now,
            }, item);
            var record = Ref(new StockRecord
            {
                Kind = RecordKind.Lend, Quantity = quantity, Borrower = loan.Borrower, Purpose = loan.Purpose,
                LoanId = loan.Id, Time = now, Balance = item.Quantity,
            }, item);
            return (loan, record);
        }

        /// <summary>
        /// 歸還（可以只還一部分）。工具已經被刪掉時 item 傳 null：只把借出單結清，不加回在庫。
        /// </summary>
        public static StockRecord Return(Loan loan, ToolItem? item, decimal quantity, string? note, DateTime now)
        {
            Ensure(Validate(RecordKind.Return, item?.Quantity ?? 0, quantity, note, outstanding: loan.Outstanding));
            loan.Returned += quantity;
            if (!loan.IsOpen) loan.ReturnedAt = now;
            if (item != null) Touch(item, quantity, now);
            var record = new StockRecord
            {
                ItemId = loan.ItemId, ItemName = item?.Name ?? loan.ItemName, ItemSpec = item?.Spec ?? loan.ItemSpec, Unit = loan.Unit,
                Kind = RecordKind.Return, Quantity = quantity, Borrower = loan.Borrower, Purpose = CategoryService.Normalize(note),
                LoanId = loan.Id, Time = now, Balance = item?.Quantity ?? 0,
            };
            return record;
        }

        /// <summary>
        /// 盤點一項工具：記下盤點日期，在庫改成實際點到的數量。
        /// 數量不符時回傳一筆盤點紀錄（記下差了多少）；相符時回傳 null。
        /// </summary>
        public static StockRecord? Count(ToolItem item, decimal counted, DateTime time)
        {
            if (counted < 0 || counted > QuantityText.Max) throw new ArgumentOutOfRangeException(nameof(counted));
            item.LastCheckedAt = time.Date;
            item.UpdatedAt = time;
            var diff = counted - item.Quantity;
            if (diff == 0) return null;
            item.Quantity = counted;
            return Ref(new StockRecord
            {
                Kind = RecordKind.Check, Quantity = Math.Abs(diff), CheckFoundMore = diff > 0, Time = time, Balance = counted,
            }, item);
        }

        /// <summary>
        /// 讓紀錄與借出單重新對上工具：找不到原本的工具時（例如「清空後匯入」換了一批工具），
        /// 改用名稱 + 規格找同一項；還沒還清的借出單順便更新成工具目前的名稱、規格、單位。
        /// </summary>
        public static void Relink(IReadOnlyCollection<ToolItem> items, IEnumerable<IItemRef> refs)
        {
            var byId = items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var r in refs)
            {
                if (!byId.TryGetValue(r.ItemId, out var item))
                {
                    item = items.FirstOrDefault(i => ExcelService.SameTool(i, new ToolItem { Name = r.ItemName, Spec = r.ItemSpec }));
                    if (item == null) continue;
                    r.ItemId = item.Id;
                }
                if (r is Loan { IsOpen: true })
                {
                    r.ItemName = item.Name;
                    r.ItemSpec = item.Spec;
                    r.Unit = item.Unit;
                }
            }
        }

        /// <summary>依紀錄與借出單算出每項工具的借出中數量、報廢數量與最後一次報廢。</summary>
        public static void ApplyDerived(IEnumerable<ToolItem> items, IEnumerable<StockRecord> records, IEnumerable<Loan> loans)
        {
            var lent = Outstanding(loans);
            var scraps = records.Where(r => r.Kind == RecordKind.Scrap).GroupBy(r => r.ItemId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var i in items)
            {
                i.LentQuantity = lent.GetValueOrDefault(i.Id);
                var mine = scraps.GetValueOrDefault(i.Id);
                var last = mine?.Select((r, n) => (r, n)).MaxBy(x => (x.r.Time, x.n)).r; // 同一時間取後登錄的
                i.ScrappedQuantity = mine?.Sum(r => r.Quantity) ?? 0;
                i.LastScrappedAt = last?.Time.Date;
                i.LastScrapReason = last?.Purpose ?? "";
            }
        }

        /// <summary>每項工具目前借出未還的總數（沒有借出的不在字典裡）。</summary>
        public static Dictionary<string, decimal> Outstanding(IEnumerable<Loan> loans) =>
            loans.Where(l => l.IsOpen).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Outstanding));

        /// <summary>最近用過、不重複（不分大小寫）的前幾個值（例如原因、借用人），最新的在前。</summary>
        public static List<string> Recent(IEnumerable<StockRecord> records, Func<StockRecord, bool> filter, Func<StockRecord, string> pick, int count = 6)
        {
            var result = new List<string>();
            foreach (var v in records.Where(filter).Reverse().OrderByDescending(r => r.Time).Select(r => CategoryService.Normalize(pick(r))))
            {
                if (v.Length == 0 || result.Any(x => CategoryService.SameName(x, v))) continue;
                result.Add(v);
                if (result.Count == count) break;
            }
            return result;
        }

        /// <summary>報廢原因的選項：先列最近用過的，再補上預設的（不重複）。</summary>
        public static List<string> ReasonChoices(IEnumerable<StockRecord> records)
        {
            var result = Recent(records, r => r.Kind == RecordKind.Scrap, r => r.Purpose);
            foreach (var r in ScrapReasons)
                if (!result.Any(x => CategoryService.SameName(x, r))) result.Add(r);
            return result;
        }

        public static ToolItem? FindItem(IEnumerable<ToolItem> items, IItemRef r) =>
            items.FirstOrDefault(i => i.Id == r.ItemId);
    }
}
