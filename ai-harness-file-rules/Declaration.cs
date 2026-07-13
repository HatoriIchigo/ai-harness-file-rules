namespace ai_harness_file_rules;

/// <summary>コメント検査の対象となる宣言の種別。</summary>
public enum DeclKind
{
    /// <summary>クラス相当（class / interface / enum / record / struct 等）。</summary>
    Class,

    /// <summary>メソッド／関数。</summary>
    Method,
}

/// <summary>
/// コメント検査の対象となる宣言 1 件。
/// <see cref="Doc"/> は宣言に紐づく doc コメントの生テキスト（前置コメント、Python は docstring）。
/// 付いていなければ <c>null</c>。<see cref="Parameters"/> はシグネチャ上の引数名
/// （レシーバ・<c>self</c>・分割代入など名前を特定できないものは含まない）。
/// </summary>
public sealed record DeclarationInfo(
    DeclKind Kind,
    string Name,
    int StartLine,
    IReadOnlyList<string> Parameters,
    string? Doc);
