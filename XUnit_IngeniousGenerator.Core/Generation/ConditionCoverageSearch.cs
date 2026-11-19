using Microsoft.CodeAnalysis;
using XUnit_IngeniousGenerator.Core.Evaluation;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// 条件の網羅を目指して、入力を追加する。
/// 判定（if・三項演算子・ループの条件）ごとに成立と不成立の両方、&amp;&amp; ・ || でつながった要素ごとに成立と不成立の両方と、
/// その要素だけが変わって判定の結果が変わる入力の組（MC/DC）を目標にする。
/// 足りない目標があれば、今の入力の値を組み合わせた候補（引数を1つ、または2つ変えたもの）を計算し、目標を満たすものだけを加える
/// </summary>
internal static class ConditionCoverageSearch
{
    // 1つの関数で候補を計算する回数の上限と、入力の行数の上限
    private const int MaxEvaluations = 400;
    private const int MaxRows = 40;

    [ThreadStatic]
    private static bool _enabled;

    /// <summary>入力を探すか（テストを生成するときだけ。警告の判定では、入力のたびに動くため探さない）</summary>
    public static bool Enabled => _enabled;

    /// <summary>
    /// この呼び出しの間だけ、入力を探すようにする
    /// </summary>
    /// <returns>使い終わったら元に戻すためのもの</returns>
    public static IDisposable Enable()
    {
        var previous = _enabled;
        _enabled = true;
        return new Restore(() => _enabled = previous);
    }

    /// <summary>
    /// 足りない目標を満たす入力を加える（もとの入力はすべて残す）
    /// </summary>
    /// <param name="rows">入力</param>
    /// <param name="constants">関数の中の数値の定数（数値の引数の候補に加える）</param>
    /// <param name="run">入力を評価し、結果と判定の記録を返す処理</param>
    /// <returns>入力</returns>
    public static IReadOnlyList<object?[]> Improve(
        IReadOnlyList<object?[]> rows, IReadOnlyCollection<long> constants, Func<object?[], (EvaluationResult Result, List<ConditionObservation> Observations)> run)
    {
        if (rows.Count == 0) return rows;

        var coverage = new Coverage();
        foreach (var row in rows) coverage.Add(run(row).Observations);
        if (coverage.Missing() == 0) return rows;

        var kept = rows.ToList();
        var tried = new List<object?[]>(rows);
        var evaluations = 0;
        foreach (var candidate in Candidates(rows, constants))
        {
            if (MaxRows <= kept.Count || MaxEvaluations <= evaluations || coverage.Missing() == 0) break;
            if (tried.Any(r => r.SequenceEqual(candidate))) continue;

            tried.Add(candidate);

            evaluations++;
            var (result, observations) = run(candidate);
            if (result.Kind != EvaluationKind.Returned) continue;
            if (coverage.AddIfImproves(observations)) kept.Add(candidate);
        }

        return kept;
    }

    /// <summary>
    /// 候補を作る。基準の行（最初の行）から引数を1つ変えたもの、2つ変えたもの（値の少ない引数の組から）、ほかの行から引数を1つ変えたものの順。
    /// 引数ごとの値は、入力に出てくる値と、数値なら関数の中の定数と、それぞれの前後
    /// </summary>
    /// <param name="rows">入力</param>
    /// <param name="constants">関数の中の数値の定数</param>
    /// <returns>候補</returns>
    private static IEnumerable<object?[]> Candidates(IReadOnlyList<object?[]> rows, IReadOnlyCollection<long> constants)
    {
        var columns = rows[0].Length;
        var values = Enumerable.Range(0, columns).Select(i => ValuesOf(rows, i, constants)).ToList();
        var baseRow = rows[0];

        for (var i = 0; i < columns; i++)
        {
            foreach (var value in values[i]) yield return With(baseRow, (i, value));
        }

        // 2つ変える組は、値の少ない引数の組（bool どうしなど）から試す
        var pairs = Enumerable.Range(0, columns)
            .SelectMany(i => Enumerable.Range(i + 1, columns - i - 1).Select(j => (I: i, J: j)))
            .OrderBy(p => values[p.I].Count * values[p.J].Count);
        foreach (var (i, j) in pairs)
        {
            foreach (var first in values[i])
            {
                foreach (var second in values[j]) yield return With(baseRow, (i, first), (j, second));
            }
        }

        foreach (var row in rows.Skip(1))
        {
            for (var i = 0; i < columns; i++)
            {
                foreach (var value in values[i]) yield return With(row, (i, value));
            }
        }
    }

    /// <summary>
    /// 列の値（入力に出てくる値と、数値なら関数の中の定数と、それぞれの前後の値）を返す
    /// </summary>
    /// <param name="rows">入力</param>
    /// <param name="column">列</param>
    /// <param name="constants">関数の中の数値の定数</param>
    /// <returns>値</returns>
    private static List<object?> ValuesOf(IReadOnlyList<object?[]> rows, int column, IReadOnlyCollection<long> constants)
    {
        var values = rows.Select(r => r[column]).Distinct().ToList();
        values.AddRange(values.FirstOrDefault(v => v is not null) switch
        {
            int => constants.Where(c => int.MinValue <= c && c <= int.MaxValue).Select(c => (object?)(int)c),
            long => constants.Select(c => (object?)c),
            decimal => constants.Select(c => (object?)(decimal)c),
            _ => [],
        });
        values = values.Distinct().ToList();
        var neighbors = values.SelectMany(v => v switch
        {
            int i when i != int.MinValue && i != int.MaxValue => new object?[] { i - 1, i + 1 },
            long l when l != long.MinValue && l != long.MaxValue => new object?[] { l - 1, l + 1 },
            decimal m when decimal.MinValue < m && m < decimal.MaxValue => new object?[] { m - 1, m + 1 },
            _ => [],
        });
        return values.Concat(neighbors).Distinct().ToList();
    }

