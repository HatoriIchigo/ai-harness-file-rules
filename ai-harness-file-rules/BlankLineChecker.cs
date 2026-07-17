namespace ai_harness_file_rules;

/// <summary>
/// ソースの行を走査し、<c>blank-line</c> の上限を超えて空行なしで続く塊を違反として挙げる。
///
/// 空行（空白のみの行を含む）とコメント行は区切りとみなし、連続の数えを 0 に戻す。
/// コメント行の判定は AST（<see cref="StructureInfo.CommentLines"/>）由来のため、
/// 文字列リテラル内の <c>//</c> をコメントと誤認しない。
/// </summary>
public static class BlankLineChecker
{
    /// <summary>reason に列挙する違反塊の最大件数。</summary>
    private const int MaxReported = 20;

    /// <summary>空行なしで続く行数の上限に対する違反リストを返す。</summary>
    public static List<string> Evaluate(string source, int maxRun, IReadOnlySet<int> commentLines)
    {
        var violations = new List<string>();
        var over = 0;

        var lines = source.Split('\n');
        // 末尾改行の後ろは行として数えない（FileRulesPlugin.CountLines と揃える）。
        var count = source.EndsWith('\n') ? lines.Length - 1 : lines.Length;

        var run = 0;
        var runStart = 0;

        for (var i = 0; i < count; i++)
        {
            var lineNumber = i + 1;
            var isBreak = string.IsNullOrWhiteSpace(lines[i].TrimEnd('\r')) || commentLines.Contains(lineNumber);

            if (isBreak)
            {
                Flush(run, runStart, lineNumber - 1, maxRun, violations, ref over);
                run = 0;
                continue;
            }
            if (run == 0)
            {
                runStart = lineNumber;
            }
            run++;
        }
        Flush(run, runStart, count, maxRun, violations, ref over);

        if (over > MaxReported)
        {
            violations.Add($"…ほか {over - MaxReported} 箇所が空行なしの連続超過");
        }
        return violations;
    }

    /// <summary>塊が途切れた時点で、上限を超えていれば違反として記録する。</summary>
    private static void Flush(
        int run, int runStart, int runEnd, int maxRun, List<string> violations, ref int over)
    {
        if (run <= maxRun)
        {
            return;
        }
        over++;
        if (violations.Count < MaxReported)
        {
            violations.Add(
                $"{runStart} 行目〜{runEnd} 行目が空行なしで {run} 行連続（上限 {maxRun} 行。区切りの空行を挟む）");
        }
    }
}
