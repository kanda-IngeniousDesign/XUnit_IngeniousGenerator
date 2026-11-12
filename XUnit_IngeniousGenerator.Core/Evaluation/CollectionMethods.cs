using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XUnit_IngeniousGenerator.Core.Values;

namespace XUnit_IngeniousGenerator.Core.Evaluation;

/// <summary>
/// List&lt;T&gt;・配列のメソッドと、LINQ（Where・Select・Sum など）を評価する。
/// LINQ の結果は、その場ですべての要素を求めた List として持つ（遅延評価は再現しない）
/// </summary>
internal static class CollectionMethods
{
    // 評価で作るコレクションの要素数の上限（IDE の解析の中で、巨大なコレクションを作らないようにする）
    private const int MaxCount = 10_000;

    /// <summary>
    /// 値の要素を順に返す（List・配列・文字列。null なら実行時と同じく NullReferenceException）
    /// </summary>
    /// <param name="value">値</param>
    /// <param name="site">列挙している構文</param>
    /// <returns>要素</returns>
    public static IReadOnlyList<object?> Enumerate(object? value, SyntaxNode site)
    {
        return value switch
        {
            null => throw new GuestThrowException(null, "System.NullReferenceException", null, site),
            ListValue list => list.Items.ToList(),
            string text => text.Select(c => (object?)c).ToList(),
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMember", site)),
        };
    }

    /// <summary>
    /// List・配列のプロパティ（Count・Length）を読む
    /// </summary>
    /// <param name="receiver">List・配列</param>
    /// <param name="name">プロパティ名</param>
    /// <param name="value">値</param>
    /// <returns>読めたらtrue</returns>
    public static bool TryReadMember(object? receiver, string name, out object? value)
    {
        value = (receiver, name) switch
        {
            (ListValue list, "Count" or "Length") => list.Items.Count,
            _ => null,
        };
        return value is not null;
    }

    /// <summary>
    /// 添字で要素を読む（範囲外は、List なら ArgumentOutOfRangeException、配列なら IndexOutOfRangeException）
    /// </summary>
    /// <param name="list">List・配列</param>
    /// <param name="index">添字</param>
    /// <param name="site">要素アクセスの構文</param>
    /// <returns>要素</returns>
    public static object? GetItem(ListValue list, object? index, SyntaxNode site)
    {
        var i = ToIndex(index);
        if (i < 0 || list.Items.Count <= i) throw OutOfRange(list, site);
        return list.Items[i];
    }

    /// <summary>
    /// 添字で要素を書く
    /// </summary>
    /// <param name="list">List・配列</param>
    /// <param name="index">添字</param>
    /// <param name="value">値</param>
    /// <param name="site">要素アクセスの構文</param>
    public static void SetItem(ListValue list, object? index, object? value, SyntaxNode site)
    {
        var i = ToIndex(index);
        if (i < 0 || list.Items.Count <= i) throw OutOfRange(list, site);
        list.Items[i] = value;
    }

