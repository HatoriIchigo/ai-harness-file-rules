using System.Collections;

namespace ai_harness_file_rules;

/// <summary>
/// doc コメントの規則。<c>null</c> は「検査しない」（省略）を表す。
/// </summary>
public readonly record struct CommentRule(
    bool? ClassRequire,
    int? ClassMinLength,
    bool? MethodRequire,
    int? MethodMinLength,
    bool? MethodParams,
    bool? MethodParamsStrict);

/// <summary>
/// 1 エントリの規則。<c>null</c> の数値/真偽は「検査しない」（<c>*</c> または省略）を表す。
/// </summary>
public readonly record struct FileRule(
    string Pattern,
    int? MaxFileLines,
    int? MaxLineLength,
    int TabWidth,
    int? MaxConsecutiveLines,
    bool? ClassOneFile,
    bool? ClassForce,
    string? ClassExtend,
    int? MethodNum,
    int? MethodMaxLines,
    bool? MethodInClass,
    CommentRule Comment);

/// <summary>
/// ai-harness-file-rules の設定を解釈・検証した結果。
///
/// スキーマ:
/// <code>
/// files:
///   - pattern: "src/main/java/**/*.java"
///     lines: 500              # 1 ファイルの最大行数（* または省略で無制限）
///     line-length: 120        # 1 行の最大文字数（* または省略で無制限）
///     tab-width: 4            # line-length でタブを何文字と数えるか（省略で 4）
///     blank-line: 10          # 空行なしで続けられる行数の上限（* または省略で無制限）
///     class:
///       one-file: true        # 1 ファイル = 1 クラス（トップレベルのクラス相当 &lt;= 1）
///       force: true           # 必ずクラスが要る（メソッドのみのファイル禁止）
///       extend: "path/to/Base.java"  # 指定ファイルで宣言されたクラスを必ず継承（配列不可・単一パス）
///     method:
///       num: 5                # ファイル内メソッド数の上限（* または省略で無制限）
///       lines: 50             # 1 メソッドの最大行数（* または省略で無制限）
///       in-class: true        # メソッド・操作は必ずクラス内（クラス外メソッド／クラス外操作を禁止）
///     comment:
///       class:
///         require: true       # クラスに doc コメント必須
///         min-length: 10      # 説明文の最低文字数
///       method:
///         require: true       # メソッドに doc コメント必須
///         min-length: 10      # 説明文の最低文字数
///         params: true        # 全ての引数が記述されていること
///         params-strict: true # 存在しない引数の記述を禁止（シグネチャとの乖離を防ぐ）
/// </code>
/// 数値に <c>*</c> を使う場合は YAML のエイリアス扱いを避けるため <c>"*"</c> と引用するか、キーを省略する。
/// </summary>
public sealed class FileRulesConfig
{
    /// <summary>tab-width 省略時に使うタブ幅。</summary>
    private const int DefaultTabWidth = 4;

    public IReadOnlyList<FileRule> Entries { get; }
    public IReadOnlyList<string> Errors { get; }

    public bool IsUsable => Errors.Count == 0 && Entries.Count > 0;

    private FileRulesConfig(IReadOnlyList<FileRule> entries, IReadOnlyList<string> errors)
    {
        Entries = entries;
        Errors = errors;
    }

    public static FileRulesConfig Parse(IReadOnlyDictionary<string, object> config)
    {
        var entries = new List<FileRule>();
        var errors = new List<string>();

        var filesRaw = config.TryGetValue("files", out var f) ? f : null;
        if (filesRaw is not IList filesList || filesRaw is string)
        {
            errors.Add("files が未設定、またはリストではありません。");
            return new FileRulesConfig(entries, errors);
        }

        for (var i = 0; i < filesList.Count; i++)
        {
            ParseEntry(filesList[i], i, entries, errors);
        }
        if (entries.Count == 0 && errors.Count == 0)
        {
            errors.Add("files に有効なエントリがありません。");
        }
        return new FileRulesConfig(entries, errors);
    }

    private static void ParseEntry(object? item, int index, List<FileRule> entries, List<string> errors)
    {
        if (item is not IDictionary map)
        {
            errors.Add($"files[{index}]: マップである必要があります。");
            return;
        }

        var pattern = Get(map, "pattern")?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            errors.Add($"files[{index}]: pattern が未設定です。");
            return;
        }

        var maxFileLines = ParseLimit(Get(map, "lines"), index, "lines", pattern, errors);
        var maxLineLength = ParseLimit(Get(map, "line-length"), index, "line-length", pattern, errors);
        var tabWidth = ParseTabWidth(Get(map, "tab-width"), index, pattern, errors);
        var maxConsecutive = ParseLimit(Get(map, "blank-line"), index, "blank-line", pattern, errors);

        bool? oneFile = null, force = null;
        string? extend = null;
        var classRaw = Get(map, "class");
        if (classRaw is not null)
        {
            if (classRaw is IDictionary classMap)
            {
                oneFile = ParseBool(Get(classMap, "one-file"), index, "class.one-file", pattern, errors);
                force = ParseBool(Get(classMap, "force"), index, "class.force", pattern, errors);
                extend = ParseExtend(Get(classMap, "extend"), index, pattern, errors);
            }
            else
            {
                errors.Add($"files[{index}] (pattern='{pattern}'): class はマップである必要があります。");
            }
        }

