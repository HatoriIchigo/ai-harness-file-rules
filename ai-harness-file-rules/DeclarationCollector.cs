using TreeSitter;

namespace ai_harness_file_rules;

/// <summary>
/// AST からコメント検査用の宣言（クラス・メソッド）を集める。
///
/// 各言語で実測した doc コメントの持ち方に合わせて抽出する:
///   Java        … <c>block_comment</c> / <c>line_comment</c> を前置（<c>/** */</c>）
///   C / C++     … <c>comment</c> を前置
///   TypeScript  … <c>comment</c> を前置。宣言が <c>export_statement</c> に包まれる場合はそこから遡る
///   Rust        … <c>line_comment</c>（<c>///</c>）を前置。1 行ごとに別ノードなので連続分を結合する
///   Go          … <c>comment</c>（<c>//</c>）を前置。Rust 同様 1 行ごとに別ノード
///   Python      … 前置コメントではなく本体先頭の docstring（<c>string</c> ノード）
/// </summary>
public static class DeclarationCollector
{
    /// <summary>言語 id → コメントのノード型。ここに無い言語は前置コメントを持たない扱い。</summary>
    private static readonly Dictionary<string, HashSet<string>> CommentTypes = new(StringComparer.Ordinal)
    {
        ["c"] = new(StringComparer.Ordinal) { "comment" },
        ["cpp"] = new(StringComparer.Ordinal) { "comment" },
        ["java"] = new(StringComparer.Ordinal) { "block_comment", "line_comment" },
        ["rust"] = new(StringComparer.Ordinal) { "line_comment", "block_comment" },
        ["go"] = new(StringComparer.Ordinal) { "comment" },
        ["typescript"] = new(StringComparer.Ordinal) { "comment" },
        ["tsx"] = new(StringComparer.Ordinal) { "comment" },
    };

    /// <summary>
    /// 言語 id → doc コメントの開始マーカー。ここに挙げた形だけを doc とみなし、実装コメント
    /// （Java の <c>//</c>、Rust の <c>//</c> 等）は doc として認めない。
    /// Go は godoc が通常の <c>//</c> を doc とするため区別しない。
    /// </summary>
    private static readonly Dictionary<string, string[]> DocMarkers = new(StringComparer.Ordinal)
    {
        ["c"] = new[] { "/**", "///", "/*!" },
        ["cpp"] = new[] { "/**", "///", "/*!" },
        ["java"] = new[] { "/**" },
        ["typescript"] = new[] { "/**" },
        ["tsx"] = new[] { "/**" },
        ["rust"] = new[] { "///", "/**" },
        ["go"] = new[] { "//" },
    };

    /// <summary>Python の引数のうち、記述を求めない暗黙のもの。</summary>
    private static readonly HashSet<string> PythonImplicitParams =
        new(StringComparer.Ordinal) { "self", "cls" };

    /// <summary>
    /// <paramref name="root"/> 配下の宣言を集める。クラス配下は探索してメソッドも拾うが、
    /// メソッド配下へは降りない（クロージャ・ローカル関数は検査対象外）。
    /// </summary>
    public static List<DeclarationInfo> Collect(
        Node root, string lang, HashSet<string>? classTypes, HashSet<string> methodTypes)
    {
        var acc = new List<DeclarationInfo>();
        Walk(root, lang, classTypes, methodTypes, acc);
        return acc;
    }

    private static void Walk(
        Node node, string lang, HashSet<string>? classTypes, HashSet<string> methodTypes,
        List<DeclarationInfo> acc)
    {
        foreach (var child in node.NamedChildren)
        {
            if (classTypes is not null && classTypes.Contains(child.Type))
            {
                acc.Add(new DeclarationInfo(
                    DeclKind.Class, StructureAnalyzer.GetName(child), child.StartPosition.Row + 1,
                    Array.Empty<string>(), ExtractDoc(child, lang)));
                // クラス配下のメソッドも検査対象なので降りる。
                Walk(child, lang, classTypes, methodTypes, acc);
                continue;
            }
            if (methodTypes.Contains(child.Type))
            {
                acc.Add(new DeclarationInfo(
                    DeclKind.Method, StructureAnalyzer.GetName(child), child.StartPosition.Row + 1,
                    ExtractParams(child, lang), ExtractDoc(child, lang)));
                continue; // メソッド配下の入れ子定義は対象外。
            }
            Walk(child, lang, classTypes, methodTypes, acc);
        }
    }

    // ---- doc コメントの抽出 ----

    /// <summary>宣言に紐づく doc コメントを取り出す。無ければ null。</summary>
    private static string? ExtractDoc(Node decl, string lang) =>
        lang == "python" ? ExtractDocstring(decl) : ExtractLeadingComment(decl, lang);

    /// <summary>
    /// Python の docstring。本体ブロックの先頭が文字列リテラルならそれを doc とみなす。
    /// </summary>
    private static string? ExtractDocstring(Node decl)
    {
        var body = decl.GetChildForField("body");
        var first = body?.NamedChildren.FirstOrDefault();
        if (first is null)
        {
            return null;
        }
        // 先頭が式文に包まれる文法もあるため 1 段だけ剥がす。
        if (first.Type == "expression_statement")
        {
            first = first.NamedChildren.FirstOrDefault();
        }
        return first is not null && first.Type == "string" ? first.Text : null;
    }

