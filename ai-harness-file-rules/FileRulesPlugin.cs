using System.Text;
using System.Text.Json.Nodes;
using ai_harness_baselib;

namespace ai_harness_file_rules;

/// <summary>
/// ファイル単位のコード構造ルールを AST で強制するプラグイン。書き込み系ツール（Write / Edit / MultiEdit）の
/// <c>PostToolUse</c> で発火し、書き込んだソースファイルを tree-sitter で解析して、設定の規則に反したら deny する。
///
/// 検査項目（設定 files のエントリ単位）:
///   lines            … ファイル総行数の上限
///   class.one-file   … トップレベルのクラス相当は 1 個まで
///   class.force      … クラスが必ず要る（メソッドのみのファイル禁止）
///   method.num       … ファイル内メソッド数の上限
///   method.lines     … 1 メソッドの行数の上限
///   method.in-class  … メソッド・操作は必ずクラス内（クラス外メソッド／クラス外操作を禁止）
///   comment.class    … クラスの doc コメント（有無・説明の最低文字数）
///   comment.method   … メソッドの doc コメント（有無・説明の最低文字数・引数の記述とシグネチャの整合）
///
/// クラス概念の無い言語（C / Go / Rust）では class 検査（class.* / method.in-class）を自動スキップする。
/// 判定（対応言語のソースのみ対象。非ソースは常に許可）:
///   1. 設定が使用不可            → deny（フェイルクローズ）
///   2. pattern にマッチ（先頭優先）→ 構造を解析し規則違反を deny
///   3. どの pattern にもマッチせず → 許可（管理対象外）
///
/// hook とは別に、<c>ai-harness-main --fire</c> の能動スキャン（<see cref="Fire"/>）で既存ツリー全体を
/// 一括点検できる（走査範囲は設定 <c>fire.exclude</c> / <c>fire.gitignore</c> で絞る）。
/// </summary>
public sealed class FileRulesPlugin : PluginBase
{
    public override string PluginName => "ai-harness-file-rules";

    public override string Description =>
        "AST 解析で、ファイル単位のコード構造ルール（行数・1クラス1ファイル・メソッド）を強制する";

    public override IReadOnlyList<string> Events => new[] { "PostToolUse" };

    public override string ConfigName => "ai-harness-file-rules.yml";

    private static readonly HashSet<string> TargetTools =
        new(StringComparer.Ordinal) { "Write", "Edit", "MultiEdit" };

    /// <summary>reason に列挙する超過メソッドの最大件数。</summary>
    private const int MaxReportedMethods = 20;

    public override IEnumerable<LogEntry> Init()
    {
        yield return LogEntry.Info("初期化");
    }

    public override IEnumerable<LogEntry> Action(HookData data, PluginResult result)
    {
        if (data.Event != HookEvent.PostToolUse)
        {
            yield break;
        }
        var toolName = data.ToolName;
        if (toolName is null || !TargetTools.Contains(toolName))
        {
            yield break;
        }

        var filePath = ExtractFilePath(data);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            yield return LogEntry.Debug($"file_path を取得できないため検査スキップ（tool={toolName}）");
            yield break;
        }

        if (!StructureAnalyzer.TryGetLanguageId(filePath, out var languageId))
        {
            yield return LogEntry.Debug($"対応言語外のため対象外: {filePath}");
            yield break;
        }

        var config = FileRulesConfig.Parse(Config);

        // 1. 設定が使用不可ならフェイルクローズで deny。
        if (!config.IsUsable)
        {
            yield return LogEntry.Warning($"設定が使用不可のため deny（フェイルクローズ）: {filePath}");
            result.ExitCode = 2;
            result.Reason = BuildConfigErrorReason(config.Errors);
            yield break;
        }

