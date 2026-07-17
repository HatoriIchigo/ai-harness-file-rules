# ai-harness-file-rules

> ファイル単位のコード構造ルール（行数・1 行の文字数・1クラス1ファイル・メソッド数/行数・doc コメント）を AST で強制する ai-harness プラグイン。

書き込み系ツール（`Write` / `Edit` / `MultiEdit`）の `PostToolUse` で発火し、書き込んだソースファイルを **tree-sitter で AST 解析**する。設定 `files` のエントリで定めた構造ルールに反したら **deny（exit 2）** する。

PostToolUse のため書き込み自体は止められない。deny 時は違反内容を reason で返し、Claude がファイルを分割・整理し、doc コメントを補ってルールに合わせる。

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
| `line-length` | 1 行の文字数の上限（超過した行を行番号付きで列挙） |
| `tab-width` | `line-length` でタブを何文字と数えるか（既定 4） |
| `class.one-file` | トップレベルのクラス相当は 1 個まで（1 ファイル = 1 クラス） |
| `class.force` | クラスが必ず要る（メソッドのみのファイル禁止） |
| `method.num` | ファイル内メソッド/関数の数の上限 |
| `method.lines` | 1 メソッド/関数の行数の上限（超過したものを列挙） |
| `method.in-class` | `true` で、クラス外のメソッド/関数定義とクラス外の操作文（if/for/while/式文 等）を禁止 |
| `comment.class.require` | クラスに doc コメントが必須 |
| `comment.class.min-length` | クラスの説明文の最低文字数 |
| `comment.method.require` | メソッド/関数に doc コメントが必須 |
| `comment.method.min-length` | メソッド/関数の説明文の最低文字数 |
| `comment.method.params` | 全ての引数が doc コメントに記述されていること（記述漏れを deny） |
| `comment.method.params-strict` | シグネチャに無い引数の記述を禁止（リネーム・削除の取り残しを deny） |

- 数値は `"*"`（要引用符）または**キー省略**で無制限。`method.in-class`・`comment.*` の真偽値は省略で無効。ただし `tab-width` は無制限にできないため `"*"` 不可（省略時は既定の 4）。
- `line-length` は**表示幅ではなく文字の個数**で数える。全角・半角を区別せず、どちらも 1 文字（サロゲートペアも 1 文字）。日本語コメントの行が実質半分の文字数に制限されることはない。タブだけは `tab-width` 文字として数える（タブストップへの桁揃えはしない）。行末の改行・`\r` は数えない。
- `method.in-class`: メソッド・操作は必ずクラス内に置かせる。トップレベル（および名前空間直下等）の関数定義と、`import`／`export` を除くトップレベルの操作文を deny する。**クラス概念のある言語（C++ / Java / Python / TypeScript）のみ**有効で、C / Go / Rust では自動スキップ。
- メソッド数は**ファイル全体**の定義数（クラス内メソッド＋トップレベル関数）。メソッド内のクロージャ/ローカル関数は数えない。TypeScript は変数/フィールドに代入された名前付き arrow も 1 メソッドとして数える。
- メソッド行数は AST の開始行〜終了行。ファイル行数は末尾改行を 1 行として数えない。

## doc コメント（`comment`）

各言語の標準的な doc 形式のみを doc として認める。実装コメント（Java や Rust の `//`）は doc とみなさず「コメントなし」として deny する（Go は godoc が `//` を doc とするため区別しない）。

| 言語 | doc として認める形式 | 引数の書き方 |
|---|---|---|
| Java | `/** */` | `@param 名前 説明` |
| C / C++ | `/** */` `///` `/*! */` | `@param 名前 説明`（`\param`・`@param[in] 名前` も可） |
| TypeScript | `/** */` | `@param 名前 - 説明` |
| Rust | `///` `/** */` | `# Arguments` セクションに `` * `名前` - 説明 `` |
| Go | `//` | 列挙タグが無いため、**本文中に引数名が現れれば**記述ありとみなす |
| Python | docstring（本体先頭の文字列） | Google スタイルの `Args:` セクションに `名前: 説明`（`名前 (型): 説明` も可） |

- doc は宣言の**直前行**に連続して置かれたコメント（Rust / Go のように 1 行ずつ別ノードになる形式は連続分を結合する）。1 行でも空けば紐づかない。TypeScript の `export class` はコメントが `export` の前に付くため、そこから遡って探す。
- Java のアノテーション・Python のデコレータが宣言との間にあっても doc は正しく紐づく。
- `params` は**シグネチャとの整合**まで見る。名前を特定できない引数は記述を求めない（Python の `self` / `cls`、Rust の `self`、Go のレシーバ、TypeScript の分割代入 `{ a, b }`）。
- `comment.class` は**クラス概念のある言語（C++ / Java / Python / TypeScript）のみ**有効。C / Go / Rust では対象外（`comment.method` は全言語で有効）。
- 説明文は doc からタグ・セクション（`@param`・`Args:`・`# Arguments` 等）を除いた本文。`min-length` はこの本文の文字数で判定する。

