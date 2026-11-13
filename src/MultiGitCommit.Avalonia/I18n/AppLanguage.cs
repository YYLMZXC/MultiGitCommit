namespace MultiGitCommit;

/// <summary>
/// 界面语言。新增语言时：在此加枚举值，并在 <see cref="LocStringsEn"/> 同目录补一份
/// <c>Strings.&lt;区域&gt;.g.cs</c> 字典，再在 <see cref="Loc.GetResourceEntries"/> 中接入。
/// 值会写入 settings.json，只能追加、不要改动已有数值。
/// </summary>
public enum AppLanguage
{
    /// <summary>简体中文（默认）。中文文案直接写在源码里，作为所有语言的回退文本。</summary>
    Chinese = 0,

    /// <summary>English。</summary>
    English = 1,
}
