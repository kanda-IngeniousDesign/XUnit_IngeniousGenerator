using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// 日付・時刻の型（DateTime・DateOnly・TimeSpan）の引数・戻り値のテストを生成し、実際にビルド・実行して成功することまで確かめる
/// </summary>
public class TemporalGenerationTests
{
    [Fact]
    public async Task Generate_DateTimeComparisonsAndProperties_TestsPassAgainstTarget()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Booking
            {
                public int Nights(DateTime checkIn, DateTime checkOut)
                {
                    if (checkOut <= checkIn) throw new ArgumentException("order", nameof(checkOut));
                    return (checkOut - checkIn).Days;
                }

                public bool IsWeekend(DateTime date)
                {
                    return date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                }

                public string Season(DateTime date)
                {
                    if (date.Month is 12 or 1 or 2) return "winter";
                    return date < new DateTime(2026, 6, 1) ? "spring" : "summer";
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Booking");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        // 本体の日付の前後（±1日・±1秒）、プロパティと比べている定数の前後（土・日曜日、11月〜3月）を入力にする
        Assert.Contains("{ new DateTime(2026, 5, 31, 23, 59, 59), \"spring\" }", result.Code);
        Assert.Contains("{ new DateTime(2026, 6, 1, 0, 0, 1), \"summer\" }", result.Code);
        Assert.Contains("{ new DateTime(2026, 1, 3), true }", result.Code);
        Assert.Contains("{ new DateTime(2026, 11, 1), \"summer\" }", result.Code);
        Assert.Contains("{ DateTime.MinValue, \"winter\" }", result.Code);
        Assert.Equal(
            [
                "Nights_DefaultInputs_ThrowsArgumentException",
                "Nights_BoundaryValues_ReturnsExpected",
                "IsWeekend_BoundaryValues_ReturnsExpected",
                "Season_BoundaryValues_ReturnsExpected",
            ],
            passed);
    }

    [Fact]
    public async Task Generate_DateOnlyTimeSpanAndExtremes_TestsPassAgainstTarget()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Planner
            {
                public int Quarter(DateOnly date) => (date.Month - 1) / 3 + 1;

                public DateOnly NextDay(DateOnly date) => date.AddDays(1);

                public string Length(TimeSpan span) => span < TimeSpan.FromHours(2) ? "short" : "long";

                public double Hours(TimeSpan span) => span.TotalHours;

                public DateTime Due(DateTime start, int days) => start.AddDays(days);
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Planner");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains("{ new DateOnly(2024, 2, 29), new DateOnly(2024, 3, 1) }", result.Code);
        Assert.Contains("{ new TimeSpan(0, 1, 59, 59), \"short\" }", result.Code);
        Assert.DoesNotContain("_NotTested", result.Code);
        Assert.Equal(
            [
                "Quarter_BoundaryValues_ReturnsExpected",
                "NextDay_BoundaryValues_ReturnsExpected",
                "NextDay_DateIsMaxValue_ThrowsArgumentOutOfRangeException",
                "Length_BoundaryValues_ReturnsExpected",
                "Hours_BoundaryValues_ReturnsExpected",
                "Due_BoundaryValues_ReturnsExpected",
                "Due_StartIsMaxValue_ThrowsArgumentOutOfRangeException",
                "Due_DaysIsMinValue_ThrowsArgumentOutOfRangeException",
                "Due_DaysIsMaxValue_ThrowsArgumentOutOfRangeException",
            ],
            passed);
    }
}
