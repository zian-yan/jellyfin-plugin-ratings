using System;
using System.Text;

namespace Jellyfin.Plugin.Ratings.Core;

/// <summary>
/// 日志文本清洗。
///
/// 影片标题、用户名这类值来自外部输入（元数据、账号名），直接插进日志时
/// 里面的 <c>\r\n</c> 可以伪造整行日志（日志注入），其他控制字符也会污染日志文件。
/// 所有外部可控字符串进日志前都应经过这里。
/// </summary>
public static class LogText
{
    private const int MaxLength = 200;

    /// <summary>去掉控制字符并截断。空值统一显示为 "-"。</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxLength) + 1);
        foreach (var ch in value)
        {
            if (builder.Length >= MaxLength)
            {
                builder.Append('…');
                break;
            }

            builder.Append(char.IsControl(ch) ? ' ' : ch);
        }

        return builder.ToString();
    }
}
