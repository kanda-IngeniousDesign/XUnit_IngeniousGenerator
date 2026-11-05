using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace XUnit_IngeniousGenerator.Core.Values;

/// <summary>
/// 日付・時刻の型の種類
/// </summary>
public enum TemporalKind
{
    /// <summary>日付・時刻の型ではない</summary>
    None,

    /// <summary>DateTime</summary>
    DateTime,

    /// <summary>DateOnly</summary>
    DateOnly,

    /// <summary>TimeSpan</summary>
    TimeSpan,
}

/// <summary>
/// テストデータとして渡す DateOnly（Core は netstandard2.0 で DateOnly 型を使えないため、日付部分の DateTime で持つ）
/// </summary>
public sealed class DateOnlyValue : IEquatable<DateOnlyValue>, IComparable<DateOnlyValue>
{
    /// <summary>
    /// DateOnly の値を作成する
    /// </summary>
    /// <param name="date">日付（時刻は切り捨てる）</param>
    public DateOnlyValue(DateTime date)
    {
        Date = date.Date;
    }

    /// <summary>日付</summary>
    public DateTime Date { get; }

    /// <summary>
    /// 同じ日付かを判定する
    /// </summary>
    /// <param name="other">比べる値</param>
    /// <returns>同じならtrue</returns>
    public bool Equals(DateOnlyValue? other)
    {
        return other is not null && Date == other.Date;
    }

    /// <summary>
    /// 日付の前後を比べる
    /// </summary>
    /// <param name="other">比べる値</param>
    /// <returns>この値が前なら負、同じなら0、後なら正</returns>
    public int CompareTo(DateOnlyValue? other)
    {
        return other is null ? 1 : Date.CompareTo(other.Date);
    }

    /// <summary>
    /// 同じ値かを判定する
    /// </summary>
    /// <param name="obj">比べる値</param>
    /// <returns>同じならtrue</returns>
    public override bool Equals(object? obj)
    {
        return Equals(obj as DateOnlyValue);
    }

    /// <summary>
    /// ハッシュ値を返す
    /// </summary>
    /// <returns>ハッシュ値</returns>
    public override int GetHashCode()
    {
        return Date.GetHashCode();
    }

