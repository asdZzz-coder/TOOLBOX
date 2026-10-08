using toolbox.Services;

namespace toolbox.Models
{
    /// <summary>左側分類清單的一列（全部工具 / 各分類 / 未分類 / 已報廢），每次資料變動時重新產生。</summary>
    public record CategoryItem(CategoryFilter Filter, string Display, int Count)
    {
        public bool IsUserCategory => Filter.Kind == CategoryFilterKind.Category;

        /// <summary>「已報廢」上方畫一條分隔線，跟一般分類分開。</summary>
        public bool IsScrapped => Filter.Kind == CategoryFilterKind.Scrapped;

        public string Icon => Filter.Kind switch
        {
            CategoryFilterKind.Scrapped => "",    // 已報廢（垃圾桶）
            CategoryFilterKind.All => "",           // 全部
            CategoryFilterKind.Uncategorized => "", // 未分類（文件）
            _ => "",                                // 資料夾
        };

        // 螢幕閱讀器與 UI 自動化讀到的名稱
        public override string ToString() => Display;
    }

    /// <summary>編輯表單「分類」下拉選單的選項；Name 為空字串表示未分類。</summary>
    public record CategoryOption(string Name, string Display)
    {
        public override string ToString() => Display;
    }
}