        // 2. 先頭でマッチした pattern の規則を適用。
        FileRule? matched = null;
        foreach (var entry in config.Entries)
        {
            if (GlobMatcher.IsMatch(entry.Pattern, filePath))
            {
                matched = entry;
                break;
            }
        }
        if (matched is null)
        {
            // 3. 管理対象外。
            yield return LogEntry.Debug($"どの pattern にもマッチせず対象外: {filePath}");
            yield break;
        }

        string? source = null;
        string? readError = null;
        try
        {
            source = File.ReadAllText(filePath);
        }
        catch (Exception e)
        {
            readError = $"ファイルを読めないため検査スキップ: {filePath} ({e.GetType().Name})";
        }
        if (readError is not null)
        {
            yield return LogEntry.Warning(readError);
            yield break;
        }

        StructureInfo info;
        string? analyzeError = null;
        try
        {
            info = StructureAnalyzer.Analyze(languageId, source!);
        }
        catch (Exception e)
        {
            analyzeError = $"AST 解析に失敗（{languageId}）: {filePath} ({e.GetType().Name}: {e.Message})";
            info = new StructureInfo();
        }
        if (analyzeError is not null)
        {
            yield return LogEntry.Error(analyzeError);
            yield break;
        }

        var rule = matched.Value;
        var violations = Evaluate(rule, info, CountLines(source!), languageId);

        if (violations.Count == 0)
        {
            yield return LogEntry.Debug($"規則 OK: {filePath}");
            yield break;
        }

