using System.Text;

namespace MultiGitCommit;

/// <summary>
/// 命令行参数拼接帮助。
///
/// git 的 pathspec 允许任意文件名（空格、引号、中文、反斜杠），
/// 直接拼字符串会被命令行解析器拆错，因此统一按 Windows 参数规则加引号转义
/// （.NET 在 Windows 用 CommandLineToArgvW 规则、在 macOS / Linux 用同样的引号规则解析 Arguments）。
/// </summary>
public static class GitArgument
{
    /// <summary>把单个参数（如文件路径）包成命令行安全的形式。</summary>
    public static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        var sb = new StringBuilder(value.Length + 8);
        sb.Append('"');

        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // 引号前的反斜杠必须成对加倍，再补一个转义引号
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }

        // 结尾的反斜杠会与收尾引号连在一起，同样需要加倍
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