    /// <summary>
    /// 宣言の直前に置かれたコメントを取り出す。Rust / Go のように 1 行ずつ別ノードになる場合は、
    /// 行が連続している限り上へ遡って結合する。宣言と 1 行以上空いたコメントは紐づけない。
    /// </summary>
    private static string? ExtractLeadingComment(Node decl, string lang)
    {
        if (!CommentTypes.TryGetValue(lang, out var commentTypes))
        {
            return null;
        }

        // TypeScript の `export class ...` はコメントが export_statement の前に付くため、そこから遡る。
        var anchor = decl;
        while (anchor.Parent is { Type: "export_statement" } parent)
        {
            anchor = parent;
        }

        var lines = new List<string>();
        var expectedEndRow = anchor.StartPosition.Row - 1;
        for (var prev = anchor.PreviousNamedSibling; prev is not null; prev = prev.PreviousNamedSibling)
        {
            if (!commentTypes.Contains(prev.Type) || EndRow(prev) != expectedEndRow)
            {
                break; // コメント以外、または行が離れたら打ち切り。
            }
            var text = prev.Text;
            if (text is null || !IsDocComment(text, lang))
            {
                break; // 実装コメント（doc マーカーが無い）は doc として認めない。
            }
            lines.Insert(0, text);
            expectedEndRow = prev.StartPosition.Row - 1;
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>
    /// ノードが終わる行。Rust の <c>///</c> のように末尾の改行までをノードに含む文法では、
    /// 終端が次行の先頭（列 0）になるため 1 行戻す。
    /// </summary>
    internal static int EndRow(Node node)
    {
        var end = node.EndPosition;
        return end.Column == 0 ? end.Row - 1 : end.Row;
    }

    /// <summary>コメントが doc の形式（javadoc / rustdoc / TSDoc / Doxygen 等）か。</summary>
    private static bool IsDocComment(string text, string lang)
    {
        if (!DocMarkers.TryGetValue(lang, out var markers))
        {
            return false;
        }
        var trimmed = text.TrimStart();
        foreach (var marker in markers)
        {
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // ---- 引数名の抽出 ----

    /// <summary>
    /// シグネチャ上の引数名を取り出す。レシーバ・<c>self</c>・分割代入など名前を特定できないものは含めない
    /// （記述を強制できないため）。
    /// </summary>
    private static IReadOnlyList<string> ExtractParams(Node method, string lang)
    {
        var list = ParamListNode(method, lang);
        if (list is null)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (var param in list.NamedChildren)
        {
            switch (lang)
            {
                case "python":
                    AddPythonParam(param, names);
                    break;
                case "go":
                    // `a, b string` のように 1 宣言が複数名を持つ。
                    foreach (var name in param.GetChildrenForField("name"))
                    {
                        AddName(name.Text, names);
                    }
                    break;
                case "c":
                case "cpp":
                    // parameter_declaration > declarator（pointer_declarator 等に包まれる）。
                    AddName(DigIdentifier(param.GetChildForField("declarator")), names);
                    break;
                case "rust":
                    // self_parameter は対象外（pattern を持たない）。
                    AddName(DigIdentifier(param.GetChildForField("pattern")), names);
                    break;
                case "java":
                    AddName(param.GetChildForField("name")?.Text, names);
                    break;
                case "typescript":
                case "tsx":
                    // 分割代入（object_pattern）は名前を特定できないので DigIdentifier が null を返す。
                    AddName(DigIdentifier(param.GetChildForField("pattern")), names);
                    break;
            }
        }
        return names;
    }

    /// <summary>メソッドの引数リストノード。C / C++ は declarator の下にある。</summary>
    private static Node? ParamListNode(Node method, string lang)
    {
        if (lang is "c" or "cpp")
        {
            var declarator = method.GetChildForField("declarator");
            return declarator?.GetChildForField("parameters");
        }
        return method.GetChildForField("parameters");
    }

    /// <summary>Python の引数（型注釈・既定値・<c>*args</c> / <c>**kwargs</c> の各形）から名前を取る。</summary>
    private static void AddPythonParam(Node param, List<string> names)
    {
        var name = param.Type switch
        {
            "identifier" => param.Text,
            "typed_parameter" => param.NamedChildren.FirstOrDefault(c => c.Type == "identifier")?.Text,
            "default_parameter" or "typed_default_parameter" => param.GetChildForField("name")?.Text,
            "list_splat_pattern" or "dictionary_splat_pattern" => DigIdentifier(param),
            _ => null,
        };
        if (name is not null && !PythonImplicitParams.Contains(name))
        {
            AddName(name, names);
        }
    }

    /// <summary>
    /// ポインタ・参照・可変長などのラッパを剥がして識別子を取り出す。
    /// 分割代入のように単一の名前へ落ちない形では null。
    /// </summary>
    private static string? DigIdentifier(Node? node, int depth = 0)
    {
        if (node is null || depth > 4)
        {
            return null;
        }
        if (node.Type is "identifier" or "field_identifier" or "type_identifier")
        {
            return node.Text;
        }
        // object_pattern / array_pattern は複数名に分かれるため単一名として扱わない。
        if (node.Type is "object_pattern" or "array_pattern")
        {
            return null;
        }
        foreach (var child in node.NamedChildren)
        {
            var name = DigIdentifier(child, depth + 1);
            if (name is not null)
            {
                return name;
            }
        }
        return null;
    }

    private static void AddName(string? name, List<string> names)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            names.Add(name);
        }
    }
}
