using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XUnit_IngeniousGenerator.Core.Values;

namespace XUnit_IngeniousGenerator.Core.Evaluation;

/// <summary>
/// 日付・時刻の型（DateTime・DateOnly・TimeSpan）の生成・プロパティ・メソッド・演算を評価する
/// （DateTime.Now など、実行するたびに値が変わるものは対象外）
/// </summary>
internal static class TemporalMethods
{
    /// <summary>
    /// new DateTime(...) などの生成を評価する
    /// </summary>
    /// <param name="creation">生成の式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="evaluate">引数を評価する処理</param>
    /// <returns>作った値</returns>
    public static object Create(BaseObjectCreationExpressionSyntax creation, SemanticModel model, Func<ExpressionSyntax, object?> evaluate)
    {
        var type = model.GetTypeInfo(creation).Type;
        var arguments = creation.ArgumentList?.Arguments.Select(a => evaluate(a.Expression)).ToList() ?? [];
        return Run(creation, () => TemporalValues.Create(type, arguments))
            ?? throw new UnsupportedSyntaxException(Messages.Get("UnsupportedCreation", creation));
    }

    /// <summary>
    /// 日付・時刻の型のプロパティ（date.Year、TimeSpan.Zero など）を読む
    /// </summary>
    /// <param name="memberAccess">メンバーアクセス式</param>
    /// <param name="property">プロパティ</param>
    /// <param name="evaluate">レシーバーを評価する処理</param>
    /// <param name="value">読んだ値</param>
    /// <returns>日付・時刻の型のプロパティならtrue</returns>
    public static bool TryReadMember(MemberAccessExpressionSyntax memberAccess, ISymbol? property, Func<ExpressionSyntax, object?> evaluate, out object? value)
    {
        value = null;
        if (property is not (IPropertySymbol or IFieldSymbol) || TemporalValues.KindOf(property.ContainingType) == TemporalKind.None) return false;

        if (property.IsStatic)
        {
            value = (TemporalValues.KindOf(property.ContainingType), property.Name) switch
            {
                (TemporalKind.DateTime, "MinValue") => DateTime.MinValue,
                (TemporalKind.DateTime, "MaxValue") => DateTime.MaxValue,
                (TemporalKind.DateOnly, "MinValue") => new DateOnlyValue(DateTime.MinValue),
                (TemporalKind.DateOnly, "MaxValue") => new DateOnlyValue(DateTime.MaxValue),
                (TemporalKind.TimeSpan, "Zero") => TimeSpan.Zero,
                (TemporalKind.TimeSpan, "MinValue") => TimeSpan.MinValue,
                (TemporalKind.TimeSpan, "MaxValue") => TimeSpan.MaxValue,
                _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedNonDeterministicMember", memberAccess)),
            };
            return true;
        }

        value = evaluate(memberAccess.Expression) switch
        {
            DateTime date => ReadDate(date, property.Name, memberAccess),
            DateOnlyValue date => property.Name switch
            {
                "DayNumber" => (int)(date.Date.Ticks / TimeSpan.TicksPerDay),
                _ => ReadDate(date.Date, property.Name, memberAccess),
            },
            TimeSpan span => ReadSpan(span, property.Name, memberAccess),
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMember", memberAccess)),
        };
        return true;
    }

    /// <summary>
    /// 日付・時刻の型のメソッド（date.AddDays(1)、TimeSpan.FromHours(2)、DateTime.IsLeapYear(2024) など）を呼ぶ
    /// </summary>
    /// <param name="invocation">呼び出し式</param>
    /// <param name="method">メソッド</param>
    /// <param name="evaluate">レシーバー・引数を評価する処理</param>
    /// <param name="value">戻り値</param>
    /// <returns>日付・時刻の型のメソッドならtrue</returns>
    public static bool TryInvoke(InvocationExpressionSyntax invocation, IMethodSymbol method, Func<ExpressionSyntax, object?> evaluate, out object? value)
    {
        value = null;
        if (TemporalValues.KindOf(method.ContainingType) == TemporalKind.None) return false;

        var arguments = invocation.ArgumentList.Arguments.Select(a => evaluate(a.Expression)).ToList();
        if (method.IsStatic)
        {
            value = Run(invocation, () => InvokeStatic(TemporalValues.KindOf(method.ContainingType), method.Name, arguments))
                ?? throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMethodCall", invocation.Expression));
            return true;
        }

        if (invocation.Expression is not MemberAccessExpressionSyntax receiver) return false;

        var target = evaluate(receiver.Expression);
        value = Run(invocation, () => InvokeInstance(target, method.Name, arguments))
            ?? throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMethodCall", invocation.Expression));
        return true;
    }

