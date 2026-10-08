using System.IO;
using System.Text.Json;
using ClosedXML.Excel;
using toolbox.Models;
using toolbox.Services;

namespace toolbox.Tests
{
    /// <summary>存檔（JSON）與 Excel 匯出 / 匯入。全部在暫存資料夾進行，不會動到真正的資料。</summary>
    public sealed class StorageTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ToolboxKeeper-Tests-" + Guid.NewGuid().ToString("N"));

        public StorageTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static List<ToolItem> Sample() =>
        [
            new() { Name = "電鑽", Category = "電動工具", Spec = "18V", Quantity = 2, Unit = "台", Note = "放在工具櫃", PurchasedAt = new DateTime(2024, 3, 15), LastCheckedAt = new DateTime(2026, 10, 1) },
            new() { Name = "捲尺", Category = "手工具", Spec = "5M", Quantity = 0.5m, Unit = "捲", Note = "第一行\n第二行", PurchasedAt = new DateTime(2026, 1, 2) },
            new() { Name = "'引號開頭", Category = "", Spec = "007", Quantity = 0, Unit = "", Note = "" },
        ];

        private static void AssertSame(IList<ToolItem> expected, IList<ToolItem> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Name, actual[i].Name);
                Assert.Equal(expected[i].Category, actual[i].Category);
                Assert.Equal(expected[i].Spec, actual[i].Spec);
                Assert.Equal(expected[i].Quantity, actual[i].Quantity);
                Assert.Equal(expected[i].Unit, actual[i].Unit);
                Assert.Equal(expected[i].Note, actual[i].Note);
                Assert.Equal(expected[i].PurchasedAt, actual[i].PurchasedAt);
                Assert.Equal(expected[i].LastCheckedAt, actual[i].LastCheckedAt);
            }
        }

        // ---------- JSON 存檔 ----------

        [Fact]
        public void DataStore_SaveThenLoad_RoundTrips()
        {
            var path = Path.Combine(_root, "sub", DataStore.FileName);
            var items = Sample();

            DataStore.Save(path, new ToolData { Items = items, Categories = ["電動工具", "手工具", "空分類"], Units = ["台", "捲"] });
            var data = DataStore.Load(path);

            AssertSame(items, data.Items);
            Assert.Equal(["電動工具", "手工具", "空分類"], data.Categories);
            Assert.Equal(["台", "捲"], data.Units);
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void DataStore_StoresChineseAsReadableText()
        {
            var path = Path.Combine(_root, DataStore.FileName);
            DataStore.Save(path, new ToolData { Items = Sample() });
            Assert.Contains("電鑽", File.ReadAllText(path));
        }

        [Fact]
        public void DataStore_MissingFile_ReturnsEmpty()
        {
            var data = DataStore.Load(Path.Combine(_root, "none.json"));
            Assert.Empty(data.Items);
            Assert.Empty(data.Categories);
        }

        [Fact]
        public void DataStore_CorruptFile_IsBackedUpAndStartsEmpty()
        {
            var path = Path.Combine(_root, DataStore.FileName);
            File.WriteAllText(path, "{ 這不是 JSON");

            var data = DataStore.Load(path);

            Assert.Empty(data.Items);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(_root, DataStore.FileName + ".corrupt-*"));
        }

        [Fact]
        public void DataStore_DoesNotSaveDisplayOnlyProperties()
        {
            var path = Path.Combine(_root, DataStore.FileName);
            DataStore.Save(path, new ToolData { Items = Sample() });
            var json = File.ReadAllText(path);
            Assert.DoesNotContain(nameof(ToolItem.QuantityDisplay), json);
            Assert.DoesNotContain(nameof(ToolItem.AvatarColor), json);
            using var _ = JsonDocument.Parse(json);
        }

        // ---------- Excel ----------

        [Fact]
        public void Excel_ExportThenImport_RoundTrips()
        {
            var path = Path.Combine(_root, "export.xlsx");
            var items = Sample();

            ExcelService.Export(items, path);
            var imported = ExcelService.Import(path);

            AssertSame(items, imported);
        }

        [Fact]
        public void Excel_Export_WritesQuantityAsNumber()
        {
            var path = Path.Combine(_root, "export.xlsx");
            ExcelService.Export(Sample(), path);

            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.First();
            Assert.Equal(ExcelService.Headers, Enumerable.Range(1, ExcelService.Headers.Length).Select(c => ws.Cell(1, c).GetString()));
            Assert.Equal(XLDataType.DateTime, ws.Cell(2, 6).DataType); // 購入日期是真的日期
            Assert.Equal(XLDataType.Number, ws.Cell(2, 4).DataType);
            Assert.Equal("007", ws.Cell(4, 3).GetString()); // 規格保持文字，前導 0 不會消失
        }

        [Fact]
        public void Excel_Export_AddsRecordAndLoanSheets_ImportStillReadsItems()
        {
            var path = Path.Combine(_root, "export.xlsx");
            var items = Sample();
            var now = new DateTime(2026, 10, 8, 9, 0, 0);
            var scrap = StockService.Scrap(items[0], 1, "壞掉", new DateTime(2026, 10, 1, 9, 0, 0));
            var (loan, lend) = StockService.Lend(items[0], 1, "小王", "", now);
            StockService.ApplyDerived(items, [scrap, lend], [loan]);

            ExcelService.Export(items, path, [lend, scrap], [loan]);

            using (var wb = new XLWorkbook(path))
            {
                Assert.Equal(["工具", "紀錄", "借出中"], wb.Worksheets.Select(w => w.Name));
                var rs = wb.Worksheet(ExcelService.RecordsSheet);
                Assert.Equal("報廢", rs.Cell(2, 2).GetString()); // 依日期排序，報廢較早
                Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0), rs.Cell(2, 1).GetDateTime());
                Assert.Equal(-1, rs.Cell(2, 5).GetDouble()); // 報廢記成負數
                Assert.Equal("壞掉", rs.Cell(2, 7).GetString());
                Assert.Equal("小王", wb.Worksheet(ExcelService.LoansSheet).Cell(2, 4).GetString());
                var ws = wb.Worksheet(ExcelService.ItemsSheet);
                Assert.Equal((1d, 1d), (ws.Cell(2, 9).GetDouble(), ws.Cell(2, 10).GetDouble())); // 借出中、已報廢
            }
            AssertSame(items, ExcelService.Import(path)); // 匯入只讀第一個工作表
        }

        [Fact]
        public void Excel_Import_ReadsTextDates()
        {
            var path = Path.Combine(_root, "dates.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("工具");
                ws.Cell(1, 1).Value = "名稱";
                ws.Cell(2, 1).Value = "板手"; ws.Cell(2, 6).Value = "115/3/5"; ws.Cell(2, 7).Value = "2026-10-01";
                ws.Cell(3, 1).Value = "鉗子"; ws.Cell(3, 6).Value = "不知道";
                wb.SaveAs(path);
            }

            var imported = ExcelService.Import(path);

            Assert.Equal((new DateTime(2026, 3, 5), new DateTime(2026, 10, 1)), (imported[0].PurchasedAt, imported[0].LastCheckedAt));
            Assert.Null(imported[1].PurchasedAt); // 看不懂的日期留空
        }

        [Fact]
        public void Excel_Import_HandlesHandMadeSheet()
        {
            var path = Path.Combine(_root, "hand.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("任意名稱");
                ws.Cell(1, 1).Value = "名稱";
                ws.Cell(2, 1).Value = "原子筆"; ws.Cell(2, 4).Value = "２０"; ws.Cell(2, 5).Value = "支";
                ws.Cell(3, 1).Value = "";       ws.Cell(3, 4).Value = 5;     // 沒有名稱 → 略過
                ws.Cell(4, 1).Value = "膠水";   ws.Cell(4, 4).Value = "很多"; // 看不懂的數量 → 0
                ws.Cell(5, 1).Value = "釘書針"; ws.Cell(5, 4).Value = -3;     // 負數 → 0
                wb.SaveAs(path);
            }

            var imported = ExcelService.Import(path);

            Assert.Equal(["原子筆", "膠水", "釘書針"], imported.Select(i => i.Name));
            Assert.Equal([20m, 0m, 0m], imported.Select(i => i.Quantity));
            Assert.Equal("支", imported[0].Unit);
            Assert.Equal("", imported[0].Category);
        }

        [Fact]
        public void SameTool_ComparesNameAndSpecIgnoringCase()
        {
            var a = new ToolItem { Name = "USB 線", Spec = "Type-C" };
            Assert.True(ExcelService.SameTool(a, new ToolItem { Name = " usb 線 ", Spec = "type-c" }));
            Assert.False(ExcelService.SameTool(a, new ToolItem { Name = "USB 線", Spec = "Micro" }));
        }
    }
}
