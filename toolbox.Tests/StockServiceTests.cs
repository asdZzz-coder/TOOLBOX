using System.IO;
using toolbox.Models;
using toolbox.Services;

namespace toolbox.Tests
{
    /// <summary>工具的動作：購入（記購入日期）、報廢（要寫原因）、借出、歸還（可分次）、盤點，以及存檔。</summary>
    public class StockServiceTests
    {
        private static readonly DateTime Now = new(2026, 10, 8, 9, 30, 0);

        private static ToolItem Item(decimal qty = 5) => new() { Name = "電鑽", Spec = "18V", Unit = "台", Quantity = qty };

        [Theory]
        [InlineData(RecordKind.Buy, 0, "", "", StockError.QuantityNotPositive)]
        [InlineData(RecordKind.Buy, 5, "", "", StockError.None)]
        [InlineData(RecordKind.Scrap, 1, "", "", StockError.ReasonRequired)]
        [InlineData(RecordKind.Scrap, 1, "  ", "", StockError.ReasonRequired)]
        [InlineData(RecordKind.Scrap, 6, "壞掉", "", StockError.NotEnoughStock)]
        [InlineData(RecordKind.Scrap, 5, "壞掉", "", StockError.None)]
        [InlineData(RecordKind.Lend, 3, "工地", "", StockError.BorrowerRequired)]
        [InlineData(RecordKind.Lend, 3, "", "小王", StockError.None)]
        [InlineData(RecordKind.Lend, 6, "", "小王", StockError.NotEnoughStock)]
        public void Validate_ChecksQuantityReasonAndBorrower(RecordKind kind, int qty, string purpose, string borrower, StockError expected) =>
            Assert.Equal(expected, StockService.Validate(kind, 5, qty, purpose, borrower));

        [Fact]
        public void Validate_RejectsTooLongText() =>
            Assert.Equal(StockError.TextTooLong, StockService.Validate(RecordKind.Scrap, 5, 1, new string('字', StockService.MaxTextLength + 1)));

        [Fact]
        public void Validate_ReturnCannotExceedOutstanding()
        {
            Assert.Equal(StockError.MoreThanOutstanding, StockService.Validate(RecordKind.Return, 0, 3, outstanding: 2));
            Assert.Equal(StockError.None, StockService.Validate(RecordKind.Return, 0, 2, outstanding: 2));
        }

        // ---------- 購入 ----------

        [Fact]
        public void Buy_AddsQuantity_RecordsDate_AndSetsPurchaseDate()
        {
            var item = Item(0);
            var bought = new DateTime(2026, 9, 1, 9, 30, 0);
            var r = StockService.Buy(item, 2, " 五金行 ", bought);

            Assert.Equal(2, item.Quantity);
            Assert.Equal(new DateTime(2026, 9, 1), item.PurchasedAt);
            Assert.Equal((RecordKind.Buy, 2m, "五金行", bought, 2m), (r.Kind, r.Quantity, r.Purpose, r.Time, r.Balance));
            Assert.Equal((item.Id, "電鑽", "18V", "台"), (r.ItemId, r.ItemName, r.ItemSpec, r.Unit));
            Assert.Equal("+2 台", r.QuantityDisplay);
            Assert.Equal("2026/09/01", r.TimeDisplay);
        }

        [Fact]
        public void Buy_KeepsEarliestPurchaseDate()
        {
            var item = Item();
            item.PurchasedAt = new DateTime(2025, 1, 10);

            StockService.Buy(item, 1, "", Now); // 之後又買：購入日期維持第一次
            Assert.Equal(new DateTime(2025, 1, 10), item.PurchasedAt);

            StockService.Buy(item, 1, "", new DateTime(2024, 6, 1)); // 補登更早的：改成更早的日期
            Assert.Equal(new DateTime(2024, 6, 1), item.PurchasedAt);
        }

        // ---------- 報廢 ----------

        [Fact]
        public void Scrap_SubtractsAndKeepsReasonAndDate()
        {
            var item = Item();
            var day = new DateTime(2026, 10, 1, 14, 0, 0);
            var r = StockService.Scrap(item, 2, "壞掉", day);

            Assert.Equal(3, item.Quantity);
            Assert.Equal((RecordKind.Scrap, "壞掉", day), (r.Kind, r.Purpose, r.Time));
            Assert.Equal("−2 台", r.QuantityDisplay);
            Assert.Equal("原因：壞掉", r.Detail);
        }

        [Fact]
        public void Scrap_WithoutReason_Throws()
        {
            var item = Item();
            Assert.Throws<InvalidOperationException>(() => StockService.Scrap(item, 1, "", Now));
            Assert.Equal(5, item.Quantity); // 失敗時不會動到在庫
        }

