using System;
using System.Globalization;

namespace GitBlameForVs.Localization
{
    /// <summary>
    /// 按 VS 的显示语言（CurrentUICulture）自动选择 blame 文案实现。
    /// 未匹配的语言统一兜底为英文。
    /// </summary>
    internal static class BlameText
    {
        private static readonly IBlameTextProvider Chinese = new ChineseBlameTextProvider();
        private static readonly IBlameTextProvider English = new EnglishBlameTextProvider();

        public static IBlameTextProvider Current =>
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? Chinese
                : English;
    }
}
