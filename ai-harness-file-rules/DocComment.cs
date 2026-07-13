using System.Text;
using System.Text.RegularExpressions;

namespace ai_harness_file_rules;

/// <summary>doc コメントを解析した結果。</summary>
/// <param name="Description">タグ・セクションを除いた説明文（前後空白は除去済み）。</param>
/// <param name="DocumentedParams">記述されている引数名の集合。</param>
public readonly record struct DocInfo(string Description, IReadOnlySet<string> DocumentedParams);

/// <summary>
/// doc コメントの生テキストから説明文と引数の記述を取り出す。
///
/// 引数の書式は言語の標準に合わせる:
///   Java / C / C++ / TypeScript … <c>@param name …</c>（Doxygen の <c>\param</c>・<c>@param[in]</c> も許容）
///   Python                      … Google スタイルの <c>Args:</c> セクション
///   Rust                        … rustdoc の <c># Arguments</c> セクション
///   Go                          … 列挙タグを持たないため、本文に引数名が現れるかで判定する
/// </summary>
public static class DocComment
{
    /// <summary><c>@param name</c> / <c>\param[in] name</c> の形。</summary>
    private static readonly Regex TagParam = new(
        @"^[@\\]param\s+(?:\[[^\]]*\]\s*)?[`']?([A-Za-z_$][A-Za-z0-9_$]*)",
        RegexOptions.Compiled);

    /// <summary>Google スタイルの引数行（<c>name:</c> / <c>name (int):</c>、<c>*args</c> 可）。</summary>
    private static readonly Regex GoogleParam = new(
        @"^\**([A-Za-z_][A-Za-z0-9_]*)\s*(?:\([^)]*\))?\s*:",
        RegexOptions.Compiled);

    /// <summary>rustdoc の <c># Arguments</c> 配下の箇条書き（<c>* `name` - …</c>）。</summary>
    private static readonly Regex RustParam = new(
        @"^[*\-+]\s*[`']?([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    /// <summary>説明文を打ち切るタグ・セクションの開始行。</summary>
    private static readonly Regex SectionStart = new(
        @"^([@\\][A-Za-z]+|#+\s|Args:|Arguments:|Parameters:|Returns:|Raises:|Yields:|:param\b|:returns?:)",
        RegexOptions.Compiled);

    /// <summary>
    /// doc を解析する。<paramref name="declaredParams"/> は Go の判定（本文に引数名が出現するか）にのみ使う。
    /// </summary>
    public static DocInfo Parse(string doc, string lang, IReadOnlyList<string> declaredParams)
    {
        var lines = Clean(doc, lang);
        var description = ExtractDescription(lines);
        var documented = lang switch
        {
            "python" => ExtractSectionParams(lines, GoogleParam, IsGoogleArgsHeader),
            "rust" => ExtractSectionParams(lines, RustParam, IsRustArgumentsHeader),
            "go" => ExtractProseParams(lines, declaredParams),
            _ => ExtractTagParams(lines),
        };
        return new DocInfo(description, documented);
    }

    /// <summary>
    /// コメントマーカー（<c>/** */</c>・行頭の <c>*</c>・<c>///</c>・<c>//</c>・<c>"""</c>）を落として行に分ける。
    /// </summary>
    private static List<string> Clean(string doc, string lang)
    {
        var body = doc;
        if (lang == "python")
        {
            body = body.Trim();
            foreach (var quote in new[] { "\"\"\"", "'''", "\"", "'" })
            {
                if (body.Length >= quote.Length * 2 && body.StartsWith(quote, StringComparison.Ordinal)
                    && body.EndsWith(quote, StringComparison.Ordinal))
                {
                    body = body[quote.Length..^quote.Length];
                    break;
                }
            }
        }

        var lines = new List<string>();
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("/**", StringComparison.Ordinal))
            {
                line = line[3..];
            }
            else if (line.StartsWith("///", StringComparison.Ordinal))
            {
                line = line[3..];
            }
            else if (line.StartsWith("//", StringComparison.Ordinal))
            {
                line = line[2..];
            }
            else if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                line = line[2..];
            }
            if (line.EndsWith("*/", StringComparison.Ordinal))
            {
                line = line[..^2];
            }
            // javadoc / doxygen の行頭 '*'（区切り線 '***' は空行扱いになる）。
            line = line.TrimStart('*').Trim();
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>最初のセクション・タグ行に当たるまでを説明文とする。</summary>
    private static string ExtractDescription(List<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (SectionStart.IsMatch(line))
            {
                break;
            }
            if (line.Length == 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                continue;
            }
            if (sb.Length > 0 && sb[^1] != '\n')
            {
                sb.Append('\n');
            }
            sb.Append(line);
        }
        return sb.ToString().Trim();
    }

    /// <summary><c>@param name</c> 形式を全行から拾う。</summary>
    private static IReadOnlySet<string> ExtractTagParams(List<string> lines)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var m = TagParam.Match(line);
            if (m.Success)
            {
                names.Add(m.Groups[1].Value);
            }
        }
        return names;
    }

    /// <summary>
    /// 引数セクション（Google の <c>Args:</c>・rustdoc の <c># Arguments</c>）の中だけを見て引数名を拾う。
    /// 次のセクション見出しに当たったら終了する。
    /// </summary>
    private static IReadOnlySet<string> ExtractSectionParams(
        List<string> lines, Regex paramPattern, Func<string, bool> isHeader)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var inSection = false;
        foreach (var line in lines)
        {
            if (isHeader(line))
            {
                inSection = true;
                continue;
            }
            if (!inSection)
            {
                continue;
            }
            if (line.Length > 0 && SectionStart.IsMatch(line))
            {
                break; // 別セクションへ移った。
            }
            var m = paramPattern.Match(line);
            if (m.Success)
            {
                names.Add(m.Groups[1].Value);
            }
        }
        return names;
    }

    private static bool IsGoogleArgsHeader(string line) =>
        line.Equals("Args:", StringComparison.Ordinal)
        || line.Equals("Arguments:", StringComparison.Ordinal);

    private static bool IsRustArgumentsHeader(string line) =>
        line.TrimStart('#').Trim().Equals("Arguments", StringComparison.OrdinalIgnoreCase)
        && line.StartsWith("#", StringComparison.Ordinal);

    /// <summary>
    /// Go 向け。列挙タグが無いため、宣言済みの引数名が本文に単語として現れるかで「記述あり」とみなす。
    /// </summary>
    private static IReadOnlySet<string> ExtractProseParams(
        List<string> lines, IReadOnlyList<string> declaredParams)
    {
        var text = string.Join("\n", lines);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var param in declaredParams)
        {
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(param)}\b"))
            {
                names.Add(param);
            }
        }
        return names;
    }
}
