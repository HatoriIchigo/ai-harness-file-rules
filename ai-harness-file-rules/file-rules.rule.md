---
paths:
  - .claude/harness/config/ai-harness-file-rules.yml
---

## 概要

ai-harness-file-rules は書き込み系ツール（Write / Edit / MultiEdit）の `PostToolUse` で発火し、
書き込んだソースを tree-sitter で AST 解析して、ファイル単位のコード構造ルールと doc コメント規約に
反したら deny（exit 2）する。C/C++/Java/Python/Rust/Go/TypeScript 対応。ファイルが `pattern` に
マッチしたら（先頭優先）そのエントリの規則を適用する。非ソース・どの `pattern` にも合わないファイルは許可。

- `lines` … ファイル総行数の上限（`"*"`／省略で無制限）
- `line-length` / `tab-width` … 1 行の文字数の上限（`"*"`／省略で無制限）／タブを何文字と数えるか（省略で 4）
- `blank-line` … 空行なしで続けられる行数の上限（`"*"`／省略で無制限）。空行とコメント行が区切りとなる
- `class.one-file` / `class.force` … 1 ファイル 1 クラス／クラス必須（メソッドのみ禁止）
- `class.extend` … 指定ファイル（単一パス、配列不可）で宣言されたクラスを必ず継承（`extends`）
- `method.num` / `method.lines` / `method.in-class` … メソッド数・行数の上限／クラス外メソッド・操作の禁止
- `comment.class` / `comment.method` … doc コメントの有無・最低文字数・引数の記述とシグネチャの整合（`params` / `params-strict`）
- `unused` … 未使用 import／変数／関数／クラスの検出。自前解析はせず LSP の診断（`ai-harness-main` の
  LSP 連携機能）をそのまま使うため、`common.yml` の `lsp:` で対象言語の LSP を有効化していないと働かない。
  hook（`Action`）では非同期キャッシュ読み取り（編集直後は反映が間に合わないことがある）、
  `ai-harness-main --fire` の能動スキャンではファイルごとに同期リクエストして応答を待つ（初回は
  対象言語の LSP のインストール・起動待ちも含めて時間がかかることがある）。

クラス概念の無い C / Go / Rust では class 検査は自動スキップ。設定不正／有効エントリ無しは
対応言語のソース書き込みを全 deny（フェイルクローズ）。

## 設定ファイル

`.claude/harness/config/ai-harness-file-rules.yml`

```yaml
files:
  - pattern: "src/main/java/**/*.java"
    lines: 500              # 1 ファイルの最大行数（"*"／省略で無制限）
    line-length: 120        # 1 行の最大文字数（"*"／省略で無制限）
    tab-width: 4            # タブを何文字と数えるか（省略で 4）
    blank-line: 10          # 空行なしで続けられる行数の上限（空行・コメント行が区切り）
    class:
      one-file: true        # 1 ファイル = 1 クラス
      force: true           # クラスが必ず要る
      extend: "src/main/java/base/Base.java"  # このファイルのクラスを必ず継承
    method:
      num: 5                # メソッド数の上限
      lines: 50             # 1 メソッドの最大行数
      in-class: true        # クラス外メソッド／操作を禁止
    comment:
      class:
        require: true       # クラスに doc コメント必須
        min-length: 10
      method:
        require: true       # メソッドに doc コメント必須
        min-length: 10
        params: true        # 全引数が doc に記述されていること
        params-strict: true # シグネチャに無い引数の記述を禁止
    unused: true           # 未使用 import／変数／関数／クラスを検出（要 common.yml の lsp: 有効化）
  - pattern: "frontend/main/**/*.ts"
    lines: 300
    line-length: 100
    method:
      lines: 40
```
