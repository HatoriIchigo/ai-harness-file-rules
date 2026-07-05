using System.Collections;

namespace ai_harness_file_rules;

/// <summary>
/// 1 エントリの規則。<c>null</c> の数値/真偽は「検査しない」（<c>*</c> または省略）を表す。
/// </summary>
public readonly record struct FileRule(
    string Pattern,
    int? MaxFileLines,
    bool? ClassOneFile,
    bool? ClassForce,
    int? MethodNum,
    int? MethodMaxLines);

/// <summary>
/// ai-harness-file-rules の設定を解釈・検証した結果。
///
/// スキーマ:
/// <code>
/// files:
///   - pattern: "src/main/java/**/*.java"
///     lines: 500              # 1 ファイルの最大行数（* または省略で無制限）
///     class:
///       one-file: true        # 1 ファイル = 1 クラス（トップレベルのクラス相当 &lt;= 1）
///       force: true           # 必ずクラスが要る（メソッドのみのファイル禁止）
///     method:
///       num: 5                # ファイル内メソッド数の上限（* または省略で無制限）
///       lines: 50             # 1 メソッドの最大行数（* または省略で無制限）
/// </code>
/// 数値に <c>*</c> を使う場合は YAML のエイリアス扱いを避けるため <c>"*"</c> と引用するか、キーを省略する。
/// </summary>
public sealed class FileRulesConfig
{
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

        bool? oneFile = null, force = null;
        var classRaw = Get(map, "class");
        if (classRaw is not null)
        {
            if (classRaw is IDictionary classMap)
            {
                oneFile = ParseBool(Get(classMap, "one-file"), index, "class.one-file", pattern, errors);
                force = ParseBool(Get(classMap, "force"), index, "class.force", pattern, errors);
            }
            else
            {
                errors.Add($"files[{index}] (pattern='{pattern}'): class はマップである必要があります。");
            }
        }

        int? methodNum = null, methodLines = null;
        var methodRaw = Get(map, "method");
        if (methodRaw is not null)
        {
            if (methodRaw is IDictionary methodMap)
            {
                methodNum = ParseLimit(Get(methodMap, "num"), index, "method.num", pattern, errors);
                methodLines = ParseLimit(Get(methodMap, "lines"), index, "method.lines", pattern, errors);
            }
            else
            {
                errors.Add($"files[{index}] (pattern='{pattern}'): method はマップである必要があります。");
            }
        }

        entries.Add(new FileRule(pattern, maxFileLines, oneFile, force, methodNum, methodLines));
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