    /// <summary>
    /// 日付・時刻の型の二項演算（比較、日付 - 日付、日付 ± 時間の長さ、時間の長さどうしの加減算）を実行する
    /// </summary>
    /// <param name="kind">演算の種類</param>
    /// <param name="left">左辺</param>
    /// <param name="right">右辺</param>
    /// <returns>演算結果</returns>
    public static object Binary(SyntaxKind kind, object? left, object? right)
    {
        return Run(null, () => (kind, left, right) switch
        {
            (SyntaxKind.SubtractExpression or SyntaxKind.SubtractAssignmentExpression, DateTime l, DateTime r) => l - r,
            (SyntaxKind.SubtractExpression or SyntaxKind.SubtractAssignmentExpression, DateTime l, TimeSpan r) => l - r,
            (SyntaxKind.AddExpression or SyntaxKind.AddAssignmentExpression, DateTime l, TimeSpan r) => l + r,
            (SyntaxKind.SubtractExpression or SyntaxKind.SubtractAssignmentExpression, TimeSpan l, TimeSpan r) => l - r,
            (SyntaxKind.AddExpression or SyntaxKind.AddAssignmentExpression, TimeSpan l, TimeSpan r) => l + r,
            (_, DateTime l, DateTime r) => Compare(kind, l.CompareTo(r)),
            (_, DateOnlyValue l, DateOnlyValue r) => Compare(kind, l.CompareTo(r)),
            (_, TimeSpan l, TimeSpan r) => Compare(kind, l.CompareTo(r)),
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedTemporalOperation", kind)),
        })!;
    }

