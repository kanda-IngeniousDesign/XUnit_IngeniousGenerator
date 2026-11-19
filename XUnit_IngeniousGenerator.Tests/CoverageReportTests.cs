using XUnit_IngeniousGenerator.Cli;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// カバレッジの結果（cobertura 形式）を、関数ごとのカバー率と通っていない行にまとめることを確かめる
/// </summary>
public class CoverageReportTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <coverage>
          <packages>
            <package name="Lib">
              <classes>
                <class name="Sample.Rules" filename="Rules.cs">
                  <methods>
                    <method name="Discount" signature="(int, int, bool)">
                      <lines>
                        <line number="5" hits="1" branch="False" />
                        <line number="6" hits="1" branch="True" condition-coverage="100% (4/4)" />
                        <line number="7" hits="1" branch="True" condition-coverage="50% (2/4)" />
                        <line number="8" hits="0" branch="False" />
                        <line number="9" hits="1" branch="False" />
                      </lines>
                    </method>
                    <method name="Level" signature="(int, int)">
                      <lines>
                        <line number="12" hits="0" branch="False" />
                        <line number="13" hits="0" branch="True" condition-coverage="0% (0/4)" />
                        <line number="14" hits="0" branch="False" />
                      </lines>
                    </method>
                  </methods>
                </class>
              </classes>
            </package>
            <package name="Lib.Tests">
              <classes>
                <class name="Sample.Tests.RulesTests" filename="RulesTests.cs">
                  <methods>
                    <method name="Discount" signature="()">
                      <lines><line number="3" hits="0" branch="False" /></lines>
                    </method>
                  </methods>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    [Fact]
    public void Parse_Cobertura_SummarizesEachMethodAndSkipsTestProject()
    {
        var report = CoverageReport.Parse(Xml);

        Assert.Equal(2, report.Methods.Count);
        Assert.Equal(50.0, report.LineRate);
        Assert.Equal([8], report.Methods[0].UncoveredLines);
        Assert.Equal([7], report.Methods[0].PartialBranchLines);
        Assert.Equal((6, 12), (report.Methods.Sum(m => m.BranchesCovered), report.Methods.Sum(m => m.BranchesValid)));
    }

    [Fact]
    public void Format_Japanese_ListsRatesAndLinesNotRun()
    {
        var text = CoverageReport.Parse(Xml).Format(japanese: true);

        Assert.Contains("合計: 行 50.0% (4/8)  分岐 50.0% (6/12)", text);
        Assert.Contains("  Discount(int, int, bool)  行 80.0% (4/5)  分岐 75.0% (6/8)", text);
        Assert.Contains("    通っていない行: 12-14", text);
        Assert.Contains("    分岐が足りない行: 13", text);
    }

    [Fact]
    public void Format_English_UsesEnglishLabels()
    {
        var text = CoverageReport.Parse(Xml).Format(japanese: false);

        Assert.Contains("Total: Lines 50.0% (4/8)  Branches 50.0% (6/12)", text);
        Assert.Contains("    Lines not run: 8", text);
    }
}
