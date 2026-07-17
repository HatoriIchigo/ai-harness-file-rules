namespace ai_harness_file_rules;

/// <summary>
/// <c>class.extend</c> に指定されたファイルパス（プロジェクトルート相対、または絶対パス）を解決し、
/// そのファイルで宣言されているトップレベルのクラス名一覧を返す。名前は AST 解析で実測する
/// （ファイル名とクラス名が一致する慣習には依存しない）。
/// </summary>
public static class ClassExtendResolver
{
    /// <summary>
    /// <paramref name="extendPath"/> を解決し、宣言されているトップレベルのクラス名一覧を返す。
    /// 解決できない場合（ファイル不在・未対応言語・読み取り不可・解析失敗・クラス宣言無し）は
    /// 空リストとエラーメッセージを返す。
    /// </summary>
    /// <param name="extendPath">class.extend の値（プロジェクトルート相対、または絶対パス）。</param>
    /// <param name="repoRootAbs">プロジェクトルートの絶対パス。</param>
    public static (IReadOnlyList<string> Names, string? Error) Resolve(string extendPath, string repoRootAbs)
    {
        var abs = Path.GetFullPath(Path.Combine(repoRootAbs, extendPath));
        if (!File.Exists(abs))
        {
            return (Array.Empty<string>(), $"ファイルが見つかりません: {extendPath}");
        }
        if (!StructureAnalyzer.TryGetLanguageId(abs, out var lang))
        {
            return (Array.Empty<string>(), $"未対応言語のファイルです: {extendPath}");
        }

        string source;
        try
        {
            source = File.ReadAllText(abs);
        }
        catch (Exception e)
        {
            return (Array.Empty<string>(), $"ファイルを読めません: {extendPath} ({e.GetType().Name})");
        }

        StructureInfo info;
        try
        {
            info = StructureAnalyzer.Analyze(lang, source);
        }
        catch (Exception e)
        {
            return (Array.Empty<string>(), $"AST 解析に失敗: {extendPath} ({e.GetType().Name}: {e.Message})");
        }

        var names = info.TopLevelClasses
            .Select(c => c.Name)
            .Where(n => n != "?")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            return (Array.Empty<string>(), $"クラス宣言が見つかりません: {extendPath}");
        }
        return (names, null);
    }
}
