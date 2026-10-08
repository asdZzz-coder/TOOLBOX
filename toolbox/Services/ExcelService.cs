using System.IO;
using ClosedXML.Excel;
using toolbox.Models;

namespace toolbox.Services
{
    /// <summary>
    /// Excel 匯出 / 匯入。工具工作表的欄位順序：名稱、分類、規格、數量、單位、購入日期、最後盤點、備註，
    /// 後面的「借出中」「已報廢」只供查看。匯出時另附「紀錄」「借出中」兩個工作表（匯入時只讀第一個工作表）。
    /// </summary>
    public static class ExcelService
    {
        public static readonly string[] Headers = { "名稱", "分類", "規格", "數量", "單位", "購入日期", "最後盤點", "備註", "借出中", "已報廢" };

        /// <summary>匯入時讀的欄位數（後面的借出中、已報廢由紀錄算出，不匯入）。</summary>
        public const int ImportColumns = 8;

        public static readonly string[] RecordHeaders = { "日期", "動作", "名稱", "規格", "數量", "單位", "說明 / 原因", "借用人", "之後在庫" };
        public static readonly string[] LoanHeaders = { "借出時間", "名稱", "規格", "借用人", "用途", "借出數量", "已還", "未還", "單位" };

        public const string ItemsSheet = "工具";
        public const string RecordsSheet = "紀錄";
        public const string LoansSheet = "借出中";

        public static void Export(IEnumerable<ToolItem> items, string path,
            IEnumerable<StockRecord>? records = null, IEnumerable<Loan>? loans = null)
        {
            using var wb = new XLWorkbook();
            var ws = Sheet(wb, ItemsSheet, Headers);

            int r = 2;
            foreach (var i in items)
            {
                // 文字一律寫成文字，避免 Excel 把「001」這種規格的前導 0 吃掉
                ws.Cell(r, 1).SetValue(Escape(i.Name));
                ws.Cell(r, 2).SetValue(Escape(i.Category));
                ws.Cell(r, 3).SetValue(Escape(i.Spec));
                ws.Cell(r, 4).SetValue(i.Quantity); // 數量是數字，Excel 裡可以直接加總
                ws.Cell(r, 5).SetValue(Escape(i.Unit));
                SetDate(ws.Cell(r, 6), i.PurchasedAt);
                SetDate(ws.Cell(r, 7), i.LastCheckedAt);
                ws.Cell(r, 8).SetValue(Escape(i.Note));
                ws.Cell(r, 8).Style.Alignment.WrapText = true;
                if (i.LentQuantity > 0) ws.Cell(r, 9).SetValue(i.LentQuantity);
                if (i.ScrappedQuantity > 0) ws.Cell(r, 10).SetValue(i.ScrappedQuantity);
                r++;
            }
            ws.Columns().AdjustToContents();

            if (records != null)
            {
                var rs = Sheet(wb, RecordsSheet, RecordHeaders);
                int row = 2;
                foreach (var x in records.OrderBy(x => x.Time))
                {
                    rs.Cell(row, 1).SetValue(x.Time);
                    rs.Cell(row, 1).Style.DateFormat.Format = x.Kind is RecordKind.Lend or RecordKind.Return ? "yyyy/mm/dd hh:mm" : "yyyy/mm/dd";
                    rs.Cell(row, 2).SetValue(StockService.KindName(x.Kind));
                    rs.Cell(row, 3).SetValue(Escape(x.ItemName));
                    rs.Cell(row, 4).SetValue(Escape(x.ItemSpec));
                    rs.Cell(row, 5).SetValue(x.Adds ? x.Quantity : -x.Quantity); // 報廢、借出、盤點短少記成負數，方便加總
                    rs.Cell(row, 6).SetValue(Escape(x.Unit));
                    rs.Cell(row, 7).SetValue(Escape(x.Purpose));
                    rs.Cell(row, 8).SetValue(Escape(x.Borrower));
                    rs.Cell(row, 9).SetValue(x.Balance);
                    row++;
                }
                rs.Columns().AdjustToContents();
            }
            if (loans != null)
            {
                var ls = Sheet(wb, LoansSheet, LoanHeaders);
                int row = 2;
                foreach (var l in loans.Where(l => l.IsOpen).OrderBy(l => l.LentAt))
                {
                    ls.Cell(row, 1).SetValue(l.LentAt);
                    ls.Cell(row, 1).Style.DateFormat.Format = "yyyy/mm/dd hh:mm";
                    ls.Cell(row, 2).SetValue(Escape(l.ItemName));
                    ls.Cell(row, 3).SetValue(Escape(l.ItemSpec));
                    ls.Cell(row, 4).SetValue(Escape(l.Borrower));
                    ls.Cell(row, 5).SetValue(Escape(l.Purpose));
                    ls.Cell(row, 6).SetValue(l.Quantity);
                    ls.Cell(row, 7).SetValue(l.Returned);
                    ls.Cell(row, 8).SetValue(l.Outstanding);
                    ls.Cell(row, 9).SetValue(Escape(l.Unit));
                    row++;
                }
                ls.Columns().AdjustToContents();
            }
            wb.SaveAs(path);
        }

