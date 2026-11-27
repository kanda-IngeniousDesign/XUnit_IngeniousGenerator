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

    // 境界を二分探索するときの、1つの関数で計算する回数の上限
    private const int MaxBoundaryEvaluations = 400;

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
    /// <param name="fixedColumns">値を変えない列（out の引数）</param>
    /// <param name="texts">関数の中の文字列の定数（文字列の引数の候補に加える）</param>
    /// <param name="run">入力を評価し、結果と判定の記録を返す処理</param>
    /// <param name="uncovered">探しても見つからなかった入力の説明（テストにコメントで書く）</param>
    /// <returns>入力</returns>
    public static IReadOnlyList<object?[]> Improve(
        IReadOnlyList<object?[]> rows, IReadOnlyCollection<long> constants, ISet<int> fixedColumns, IReadOnlyCollection<string> texts,
        Func<object?[], (EvaluationResult Result, List<ConditionObservation> Observations)> run, out IReadOnlyList<string> uncovered)
    {
        uncovered = [];
        if (rows.Count == 0) return rows;

        var coverage = new Coverage();
        foreach (var row in rows) coverage.Add(run(row).Observations);

        // 判定が切り替わる境界（ローカル変数を通した比較なども）と、bool の引数の組み合わせは、目標を満たしていても加える
        var kept = rows.ToList();
        AddBoundaryRows(kept, constants, fixedColumns, run, coverage);
        AddBooleanPairs(kept, fixedColumns, run, coverage);
        if (coverage.Missing() == 0) return kept;

        var tried = new List<object?[]>(kept);
        var evaluations = 0;
        foreach (var candidate in Candidates(kept, constants, fixedColumns, texts))
        {
            if (MaxRows <= kept.Count || MaxEvaluations <= evaluations || coverage.Missing() == 0) break;
            if (tried.Any(r => r.SequenceEqual(candidate))) continue;

            tried.Add(candidate);

            evaluations++;
            // 例外になる入力も、判定の成立（ガードの throw など）としては数える（例外のテストで確かめるため、行は戻り値テストに入らない）
            var (result, observations) = run(candidate);
            if (result.Kind is not (EvaluationKind.Returned or EvaluationKind.Threw)) continue;
            if (coverage.AddIfImproves(observations)) kept.Add(candidate);
        }

        uncovered = coverage.DescribeMissing();
        return kept;
    }

    /// <summary>
    /// 数値の引数ごとに、ほかの引数を基準の行のままにして、判定の結果（要素の値・成立・例外）が切り替わる境界を二分探索で探し、
    /// その前後の値の行を加える。ローカル変数を通した比較（var adjusted = score / 2; if (adjusted >= 40)）でも、
    /// 引数の境界（79 と 80）が分かる。探す範囲は、今の入力の値・関数の中の定数・0 と、それらの10倍程度まで
    /// （型の端まで探すと、オーバーフローで結果が変わる値まで加わるため）
    /// </summary>
    /// <param name="kept">入力（加える先）</param>
    /// <param name="constants">関数の中の数値の定数</param>
    /// <param name="run">入力を評価し、結果と判定の記録を返す処理</param>
    /// <param name="coverage">満たした目標の記録</param>
    private static void AddBoundaryRows(
        List<object?[]> kept, IReadOnlyCollection<long> constants, ISet<int> fixedColumns, Func<object?[], (EvaluationResult Result, List<ConditionObservation> Observations)> run, Coverage coverage)
    {
        var baseRow = kept[0];
        var evaluations = 0;
        for (var column = 0; column < baseRow.Length; column++)
        {
            var sample = baseRow[column];
            if (fixedColumns.Contains(column) || sample is not (int or long or decimal)) continue;

            var cache = new Dictionary<decimal, (string Signature, List<ConditionObservation> Observations)>();
            (string Signature, List<ConditionObservation> Observations) At(decimal value)
            {
                if (cache.TryGetValue(value, out var known)) return known;

                evaluations++;
                var (result, observations) = run(With(baseRow, (column, FromDecimal(value, sample))));
                return cache[value] = (DecisionSignature(result, observations), observations);
            }

            var found = new SortedSet<decimal>();
            void Bisect(decimal low, decimal high)
            {
                if (MaxBoundaryEvaluations <= evaluations || At(low).Signature == At(high).Signature) return;
                if (high - low <= 1)
                {
                    found.Add(low);
                    found.Add(high);
                    return;
                }

                var middle = Math.Floor((low + high) / 2);
                Bisect(low, middle);
                Bisect(middle, high);
            }

            var probes = Probes(kept, column, constants, sample);
            for (var k = 0; k + 1 < probes.Count; k++) Bisect(probes[k], probes[k + 1]);

            foreach (var value in found)
            {
                var row = With(baseRow, (column, FromDecimal(value, sample)));
                if (MaxRows <= kept.Count || kept.Any(r => r.SequenceEqual(row))) continue;

                kept.Add(row);
                coverage.Add(At(value).Observations);
            }
        }
    }

    /// <summary>
    /// 境界を探す値（今の入力の値・関数の中の定数・0 と、その最大の絶対値の10倍＋100 の正負）を、小さい順に返す
    /// </summary>
    /// <param name="rows">入力</param>
    /// <param name="column">列</param>
    /// <param name="constants">関数の中の数値の定数</param>
    /// <param name="sample">列の値の例（型を決める）</param>
    /// <returns>値</returns>
    private static List<decimal> Probes(List<object?[]> rows, int column, IReadOnlyCollection<long> constants, object sample)
    {
        var values = rows.Select(r => r[column]).Where(v => v is int or long or decimal)
            .Select(v => System.Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture))
            .Concat(constants.Select(c => (decimal)c))
            .Append(0m)
            .Where(v => Fits(v, sample))
            .ToList();
        var reach = values.Max(Math.Abs) * 10 + 100;
        return values.Append(reach).Append(-reach).Where(v => Fits(v, sample)).Select(Math.Floor).Distinct().OrderBy(v => v).ToList();
    }

    /// <summary>
    /// 値が列の型に収まるか（decimal は、探す範囲を ±10^15 までにする）を判定する
    /// </summary>
    /// <param name="value">値</param>
    /// <param name="sample">列の値の例</param>
    /// <returns>収まるならtrue</returns>
    private static bool Fits(decimal value, object sample)
    {
        return sample switch
        {
            int => int.MinValue <= value && value <= int.MaxValue,
            long => long.MinValue <= value && value <= long.MaxValue,
            _ => -1_000_000_000_000_000m <= value && value <= 1_000_000_000_000_000m,
        };
    }

    /// <summary>
    /// 値を列の型にする
    /// </summary>
    /// <param name="value">値</param>
    /// <param name="sample">列の値の例</param>
    /// <returns>列の型の値</returns>
    private static object FromDecimal(decimal value, object sample)
    {
        return sample switch
        {
            int => (object)(int)value,
            long => (object)(long)value,
            _ => (object)value,
        };
    }

    /// <summary>
    /// 判定の結果をまとめた文字列（判定ごとの最初の評価の、要素の値と成立。例外になったかも含める）。
    /// ループの条件は、回数で毎回変わらないよう、最初の評価だけを使う
    /// </summary>
    /// <param name="result">評価の結果</param>
    /// <param name="observations">判定の記録</param>
    /// <returns>文字列</returns>
    private static string DecisionSignature(EvaluationResult result, List<ConditionObservation> observations)
    {
        var builder = new System.Text.StringBuilder(result.Kind == EvaluationKind.Threw ? "!" : string.Empty);
        var seen = new HashSet<SyntaxNode>();
        foreach (var observation in observations)
        {
            if (seen.Add(observation.Decision)) builder.Append(observation.Decision.SpanStart).Append(':').Append(Coverage.Signature(observation)).Append(';');
        }

        return builder.ToString();
    }

    /// <summary>
    /// bool の引数が2つ以上あれば、どの2つの組でも true / false の4通りがそろうように行を加える（ペアワイズ）。
    /// ほかの引数は基準の行のまま。基準の行がすべて false に偏らないようにするため
    /// </summary>
    /// <param name="kept">入力（加える先）</param>
    /// <param name="run">入力を評価し、結果と判定の記録を返す処理</param>
    /// <param name="coverage">満たした目標の記録</param>
    private static void AddBooleanPairs(
        List<object?[]> kept, ISet<int> fixedColumns, Func<object?[], (EvaluationResult Result, List<ConditionObservation> Observations)> run, Coverage coverage)
    {
        var baseRow = kept[0];
        var columns = Enumerable.Range(0, baseRow.Length).Where(i => baseRow[i] is bool && !fixedColumns.Contains(i)).ToList();
        if (columns.Count < 2) return;

        var pairs = columns.SelectMany((c, k) => columns.Skip(k + 1).Select(d => (I: c, J: d))).ToList();
        HashSet<(int, int, bool, bool)> Missing() => [.. pairs
            .SelectMany(p => new[] { (p.I, p.J, false, false), (p.I, p.J, false, true), (p.I, p.J, true, false), (p.I, p.J, true, true) })
            .Where(m => !kept.Any(r => r[m.Item1] is bool a && a == m.Item3 && r[m.Item2] is bool b && b == m.Item4))];

        for (var missing = Missing(); 0 < missing.Count && kept.Count < MaxRows; missing = Missing())
        {
            // 足りない組を1つ入れ、残りの bool は、足りない組をできるだけ多く埋める値にする
            var (first, second, firstValue, secondValue) = missing.First();
            var row = (object?[])baseRow.Clone();
            row[first] = firstValue;
            row[second] = secondValue;
            foreach (var column in columns.Where(c => c != first && c != second))
            {
                row[column] = CountFilled(missing, row, column, false) <= CountFilled(missing, row, column, true);
            }

            if (kept.Any(r => r.SequenceEqual(row))) break;

            kept.Add(row);
            coverage.Add(run(row).Observations);
        }
    }

    /// <summary>
    /// 列を value にしたとき、埋まる足りない組の数を返す（値が決まっている列との組だけを数える）
    /// </summary>
    /// <param name="missing">足りない組</param>
    /// <param name="row">作っている行</param>
    /// <param name="column">決める列</param>
    /// <param name="value">値</param>
    /// <returns>埋まる数</returns>
    private static int CountFilled(HashSet<(int, int, bool, bool)> missing, object?[] row, int column, bool value)
    {
        return missing.Count(m =>
            (m.Item1 == column && m.Item3 == value && row[m.Item2] is bool b && b == m.Item4)
            || (m.Item2 == column && m.Item4 == value && row[m.Item1] is bool a && a == m.Item3));
    }

    /// <summary>
    /// 候補を作る。基準の行（最初の行）から引数を1つ変えたもの、2つ変えたもの（値の少ない引数の組から）、ほかの行から引数を1つ変えたものの順。
    /// 引数ごとの値は、入力に出てくる値と、数値なら関数の中の定数と、それぞれの前後
    /// </summary>
    /// <param name="rows">入力</param>
    /// <param name="constants">関数の中の数値の定数</param>
    /// <param name="fixedColumns">値を変えない列</param>
    /// <param name="texts">関数の中の文字列の定数</param>
    /// <returns>候補</returns>
    private static IEnumerable<object?[]> Candidates(
        IReadOnlyList<object?[]> rows, IReadOnlyCollection<long> constants, ISet<int> fixedColumns, IReadOnlyCollection<string> texts)
    {
        var columns = rows[0].Length;
        var values = Enumerable.Range(0, columns).Select(i => fixedColumns.Contains(i) ? [rows[0][i]] : ValuesOf(rows, i, constants, texts)).ToList();
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
    /// <param name="texts">関数の中の文字列の定数</param>
    /// <returns>値</returns>
    private static List<object?> ValuesOf(IReadOnlyList<object?[]> rows, int column, IReadOnlyCollection<long> constants, IReadOnlyCollection<string> texts)
    {
        var values = rows.Select(r => r[column]).Distinct().ToList();
        values.AddRange(values.FirstOrDefault(v => v is not null) switch
        {
            int => constants.Where(c => int.MinValue <= c && c <= int.MaxValue).Select(c => (object?)(int)c),
            long => constants.Select(c => (object?)c),
            decimal => constants.Select(c => (object?)(decimal)c),

            // 文字列は、関数の中の文字列（name.Contains("キティ") の "キティ"）と、その前後に文字を足したもの、null・空・空白
            string => texts.SelectMany(t => new object?[] { t, "a" + t, t + "a" }).Concat([null, string.Empty, " "]),

            // 日付は、今の日付の「日」と曜日のすべての組み合わせ（13日の金曜日など）になる日付
            DateTime => DateCombinations(values.OfType<DateTime>().ToList()).Cast<object?>(),
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
    /// 今の日付に出てくる「日」と、7つの曜日の組み合わせになる日付を、最初の日付から先へ探して返す（13日の金曜日など）
    /// </summary>
    /// <param name="dates">今の日付</param>
    /// <returns>日付</returns>
    private static IEnumerable<DateTime> DateCombinations(List<DateTime> dates)
    {
        if (dates.Count == 0) return [];

        var start = dates.Min();
        if (DateTime.MaxValue.AddYears(-30) < start) return [];

        var result = new List<DateTime>();
        foreach (var day in dates.Select(d => d.Day).Distinct().Where(d => d <= 28))
        {
            var found = new HashSet<DayOfWeek>();
            for (var month = new DateTime(start.Year, start.Month, 1); found.Count < 7 && month.Year < start.Year + 30; month = month.AddMonths(1))
            {
                var date = new DateTime(month.Year, month.Month, day, start.Hour, start.Minute, start.Second, start.Kind);
                if (start <= date && found.Add(date.DayOfWeek)) result.Add(date);
            }
        }

        return result;
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
        private readonly HashSet<SyntaxNode> _implicit = [];
        private readonly Dictionary<SyntaxNode, IReadOnlyList<Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax>> _atoms = [];

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
                _atoms[observation.Decision] = observation.Atoms;
                if (observation.IsImplicit) _implicit.Add(observation.Decision);
            }
        }

        /// <summary>
        /// 満たしていない目標を、テストのコメントに書く説明にする（判定の成立・不成立、要素の成立・不成立、要素だけを変えて結果が変わる組）
        /// </summary>
        /// <returns>説明</returns>
        public IReadOnlyList<string> DescribeMissing()
        {
            var result = new List<string>();
            foreach (var pair in _signatures.Where(p => !_implicit.Contains(p.Key)).OrderBy(p => p.Key.SpanStart))
            {
                var decision = Shorten(pair.Key);
                var observed = pair.Value.Select(Parse).ToList();
                if (!observed.Any(o => o.Outcome)) result.Add(Messages.Get("CoverageNoteDecisionTrue", decision));
                if (!observed.Any(o => !o.Outcome)) result.Add(Messages.Get("CoverageNoteDecisionFalse", decision));
                if (_atomCounts[pair.Key] < 2) continue;

                for (var k = 0; k < _atomCounts[pair.Key]; k++)
                {
                    var atom = Shorten(_atoms[pair.Key][k]);
                    if (!observed.Any(o => o.Values[k] == true)) result.Add(Messages.Get("CoverageNoteAtomTrue", atom, decision));
                    if (!observed.Any(o => o.Values[k] == false)) result.Add(Messages.Get("CoverageNoteAtomFalse", atom, decision));
                    if (!HasIndependencePair(observed, k)) result.Add(Messages.Get("CoverageNoteIndependence", atom, decision));
                }
            }

            return result;
        }

        /// <summary>
        /// 式を1行にし、長ければ省略する
        /// </summary>
        /// <param name="node">式</param>
        /// <returns>文字列</returns>
        private static string Shorten(SyntaxNode node)
        {
            var text = string.Join(" ", node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return text.Length <= 60 ? text : text.Substring(0, 57) + "...";
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
        public static string Signature(ConditionObservation observation)
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