    /// <summary>
    /// 実行時の例外（範囲外の日付など）を、評価中のコードの例外として伝える
    /// </summary>
    /// <param name="site">例外が発生した構文</param>
    /// <param name="action">実行する処理</param>
    /// <returns>処理の結果</returns>
    private static object? Run(SyntaxNode? site, Func<object?> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException ex)
        {
            // ParamName は .NET の内部の名前（value、months など）なので、テストでは確かめない
            throw new GuestThrowException(null, ex.GetType().FullName, null, site);
        }
    }

    /// <summary>
    /// DateTime のプロパティを読む
    /// </summary>
    /// <param name="date">日時</param>
    /// <param name="name">プロパティ名</param>
    /// <param name="site">参照の式</param>
    /// <returns>プロパティの値</returns>
    private static object ReadDate(DateTime date, string name, SyntaxNode site)
    {
        return name switch
        {
            "Year" => (object)date.Year,
            "Month" => date.Month,
            "Day" => date.Day,
            "Hour" => date.Hour,
            "Minute" => date.Minute,
            "Second" => date.Second,
            "Millisecond" => date.Millisecond,
            "DayOfYear" => date.DayOfYear,
            "DayOfWeek" => (int)date.DayOfWeek,
            "Ticks" => date.Ticks,
            "Date" => date.Date,
            "TimeOfDay" => date.TimeOfDay,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMember", site)),
        };
    }

    /// <summary>
    /// TimeSpan のプロパティを読む
    /// </summary>
    /// <param name="span">時間の長さ</param>
    /// <param name="name">プロパティ名</param>
    /// <param name="site">参照の式</param>
    /// <returns>プロパティの値</returns>
    private static object ReadSpan(TimeSpan span, string name, SyntaxNode site)
    {
        // 各腕を object にして、int のプロパティが double に変換されないようにする（switch 式は腕の共通の型になるため）
        return name switch
        {
            "Days" => (object)span.Days,
            "Hours" => span.Hours,
            "Minutes" => span.Minutes,
            "Seconds" => span.Seconds,
            "Milliseconds" => span.Milliseconds,
            "Ticks" => span.Ticks,
            "TotalDays" => span.TotalDays,
            "TotalHours" => span.TotalHours,
            "TotalMinutes" => span.TotalMinutes,
            "TotalSeconds" => span.TotalSeconds,
            "TotalMilliseconds" => span.TotalMilliseconds,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMember", site)),
        };
    }

    /// <summary>
    /// 静的メソッドを呼ぶ
    /// </summary>
    /// <param name="kind">型の種類</param>
    /// <param name="name">メソッド名</param>
    /// <param name="arguments">引数</param>
    /// <returns>戻り値（対応していなければnull）</returns>
    private static object? InvokeStatic(TemporalKind kind, string name, List<object?> arguments)
    {
        return (kind, name, arguments.Count) switch
        {
            (TemporalKind.DateTime, "IsLeapYear", 1) => DateTime.IsLeapYear(ToInt(arguments[0])),
            (TemporalKind.DateTime, "DaysInMonth", 2) => DateTime.DaysInMonth(ToInt(arguments[0]), ToInt(arguments[1])),
            (TemporalKind.DateOnly, "FromDateTime", 1) when arguments[0] is DateTime date => new DateOnlyValue(date),
            (TemporalKind.TimeSpan, "FromDays", 1) => TimeSpan.FromDays(ToDouble(arguments[0])),
            (TemporalKind.TimeSpan, "FromHours", 1) => TimeSpan.FromHours(ToDouble(arguments[0])),
            (TemporalKind.TimeSpan, "FromMinutes", 1) => TimeSpan.FromMinutes(ToDouble(arguments[0])),
            (TemporalKind.TimeSpan, "FromSeconds", 1) => TimeSpan.FromSeconds(ToDouble(arguments[0])),
            (TemporalKind.TimeSpan, "FromMilliseconds", 1) => TimeSpan.FromMilliseconds(ToDouble(arguments[0])),
            _ => null,
        };
    }

    /// <summary>
    /// インスタンスのメソッドを呼ぶ（文字列にする ToString などはカルチャに依存するため対象外）
    /// </summary>
    /// <param name="target">レシーバー</param>
    /// <param name="name">メソッド名</param>
    /// <param name="arguments">引数</param>
    /// <returns>戻り値（対応していなければnull）</returns>
    private static object? InvokeInstance(object? target, string name, List<object?> arguments)
    {
        return (target, name, arguments.Count) switch
        {
            (DateTime date, "AddYears", 1) => date.AddYears(ToInt(arguments[0])),
            (DateTime date, "AddMonths", 1) => date.AddMonths(ToInt(arguments[0])),
            (DateTime date, "AddDays", 1) => date.AddDays(ToDouble(arguments[0])),
            (DateTime date, "AddHours", 1) => date.AddHours(ToDouble(arguments[0])),
            (DateTime date, "AddMinutes", 1) => date.AddMinutes(ToDouble(arguments[0])),
            (DateTime date, "AddSeconds", 1) => date.AddSeconds(ToDouble(arguments[0])),
            (DateTime date, "Add", 1) when arguments[0] is TimeSpan span => date.Add(span),
            (DateTime date, "Subtract", 1) when arguments[0] is TimeSpan span => date.Subtract(span),
            (DateTime date, "Subtract", 1) when arguments[0] is DateTime other => date.Subtract(other),
            (DateOnlyValue date, "AddYears", 1) => new DateOnlyValue(date.Date.AddYears(ToInt(arguments[0]))),
            (DateOnlyValue date, "AddMonths", 1) => new DateOnlyValue(date.Date.AddMonths(ToInt(arguments[0]))),
            (DateOnlyValue date, "AddDays", 1) => new DateOnlyValue(date.Date.AddDays(ToInt(arguments[0]))),
            (TimeSpan span, "Add", 1) when arguments[0] is TimeSpan other => span.Add(other),
            (TimeSpan span, "Subtract", 1) when arguments[0] is TimeSpan other => span.Subtract(other),
            (TimeSpan span, "Negate", 0) => span.Negate(),
            (TimeSpan span, "Duration", 0) => span.Duration(),
            (null, _, _) => throw new GuestThrowException(null, "System.NullReferenceException", null, null),
            _ => null,
        };
    }

    /// <summary>
    /// 比較結果（CompareTo の値）から、比較演算の結果を返す
    /// </summary>
    /// <param name="kind">比較の種類</param>
    /// <param name="comparison">CompareTo の結果</param>
    /// <returns>比較演算の結果</returns>
    private static object Compare(SyntaxKind kind, int comparison)
    {
        return kind switch
        {
            SyntaxKind.EqualsExpression => comparison == 0,
            SyntaxKind.NotEqualsExpression => comparison != 0,
            SyntaxKind.LessThanExpression => comparison < 0,
            SyntaxKind.LessThanOrEqualExpression => comparison <= 0,
            SyntaxKind.GreaterThanExpression => 0 < comparison,
            SyntaxKind.GreaterThanOrEqualExpression => 0 <= comparison,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedTemporalOperation", kind)),
        };
    }

    /// <summary>
    /// 整数の引数を int として取り出す
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>int の値</returns>
    private static int ToInt(object? value)
    {
        return value is int i ? i : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedNonIntArgument"));
    }

    /// <summary>
    /// 数値の引数を double として取り出す
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>double の値</returns>
    private static double ToDouble(object? value)
    {
        return ClrOperations.IsNumber(value)
            ? System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)
            : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedNonNumericArgument"));
    }
}
