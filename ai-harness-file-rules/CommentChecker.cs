namespace ai_harness_file_rules;

/// <summary>
/// 宣言（クラス・メソッド）の doc コメントを設定の規則と突き合わせて違反を挙げる。
///
/// <c>params</c> はシグネチャとの整合まで見る:
///   未記述の引数があれば違反（記述漏れ）。<c>params-strict</c> なら、存在しない引数の記述も違反
///   （引数のリネーム・削除でコメントが取り残された状態を検出する）。
/// </summary>
public static class CommentChecker
{
    /// <summary>reason に列挙する違反の最大件数。</summary>
    private const int MaxReported = 20;

    /// <summary>言語 id → 引数の書き方（違反時に示す修正のヒント）。</summary>
    private static readonly Dictionary<string, string> ParamHint = new(StringComparer.Ordinal)
    {
        ["c"] = "@param 名前 説明",
        ["cpp"] = "@param 名前 説明",
        ["java"] = "@param 名前 説明",
        ["typescript"] = "@param 名前 - 説明",
        ["tsx"] = "@param 名前 - 説明",
        ["python"] = "Google スタイルの Args: セクションに 名前: 説明",
        ["rust"] = "# Arguments セクションに * `名前` - 説明",
        ["go"] = "本文中で引数名に触れる",
    };

    /// <summary>doc コメント規則に対する違反リストを返す。</summary>
    public static List<string> Evaluate(
        CommentRule rule, IReadOnlyList<DeclarationInfo> declarations, string lang)
    {
        var violations = new List<string>();
        if (declarations.Count == 0)
        {
            return violations;
        }

        foreach (var decl in declarations)
        {
            if (violations.Count >= MaxReported)
            {
                violations.Add("…ほかにも doc コメントの違反があります");
                break;
            }
            if (decl.Kind == DeclKind.Class)
            {
                CheckClass(rule, decl, lang, violations);
            }
            else
            {
                CheckMethod(rule, decl, lang, violations);
            }
        }
        return violations;
    }

    private static void CheckClass(
        CommentRule rule, DeclarationInfo decl, string lang, List<string> violations)
    {
        if (rule.ClassRequire != true)
        {
            return;
        }
        if (decl.Doc is null)
        {
            violations.Add($"クラス '{decl.Name}'（{decl.StartLine} 行目）に doc コメントがない");
            return;
        }
        var info = DocComment.Parse(decl.Doc, lang, decl.Parameters);
        CheckDescription(info.Description, rule.ClassMinLength, "クラス", decl, violations);
    }

    private static void CheckMethod(
        CommentRule rule, DeclarationInfo decl, string lang, List<string> violations)
    {
        var needsDoc = rule.MethodRequire == true;
        var needsParams = rule.MethodParams == true || rule.MethodParamsStrict == true;
        if (!needsDoc && !needsParams)
        {
            return;
        }

        if (decl.Doc is null)
        {
            // doc が無い場合、引数の記述漏れは「コメントが無い」として 1 件にまとめる（重複報告を避ける）。
            if (needsDoc || decl.Parameters.Count > 0)
            {
                violations.Add($"メソッド '{decl.Name}'（{decl.StartLine} 行目）に doc コメントがない");
            }
            return;
        }

        var info = DocComment.Parse(decl.Doc, lang, decl.Parameters);
        if (needsDoc)
        {
            CheckDescription(info.Description, rule.MethodMinLength, "メソッド", decl, violations);
        }

        if (rule.MethodParams == true)
        {
            var missing = decl.Parameters.Where(p => !info.DocumentedParams.Contains(p)).ToList();
            if (missing.Count > 0)
            {
                violations.Add(
                    $"メソッド '{decl.Name}'（{decl.StartLine} 行目）の引数 {Quote(missing)} が doc コメントに未記述"
                    + $"（{Hint(lang)}）");
            }
        }

        if (rule.MethodParamsStrict == true)
        {
            var declared = new HashSet<string>(decl.Parameters, StringComparer.Ordinal);
            var unknown = info.DocumentedParams.Where(p => !declared.Contains(p)).ToList();
            if (unknown.Count > 0)
            {
                violations.Add(
                    $"メソッド '{decl.Name}'（{decl.StartLine} 行目）の doc コメントに存在しない引数 {Quote(unknown)} が記述されている"
                    + "（シグネチャに合わせて修正する）");
            }
        }
    }

    /// <summary>説明文の有無と最低文字数を確認する。</summary>
    private static void CheckDescription(
        string description, int? minLength, string kindLabel, DeclarationInfo decl, List<string> violations)
    {
        if (description.Length == 0)
        {
            violations.Add($"{kindLabel} '{decl.Name}'（{decl.StartLine} 行目）の doc コメントに説明がない");
            return;
        }
        if (minLength is { } min && description.Length < min)
        {
            violations.Add(
                $"{kindLabel} '{decl.Name}'（{decl.StartLine} 行目）の説明が短い（{description.Length} 文字 < 最低 {min} 文字）");
        }
    }

    private static string Hint(string lang) =>
        ParamHint.TryGetValue(lang, out var hint) ? hint : "doc コメントに引数を記述する";

    private static string Quote(IEnumerable<string> names) =>
        string.Join(", ", names.Select(n => $"'{n}'"));
}