        foreach (var v in violations)
        {
            yield return LogEntry.Warning($"規則違反: {filePath}: {v}");
        }
        result.ExitCode = 2;
        result.Reason = BuildViolationReason(filePath, violations);
    }

    /// <summary>
    /// 能動スキャン。<c>ai-harness-main --fire</c> から起動され、<paramref name="projectRoot"/> 配下を走査して、
    /// <c>files</c> の <c>pattern</c> に合致する全ソースの構造を解析し、規則に合致しないファイルがあれば
    /// exit 2（検出）。走査対象は <c>fire.exclude</c>／<c>fire.gitignore</c> で絞る。
    ///
    /// hook（<see cref="Action"/>）が書き込みごとに 1 ファイルを検査するのに対し、こちらは既存ツリー全体を
    /// 一括点検する。hook のゲートではないため、非 0 は「差し戻し」ではなく検出結果のレポート表示。
    /// </summary>
    public override IEnumerable<LogEntry> Fire(string projectRoot, PluginResult result)
    {
        var config = FileRulesConfig.Parse(Config);

        // 設定が使えなければ検査対象・規則を決められない（Action と同じフェイルクローズ）。
        if (!config.IsUsable)
        {
            yield return LogEntry.Warning("設定が使用不可のためスキャンできない（フェイルクローズ）");
            result.ExitCode = 2;
            result.Reason = BuildConfigErrorReason(config.Errors);
            yield break;
        }

        var options = FireScanner.ReadOptions(Config);
        yield return LogEntry.Info(
            $"構造スキャン開始 root={projectRoot} 除外パターン=[{string.Join(", ", options.Exclude)}] gitignore={options.Gitignore}");

        var scan = FireScanner.Collect(projectRoot, options);
        if (scan.Warning is { } warning)
        {
            yield return LogEntry.Warning(warning);
        }

        var targets = SelectTargets(scan.Files, config);
        yield return LogEntry.Debug($"検査対象ファイル数: {targets.Count}（走査 {scan.Files.Count} 件）");

        var findings = new List<FileFinding>();
        foreach (var target in targets)
        {
            string? source = null;
            string? error = null;
            try
            {
                source = File.ReadAllText(target.Path);
            }
            catch (Exception e)
            {
                error = $"ファイルを読めないため検査スキップ: {target.Path} ({e.GetType().Name})";
            }
            if (error is not null)
            {
                yield return LogEntry.Warning(error);
                continue;
            }

            StructureInfo info = new();
            try
            {
                info = StructureAnalyzer.Analyze(target.LanguageId, source!);
            }
            catch (Exception e)
            {
                error = $"AST 解析に失敗（{target.LanguageId}）: {target.Path} ({e.GetType().Name}: {e.Message})";
            }
            if (error is not null)
            {
                yield return LogEntry.Error(error);
                continue;
            }

            var violations = Evaluate(target.Rule, info, CountLines(source!), target.LanguageId);
            if (violations.Count == 0)
            {
                continue;
            }
            yield return LogEntry.Warning($"規則違反 {violations.Count} 件: {target.Path}");
            findings.Add(new FileFinding(target.Path, violations));
        }

        if (findings.Count == 0)
        {
            yield return LogEntry.Info("規則に違反するファイルは見つからない");
            yield break; // ExitCode 0（許可）のまま
        }

        yield return LogEntry.Warning($"規則に違反するファイルを {findings.Count} 件検出");
        result.ExitCode = 2;
        result.Reason = BuildFireReason(findings);
    }

    /// <summary>
    /// 走査したファイルから検査対象を選ぶ。Action の判定順と同じく、対応言語のソースで、
    /// pattern に合致するもの（複数マッチは先頭優先）だけを、適用する規則とともに残す。
    /// </summary>
    private static List<FireTarget> SelectTargets(IReadOnlyList<string> files, FileRulesConfig config)
    {
        var targets = new List<FireTarget>();
        foreach (var file in files)
        {
            if (!StructureAnalyzer.TryGetLanguageId(file, out var languageId))
            {
                continue; // 対応言語外（.md 等）は対象外
            }
            foreach (var entry in config.Entries)
            {
                if (GlobMatcher.IsMatch(entry.Pattern, file))
                {
                    targets.Add(new FireTarget(file, languageId, entry));
                    break; // 先頭優先
                }
            }
        }
        return targets;
    }

    /// <summary>検査対象 1 件（パス・解析に使う言語 ID・適用する規則）。</summary>
    private readonly record struct FireTarget(string Path, string LanguageId, FileRule Rule);

    /// <summary>スキャンで違反が見つかったファイル 1 件。</summary>
    private readonly record struct FileFinding(string Path, IReadOnlyList<string> Violations);

    /// <summary>reason に列挙する違反ファイルの最大件数と、1 ファイルあたりの違反の最大件数。</summary>
    private const int MaxReportedFiles = 50;
    private const int MaxReportedPerFile = 5;

    private static string BuildFireReason(IReadOnlyList<FileFinding> findings)
    {
        var sb = new StringBuilder();
        sb.Append("コード構造ルールに違反しているファイルが ").Append(findings.Count).Append(" 件あります:\n");
        foreach (var finding in findings.Take(MaxReportedFiles))
        {
            sb.Append("- ").Append(finding.Path).Append('\n');
            foreach (var violation in finding.Violations.Take(MaxReportedPerFile))
            {
                sb.Append("    - ").Append(violation).Append('\n');
            }
            if (finding.Violations.Count > MaxReportedPerFile)
            {
                sb.Append("    - …ほか ").Append(finding.Violations.Count - MaxReportedPerFile).Append(" 件\n");
            }
        }
        if (findings.Count > MaxReportedFiles)
        {
            sb.Append("- …ほか ").Append(findings.Count - MaxReportedFiles).Append(" ファイル\n");
        }
        sb.Append("\nルールに沿うよう修正してください（ファイルの分割・整理、doc コメントの追記）。");
        return sb.ToString();
    }

    /// <summary>規則に対する違反リストを構築する。</summary>
    private static List<string> Evaluate(FileRule rule, StructureInfo info, int fileLines, string languageId)
    {
        var violations = new List<string>();

        if (rule.MaxFileLines is { } maxLines && fileLines > maxLines)
        {
            violations.Add($"ファイル行数が上限を超過（{fileLines} 行 > 上限 {maxLines} 行）");
        }

        // class 検査はクラス概念のある言語のみ（無い言語では自動スキップ）。
        if (info.HasClassConcept)
        {
            if (rule.ClassOneFile == true && info.TopLevelClassCount > 1)
            {
                violations.Add($"1 ファイル 1 クラス違反（トップレベルのクラス相当が {info.TopLevelClassCount} 個。1 個にする）");
            }
            if (rule.ClassForce == true && info.TopLevelClassCount < 1)
            {
                violations.Add("クラスが必要（このファイルにトップレベルのクラスがない。メソッドのみのファイルは禁止）");
            }
        }

        if (rule.MethodNum is { } maxNum && info.Methods.Count > maxNum)
        {
            violations.Add($"メソッド数が上限を超過（{info.Methods.Count} 個 > 上限 {maxNum} 個）");
        }

        // method.in-class: メソッド・操作は必ずクラス内（クラス概念のある言語のみ）。
        if (rule.MethodInClass == true && info.HasClassConcept)
        {
            foreach (var m in info.OutsideClassMethods.Take(MaxReportedMethods))
            {
                violations.Add($"クラス外メソッド '{m.Name}'（{m.StartLine} 行目）は禁止（クラス内に定義する）");
            }
            if (info.OutsideClassMethods.Count > MaxReportedMethods)
            {
                violations.Add($"…ほか {info.OutsideClassMethods.Count - MaxReportedMethods} 個のクラス外メソッド");
            }
            foreach (var op in info.OutsideClassOperations.Take(MaxReportedMethods))
            {
                violations.Add($"クラス外の操作 '{op.Kind}'（{op.StartLine} 行目）は禁止（クラス内に置く）");
            }
            if (info.OutsideClassOperations.Count > MaxReportedMethods)
            {
                violations.Add($"…ほか {info.OutsideClassOperations.Count - MaxReportedMethods} 個のクラス外操作");
            }
        }

        if (rule.MethodMaxLines is { } maxMethodLines)
        {
            var over = info.Methods.Where(m => m.Lines > maxMethodLines).ToList();
            foreach (var m in over.Take(MaxReportedMethods))
            {
                violations.Add($"メソッド '{m.Name}'（{m.StartLine} 行目）が {m.Lines} 行で上限 {maxMethodLines} 行を超過");
            }
            if (over.Count > MaxReportedMethods)
            {
                violations.Add($"…ほか {over.Count - MaxReportedMethods} 個のメソッドが行数超過");
            }
        }

        violations.AddRange(CommentChecker.Evaluate(rule.Comment, info.Declarations, languageId));

        return violations;
    }

    /// <summary>ソースの行数を数える（末尾改行は 1 行として数えない）。</summary>
    private static int CountLines(string source)
    {
        if (source.Length == 0)
        {
            return 0;
        }
        var lines = 1;
        foreach (var c in source)
        {
            if (c == '\n')
            {
                lines++;
            }
        }
        if (source.EndsWith('\n'))
        {
            lines--;
        }
        return lines;
    }

    private static string BuildConfigErrorReason(IReadOnlyList<string> errors)
    {
        var sb = new StringBuilder();
        sb.Append("ai-harness-file-rules の設定が不正なため、ソースファイルの書き込みをブロックしました（フェイルクローズ）。\n");
        sb.Append("ai-harness-file-rules.yml を修正してください:\n- ");
        sb.Append(string.Join("\n- ", errors));
        return sb.ToString();
    }

    private static string BuildViolationReason(string filePath, IReadOnlyList<string> violations)
    {
        var sb = new StringBuilder();
        sb.Append("コード構造ルールに違反しています: '").Append(filePath).Append("'\n\n");
        sb.Append("違反内容:\n- ");
        sb.Append(string.Join("\n- ", violations));
        sb.Append("\n\nルールに沿うよう修正してください（ファイルの分割・整理、doc コメントの追記）。");
        return sb.ToString();
    }

    private static string? ExtractFilePath(HookData data) =>
        AsString(GetMember(data.ToolInput, "file_path")) ?? data.FilePath;

    private static JsonNode? GetMember(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var v) ? v : null;

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
