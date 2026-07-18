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
///   line-length      … 1 行の文字数の上限（タブは tab-width 文字として数える）
///   blank-line       … 空行なしで続けられる行数の上限（空行・コメント行が区切り）
///   class.one-file   … トップレベルのクラス相当は 1 個まで
///   class.force      … クラスが必ず要る（メソッドのみのファイル禁止）
///   class.extend     … 指定ファイルで宣言されたクラスを必ず継承（extends。単一パス、配列不可）
///   method.num       … ファイル内メソッド数の上限
///   method.lines     … 1 メソッドの行数の上限
///   method.in-class  … メソッド・操作は必ずクラス内（クラス外メソッド／クラス外操作を禁止）
///   comment.class    … クラスの doc コメント（有無・説明の最低文字数）
///   comment.method   … メソッドの doc コメント（有無・説明の最低文字数・引数の記述とシグネチャの整合）
///   unused           … 未使用 import／変数／関数／クラスの検出（<see cref="UnusedSymbolChecker"/>）。
///                       自前のスコープ解析はせず LSP 診断をそのまま使う。<see cref="Action"/>（hook）では
///                       <see cref="HookData.LspDiagnostics"/>（非同期キャッシュ読み取り・ブロックしない）、
///                       <see cref="Fire"/>（能動スキャン）では <see cref="PluginBase.FireLsp"/>
///                       （ファイルごとに同期リクエストして応答を待つ）と、経路も待ち方も異なる。
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
        "AST 解析で、ファイル単位のコード構造ルール（行数・文字数・1クラス1ファイル・メソッド）を強制する";

    public override IReadOnlyList<string> Events => new[] { "PostToolUse" };

    public override string ConfigName => "ai-harness-file-rules.yml";

    /// <summary>埋め込み rule（<c>file-rules.rule.md</c>）を各プロジェクトの <c>.claude/rules</c> へ配布する。</summary>
    public override bool ProvidesRule => true;

    private static readonly HashSet<string> TargetTools =
        new(StringComparer.Ordinal) { "Write", "Edit", "MultiEdit" };

    /// <summary>reason に列挙する超過メソッドの最大件数。</summary>
    private const int MaxReportedMethods = 20;

    /// <summary>
    /// Fire で <c>unused: true</c> のファイルごとに LSP 診断を待つ上限。対象言語の LSP がまだ未起動なら、
    /// その言語の最初の1ファイルではインストール・起動完了までの待ちもこの中に含まれる
    /// （<see cref="LspManager.RequestDiagnosticsSync"/> 参照）。2 ファイル目以降は既に起動済みのため、
    /// 実際にはこの上限よりずっと短い時間で返ることが多い。
    /// </summary>
    private static readonly TimeSpan UnusedRequestTimeout = TimeSpan.FromSeconds(60);

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

        // class.extend が設定されていれば、継承先クラス名を先に解決しておく（対象ファイルの解析とは独立）。
        IReadOnlyList<string>? extendTargets = null;
        string? extendError = null;
        if (!string.IsNullOrEmpty(matched.Value.ClassExtend))
        {
            // 解決の基準となるプロジェクトルート。cwd が無ければ環境要因のため検査せず許可する
            // （ai-harness-import-rules のモジュール解決と同じ扱い）。
            var repoRoot = data.Cwd;
            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                yield return LogEntry.Warning($"cwd 不明のため class.extend 検証不可・スキップ: {filePath}");
                yield break;
            }
            (extendTargets, extendError) = ClassExtendResolver.Resolve(matched.Value.ClassExtend, Path.GetFullPath(repoRoot));
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
        var fileDiagnostics = LookupDiagnostics(data.LspDiagnostics, filePath);
        if (rule.Unused == true && fileDiagnostics is null)
        {
            // deny はしない（LSP の不調・未設定は hook をブロックしない方針）が、unused が実質何も検査していない
            // ことに気づけるようログだけは残す。原因は複数あり得るため特定はしない
            // （未対応言語／common.yml の lsp: 未設定／LSP起動前／このファイル未同期のいずれか）。
            yield return LogEntry.Warning(
                $"unused: LSP 診断が見つからないため検査されていません（未対応言語／lsp: 未設定／起動前／未同期のいずれか）: {filePath}");
        }
        var violations = Evaluate(rule, info, source!, languageId, extendTargets, extendError, fileDiagnostics);

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

        var usesUnused = config.Entries.Any(e => e.Unused == true);
        if (usesUnused && FireLsp is null)
        {
            // 通常は host が必ず注入するので起きないはずだが、万一の防御的メッセージ。
            yield return LogEntry.Warning(
                "unused: true が設定されていますが LSP リクエスタが利用できないため評価しません。");
        }
        else if (usesUnused)
        {
            // Fire はファイルごとに同期待ち（RequestDiagnostics）でブロックするため、対象言語の LSP が
            // 未起動だとその言語の最初の1ファイルでインストール・起動完了まで待つ（数秒〜数十秒かかることがある）。
            yield return LogEntry.Info(
                "unused: true を含むため、対象言語の LSP 診断が届くまで待ちながら走査します（初回は時間がかかることがあります）。");
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

        // class.extend の解決結果はエントリの規則が同じなら使い回す（同じ継承元を何度も解析しない）。
        var extendCache = new Dictionary<string, (IReadOnlyList<string> Names, string? Error)>(StringComparer.Ordinal);

        var findings = new List<FileFinding>();
        foreach (var target in targets)
        {
            IReadOnlyList<string>? extendTargets = null;
            string? extendError = null;
            if (!string.IsNullOrEmpty(target.Rule.ClassExtend))
            {
                if (!extendCache.TryGetValue(target.Rule.ClassExtend, out var resolved))
                {
                    resolved = ClassExtendResolver.Resolve(target.Rule.ClassExtend, projectRoot);
                    extendCache[target.Rule.ClassExtend] = resolved;
                }
                extendTargets = resolved.Names;
                extendError = resolved.Error;
            }

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

            // unused は Fire 専用の同期リクエスト（FireLsp）で都度取得する。届くまでブロックする
            // （Action の HookData.LspDiagnostics のような非同期キャッシュ読みではない。Fire は同期バッチのため許容）。
            IReadOnlyList<LspDiagnostic>? fileDiagnostics = null;
            if (target.Rule.Unused == true && FireLsp is not null)
            {
                fileDiagnostics = FireLsp.RequestDiagnostics(target.Path, source!, UnusedRequestTimeout);
            }

            var violations = Evaluate(
                target.Rule, info, source!, target.LanguageId, extendTargets, extendError, fileDiagnostics);
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

    /// <summary>
    /// 規則に対する違反リストを構築する。<paramref name="extendTargets"/> / <paramref name="extendError"/> は
    /// <c>class.extend</c> の解決結果（呼び出し側が先に <see cref="ClassExtendResolver.Resolve"/> 済み）。
    /// <paramref name="fileDiagnostics"/> は対象ファイルの LSP 診断（hook 駆動のときのみ非 null。Fire は null）。
    /// </summary>
    private static List<string> Evaluate(
        FileRule rule, StructureInfo info, string source, string languageId,
        IReadOnlyList<string>? extendTargets, string? extendError,
        IReadOnlyList<LspDiagnostic>? fileDiagnostics)
    {
        var violations = new List<string>();

        if (rule.Unused == true)
        {
            violations.AddRange(UnusedSymbolChecker.Evaluate(fileDiagnostics));
        }

        var fileLines = CountLines(source);
        if (rule.MaxFileLines is { } maxLines && fileLines > maxLines)
        {
            violations.Add($"ファイル行数が上限を超過（{fileLines} 行 > 上限 {maxLines} 行）");
        }

        if (rule.MaxLineLength is { } maxLineLength)
        {
            violations.AddRange(LineLengthChecker.Evaluate(source, maxLineLength, rule.TabWidth));
        }

        if (rule.MaxConsecutiveLines is { } maxRun)
        {
            violations.AddRange(BlankLineChecker.Evaluate(source, maxRun, info.CommentLines));
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
            if (rule.ClassExtend is { } extendPath)
            {
                if (extendError is not null)
                {
                    violations.Add($"class.extend を解決できません（{extendPath}）: {extendError}");
                }
                else if (extendTargets is { Count: > 0 })
                {
                    foreach (var cls in info.TopLevelClasses)
                    {
                        if (!cls.BaseNames.Any(b => extendTargets.Contains(b, StringComparer.Ordinal)))
                        {
                            violations.Add(
                                $"クラス '{cls.Name}'（{cls.StartLine} 行目）が {extendPath} のクラス" +
                                $"（{string.Join("/", extendTargets)}）を継承していません");
                        }
                    }
                }
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

    /// <summary>
    /// <paramref name="filePath"/> に対応する LSP 診断を <paramref name="diagnostics"/> から引く。
    /// キーは main 側で <c>Uri.LocalPath</c> 経由に正規化されているため、まず素の一致を試し、
    /// 無ければ <see cref="Path.GetFullPath(string)"/> で正規化して再度引く。
    /// </summary>
    private static IReadOnlyList<LspDiagnostic>? LookupDiagnostics(
        IReadOnlyDictionary<string, IReadOnlyList<LspDiagnostic>>? diagnostics, string filePath)
    {
        if (diagnostics is null)
        {
            return null;
        }
        if (diagnostics.TryGetValue(filePath, out var direct))
        {
            return direct;
        }
        return diagnostics.TryGetValue(Path.GetFullPath(filePath), out var normalized) ? normalized : null;
    }

    private static JsonNode? GetMember(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var v) ? v : null;

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