    /// <summary>
    /// 表示用の文字列を返す
    /// </summary>
    /// <returns>表示用の文字列</returns>
    public override string ToString()
    {
        return Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// 日付・時刻の型（DateTime・DateOnly・TimeSpan）の基準の値・境界値・端の値と、テストコードへの書き出し
/// </summary>
public static class TemporalValues
{
    /// <summary>DateOnly の最小値（0001-01-01）</summary>
    private static readonly DateOnlyValue DateOnlyMin = new(DateTime.MinValue);

    /// <summary>DateOnly の最大値（9999-12-31）</summary>
    private static readonly DateOnlyValue DateOnlyMax = new(DateTime.MaxValue);

    /// <summary>
    /// 型が日付・時刻のどの型かを返す
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>日付・時刻の型の種類</returns>
    public static TemporalKind KindOf(ITypeSymbol? type)
    {
        if (type?.SpecialType == SpecialType.System_DateTime) return TemporalKind.DateTime;

        return type?.ToDisplayString() switch
        {
            "System.DateOnly" => TemporalKind.DateOnly,
            "System.TimeSpan" => TemporalKind.TimeSpan,
            _ => TemporalKind.None,
        };
    }

    /// <summary>
    /// 値が日付・時刻の型（DateTime・DateOnly・TimeSpan）の値かを判定する
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>日付・時刻の値ならtrue</returns>
    public static bool IsTemporal(object? value)
    {
        return value is DateTime or DateOnlyValue or TimeSpan;
    }

    /// <summary>
    /// 基準の値を返す（日付は 2026-01-01、時間の長さは 1時間）
    /// </summary>
    /// <param name="kind">型の種類</param>
    /// <returns>基準の値</returns>
    public static object BaseValue(TemporalKind kind)
    {
        return kind switch
        {
            TemporalKind.DateTime => new DateTime(2026, 1, 1),
            TemporalKind.DateOnly => new DateOnlyValue(new DateTime(2026, 1, 1)),
            _ => TimeSpan.FromHours(1),
        };
    }

    /// <summary>
    /// 境界値の候補を作る。比べている値があれば、その前後（日付は ±1日・±1秒、DateOnly は ±1日、時間の長さは ±1秒）。
    /// なければ代表値（日付はうるう日 2024-02-29 と年末 2026-12-31、時間の長さは 0 と -1秒）
    /// </summary>
    /// <param name="kind">型の種類</param>
    /// <param name="constants">本体で比べている値</param>
    /// <returns>境界値の候補（型に収まらない値は除く）</returns>
    public static List<object?> Variations(TemporalKind kind, IEnumerable<object> constants)
    {
        var compared = constants.Where(c => KindOfValue(c) == kind).ToList();
        var exact = constants.OfType<ExactValue>().Select(e => e.Value).Where(v => KindOfValue(v) == kind).ToList();
        IEnumerable<object> candidates = compared.Count == 0 && exact.Count == 0
            ? Representatives(kind)
            : compared.SelectMany(c => Around(kind, c)).Concat(exact);
        return candidates.Distinct().OrderBy(Order).Cast<object?>().ToList();
    }

    /// <summary>
    /// 日付・時刻のプロパティ（date.Year、date.Month、date.DayOfWeek など）と比べている定数から、その前後になる日付・時刻を作る
    /// （date.Month == 2 なら、基準の年の 1月・2月・3月の1日。date.DayOfWeek == Saturday なら、基準の週の金・土・日曜日）
    /// </summary>
    /// <param name="kind">引数の型の種類</param>
    /// <param name="property">プロパティ名</param>
    /// <param name="constant">比べている定数</param>
    /// <returns>日付・時刻（境界値の定数として、前後にずらさずにそのまま使う）</returns>
    public static IEnumerable<object> FromProperty(TemporalKind kind, string property, long constant)
    {
        var baseDate = new DateTime(2026, 1, 1);
        foreach (var number in new[] { constant - 1, constant, constant + 1 })
        {
            DateTime? date = (property, kind) switch
            {
                ("Year", not TemporalKind.TimeSpan) when 1 <= number && number <= 9999 => new DateTime((int)number, 1, 1),
                ("Month", not TemporalKind.TimeSpan) when 1 <= number && number <= 12 => new DateTime(baseDate.Year, (int)number, 1),
                ("Day", not TemporalKind.TimeSpan) when 1 <= number && number <= 31 => baseDate.AddDays(number - 1),
                ("DayOfWeek", not TemporalKind.TimeSpan) => baseDate.AddDays((((number - (int)baseDate.DayOfWeek) % 7) + 7) % 7),
                ("Hour", TemporalKind.DateTime) when 0 <= number && number <= 23 => baseDate.AddHours(number),
                ("Minute", TemporalKind.DateTime) when 0 <= number && number <= 59 => baseDate.AddMinutes(number),
                _ => null,
            };
            if (date is null) continue;

            yield return new ExactValue(kind == TemporalKind.DateOnly ? new DateOnlyValue(date.Value) : date.Value);
        }

        if (kind != TemporalKind.TimeSpan) yield break;

        // 時間の長さは、日・時間・分・秒の単位の値の前後にする
        foreach (var number in new[] { constant - 1, constant, constant + 1 })
        {
            TimeSpan? span = property switch
            {
                "Days" or "TotalDays" => TimeSpan.FromDays(number),
                "Hours" or "TotalHours" => TimeSpan.FromHours(number),
                "Minutes" or "TotalMinutes" => TimeSpan.FromMinutes(number),
                "Seconds" or "TotalSeconds" => TimeSpan.FromSeconds(number),
                _ => null,
            };
            if (span is not null) yield return new ExactValue(span.Value);
        }
    }

    /// <summary>
    /// 前後にずらさずにそのまま境界値に使う日付・時刻（プロパティとの比較から作ったもの）
    /// </summary>
    public sealed class ExactValue
    {
        /// <summary>
        /// 値を作成する
        /// </summary>
        /// <param name="value">日付・時刻の値</param>
        public ExactValue(object value)
        {
            Value = value;
        }

        /// <summary>日付・時刻の値</summary>
        public object Value { get; }

        /// <summary>
        /// 同じ値かを判定する（定数の重複を除くため）
        /// </summary>
        /// <param name="obj">比べる値</param>
        /// <returns>同じならtrue</returns>
        public override bool Equals(object? obj)
        {
            return obj is ExactValue other && Value.Equals(other.Value);
        }

        /// <summary>
        /// ハッシュ値を返す
        /// </summary>
        /// <returns>ハッシュ値</returns>
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }
    }

    /// <summary>
    /// 型の端の値（MinValue・MaxValue）を返す
    /// </summary>
    /// <param name="kind">型の種類</param>
    /// <returns>端の値</returns>
    public static IReadOnlyList<object?> Extremes(TemporalKind kind)
    {
        return kind switch
        {
            TemporalKind.DateTime => [DateTime.MinValue, DateTime.MaxValue],
            TemporalKind.DateOnly => [DateOnlyMin, DateOnlyMax],
            TemporalKind.TimeSpan => [TimeSpan.MinValue, TimeSpan.MaxValue],
            _ => [],
        };
    }

    /// <summary>
    /// 値を pivot の前後（-1, ±0, +1 の単位）にずらした値を返す（引数どうしの比較に使う）
    /// </summary>
    /// <param name="pivot">基準の値</param>
    /// <returns>ずらした値（型に収まらない値は除く）</returns>
    public static IEnumerable<object> Around(object pivot)
    {
        return Around(KindOfValue(pivot), pivot);
    }

    /// <summary>
    /// 端の値なら、テスト名の条件部分（IsMinValue / IsMaxValue）にする
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>条件部分（端の値でなければnull）</returns>
    public static string? DescribeExtreme(object? value)
    {
        return ExtremeName(value) is { } name ? "Is" + name : null;
    }

    /// <summary>
    /// 値を式にする（端の値は DateTime.MinValue、0 の時間は TimeSpan.Zero、それ以外は new DateTime(2026, 1, 1) など）
    /// </summary>
    /// <param name="value">日付・時刻の値</param>
    /// <returns>式の構文</returns>
    public static ExpressionSyntax Render(object value)
    {
        var typeName = KindOfValue(value).ToString();
        if (ExtremeName(value) is { } extreme) return StaticMember(typeName, extreme);

        return value switch
        {
            DateTime date => New(typeName, DateArguments(date)),
            DateOnlyValue date => New(typeName, [date.Date.Year, date.Date.Month, date.Date.Day]),
            TimeSpan span when span == TimeSpan.Zero => StaticMember(typeName, "Zero"),
            TimeSpan span when span.Ticks % TimeSpan.TicksPerSecond != 0 => FromTicks(span.Ticks),
            TimeSpan span => New(typeName, [span.Days, span.Hours, span.Minutes, span.Seconds]),
            _ => throw new ArgumentException($"未対応の値の型です: {value.GetType()}", nameof(value)),
        };
    }

    /// <summary>
    /// 本体に書かれた日付・時刻の値（new DateTime(2026, 6, 1)、TimeSpan.FromDays(1) など、引数がすべて定数のもの）を求める
    /// </summary>
    /// <param name="expression">式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="value">求めた値</param>
    /// <returns>求められたらtrue</returns>
    public static bool TryGetConstant(ExpressionSyntax expression, SemanticModel model, out object value)
    {
        value = null!;
        var arguments = expression switch
        {
            BaseObjectCreationExpressionSyntax creation => creation.ArgumentList?.Arguments,
            InvocationExpressionSyntax invocation => invocation.ArgumentList.Arguments,
            _ => null,
        };
        if (arguments is null) return false;

        var constants = arguments.Value.Select(a => model.GetConstantValue(a.Expression)).ToList();
        if (constants.Any(c => !c.HasValue || c.Value is null)) return false;

        try
        {
            var result = expression is InvocationExpressionSyntax
                ? InvokeStatic(model.GetSymbolInfo(expression).Symbol as IMethodSymbol, constants.Select(c => c.Value).ToList())
                : Create(model.GetTypeInfo(expression).Type, constants.Select(c => c.Value).ToList());
            if (result is null) return false;

            value = result;
            return true;
        }
        catch (ArgumentException)
        {
            // 範囲外の日付など、実行時にも例外になる値は境界値にしない
            return false;
        }
    }

    /// <summary>
    /// コンストラクタ（new DateTime(年, 月, 日) など）で値を作る
    /// </summary>
    /// <param name="type">作る型</param>
    /// <param name="arguments">コンストラクタの引数（評価済み）</param>
    /// <returns>作った値（対応していないコンストラクタならnull）</returns>
    public static object? Create(ITypeSymbol? type, IReadOnlyList<object?> arguments)
    {
        var numbers = arguments.Select(a => a is int or long ? System.Convert.ToInt64(a, System.Globalization.CultureInfo.InvariantCulture) : (long?)null).ToList();
        if (numbers.Any(n => n is null)) return null;

        var n = numbers.Select(x => x!.Value).ToList();
        return (KindOf(type), n.Count) switch
        {
            (TemporalKind.DateTime, 1) => new DateTime(n[0]),
            (TemporalKind.DateTime, 3) => new DateTime((int)n[0], (int)n[1], (int)n[2]),
            (TemporalKind.DateTime, 6) => new DateTime((int)n[0], (int)n[1], (int)n[2], (int)n[3], (int)n[4], (int)n[5]),
            (TemporalKind.DateOnly, 3) => new DateOnlyValue(new DateTime((int)n[0], (int)n[1], (int)n[2])),
            (TemporalKind.TimeSpan, 1) => new TimeSpan(n[0]),
            (TemporalKind.TimeSpan, 3) => new TimeSpan((int)n[0], (int)n[1], (int)n[2]),
            (TemporalKind.TimeSpan, 4) => new TimeSpan((int)n[0], (int)n[1], (int)n[2], (int)n[3]),
            _ => null,
        };
    }

    /// <summary>
    /// TimeSpan.FromDays(1) などの静的メソッドで値を作る
    /// </summary>
    /// <param name="method">呼び出すメソッド</param>
    /// <param name="arguments">引数（評価済み）</param>
    /// <returns>作った値（対応していないメソッドならnull）</returns>
    private static object? InvokeStatic(IMethodSymbol? method, IReadOnlyList<object?> arguments)
    {
        if (method is null || !method.IsStatic || KindOf(method.ContainingType) != TemporalKind.TimeSpan || arguments.Count != 1) return null;

        var amount = System.Convert.ToDouble(arguments[0], System.Globalization.CultureInfo.InvariantCulture);
        return method.Name switch
        {
            "FromDays" => TimeSpan.FromDays(amount),
            "FromHours" => TimeSpan.FromHours(amount),
            "FromMinutes" => TimeSpan.FromMinutes(amount),
            "FromSeconds" => TimeSpan.FromSeconds(amount),
            _ => null,
        };
    }

    /// <summary>
    /// 値の型の種類を返す
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>型の種類</returns>
    private static TemporalKind KindOfValue(object? value)
    {
        return value switch
        {
            DateTime => TemporalKind.DateTime,
            DateOnlyValue => TemporalKind.DateOnly,
            TimeSpan => TemporalKind.TimeSpan,
            _ => TemporalKind.None,
        };
    }

    /// <summary>
    /// 比べている値がないときの代表値
    /// </summary>
    /// <param name="kind">型の種類</param>
    /// <returns>代表値</returns>
    private static IEnumerable<object> Representatives(TemporalKind kind)
    {
        return kind switch
        {
            TemporalKind.DateTime => [new DateTime(2024, 2, 29), new DateTime(2026, 12, 31)],
            TemporalKind.DateOnly => [new DateOnlyValue(new DateTime(2024, 2, 29)), new DateOnlyValue(new DateTime(2026, 12, 31))],
            _ => [TimeSpan.Zero, TimeSpan.FromSeconds(-1)],
        };
    }

    /// <summary>
    /// 比べている値とその前後の値を返す（型に収まらない値は除く）
    /// </summary>
    /// <param name="kind">型の種類</param>
    /// <param name="pivot">比べている値</param>
    /// <returns>前後の値</returns>
    private static IEnumerable<object> Around(TemporalKind kind, object pivot)
    {
        var steps = kind switch
        {
            TemporalKind.DateTime => new[] { -TimeSpan.TicksPerDay, -TimeSpan.TicksPerSecond, 0, TimeSpan.TicksPerSecond, TimeSpan.TicksPerDay },
            TemporalKind.DateOnly => [-TimeSpan.TicksPerDay, 0, TimeSpan.TicksPerDay],
            _ => [-TimeSpan.TicksPerSecond, 0, TimeSpan.TicksPerSecond],
        };

        foreach (var step in steps)
        {
            var ticks = TicksOf(pivot);
            var (min, max) = kind == TemporalKind.TimeSpan ? (TimeSpan.MinValue.Ticks, TimeSpan.MaxValue.Ticks) : (DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
            if ((step < 0 && ticks < min - step) || (0 < step && max - step < ticks)) continue;

            yield return kind switch
            {
                TemporalKind.DateTime => new DateTime(ticks + step),
                TemporalKind.DateOnly => new DateOnlyValue(new DateTime(ticks + step)),
                _ => new TimeSpan(ticks + step),
            };
        }
    }

    /// <summary>
    /// 並べ替え用の値（tick）を返す
    /// </summary>
    /// <param name="value">日付・時刻の値</param>
    /// <returns>tick</returns>
    private static long Order(object value)
    {
        return TicksOf(value);
    }

    /// <summary>
    /// 値の tick を返す
    /// </summary>
    /// <param name="value">日付・時刻の値</param>
    /// <returns>tick</returns>
    private static long TicksOf(object value)
    {
        return value switch
        {
            DateTime date => date.Ticks,
            DateOnlyValue date => date.Date.Ticks,
            TimeSpan span => span.Ticks,
            _ => 0,
        };
    }

    /// <summary>
    /// 端の値なら、メンバー名（MinValue / MaxValue）を返す
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>メンバー名（端の値でなければnull）</returns>
    private static string? ExtremeName(object? value)
    {
        return value switch
        {
            DateTime date when date == DateTime.MinValue => "MinValue",
            DateTime date when date == DateTime.MaxValue => "MaxValue",
            DateOnlyValue date when date.Equals(DateOnlyMin) => "MinValue",
            DateOnlyValue date when date.Equals(DateOnlyMax) => "MaxValue",
            TimeSpan span when span == TimeSpan.MinValue => "MinValue",
            TimeSpan span when span == TimeSpan.MaxValue => "MaxValue",
            _ => null,
        };
    }

    /// <summary>
    /// DateTime のコンストラクタの引数（時刻が 0 時なら年月日だけ、秒未満があれば tick）を返す
    /// </summary>
    /// <param name="date">日時</param>
    /// <returns>コンストラクタの引数</returns>
    private static long[] DateArguments(DateTime date)
    {
        if (date.Ticks % TimeSpan.TicksPerSecond != 0) return [date.Ticks];
        if (date.TimeOfDay == TimeSpan.Zero) return [date.Year, date.Month, date.Day];
        return [date.Year, date.Month, date.Day, date.Hour, date.Minute, date.Second];
    }

    /// <summary>
    /// 型の静的メンバー（DateTime.MinValue など）の式を作る
    /// </summary>
    /// <param name="typeName">型名</param>
    /// <param name="member">メンバー名</param>
    /// <returns>式の構文</returns>
    private static ExpressionSyntax StaticMember(string typeName, string member)
    {
        return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName(typeName), IdentifierName(member));
    }

    /// <summary>
    /// new 型名(引数) の式を作る（負の数は単項マイナスを付ける）
    /// </summary>
    /// <param name="typeName">型名</param>
    /// <param name="arguments">引数</param>
    /// <returns>式の構文</returns>
    private static ExpressionSyntax New(string typeName, IEnumerable<long> arguments)
    {
        var list = arguments.Select(a => Argument(Number(a)));
        return ObjectCreationExpression(IdentifierName(typeName)).WithArgumentList(ArgumentList(SeparatedList(list)));
    }

    /// <summary>
    /// TimeSpan.FromTicks(n) の式を作る
    /// </summary>
    /// <param name="ticks">tick</param>
    /// <returns>式の構文</returns>
    private static ExpressionSyntax FromTicks(long ticks)
    {
        return InvocationExpression(StaticMember(nameof(TemporalKind.TimeSpan), "FromTicks"))
            .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(Number(ticks)))));
    }

    /// <summary>
    /// 整数のリテラルを作る（int に収まらなければ L を付ける）
    /// </summary>
    /// <param name="number">整数</param>
    /// <returns>式の構文</returns>
    private static ExpressionSyntax Number(long number)
    {
        var magnitude = number == long.MinValue ? (ulong)long.MaxValue + 1 : (ulong)Math.Abs(number);
        var text = magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var token = magnitude <= int.MaxValue ? Literal(text, (int)magnitude) : Literal(text + "L", magnitude);
        var literal = LiteralExpression(SyntaxKind.NumericLiteralExpression, token);
        return number < 0 ? PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, literal) : literal;
    }
}
