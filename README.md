# ai-harness-file-rules

> ファイル単位のコード構造ルール（行数・1クラス1ファイル・メソッド数/行数）を AST で強制する ai-harness プラグイン。

書き込み系ツール（`Write` / `Edit` / `MultiEdit`）の `PostToolUse` で発火し、書き込んだソースファイルを **tree-sitter で AST 解析**する。設定 `files` のエントリで定めた構造ルールに反したら **deny（exit 2）** する。

PostToolUse のため書き込み自体は止められない。deny 時は違反内容を reason で返し、Claude がファイルを分割・整理してルールに合わせる。

## 対象言語

| 言語 | 拡張子 | class 検査 |
|---|---|---|
| C | `.c` `.h` | スキップ（クラス概念なし） |
| C++ | `.cpp` `.cc` `.cxx` `.hpp` `.hh` `.hxx` | 有効（class / struct） |
| Java | `.java` | 有効（class / interface / enum / record） |
| Python | `.py` `.pyi` | 有効 |
| Rust | `.rs` | スキップ（struct + impl のみ） |
| Go | `.go` | スキップ |
| TypeScript | `.ts` `.mts` `.cts` `.tsx` | 有効 |

上記以外の拡張子（`.md` 等）は対象外＝常に許可。クラス概念の無い言語では `class` 検査は自動でスキップされる（`method`・`lines` は適用）。

## 検査項目

| 項目 | 内容 |
|---|---|
| `lines` | ファイル総行数の上限 |
| `class.one-file` | トップレベルのクラス相当は 1 個まで（1 ファイル = 1 クラス） |
| `class.force` | クラスが必ず要る（メソッドのみのファイル禁止） |
| `method.num` | ファイル内メソッド/関数の数の上限 |
| `method.lines` | 1 メソッド/関数の行数の上限（超過したものを列挙） |

- 数値は `"*"`（要引用符）または**キー省略**で無制限。
- メソッド数は**ファイル全体**の定義数（クラス内メソッド＋トップレベル関数）。メソッド内のクロージャ/ローカル関数は数えない。TypeScript は変数/フィールドに代入された名前付き arrow も 1 メソッドとして数える。
- メソッド行数は AST の開始行〜終了行。ファイル行数は末尾改行を 1 行として数えない。

## 設定（config/ai-harness-file-rules.yml）

```yaml
files:
  - pattern: "src/main/java/**/*.java"
    lines: 500
    class:
      one-file: true
      force: true
    method:
      num: 5
      lines: 50

  - pattern: "frontend/main/**/*.ts"
    lines: 300
    method:
      lines: 40
    # class を書かなければ class 検査はしない
```

ファイルが `pattern` にマッチしたら（複数マッチは先頭優先）そのエントリの規則を適用する。

## 判定フロー

対象言語のソースファイルのみが対象（非ソースは常に許可）。

```
1. 設定が使用不可            → deny（フェイルクローズ。エラー内容を提示）
2. pattern にマッチ（先頭優先）→ 構造解析：規則違反あり→deny / なし→許可
3. どの pattern にもマッチせず → 許可（管理対象外）
```

- **フェイルクローズ**: `files` 未設定／有効エントリなし／不正な値（`lines`・`method.num`・`method.lines` が整数でも `*` でもない、`class.*` が真偽でない等）があると、対応言語のソース書き込みを **全て deny**。エラー内容は reason に列挙される。
- テスト等を検査対象外にしたい場合は `pattern` に含めなければよい（3 で許可）。
- ファイルは PostToolUse 時点でディスク上にあるため、書き込んだ**ファイル全体**を解析する。

## エンジン

[TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet)（tree-sitter の .NET バインディング）を使用。ネイティブ grammar（`tree-sitter-*.dll`）を同梱し、Windows / Linux（x64）で動作する。ai-harness-constants と同じエンジン。

## ビルドと配置

```sh
dotnet build ai-harness-file-rules/ai-harness-file-rules/ai-harness-file-rules.csproj -c Release

# 配布物は lib の管理 DLL のみ: プラグイン DLL・TreeSitter.dll（マネージド）を lib/ へ。
# .deps.json は不要（host の ALC が lib 直下を直接プローブして TreeSitter.dll を解決する）。
BIN=ai-harness-file-rules/ai-harness-file-rules/bin/Release/net10.0
cp "$BIN/ai-harness-file-rules.dll"       <配置先>/lib/
cp "$BIN/TreeSitter.dll"                   <配置先>/lib/
# ネイティブ grammar（tree-sitter-*.dll）は**プラグイン側では配置しない**。汎用ゆえ host（ai-harness-main）の
# リリースに runtimes/<rid>/native として同梱され、host が起動時にフルパスで事前ロードして解決する。
# from-source で host を自前配置する場合のみ runtimes/ を実行体隣へ置く（build-and-deploy.md 参照）。

cp ai-harness-file-rules/config/ai-harness-file-rules.yml  <プロジェクト>/.claude/harness/config/

# common.yml の tools で有効化してから
<配置先>/ai-harness-main --restart
```

`baselib.dll` は host が共有ロードするため `lib/` に置かない。`common.yml` の `tools` に `- ai-harness-file-rules: true` を追加して有効化する。詳細は `ai-harness-main/docs/plugin-development.md` を参照。

> `runtimes/` は ai-harness-constants と同じ tree-sitter ネイティブ grammar。両プラグインを併用する場合、同一内容なので実行体隣の `runtimes/` で共有される（後勝ちで上書きされても同一）。host が起動時に一度だけ事前ロードするため、プラグインごとに重複ロードもしない。

## 構成

```
ai-harness-file-rules/
├── README.md
├── config/
│   └── ai-harness-file-rules.yml   検査エントリの定義（配置元）
└── ai-harness-file-rules/
    ├── ai-harness-file-rules.csproj
    ├── FileRulesPlugin.cs          PostToolUse の発火・判定・reason 生成
    ├── FileRulesConfig.cs          設定の解釈とバリデーション
    ├── StructureAnalyzer.cs        tree-sitter でクラス/メソッド構造を解析
    └── GlobMatcher.cs              ** 対応の glob 一致（他プラグインと同一）
```