## 設定（config/ai-harness-file-rules.yml）

```yaml
files:
  - pattern: "src/main/java/**/*.java"
    lines: 500
    line-length: 120        # 1 行は 120 文字まで
    tab-width: 4            # タブは 4 文字として数える（省略で 4）
    class:
      one-file: true
      force: true
    method:
      num: 5
      lines: 50
      in-class: true          # クラス外メソッド／クラス外操作を禁止
    comment:
      class:
        require: true         # クラスに javadoc 必須
        min-length: 10        # 説明文は 10 文字以上
      method:
        require: true
        min-length: 10
        params: true          # 全ての引数を @param で記述
        params-strict: true   # 存在しない引数の @param を禁止

  - pattern: "frontend/main/**/*.ts"
    lines: 300
    line-length: 100
    method:
      lines: 40
    # class・comment を書かなければ、その検査はしない
```

ファイルが `pattern` にマッチしたら（複数マッチは先頭優先）そのエントリの規則を適用する。

## 判定フロー

対象言語のソースファイルのみが対象（非ソースは常に許可）。

```
1. 設定が使用不可            → deny（フェイルクローズ。エラー内容を提示）
2. pattern にマッチ（先頭優先）→ 構造解析：規則違反あり→deny / なし→許可
3. どの pattern にもマッチせず → 許可（管理対象外）
```

- **フェイルクローズ**: `files` 未設定／有効エントリなし／不正な値（`lines`・`line-length`・`method.num`・`method.lines`・`comment.*.min-length` が整数でも `*` でもない、`tab-width` が 1 以上の整数でない、`class.*`・`method.in-class`・`comment.*.require` 等が真偽でない等）があると、対応言語のソース書き込みを **全て deny**。エラー内容は reason に列挙される。
- テスト等を検査対象外にしたい場合は `pattern` に含めなければよい（3 で許可）。
- ファイルは PostToolUse 時点でディスク上にあるため、書き込んだ**ファイル全体**を解析する。

## 能動スキャン（`ai-harness-main --fire`）

hook は書き込みごとに 1 ファイルを検査する。これに対し `--fire` はプロジェクトの**既存ツリー全体**を一括点検する。`pattern` に合致する全ソースの構造を解析し、規則に違反するファイルがあれば **exit 2**（検出）。適用する規則は hook と同じ（複数マッチは先頭優先）。

hook のゲートではないため、exit 2 は書き込みの差し戻しではなく**スキャン結果のレポート**（CI 等で扱えるようコマンドの終了コードへ反映される）。設定が使用不可なら検査対象・規則を決められないため、hook と同じくフェイルクローズで exit 2。

```yaml
fire:
  gitignore: true
  exclude:
    - .git
    - node_modules
```

- `exclude` … 一致するディレクトリを**部分木ごと**枝刈りし、一致するファイルも走査から外す。`pattern` と同じ glob（`**` / `*` / `?`）。フルパスと各 `/` 区切りサフィックスに照合されるため、名前指定（`node_modules`）・パス指定（`.claude/harness`）・glob（`"**/dist"`）のいずれも書ける。
- `gitignore` … `true` で、git が無視する（未追跡かつ ignore の）ファイル／ディレクトリも走査から外す。各階層の `.gitignore`・否定（`!`）・`core.excludesFile`・`.git/info/exclude` を尊重する（git に問い合わせる）。git 未導入・非リポジトリなら警告して無効化し、スキャンは継続。既定は `false`。
- 読めない／解析できないファイルは警告ログを出してスキップする（違反として扱わない）。

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
│   └── ai-harness-file-rules.yml   検査エントリ・fire の定義（配置元）
└── ai-harness-file-rules/
    ├── ai-harness-file-rules.csproj
    ├── FileRulesPlugin.cs          PostToolUse の発火・能動スキャン・判定・reason 生成
    ├── FileRulesConfig.cs          設定の解釈とバリデーション
    ├── StructureAnalyzer.cs        tree-sitter でクラス/メソッド構造を解析
    ├── Declaration.cs              doc コメント検査の対象となる宣言のモデル
    ├── DeclarationCollector.cs     AST から宣言・引数名・doc コメントを収集（言語差はここに閉じる）
    ├── DocComment.cs               doc コメントから説明文と引数の記述を取り出す
    ├── CommentChecker.cs           doc コメントとシグネチャの突き合わせ
    ├── LineLengthChecker.cs        1 行の文字数の検査（タブは tab-width 文字）
    ├── FireScanner.cs              能動スキャンの走査（fire.exclude / fire.gitignore）
    └── GlobMatcher.cs              ** 対応の glob 一致（他プラグインと同一）
```
