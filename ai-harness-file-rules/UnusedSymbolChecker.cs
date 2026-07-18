using ai_harness_baselib;

namespace ai_harness_file_rules;

/// <summary>
/// LSP の <c>publishDiagnostics</c> 由来の診断一覧から、未使用 import／変数／関数／クラスを示すものを
/// 拾って違反として挙げる。呼び出し元（<see cref="FileRulesPlugin"/>）が hook（<see cref="HookData.LspDiagnostics"/>
/// のキャッシュ読み取り）と Fire（<see cref="PluginBase.FireLsp"/> の同期リクエスト）のどちらから集めた
/// リストでも同じロジックで判定する（このクラス自体は経路を意識しない）。
///
/// tree-sitter による自前のスコープ解析はしない（シャドーイング・クロージャ捕捉・条件付き import 等の
/// 正確な判定は LSP サーバに委ねる）。診断の発生元はサーバごとに異なる code／メッセージ文言のため、
/// 既知の code（実測で確認済み: pyright の <c>reportUnusedImport</c>／<c>reportUnusedVariable</c>、
/// rust-analyzer の <c>unused_imports</c>／<c>unused_variables</c>／<c>dead_code</c>）に加え、
/// メッセージに "unused" 等の語が含まれるかの緩い判定でフォールバックする（未検証の言語・サーバでも拾える
/// ようにするため。誤検出の可能性はあるが、検出漏れよりは違反として提示される方を優先する）。
///
/// <c>severity</c> が <c>hint</c>／<c>information</c> の診断（多くは「アンダースコアを付けろ」等の
/// 付随的な示唆）は対象外とし、<c>error</c>／<c>warning</c> のみを違反として扱う。
/// </summary>
public static class UnusedSymbolChecker
{
    /// <summary>実測・既知の「未使用」系診断コード（サーバごとの命名規則がまちまちなため文字列比較）。</summary>
    private static readonly HashSet<string> KnownCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // pyright（実測。既定では無効なため ai-harness-main 側で有効化した上での挙動）。
        "reportUnusedImport", "reportUnusedVariable", "reportUnusedClass", "reportUnusedFunction",
        // rust-analyzer（実測。Cargo プロジェクトなら既定で有効）。
        "unused_imports", "unused_variables", "dead_code",
        // TypeScript（tsconfig の noUnusedLocals/noUnusedParameters 有効時のみ発生。未実測）。
        "6133", "6192", "6196", "6198",
        // C#（Roslyn の既定コンパイラ警告。未実測）。
        "CS0168", "CS0219", "IDE0005",
    };

    /// <summary>メッセージ文言のフォールバック判定に使う語（英語のみ。LSP サーバの診断メッセージは基本英語）。</summary>
    private static readonly string[] UnusedPhrases =
    [
        "unused", "is not accessed", "never used", "never read", "not used",
    ];

    /// <summary><paramref name="diagnostics"/>（対象ファイルの LSP 診断）から未使用系のものだけを違反文へ変換する。</summary>
    public static List<string> Evaluate(IReadOnlyList<LspDiagnostic>? diagnostics)
    {
        var violations = new List<string>();
        if (diagnostics is null)
        {
            return violations;
        }

        foreach (var d in diagnostics)
        {
            if (!IsUnused(d))
            {
                continue;
            }
            var location = $"{d.StartLine + 1}行目";
            var source = string.IsNullOrEmpty(d.Source) ? "" : $"{d.Source}, ";
            violations.Add($"未使用の可能性（LSP診断, {source}{location}）: {FirstLine(d.Message)}");
        }
        return violations;
    }

    private static bool IsUnused(LspDiagnostic d)
    {
        if (d.Severity is not ("error" or "warning"))
        {
            return false; // hint/information は付随的な示唆に留め、違反としては数えない。
        }
        if (d.Code is { } code && KnownCodes.Contains(code.Trim('"')))
        {
            return true;
        }
        foreach (var phrase in UnusedPhrases)
        {
            if (d.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>複数行メッセージ（rust-analyzer の lint 注釈等）の 1 行目だけを reason に出す。</summary>
    private static string FirstLine(string message)
    {
        var idx = message.IndexOf('\n');
        return idx < 0 ? message : message[..idx];
    }
}