    /// <summary>
    /// 行の一部の値を変えた行を作る
    /// </summary>
    /// <param name="row">元の行</param>
    /// <param name="changes">変える位置と値</param>
    /// <returns>新しい行</returns>
    private static object?[] With(object?[] row, params (int Index, object? Value)[] changes)
    {
        var copy = (object?[])row.Clone();
        foreach (var (index, value) in changes) copy[index] = value;
        return copy;
    }

    /// <summary>
    /// 目標と、満たした目標の記録
    /// </summary>
    private sealed class Coverage
    {
        // 判定ごとの、要素の値と結果の組（同じ組は1つにまとめる）
        private readonly Dictionary<SyntaxNode, HashSet<string>> _signatures = [];
        private readonly Dictionary<SyntaxNode, int> _atomCounts = [];

        /// <summary>
        /// 判定の記録を加える
        /// </summary>
        /// <param name="observations">判定の記録</param>
        public void Add(IEnumerable<ConditionObservation> observations)
        {
            foreach (var observation in observations)
            {
                if (!_signatures.TryGetValue(observation.Decision, out var set)) _signatures[observation.Decision] = set = [];
                set.Add(Signature(observation));
                _atomCounts[observation.Decision] = observation.Atoms.Count;
            }
        }

        /// <summary>
        /// 判定の記録を加え、満たした目標が増えたかを返す（増えなければ加えない）
        /// </summary>
        /// <param name="observations">判定の記録</param>
        /// <returns>増えたらtrue</returns>
        public bool AddIfImproves(List<ConditionObservation> observations)
        {
            var before = Satisfied();
            var snapshot = _signatures.ToDictionary(p => p.Key, p => new HashSet<string>(p.Value));
            Add(observations);
            if (before < Satisfied()) return true;

            _signatures.Clear();
            foreach (var pair in snapshot) _signatures[pair.Key] = pair.Value;
            return false;
        }

        /// <summary>
        /// 満たしていない目標の数を返す
        /// </summary>
        /// <returns>満たしていない目標の数</returns>
        public int Missing()
        {
            return Goals() - Satisfied();
        }

        /// <summary>
        /// 目標の数（判定ごとに成立・不成立の2つ、要素が2つ以上なら要素ごとに成立・不成立・MC/DC の3つ）
        /// </summary>
        /// <returns>目標の数</returns>
        private int Goals()
        {
            return _signatures.Keys.Sum(d => 2 + (_atomCounts[d] < 2 ? 0 : _atomCounts[d] * 3));
        }

        /// <summary>
        /// 満たした目標の数
        /// </summary>
        /// <returns>満たした目標の数</returns>
        private int Satisfied()
        {
            var count = 0;
            foreach (var pair in _signatures)
            {
                var observed = pair.Value.Select(Parse).ToList();
                count += observed.Select(o => o.Outcome).Distinct().Count();
                var atoms = _atomCounts[pair.Key];
                if (atoms < 2) continue;

                for (var k = 0; k < atoms; k++)
                {
                    count += observed.Where(o => o.Values[k] is not null).Select(o => o.Values[k]).Distinct().Count();
                    if (HasIndependencePair(observed, k)) count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 要素 k だけが変わって判定の結果が変わる組（ほかの要素は同じか、どちらかで評価されていない）があるかを判定する
        /// </summary>
        /// <param name="observed">判定の記録</param>
        /// <param name="k">要素の位置</param>
        /// <returns>あればtrue</returns>
        private static bool HasIndependencePair(List<(bool?[] Values, bool Outcome)> observed, int k)
        {
            foreach (var first in observed)
            {
                foreach (var second in observed)
                {
                    if (first.Outcome == second.Outcome || first.Values[k] is null || second.Values[k] is null || first.Values[k] == second.Values[k]) continue;

                    var othersMatch = true;
                    for (var j = 0; j < first.Values.Length && othersMatch; j++)
                    {
                        if (j != k && first.Values[j] is not null && second.Values[j] is not null && first.Values[j] != second.Values[j]) othersMatch = false;
                    }

                    if (othersMatch) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 判定の記録を、まとめるための文字列にする（要素の値 T/F/-、結果 T/F）
        /// </summary>
        /// <param name="observation">判定の記録</param>
        /// <returns>文字列</returns>
        private static string Signature(ConditionObservation observation)
        {
            return new string(observation.Values.Select(v => v is null ? '-' : v.Value ? 'T' : 'F').ToArray()) + (observation.Outcome ? "T" : "F");
        }

        /// <summary>
        /// 文字列から判定の記録に戻す
        /// </summary>
        /// <param name="signature">文字列</param>
        /// <returns>要素の値と結果</returns>
        private static (bool?[] Values, bool Outcome) Parse(string signature)
        {
            var values = signature.Take(signature.Length - 1).Select(c => c == '-' ? (bool?)null : c == 'T').ToArray();
            return (values, signature[signature.Length - 1] == 'T');
        }
    }

    /// <summary>
    /// 使い終わったら元に戻す
    /// </summary>
    private sealed class Restore : IDisposable
    {
        private readonly Action _restore;

        /// <summary>
        /// 元に戻す処理を覚えておく
        /// </summary>
        /// <param name="restore">元に戻す処理</param>
        public Restore(Action restore)
        {
            _restore = restore;
        }

        /// <summary>
        /// 元に戻す
        /// </summary>
        public void Dispose()
        {
            _restore();
        }
    }
}
