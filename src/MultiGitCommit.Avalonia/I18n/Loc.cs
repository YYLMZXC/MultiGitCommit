using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiGitCommit;

/// <summary>
/// 轻量本地化服务：中文文案直接以源码字符串为「键」，其他语言查表替换，查不到就回退中文。
///
/// 之所以不用 .resx / 卫星程序集：本项目是单项目多目标（桌面 + Android + iOS）且桌面发布为单文件，
/// 卫星程序集在移动端打包与裁剪时更容易出问题；内置字典零 IO、零反射、各平台行为一致。
///
/// XAML 里的静态文本走 <c>{DynamicResource L.xxx}</c>，由 App 把 <see cref="GetResourceEntries"/>
/// 注入 Application.Resources；切换语言时事件通知 App 原地重写这些资源值，界面即刻刷新。
/// C# 里的文案用 <see cref="T(string)"/> / <see cref="F(FormattableString)"/> 取值。
/// </summary>
public static class Loc
{
    private static AppLanguage s_language = AppLanguage.Chinese;

    /// <summary>当前界面语言（默认简体中文）。</summary>
    public static AppLanguage Current => s_language;

    /// <summary>语言变更后触发；宿主用它刷新 XAML 资源与少量由代码赋值的文本。</summary>
    public static event Action? LanguageChanged;

    /// <summary>启动时按设置初始化语言，不触发变更事件（此时还没有任何界面）。</summary>
    public static void Initialize(AppLanguage language)
    {
        s_language = Enum.IsDefined(language) ? language : AppLanguage.Chinese;
    }

    /// <summary>运行时切换语言并通知界面刷新。</summary>
    public static void SetLanguage(AppLanguage language)
    {
        AppLanguage next = Enum.IsDefined(language) ? language : AppLanguage.Chinese;
        if (next == s_language) return;
        s_language = next;
        LanguageChanged?.Invoke();
    }

    /// <summary>翻译一段纯文本；中文或未收录的文案原样返回。</summary>
    public static string T(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (s_language == AppLanguage.English && LocStringsEn.Values.TryGetValue(text, out string? english))
            return english;
        return text;
    }

    /// <summary>
    /// 翻译插值字符串：<c>Loc.F($"共 {count} 项")</c> 会以去掉插值表达式后的格式串为键查表，
    /// 再用 <see cref="string.Format(IFormatProvider, string, object?[])"/> 把原参数填回去，
    /// 因此格式说明符（如 <c>{0:0.##}</c>）与参数顺序都保持不变。
    /// </summary>
    public static string F(FormattableString text)
    {
        if (text == null) return string.Empty;
        string format = T(text.Format);
        return string.Format(CultureInfo.CurrentCulture, format, text.GetArguments());
    }

    /// <summary>供 App 注入 Application.Resources 的全部 XAML 文案（键固定，值随语言变化）。</summary>
    public static IEnumerable<KeyValuePair<string, string>> GetResourceEntries()
    {
        Dictionary<string, string>? english = s_language == AppLanguage.English ? LocStringsEn.Values : null;
        foreach (KeyValuePair<string, string> entry in LocStringsZh.Values)
        {
            string value = english != null && english.TryGetValue(entry.Key, out string? localized)
                ? localized
                : entry.Value;
            yield return new KeyValuePair<string, string>(entry.Key, value);
        }
    }
}
