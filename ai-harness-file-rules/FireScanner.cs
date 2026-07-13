using System.Collections;
using System.Diagnostics;

namespace ai_harness_file_rules;

/// <summary>設定 <c>fire</c> の内容。<c>exclude</c>（除外 glob）と <c>gitignore</c>（git 無視パスを除外するか）。</summary>
public readonly record struct FireOptions(IReadOnlyList<string> Exclude, bool Gitignore);

/// <summary>
/// 走査結果。<see cref="Files"/> は検査候補のファイル（絶対パス）。
/// <see cref="Warning"/> は gitignore 除外を有効化できなかった旨（git 未導入・非リポジトリ等）。走査自体は継続する。
/// </summary>
public readonly record struct FireScan(IReadOnlyList<string> Files, string? Warning);

/// <summary>
/// 能動スキャン（<c>ai-harness-main --fire</c>）で走査するファイルを集めるスキャナ。
///
/// <c>fire.exclude</c> の glob に一致するディレクトリは部分木ごと枝刈りし、一致するファイルは走査から外す。
/// <c>fire.gitignore: true</c> なら git が無視する（未追跡かつ ignore の）パスも外す。
/// ai-harness-directory-checker と同一のセマンティクス。
/// </summary>
public static class FireScanner
{
    /// <summary>git の実行に許す最大時間（ミリ秒）。大規模リポジトリでも ls-files は速いが、保険として上限。</summary>
    private const int GitTimeoutMs = 15000;

    /// <summary>プラグイン設定の <c>fire</c> ネストマップを読む。未設定は「除外なし・gitignore 無効」。</summary>
    public static FireOptions ReadOptions(IReadOnlyDictionary<string, object> config)
    {
        var fire = config.TryGetValue("fire", out var value) ? value as IDictionary : null;
        return new FireOptions(ReadList(fire, "exclude"), ReadBool(fire, "gitignore"));
    }

    /// <summary>
    /// <paramref name="root"/> 配下を走査し、除外を適用した残りのファイル（絶対パス）を返す。
    /// アクセス不能なディレクトリは黙ってスキップする。
    /// </summary>
    public static FireScan Collect(string root, FireOptions options)
    {
        var ignored = options.Gitignore ? LoadGitIgnored(root) : GitIgnored.Inactive;
        return new FireScan(CollectFiles(root, options.Exclude, ignored), ignored.Warning);
    }

    /// <summary>
    /// 深さ優先で全ファイルを集める。<paramref name="excludePatterns"/> に一致するディレクトリは部分木ごと、
    /// <paramref name="ignored"/> が無視するディレクトリも部分木ごと枝刈りする。
    /// </summary>
    private static List<string> CollectFiles(
        string root, IReadOnlyList<string> excludePatterns, GitIgnored ignored)
    {
        var results = new List<string>();
        if (!Directory.Exists(root))
        {
            return results;
        }

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subdirs;
            string[] filesInDir;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                filesInDir = Directory.GetFiles(dir);
            }
            catch
            {
                continue; // 権限不足・削除競合等は黙ってスキップ
            }

            foreach (var file in filesInDir)
            {
                if (!MatchesAny(file, excludePatterns) && !ignored.IsIgnoredFile(root, file))
                {
                    results.Add(file);
                }
            }
            foreach (var sub in subdirs)
            {
                if (!MatchesAny(sub, excludePatterns) && !ignored.IsIgnoredDir(root, sub))
                {
                    stack.Push(sub);
                }
            }
        }
        return results;
    }

    /// <summary>いずれかの glob に一致すれば true。</summary>
    private static bool MatchesAny(string path, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (GlobMatcher.IsMatch(pattern, path))
            {
                return true;
            }
        }
        return false;
    }

    // ---- gitignore 判定（git に問い合わせ） ----

    /// <summary>
    /// git が無視するパス集合（root 相対・<c>/</c> 区切り）。<see cref="Active"/> が false なら判定しない。
    /// <see cref="Warning"/> は判定できなかった理由。
    /// </summary>
    private readonly record struct GitIgnored(
        bool Active, HashSet<string> Dirs, HashSet<string> Files, string? Warning)
    {
        public static GitIgnored Inactive => new(false, new(), new(), null);

        public bool IsIgnoredDir(string root, string dir) =>
            Active && Dirs.Contains(RelPath(root, dir));

        public bool IsIgnoredFile(string root, string file) =>
            Active && Files.Contains(RelPath(root, file));

        private static string RelPath(string root, string path) =>
            Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    /// <summary>
    /// git に無視対象（未追跡かつ ignore）を問い合わせる。
    /// <c>ls-files --others --ignored --exclude-standard --directory -z</c> は標準の全 ignore 源
    /// （各階層の .gitignore・否定 <c>!</c>・<c>core.excludesFile</c>・<c>.git/info/exclude</c>）を尊重し、
    /// 完全に無視されたディレクトリは <c>dir/</c> 1 件として返す（配下を列挙せず枝刈りできる）。
    /// git 未導入・非リポジトリ・タイムアウトは警告を立てて無効化する（スキャンは継続）。
    /// </summary>
    private static GitIgnored LoadGitIgnored(string root)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                ArgumentList =
                {
                    "-C", root, "ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return GitIgnored.Inactive with { Warning = "git を起動できないため gitignore 除外は無効。" };
            }

            // stdout を先に読み切ってから待つ（バッファ滞留による停止を避ける）。
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(GitTimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 既に終了 */ }
                return GitIgnored.Inactive with { Warning = "git ls-files がタイムアウトしたため gitignore 除外は無効。" };
            }
            if (proc.ExitCode != 0)
            {
                return GitIgnored.Inactive with
                {
                    Warning = "git 管理下のプロジェクトではないため gitignore 除外は無効。",
                };
            }

            var dirs = new HashSet<string>(StringComparer.Ordinal);
            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = raw.Replace('\\', '/');
                if (entry.EndsWith('/'))
                {
                    dirs.Add(entry.TrimEnd('/'));
                }
                else
                {
                    files.Add(entry);
                }
            }
            return new GitIgnored(true, dirs, files, null);
        }
        catch (Exception ex)
        {
            return GitIgnored.Inactive with { Warning = $"gitignore 判定に失敗したため無効: {ex.Message}" };
        }
    }

    // ---- YamlDotNet の既定型（マップ=IDictionary, リスト=IEnumerable, スカラ=string）ヘルパ ----

    private static object? Get(IDictionary? map, string key)
    {
        if (map is null)
        {
            return null;
        }
        foreach (DictionaryEntry entry in map)
        {
            if (entry.Key?.ToString() == key)
            {
                return entry.Value;
            }
        }
        return null;
    }

    private static IReadOnlyList<string> ReadList(IDictionary? map, string key)
    {
        if (Get(map, key) is not IEnumerable seq || seq is string)
        {
            return Array.Empty<string>();
        }
        var list = new List<string>();
        foreach (var item in seq)
        {
            var s = item?.ToString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                list.Add(s.Trim());
            }
        }
        return list;
    }

    private static bool ReadBool(IDictionary? map, string key) =>
        bool.TryParse(Get(map, key)?.ToString(), out var b) && b;
}
