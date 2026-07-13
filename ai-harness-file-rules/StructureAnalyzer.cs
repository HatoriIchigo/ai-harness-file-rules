using TreeSitter;

namespace ai_harness_file_rules;

/// <summary>メソッド/関数 1 件の情報（名前・開始行・行数）。</summary>
public sealed record MethodInfo(string Name, int StartLine, int Lines);

/// <summary>クラス外に置かれた操作（if/for/式文など）1 件の情報（種別・開始行）。</summary>
public sealed record OutsideClassOperation(string Kind, int StartLine);

/// <summary>ソース 1 ファイルを AST 解析した構造情報。</summary>
public sealed class StructureInfo
{
    /// <summary>この言語にクラスの概念があるか（無ければ class 検査はスキップ）。</summary>
    public bool HasClassConcept { get; init; }

    /// <summary>トップレベルのクラス相当（class/interface/enum/record/struct 等）の数。</summary>
    public int TopLevelClassCount { get; init; }

    /// <summary>ファイル内のメソッド/関数定義（クロージャ等の入れ子定義は除く）。</summary>
    public IReadOnlyList<MethodInfo> Methods { get; init; } = Array.Empty<MethodInfo>();

    /// <summary>どのクラスにも属さない（トップレベル／名前空間直下等の）メソッド/関数定義。</summary>
    public IReadOnlyList<MethodInfo> OutsideClassMethods { get; init; } = Array.Empty<MethodInfo>();

    /// <summary>どのクラス・メソッドにも属さないトップレベルの操作文（if/for/while/式文 等）。</summary>
    public IReadOnlyList<OutsideClassOperation> OutsideClassOperations { get; init; } = Array.Empty<OutsideClassOperation>();

    /// <summary>コメント検査の対象となる宣言（クラス・メソッド）と、それぞれの引数・doc コメント。</summary>
    public IReadOnlyList<DeclarationInfo> Declarations { get; init; } = Array.Empty<DeclarationInfo>();
}

/// <summary>
/// tree-sitter（TreeSitter.DotNet）で対象言語のソースを解析し、クラス・メソッド/関数の構造を取り出す。
/// 各ノード型は 6 言語で実測して定義。クラス概念の無い C / Go / Rust は <see cref="StructureInfo.HasClassConcept"/>=false。
/// </summary>
public static class StructureAnalyzer
{
    private static readonly Dictionary<string, string> ExtToLang = new(StringComparer.OrdinalIgnoreCase)
    {
        [".c"] = "c", [".h"] = "c",
        [".cpp"] = "cpp", [".cc"] = "cpp", [".cxx"] = "cpp", [".c++"] = "cpp",
        [".hpp"] = "cpp", [".hh"] = "cpp", [".hxx"] = "cpp",
        [".java"] = "java",
        [".py"] = "python", [".pyi"] = "python",
        [".rs"] = "rust",
        [".go"] = "go",
        [".ts"] = "typescript", [".mts"] = "typescript", [".cts"] = "typescript",
        [".tsx"] = "tsx",
    };

    /// <summary>クラス概念を持つ言語。ここに無い言語では class 検査をスキップする。</summary>
    private static readonly HashSet<string> HasClass = new(StringComparer.Ordinal)
    {
        "java", "python", "typescript", "tsx", "cpp",
    };

    /// <summary>言語 id → トップレベルのクラス相当ノード型。</summary>
    private static readonly Dictionary<string, HashSet<string>> ClassTypes = new(StringComparer.Ordinal)
    {
        ["java"] = new(StringComparer.Ordinal)
            { "class_declaration", "interface_declaration", "enum_declaration", "record_declaration" },
        ["python"] = new(StringComparer.Ordinal) { "class_definition" },
        ["typescript"] = new(StringComparer.Ordinal) { "class_declaration", "abstract_class_declaration" },
        ["tsx"] = new(StringComparer.Ordinal) { "class_declaration", "abstract_class_declaration" },
        ["cpp"] = new(StringComparer.Ordinal) { "class_specifier", "struct_specifier" },
    };