    /// <summary>
    /// List のメソッド（Add・Contains・Remove など）を呼ぶ
    /// </summary>
    /// <param name="list">List</param>
    /// <param name="name">メソッド名</param>
    /// <param name="arguments">引数（評価済み）</param>
    /// <param name="site">呼び出し式</param>
    /// <param name="value">戻り値（void のメソッドはnull）</param>
    /// <returns>対応しているメソッドならtrue</returns>
    public static bool TryInvokeList(ListValue list, string name, IReadOnlyList<object?> arguments, SyntaxNode site, out object? value)
    {
        value = null;
        switch (name, arguments.Count)
        {
            case ("Add", 1) when !list.IsArray:
                Grow(list, 1);
                list.Items.Add(arguments[0]);
                return true;
            case ("AddRange", 1) when !list.IsArray:
                var added = Enumerate(arguments[0], site);
                Grow(list, added.Count);
                list.Items.AddRange(added);
                return true;
            case ("Insert", 2) when !list.IsArray:
                var at = ToIndex(arguments[0]);
                if (at < 0 || list.Items.Count < at) throw OutOfRange(list, site);
                Grow(list, 1);
                list.Items.Insert(at, arguments[1]);
                return true;
            case ("Clear", 0) when !list.IsArray:
                list.Items.Clear();
                return true;
            case ("Contains", 1):
                value = 0 <= IndexOf(list.Items, arguments[0]);
                return true;
            case ("IndexOf", 1):
                value = IndexOf(list.Items, arguments[0]);
                return true;
            case ("Remove", 1) when !list.IsArray:
                var index = IndexOf(list.Items, arguments[0]);
                if (0 <= index) list.Items.RemoveAt(index);
                value = 0 <= index;
                return true;
            case ("RemoveAt", 1) when !list.IsArray:
                var removeAt = ToIndex(arguments[0]);
                if (removeAt < 0 || list.Items.Count <= removeAt) throw OutOfRange(list, site);
                list.Items.RemoveAt(removeAt);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// LINQ のメソッドを呼ぶ
    /// </summary>
    /// <param name="method">メソッド（拡張メソッドの形）</param>
    /// <param name="source">対象のコレクション（評価済み）</param>
    /// <param name="arguments">対象以外の引数の式</param>
    /// <param name="evaluate">式を評価する処理</param>
    /// <param name="invokeLambda">ラムダ式を引数つきで評価する処理</param>
    /// <param name="site">呼び出し式</param>
    /// <param name="value">戻り値</param>
    /// <returns>対応しているメソッドならtrue</returns>
    public static bool TryInvokeLinq(
        IMethodSymbol method, object? source, IReadOnlyList<ExpressionSyntax> arguments, Func<ExpressionSyntax, object?> evaluate,
        Func<ExpressionSyntax, object?, object?> invokeLambda, SyntaxNode site, out object? value)
    {
        value = null;
        var original = method.ReducedFrom ?? method;
        if (original.ContainingType.ToDisplayString() != "System.Linq.Enumerable") return false;

        var items = Enumerate(source, site);
        var elementType = ElementTypeOf(method.ReturnType) ?? (source as ListValue)?.ElementType;
        Func<object?, object?> lambda = item => invokeLambda(arguments[0], item);
        bool Test(object? item) => invokeLambda(arguments[0], item) is true;

        value = (method.Name, arguments.Count) switch
        {
            ("Count", 0) => items.Count,
            ("Count", 1) => items.Count(Test),
            ("Any", 0) => items.Count != 0,
            ("Any", 1) => items.Any(Test),
            ("All", 1) => items.All(Test),
            ("Contains", 1) => 0 <= IndexOf(items, evaluate(arguments[0])),
            ("Where", 1) => new ListValue(elementType, items.Where(Test)),
            ("Select", 1) => new ListValue(elementType, items.Select(lambda)),
            ("ToList" or "ToArray" or "AsEnumerable", 0) => new ListValue(elementType, items, method.Name == "ToArray"),
            ("Distinct", 0) => new ListValue(elementType, Distinct(items)),
            ("Skip", 1) => new ListValue(elementType, items.Skip(ToIndex(evaluate(arguments[0])))),
            ("Take", 1) => new ListValue(elementType, items.Take(ToIndex(evaluate(arguments[0])))),
            ("Reverse", 0) => new ListValue(elementType, Enumerable.Reverse(items)),
            ("OrderBy" or "OrderByDescending", 1) => new ListValue(elementType, Order(items, lambda, method.Name == "OrderByDescending")),
            _ => Aggregate(method, arguments.Count == 0 ? items : null, arguments.Count == 0 ? null : items, Test, lambda, site),
        };
        return true;
    }

    /// <summary>
    /// 要素を1つにまとめる LINQ（First・Sum・Min・Average など）を評価する
    /// </summary>
    /// <param name="method">メソッド</param>
    /// <param name="items">引数のない呼び出しの要素</param>
    /// <param name="withArgument">引数（条件・選択）のある呼び出しの要素</param>
    /// <param name="test">条件のラムダ式を評価する処理</param>
    /// <param name="select">選択のラムダ式を評価する処理</param>
    /// <param name="site">呼び出し式</param>
    /// <returns>結果</returns>
    private static object? Aggregate(
        IMethodSymbol method, IReadOnlyList<object?>? items, IReadOnlyList<object?>? withArgument, Func<object?, bool> test, Func<object?, object?> select, SyntaxNode site)
    {
        // 引数のラムダ式は、条件（First など）か選択（Sum など）のどちらか一方としてだけ評価する
        IReadOnlyList<object?> Filtered() => items ?? withArgument!.Where(test).ToList();
        IReadOnlyList<object?> Selected() => items ?? withArgument!.Select(select).ToList();
        return method.Name switch
        {
            "First" or "Last" or "Single" => Pick(method.Name, Filtered(), site),
            "FirstOrDefault" or "LastOrDefault" => PickOrDefault(method.Name, Filtered(), method.ReturnType),
            "Sum" => Sum(Selected(), method.ReturnType),
            "Min" or "Max" => MinMax(method.Name, Selected(), site),
            "Average" => Average(Selected(), method.ReturnType, site),
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMethodCall", site)),
        };
    }

    /// <summary>
    /// コレクションの型の要素の型を返す（IEnumerable&lt;T&gt;・List&lt;T&gt;・T[] など）
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>要素の型（コレクションでなければnull）</returns>
    public static ITypeSymbol? ElementTypeOf(ITypeSymbol? type)
    {
        return type switch
        {
            IArrayTypeSymbol array => array.ElementType,
            INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named
                when named.AllInterfaces.Append(named).Any(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) =>
                named.TypeArguments[0],
            _ => null,
        };
    }

    /// <summary>
    /// 要素の数を増やせるか確かめる（上限を超えたら評価をやめる）
    /// </summary>
    /// <param name="list">List</param>
    /// <param name="count">増やす数</param>
    private static void Grow(ListValue list, int count)
    {
        if (MaxCount < list.Items.Count + count) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedTooManySteps"));
    }

    /// <summary>
    /// 添字を int として取り出す
    /// </summary>
    /// <param name="index">添字</param>
    /// <returns>添字</returns>
    private static int ToIndex(object? index)
    {
        return index is int i ? i : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedNonIntArgument"));
    }

    /// <summary>
    /// 範囲外の添字の例外を作る
    /// </summary>
    /// <param name="list">List・配列</param>
    /// <param name="site">構文</param>
    /// <returns>例外</returns>
    private static GuestThrowException OutOfRange(ListValue list, SyntaxNode site)
    {
        return new GuestThrowException(null, list.IsArray ? "System.IndexOutOfRangeException" : "System.ArgumentOutOfRangeException", null, site);
    }

    /// <summary>
    /// 値が等しい最初の要素の位置を返す
    /// </summary>
    /// <param name="items">要素</param>
    /// <param name="value">探す値</param>
    /// <returns>位置（なければ -1）</returns>
    private static int IndexOf(IReadOnlyList<object?> items, object? value)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ClrOperations.ValuesEqual(items[i], value)) return i;
        }