        int? methodNum = null, methodLines = null;
        bool? methodInClass = null;
        var methodRaw = Get(map, "method");
        if (methodRaw is not null)
        {
            if (methodRaw is IDictionary methodMap)
            {
                methodNum = ParseLimit(Get(methodMap, "num"), index, "method.num", pattern, errors);
                methodLines = ParseLimit(Get(methodMap, "lines"), index, "method.lines", pattern, errors);
                methodInClass = ParseBool(Get(methodMap, "in-class"), index, "method.in-class", pattern, errors);
            }
            else
            {
                errors.Add($"files[{index}] (pattern='{pattern}'): method はマップである必要があります。");
            }
        }

        var comment = ParseComment(map, index, pattern, errors);

        entries.Add(new FileRule(
            pattern, maxFileLines, maxLineLength, tabWidth, maxConsecutive,
            oneFile, force, extend, methodNum, methodLines, methodInClass, comment));
    }

    /// <summary>doc コメント規則（<c>comment.class</c> / <c>comment.method</c>）を解釈する。</summary>
    private static CommentRule ParseComment(IDictionary map, int index, string pattern, List<string> errors)
    {
        var commentRaw = Get(map, "comment");
        if (commentRaw is null)
        {
            return default;
        }
        if (commentRaw is not IDictionary commentMap)
        {
            errors.Add($"files[{index}] (pattern='{pattern}'): comment はマップである必要があります。");
            return default;
        }

        bool? classRequire = null;
        int? classMinLength = null;
        var classRaw = Get(commentMap, "class");
        if (classRaw is not null)
        {
            if (classRaw is IDictionary classMap)
            {
                classRequire = ParseBool(Get(classMap, "require"), index, "comment.class.require", pattern, errors);
                classMinLength = ParseLimit(
                    Get(classMap, "min-length"), index, "comment.class.min-length", pattern, errors);
            }
            else
            {
                errors.Add($"files[{index}] (pattern='{pattern}'): comment.class はマップである必要があります。");
            }
        }

        bool? methodRequire = null, methodParams = null, methodParamsStrict = null;
        int? methodMinLength = null;
        var methodRaw = Get(commentMap, "method");
        if (methodRaw is not null)
        {
            if (methodRaw is IDictionary methodMap)
            {
                methodRequire = ParseBool(Get(methodMap, "require"), index, "comment.method.require", pattern, errors);
                methodMinLength = ParseLimit(
                    Get(methodMap, "min-length"), index, "comment.method.min-length", pattern, errors);
                methodParams = ParseBool(Get(methodMap, "params"), index, "comment.method.params", pattern, errors);
                methodParamsStrict = ParseBool(
                    Get(methodMap, "params-strict"), index, "comment.method.params-strict", pattern, errors);
            }
            else
            {
                errors.Add($"files[{index}] (pattern='{pattern}'): comment.method はマップである必要があります。");
            }
        }

        return new CommentRule(
            classRequire, classMinLength, methodRequire, methodMinLength, methodParams, methodParamsStrict);
    }

    /// <summary>
    /// class.extend を解釈。単一のファイルパス（プロジェクトルート相対、または絶対パス）のみ許可し、
    /// 配列は誤用としてエラーにする。未設定・空文字は null（検査しない）。
    /// </summary>
    private static string? ParseExtend(object? raw, int index, string pattern, List<string> errors)
    {
        if (raw is null)
        {
            return null;
        }
        if (raw is IList or IDictionary)
        {
            errors.Add($"files[{index}] (pattern='{pattern}'): class.extend は配列ではなく単一のファイルパスを指定してください。");
            return null;
        }
        var s = raw.ToString()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>数値上限を解釈。null/空/<c>*</c> は「無制限」(null)。整数以外はエラー。</summary>
    private static int? ParseLimit(object? raw, int index, string key, string pattern, List<string> errors)
    {
        var s = raw?.ToString()?.Trim();
        if (string.IsNullOrEmpty(s) || s == "*")
        {
            return null;
        }
        if (int.TryParse(s, out var n) && n >= 0)
        {
            return n;
        }
        errors.Add($"files[{index}] (pattern='{pattern}'): {key} は 0 以上の整数か * を指定してください（値='{s}'）。");
        return null;
    }

    /// <summary>タブ幅を解釈。省略は既定の 4。1 以上の整数以外はエラー（* は無制限にできないため不可）。</summary>
    private static int ParseTabWidth(object? raw, int index, string pattern, List<string> errors)
    {
        var s = raw?.ToString()?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return DefaultTabWidth;
        }
        if (int.TryParse(s, out var n) && n >= 1)
        {
            return n;
        }
        errors.Add($"files[{index}] (pattern='{pattern}'): tab-width は 1 以上の整数を指定してください（値='{s}'）。");
        return DefaultTabWidth;
    }

    /// <summary>真偽を解釈。省略は null（未指定）。true/false 以外はエラー。</summary>
    private static bool? ParseBool(object? raw, int index, string key, string pattern, List<string> errors)
    {
        if (raw is null)
        {
            return null;
        }
        var s = raw.ToString()?.Trim();
        if (bool.TryParse(s, out var b))
        {
            return b;
        }
        errors.Add($"files[{index}] (pattern='{pattern}'): {key} は true/false を指定してください（値='{s}'）。");
        return null;
    }

    private static object? Get(IDictionary map, string key) => map.Contains(key) ? map[key] : null;
}