        [Fact]
        public void ApplyDerived_ScrappingEverything_MarksToolScrapped()
        {
            var item = Item(2);
            var records = new List<StockRecord>
            {
                StockService.Scrap(item, 1, "遺失", new DateTime(2026, 9, 1)),
                StockService.Scrap(item, 1, "壞掉", new DateTime(2026, 10, 1)),
            };

            StockService.ApplyDerived([item], records, []);

            Assert.True(item.IsScrapped);
            Assert.Equal(2, item.ScrappedQuantity);
            Assert.Equal((new DateTime(2026, 10, 1), "壞掉"), (item.LastScrappedAt, item.LastScrapReason));
            Assert.Equal("18V · 2026/10/01 報廢（壞掉）", item.Subtitle);
        }

        [Fact]
        public void IsScrapped_NotWhenStillOnHandOrLent()
        {
            var item = Item(2);
            var scrap = StockService.Scrap(item, 1, "壞掉", Now);
            var (loan, _) = StockService.Lend(item, 1, "小王", "", Now);

            StockService.ApplyDerived([item], [scrap], [loan]);
            Assert.Equal(0, item.Quantity);
            Assert.False(item.IsScrapped); // 還有一台借出去，不算全部報廢

            StockService.ApplyDerived([item], [scrap], []);
            Assert.True(item.IsScrapped);
            Assert.False(CategoryFilter.All.Matches(item));
            Assert.True(CategoryFilter.Scrapped.Matches(item));
        }

        [Fact]
        public void Subtitle_ShowsSpecAndPurchaseDate()
        {
            var item = Item();
            Assert.Equal("18V", item.Subtitle);
            item.PurchasedAt = new DateTime(2026, 3, 5, 15, 0, 0);
            Assert.Equal(new DateTime(2026, 3, 5), item.PurchasedAt); // 只留日期
            Assert.Equal("18V · 2026/03/05 購入", item.Subtitle);
        }

        [Fact]
        public void ReasonChoices_RecentFirstThenDefaults()
        {
            var item = Item();
            var records = new List<StockRecord> { StockService.Scrap(item, 1, "被雷打到", Now), StockService.Scrap(item, 1, "壞掉", Now.AddMinutes(1)) };

            Assert.Equal(["壞掉", "被雷打到", "遺失", "老舊汰換", "無法維修"], StockService.ReasonChoices(records));
        }

        // ---------- 借出 / 歸還 ----------

        [Fact]
        public void LendThenReturnInParts_ClosesLoan()
        {
            var item = Item();
            var (loan, lend) = StockService.Lend(item, 3, "小王", "工地", Now);

            Assert.Equal(2, item.Quantity);
            Assert.Equal((3m, 0m, true), (loan.Quantity, loan.Returned, loan.IsOpen));
            Assert.Equal(loan.Id, lend.LoanId);
            Assert.Equal(3m, StockService.Outstanding([loan])[item.Id]);

            var first = StockService.Return(loan, item, 1, "", Now.AddDays(1));
            Assert.Equal(3, item.Quantity);
            Assert.Equal(2, loan.Outstanding);
            Assert.Null(loan.ReturnedAt);
            Assert.Equal((RecordKind.Return, "小王", loan.Id), (first.Kind, first.Borrower, first.LoanId));

            StockService.Return(loan, item, 2, "完好", Now.AddDays(2));
            Assert.Equal(5, item.Quantity);
            Assert.False(loan.IsOpen);
            Assert.Equal(Now.AddDays(2), loan.ReturnedAt);
            Assert.Empty(StockService.Outstanding([loan]));
        }

        [Fact]
        public void Return_ItemDeleted_ClosesLoanWithoutStock()
        {
            var item = Item();
            var (loan, _) = StockService.Lend(item, 2, "小王", "", Now);

            var r = StockService.Return(loan, null, 2, "", Now);

            Assert.False(loan.IsOpen);
            Assert.Equal("電鑽", r.ItemName);
        }

        // ---------- 盤點 ----------

        [Fact]
        public void Count_Match_OnlyRecordsCheckDate()
        {
            var item = Item();
            Assert.Null(StockService.Count(item, 5, Now));
            Assert.Equal(Now.Date, item.LastCheckedAt);
            Assert.Equal(5, item.Quantity);
        }

        [Fact]
        public void Count_Difference_AdjustsQuantityAndRecords()
        {
            var item = Item();

            var less = StockService.Count(item, 3, Now)!;
            Assert.Equal(3, item.Quantity);
            Assert.Equal((RecordKind.Check, 2m, false, 3m), (less.Kind, less.Quantity, less.CheckFoundMore, less.Balance));
            Assert.Equal("−2 台", less.QuantityDisplay);
            Assert.Equal("實點 3 台（帳面 5）", less.Detail);

            var more = StockService.Count(item, 4.5m, Now.AddDays(1))!;
            Assert.Equal("+1.5 台", more.QuantityDisplay);
            Assert.Equal("實點 4.5 台（帳面 3）", more.Detail);
            Assert.Equal(Now.AddDays(1).Date, item.LastCheckedAt);
        }

