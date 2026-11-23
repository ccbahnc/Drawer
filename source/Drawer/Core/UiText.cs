using System.Globalization;

namespace Drawer.Core;

internal static partial class UiText
{
    private static readonly DrawerLanguage SystemLanguage = FromLanguageTag(CultureInfo.CurrentUICulture.Name);
    internal static DrawerLanguage Language { get; private set; } = SystemLanguage;

    internal static DrawerLanguage FromLanguageTag(string tag) =>
        tag.Equals("zh", System.StringComparison.OrdinalIgnoreCase) ||
        tag.StartsWith("zh-", System.StringComparison.OrdinalIgnoreCase)
            ? DrawerLanguage.SimplifiedChinese : DrawerLanguage.English;

    internal static void Use(DrawerLanguage? language) => Language = language ?? SystemLanguage;

    internal static string Get(string key, params object?[] arguments)
    {
        var resource = Resources[key];
        bool chinese = Language == DrawerLanguage.SimplifiedChinese;
        string template = chinese ? resource.Chinese : resource.English;
        return arguments.Length == 0 ? template : string.Format(
            CultureInfo.GetCultureInfo(chinese ? "zh-CN" : "en"), template, arguments);
    }

    internal static string Items(int count) => Get(count == 1 ? "ItemOne" : "ItemMany", count);
}