        return -1;
    }

    /// <summary>
    /// 重複を除く（最初に出たものを残す）
    /// </summary>
    /// <param name="items">要素</param>
    /// <returns>重複を除いた要素</returns>
    private static List<object?> Distinct(IReadOnlyList<object?> items)
    {
        var result = new List<object?>();
        foreach (var item in items)
        {
            if (IndexOf(result, item) < 0) result.Add(item);
        }

        return result;
    }

    /// <summary>
    /// First・Last・Single を評価する（要素がない・Single で2件以上なら InvalidOperationException）
    /// </summary>
    /// <param name="name">メソッド名</param>
    /// <param name="items">要素</param>
    /// <param name="site">呼び出し式</param>
    /// <returns>要素</returns>
    private static object? Pick(string name, IReadOnlyList<object?> items, SyntaxNode site)
    {
        if (items.Count == 0 || (name == "Single" && items.Count != 1)) throw new GuestThrowException(null, "System.InvalidOperationException", null, site);
        return name == "Last" ? items[items.Count - 1] : items[0];
    }

    /// <summary>
    /// FirstOrDefault・LastOrDefault を評価する（要素がなければ型の既定値）
    /// </summary>
    /// <param name="name">メソッド名</param>
    /// <param name="items">要素</param>
    /// <param name="type">戻り値の型</param>
    /// <returns>要素、または既定値</returns>
    private static object? PickOrDefault(string name, IReadOnlyList<object?> items, ITypeSymbol type)
    {
        if (items.Count != 0) return name == "LastOrDefault" ? items[items.Count - 1] : items[0];

        var value = InstanceState.DefaultValue(type);
        return ReferenceEquals(value, InstanceState.Unknown) ? throw new UnsupportedSyntaxException(Messages.Get("UnsupportedConversion", type)) : value;
    }

    /// <summary>
    /// Sum を評価する（int・long は実行時と同じくオーバーフローで OverflowException）
    /// </summary>
    /// <param name="items">要素</param>
    /// <param name="type">戻り値の型</param>
    /// <returns>合計</returns>
    private static object Sum(IReadOnlyList<object?> items, ITypeSymbol type)
    {
        return type.SpecialType switch
        {
            SpecialType.System_Int32 => items.Aggregate(0, (sum, item) => checked(sum + (int)ClrOperations.Convert(item, type)!)),
            SpecialType.System_Int64 => items.Aggregate(0L, (sum, item) => checked(sum + (long)ClrOperations.Convert(item, type)!)),
            SpecialType.System_Decimal => items.Aggregate(0M, (sum, item) => sum + (decimal)ClrOperations.Convert(item, type)!),
            SpecialType.System_Double => items.Aggregate(0D, (sum, item) => sum + (double)ClrOperations.Convert(item, type)!),
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedTypeOperation", type)),
        };
    }

    /// <summary>
    /// Min・Max を評価する（数値・日付だけ。要素がなければ InvalidOperationException）
    /// </summary>
    /// <param name="name">メソッド名</param>
    /// <param name="items">要素</param>
    /// <param name="site">呼び出し式</param>
    /// <returns>最小値・最大値</returns>
    private static object? MinMax(string name, IReadOnlyList<object?> items, SyntaxNode site)
    {
        if (items.Count == 0) throw new GuestThrowException(null, "System.InvalidOperationException", null, site);

        var best = items[0];
        foreach (var item in items.Skip(1))
        {
            var comparison = Compare(item, best);
            if (name == "Min" ? comparison < 0 : 0 < comparison) best = item;
        }

        return best;
    }

    /// <summary>
    /// Average を評価する（int・long の平均は double。要素がなければ InvalidOperationException）
    /// </summary>
    /// <param name="items">要素</param>
    /// <param name="type">戻り値の型</param>
    /// <param name="site">呼び出し式</param>
    /// <returns>平均</returns>
    private static object Average(IReadOnlyList<object?> items, ITypeSymbol type, SyntaxNode site)
    {
        if (items.Count == 0) throw new GuestThrowException(null, "System.InvalidOperationException", null, site);

        return type.SpecialType switch
        {
            SpecialType.System_Decimal => items.Sum(i => (decimal)ClrOperations.Convert(i, type)!) / items.Count,
            SpecialType.System_Double => items.Sum(i => (double)ClrOperations.Convert(i, type)!) / items.Count,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedTypeOperation", type)),
        };
    }

    /// <summary>
    /// OrderBy・OrderByDescending を評価する（キーは数値・日付だけ。文字列の並びはカルチャで変わるため対象外）。同じキーの順序は保つ
    /// </summary>
    /// <param name="items">要素</param>
    /// <param name="key">キーを求める処理</param>
    /// <param name="descending">降順か</param>
    /// <returns>並べた要素</returns>
    private static List<object?> Order(IReadOnlyList<object?> items, Func<object?, object?> key, bool descending)
    {
        var keyed = items.Select((item, index) => (Item: item, Key: key(item), Index: index)).ToList();
        keyed.Sort((a, b) =>
        {
            var comparison = Compare(a.Key, b.Key);
            if (descending) comparison = -comparison;
            return comparison != 0 ? comparison : a.Index.CompareTo(b.Index);
        });
        return keyed.Select(k => k.Item).ToList();
    }

    /// <summary>
    /// 数値どうし・日付どうしを比べる
    /// </summary>
    /// <param name="left">値</param>
    /// <param name="right">比べる値</param>
    /// <returns>left が小さければ負、等しければ0、大きければ正</returns>
    private static int Compare(object? left, object? right)
    {
        if (ClrOperations.IsNumber(left) && ClrOperations.IsNumber(right)) return ClrOperations.CompareNumbers(left!, right!);
        if (left is DateTime or TimeSpan && left.GetType() == right?.GetType()) return ((IComparable)left).CompareTo(right);
        if (left is DateOnlyValue date && right is DateOnlyValue other) return date.CompareTo(other);

        throw new UnsupportedSyntaxException(Messages.Get("UnsupportedNonNumericComparison"));
    }
}