    /// <summary>言語 id → メソッド/関数定義のノード型。</summary>
    private static readonly Dictionary<string, HashSet<string>> MethodTypes = new(StringComparer.Ordinal)
    {
        ["c"] = new(StringComparer.Ordinal) { "function_definition" },
        ["cpp"] = new(StringComparer.Ordinal) { "function_definition" },
        ["java"] = new(StringComparer.Ordinal) { "method_declaration", "constructor_declaration" },
        ["python"] = new(StringComparer.Ordinal) { "function_definition" },
        ["rust"] = new(StringComparer.Ordinal) { "function_item" },
        ["go"] = new(StringComparer.Ordinal) { "function_declaration", "method_declaration" },
        ["typescript"] = new(StringComparer.Ordinal)
            { "function_declaration", "generator_function_declaration", "method_definition" },
        ["tsx"] = new(StringComparer.Ordinal)
            { "function_declaration", "generator_function_declaration", "method_definition" },
    };

    /// <summary>名前として採用するノード型（メソッド/関数名の抽出用）。</summary>
    private static readonly HashSet<string> NameTypes = new(StringComparer.Ordinal)
        { "identifier", "field_identifier", "property_identifier", "type_identifier" };

    /// <summary>
    /// 型名に <c>statement</c> を含むが「操作」とは見なさないノード型（import 等の宣言系）。
    /// これら以外で <c>statement</c> を含むトップレベル文を「クラス外操作」として扱う。
    /// </summary>
    private static readonly HashSet<string> NonOperationStatements = new(StringComparer.Ordinal)
        { "import_statement", "import_from_statement", "future_import_statement", "export_statement" };

    public static bool TryGetLanguageId(string filePath, out string languageId)
    {
        var ext = Path.GetExtension(filePath);
        return ExtToLang.TryGetValue(ext, out languageId!);
    }

    public static bool IsSupported(string filePath) => TryGetLanguageId(filePath, out _);

    /// <summary>ソースを解析し構造情報を返す。未対応言語なら空の情報。</summary>
    public static StructureInfo Analyze(string languageId, string source)
    {
        if (!MethodTypes.TryGetValue(languageId, out var methodTypes))
        {
            return new StructureInfo();
        }
        var hasClass = HasClass.Contains(languageId);
        ClassTypes.TryGetValue(languageId, out var classTypes);

        using var language = new Language(languageId);
        using var parser = new Parser(language);
        using var tree = parser.Parse(source);
        if (tree is null)
        {
            return new StructureInfo { HasClassConcept = hasClass };
        }
        var root = tree.RootNode;

        var classCount = 0;
        if (classTypes is not null)
        {
            foreach (var child in root.NamedChildren)
            {
                if (child.Type == "export_statement")
                {
                    // TypeScript: export (default) class ... を剥がして数える。
                    foreach (var inner in child.NamedChildren)
                    {
                        if (classTypes.Contains(inner.Type))
                        {
                            classCount++;
                        }
                    }
                }
                else if (classTypes.Contains(child.Type))
                {
                    classCount++;
                }
            }
        }

        var methods = new List<MethodInfo>();
        CollectMethods(root, languageId, methodTypes, methods);

        var outsideMethods = new List<MethodInfo>();
        var outsideOps = new List<OutsideClassOperation>();
        CollectOutsideClass(root, languageId, methodTypes, classTypes, outsideMethods, outsideOps);

        var declarations = DeclarationCollector.Collect(root, languageId, classTypes, methodTypes);

        return new StructureInfo
        {
            HasClassConcept = hasClass,
            TopLevelClassCount = classCount,
            Methods = methods,
            OutsideClassMethods = outsideMethods,
            OutsideClassOperations = outsideOps,
            Declarations = declarations,
        };
    }

    /// <summary>メソッド/関数定義を収集。マッチしたノードの配下へは降りない（クロージャ等を数えない）。</summary>
    private static void CollectMethods(Node node, string lang, HashSet<string> methodTypes, List<MethodInfo> acc)
    {
        if (IsMethodNode(node, lang, methodTypes))
        {
            acc.Add(ToMethodInfo(node));
            return; // メソッド配下の入れ子定義（クロージャ・ローカル関数）は数えない。
        }

        foreach (var child in node.NamedChildren)
        {
            CollectMethods(child, lang, methodTypes, acc);
        }
    }

