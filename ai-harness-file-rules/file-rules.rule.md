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
- `class.one-file` / `class.force` … 1 ファイル 1 クラス／クラス必須（メソッドのみ禁止）
- `method.num` / `method.lines` / `method.in-class` … メソッド数・行数の上限／クラス外メソッド・操作の禁止
- `comment.class` / `comment.method` … doc コメントの有無・最低文字数・引数の記述とシグネチャの整合（`params` / `params-strict`）

クラス概念の無い C / Go / Rust では class 検査は自動スキップ。設定不正／有効エントリ無しは
対応言語のソース書き込みを全 deny（フェイルクローズ）。

## 設定ファイル

`.claude/harness/config/ai-harness-file-rules.yml`

```yaml
files:
  - pattern: "src/main/java/**/*.java"
    lines: 500              # 1 ファイルの最大行数（"*"／省略で無制限）
    class:
      one-file: true        # 1 ファイル = 1 クラス
      force: true           # クラスが必ず要る
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
  - pattern: "frontend/main/**/*.ts"
    lines: 300
    method:
      lines: 40
```