        [Fact]
        public void StocktakeRow_TracksStateAndDifference()
        {
            var row = new StocktakeRow(Item());

            Assert.Equal((StocktakeState.NotCounted, "未點"), (row.State, row.DiffDisplay));
            row.CountText = "5";
            Assert.Equal((StocktakeState.Match, "相符"), (row.State, row.DiffDisplay));
            row.CountText = "３";
            Assert.Equal((StocktakeState.Less, "少 2", 3m), (row.State, row.DiffDisplay, row.Counted));
            row.CountText = "7";
            Assert.Equal((StocktakeState.More, "多 2"), (row.State, row.DiffDisplay));
            row.CountText = "很多";
            Assert.Equal(StocktakeState.Invalid, row.State);
            Assert.Null(row.Counted);
            row.CountText = " ";
            Assert.False(row.IsCounted);
        }

        // ---------- 重新對上工具 ----------

        [Fact]
        public void Relink_FindsItemByNameAndSpec_AndUpdatesOpenLoans()
        {
            var old = Item();
            var (open, lendRecord) = StockService.Lend(old, 1, "小王", "", Now);
            var (closed, _) = StockService.Lend(old, 1, "小李", "", Now);
            StockService.Return(closed, old, 1, "", Now);

            // 「清空後匯入」：同名同規格、但是新的工具（Id 不同），單位也改了
            var fresh = new ToolItem { Name = "電鑽", Spec = "18v", Unit = "支" };
            StockService.Relink([fresh], [open, closed, lendRecord]);

            Assert.Equal(fresh.Id, open.ItemId);
            Assert.Equal(fresh.Id, closed.ItemId);
            Assert.Equal(fresh.Id, lendRecord.ItemId);
            Assert.Equal(("18v", "支"), (open.ItemSpec, open.Unit)); // 還沒還清的借出單跟著工具更新
            Assert.Equal(("18V", "台"), (closed.ItemSpec, closed.Unit)); // 已還清的保留當時的寫法
        }

        [Fact]
        public void Recent_ReturnsDistinctNewestFirst()
        {
            var item = Item(100);
            var records = new List<StockRecord>
            {
                StockService.Lend(item, 1, "小王", "", Now).Record,
                StockService.Lend(item, 1, "小李", "", Now.AddMinutes(1)).Record,
                StockService.Lend(item, 1, "小王", "", Now.AddMinutes(2)).Record,
                StockService.Buy(item, 1, "", Now.AddMinutes(3)),
            };

            Assert.Equal(["小王", "小李"], StockService.Recent(records, r => r.Kind == RecordKind.Lend, r => r.Borrower));
        }

        [Theory]
        [InlineData(0, "今天")]
        [InlineData(1, "昨天")]
        [InlineData(5, "5 天前")]
        public void DaysAgo_IsFriendly(int days, string expected) =>
            Assert.Equal(expected, StockService.DaysAgo(Now.AddDays(-days), Now));

        [Fact]
        public void DataStore_RoundTripsDatesRecordsAndLoans()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ToolboxKeeper-Tests-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, DataStore.FileName);
            try
            {
                var item = Item();
                var buy = StockService.Buy(item, 1, "", new DateTime(2026, 1, 2));
                var scrap = StockService.Scrap(item, 1, "壞掉", Now);
                var check = StockService.Count(item, 3, Now)!;
                var (loan, lend) = StockService.Lend(item, 2, "小王", "", Now);
                DataStore.Save(path, new ToolData { Items = [item], Records = [buy, scrap, check, lend], Loans = [loan] });

                var json = File.ReadAllText(path);
                Assert.Contains("\"Scrap\"", json); // 動作存成文字，用記事本打開也看得懂
                Assert.DoesNotContain(nameof(ToolItem.ScrappedQuantity), json); // 由紀錄算出，不存檔
                Assert.DoesNotContain(nameof(ToolItem.Subtitle), json);

                var data = DataStore.Load(path);
                var loaded = data.Items[0];
                Assert.Equal((item.Id, new DateTime(2026, 1, 2), Now.Date), (loaded.Id, loaded.PurchasedAt, loaded.LastCheckedAt));
                Assert.Equal([RecordKind.Buy, RecordKind.Scrap, RecordKind.Check, RecordKind.Lend], data.Records.Select(r => r.Kind));
                Assert.False(data.Records[2].CheckFoundMore);
                Assert.Equal((loan.Id, 2m), (data.Loans[0].Id, data.Loans[0].Outstanding));

                StockService.ApplyDerived(data.Items, data.Records, data.Loans);
                Assert.Equal((1m, 2m), (loaded.ScrappedQuantity, loaded.LentQuantity));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }
}