        private static IXLWorksheet Sheet(XLWorkbook wb, string name, string[] headers)
        {
            var ws = wb.Worksheets.Add(name);
            for (int c = 0; c < headers.Length; c++)
                ws.Cell(1, c + 1).Value = headers[c];
            ws.Row(1).Style.Font.Bold = true;
            ws.SheetView.FreezeRows(1);
            return ws;
        }

        private static void SetDate(IXLCell cell, DateTime? value)
        {
            if (value is not { } d) return;
            cell.SetValue(d);
            cell.Style.DateFormat.Format = "yyyy/mm/dd";
        }

        // 開頭的 ' 會被 ClosedXML 當成 Excel 的「文字前綴」而吞掉，多加一個才能原樣保留
        private static string Escape(string s) => s.StartsWith('\'') ? "'" + s : s;

        /// <summary>讀取第一個工作表（跳過標題列）；沒有名稱的列略過，數量看不懂時當作 0，日期看不懂時留空。</summary>
        public static List<ToolItem> Import(string path)
        {
            // 允許讀取「正在被 Excel 開啟」的檔案，否則會因檔案被鎖定而失敗
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var wb = new XLWorkbook(stream);
            var ws = wb.Worksheets.FirstOrDefault();
            if (ws == null) return new List<ToolItem>();
            var result = new List<ToolItem>();
            foreach (var row in ws.RowsUsed().Skip(1)) // 跳過標題列
            {
                var name = row.Cell(1).GetFormattedString().Trim();
                if (name.Length == 0) continue;
                var qtyCell = row.Cell(4);
                decimal qty = 0;
                if (qtyCell.DataType == XLDataType.Number)
                    qty = Math.Round((decimal)Math.Clamp(qtyCell.GetDouble(), 0, (double)QuantityText.Max), 4);
                else
                    QuantityText.TryParse(qtyCell.GetFormattedString(), out qty);

                result.Add(new ToolItem
                {
                    Name = name,
                    Category = CategoryService.Normalize(row.Cell(2).GetFormattedString()),
                    Spec = row.Cell(3).GetFormattedString().Trim(),
                    Quantity = qty,
                    Unit = row.Cell(5).GetFormattedString().Trim(),
                    PurchasedAt = ReadDate(row.Cell(6)),
                    LastCheckedAt = ReadDate(row.Cell(7)),
                    Note = row.Cell(8).GetFormattedString(),
                });
            }
            return result;
        }

        /// <summary>日期儲存格可能是真的日期，也可能是文字（例如「115/10/8」）。</summary>
        private static DateTime? ReadDate(IXLCell cell)
        {
            if (cell.DataType == XLDataType.DateTime) return cell.GetDateTime().Date;
            return DateText.TryParse(cell.GetFormattedString(), DateTime.Today, out var d) ? d : null;
        }

        /// <summary>匯入時用來判斷「同一項工具」：名稱 + 規格相同（不分大小寫、忽略前後空白）。</summary>
        public static bool SameTool(ToolItem a, ToolItem b) =>
            string.Equals(a.Name.Trim(), b.Name.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Spec.Trim(), b.Spec.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
