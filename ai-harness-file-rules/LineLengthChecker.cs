namespace ai_harness_file_rules;

/// <summary>
/// ソースの各行を <c>line-length</c> の上限と突き合わせて違反を挙げる。
///
/// 数えるのは表示幅ではなく文字の個数。全角・半角を区別せず、どちらも 1 文字とする
/// （サロゲートペアも 1 文字）。タブだけは設定の <c>tab-width</c> 文字として数える。
/// </summary>
public static class LineLengthChecker
{
    /// <summary>reason に列挙する違反行の最大件数。</summary>
    private const int MaxReported = 20;

    /// <summary>1 行の文字数の上限に対する違反リストを返す。</summary>
    public static List<string> Evaluate(string source, int maxLength, int tabWidth)
    {
        var violations = new List<string>();
        var over = 0;

        var lines = source.Split('\n');
        // 末尾改行の後ろは行として数えない（FileRulesPlugin.CountLines と揃える）。
        var count = source.EndsWith('\n') ? lines.Length - 1 : lines.Length;

        for (var i = 0; i < count; i++)
        {
            var length = Measure(lines[i].TrimEnd('\r'), tabWidth);
            if (length <= maxLength)
            {
                continue;
            }
            over++;
            if (violations.Count < MaxReported)
            {
                violations.Add($"{i + 1} 行目が {length} 文字で上限 {maxLength} 文字を超過");
            }
        }

        if (over > MaxReported)
        {
            violations.Add($"…ほか {over - MaxReported} 行が文字数超過");
        }
        return violations;
    }

    /// <summary>1 行の文字数を数える。タブは <paramref name="tabWidth"/> 文字。</summary>
    private static int Measure(string line, int tabWidth)
    {
        var count = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\t')
            {
                count += tabWidth;
                continue;
            }
            if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
            {
                i++;
            }
            count++;
        }
        return count;
    }
}