    /// <summary>
    /// クラス外（トップレベル・名前空間直下等）に置かれたメソッド/関数定義と操作文を収集する。
    /// クラス相当ノードで打ち切り（内部はクラス内なので対象外）、メソッド／操作文はそこで確定し配下へは降りない。
    /// それ以外のコンテナ（名前空間・宣言・export ラッパ等）は配下へ降りて探索する。
    /// </summary>
    private static void CollectOutsideClass(Node node, string lang, HashSet<string> methodTypes,
        HashSet<string>? classTypes, List<MethodInfo> methods, List<OutsideClassOperation> operations)
    {
        foreach (var child in node.NamedChildren)
        {
            var type = child.Type;

            // TypeScript: export (default) ... を剥がして中身をトップレベルとして分類する。
            if (type == "export_statement")
            {
                CollectOutsideClass(child, lang, methodTypes, classTypes, methods, operations);
                continue;
            }
            // クラス内は対象外（配下へ降りない）。
            if (classTypes is not null && classTypes.Contains(type))
            {
                continue;
            }
            if (IsMethodNode(child, lang, methodTypes))
            {
                methods.Add(ToMethodInfo(child));
                continue;
            }
            if (IsOperationStatement(type))
            {
                operations.Add(new OutsideClassOperation(FriendlyStatementKind(type), child.StartPosition.Row + 1));
                continue;
            }
            // 名前空間・宣言などのコンテナは中を探索（内部のクラス外メソッド/操作を拾う）。
            CollectOutsideClass(child, lang, methodTypes, classTypes, methods, operations);
        }
    }

    /// <summary>ノードがメソッド/関数定義に相当するか。</summary>
    private static bool IsMethodNode(Node node, string lang, HashSet<string> methodTypes)
    {
        var type = node.Type;
        if (methodTypes.Contains(type))
        {
            return true;
        }
        // TypeScript: 変数/フィールドに代入された名前付き arrow / function 式もメソッドとして数える。
        if ((lang == "typescript" || lang == "tsx")
            && (type == "arrow_function" || type == "function_expression"))
        {
            var parentType = node.Parent?.Type;
            if (parentType is "variable_declarator" or "public_field_definition")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>型名が操作文（if/for/while/式文 等）か。import 等の宣言系は除外する。</summary>
    private static bool IsOperationStatement(string type) =>
        type.Contains("statement") && !NonOperationStatements.Contains(type);

    /// <summary>操作文の型名を読みやすい種別へ（例: <c>if_statement</c> → <c>if</c>）。</summary>
    private static string FriendlyStatementKind(string type)
    {
        var kind = type.EndsWith("_statement", StringComparison.Ordinal)
            ? type[..^"_statement".Length]
            : type;
        return kind.Replace('_', ' ');
    }

    private static MethodInfo ToMethodInfo(Node node)
    {
        var lines = node.EndPosition.Row - node.StartPosition.Row + 1;
        return new MethodInfo(GetName(node), node.StartPosition.Row + 1, lines);
    }

    /// <summary>メソッド/関数・クラス名を最善努力で抽出（本体には降りない。見つからなければ "?"）。</summary>
    internal static string GetName(Node node, int depth = 0)
    {
        if (depth > 3)
        {
            return "?";
        }
        foreach (var child in node.NamedChildren)
        {
            var t = child.Type;
            if (NameTypes.Contains(t))
            {
                return child.Text ?? "?";
            }
            // 本体・引数リストへは降りない（内部の識別子を拾わないため）。
            if (t.Contains("block") || t.Contains("body") || t.Contains("statement")
                || t is "declaration_list" or "parameters" or "parameter_list" or "formal_parameters")
            {
                continue;
            }
            var nested = GetName(child, depth + 1);
            if (nested != "?")
            {
                return nested;
            }
        }
        return "?";
    }
}
