using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace ai_harness_file_rules;

/// <summary>
/// glob パターンとパスの一致判定。<c>**</c>（任意段のディレクトリ, 0 段含む）／<c>*</c>（区切りを跨がない任意長）／
/// <c>?</c>（区切り以外の任意 1 文字）を解釈する。ai-harness-directory-checker / ai-harness-constants と同一。
/// </summary>
public static class GlobMatcher
{
    /// <summary>
    /// パターンとパスの一致。パスは <c>/</c> 区切りに正規化し、フルパスと各 <c>/</c> 区切りサフィックスの
    /// いずれかにマッチすれば true（相対パターンが絶対パスにも効く）。大文字小文字は無視。
    /// </summary>
    public static bool IsMatch(string pattern, string? path)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(path))
        {
            return false;
        }
        var regex = ToRegex(pattern.Replace('\\', '/'));
        var normPath = path.Replace('\\', '/');

        if (regex.IsMatch(normPath))
        {
            return true;
        }
        var idx = normPath.IndexOf('/');
        while (idx >= 0 && idx + 1 < normPath.Length)
        {
            var suffix = normPath[(idx + 1)..];
            if (regex.IsMatch(suffix))
            {
                return true;
            }
            idx = normPath.IndexOf('/', idx + 1);
        }
        return false;
    }

    /// <summary>
    /// 変換済み正規表現のキャッシュ。能動スキャン（Fire）は全ファイル × 全パターンで照合するため、
    /// 同じ glob のコンパイルを繰り返さない。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);

    /// <summary>
    /// glob を正規表現へ変換する。<c>**/</c>＝任意段のディレクトリ（0 段含む）、<c>**</c>＝区切りを跨ぐ任意長、
    /// <c>*</c>＝区切りを跨がない任意長、<c>?</c>＝区切り以外の任意 1 文字。それ以外のメタ文字はエスケープ。
    /// </summary>
    public static Regex ToRegex(string glob) => RegexCache.GetOrAdd(glob, Compile);

    private static Regex Compile(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(?:.*/)?");
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '.':
                case '$':
                case '^':
                case '{':
                case '}':
                case '[':
                case ']':
                case '(':
                case ')':
                case '+':
                case '|':
                case '\\':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
