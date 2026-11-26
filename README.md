# XUnit_IngeniousGenerator

Generates xUnit tests from C# code deterministically with Roslyn — no LLM, no external services.

Install the `XUnit_IngeniousGenerator` package in the project under test. Both rules are **enabled by default**, since installing this package is the opt-in. To turn a rule off, set its severity to `none` in `.editorconfig`; while it is off, nothing is analyzed.

```
dotnet_diagnostic.TEST001.severity = none
dotnet_diagnostic.TEST002.severity = none
```

Every member with logic in a class ends up in the test project either as tests or as a **skipped test that lists it as not tested**, with the reason (`[Fact(Skip = "Not tested: ...")] public void Load_NotTested() { }`). Warning messages, reasons, comments in the generated tests and Quick Fix titles are written in Japanese when the IDE or the OS uses Japanese, and in English otherwise. To fix the language, set `<IngeniousLanguage>ja</IngeniousLanguage>` (or `en`) in a `PropertyGroup` of the project under test. After a test run, Test Explorer (and `dotnet test`) shows the members that are not tested next to the passed and failed tests.

- **TEST001** is reported when the Quick Fix can add something to the test project. If the class has no test file (`XxxTests.cs`), it is reported on the class name, and the Quick Fix creates the test file with the generated tests and the skipped tests (creating the test project if needed and adding it to the solution). If the test file exists, it is reported on each member whose tests can be generated but are missing, and the Quick Fix adds the tests for that member only (replacing its skipped test, if any). "Fix all occurrences" in a document, project or solution works too; create the test project with a single Quick Fix first.
- **TEST002** is reported on members whose tests cannot be generated and that are not yet in the list, with the reason. The Quick Fix adds the skipped test (`MemberName_NotTested`) to the list, or inserts `// Ignore TEST002`.
- Members that are not generated automatically but still listed as not tested: members of non-public classes (classes without an access modifier are `internal`; make the class `public` to generate tests), non-public members, property getters with logic, operators, indexers, static constructors and members of nested classes. Private methods are verified through the tests of the public methods that call them.
- Records and structs are not analyzed. Existing test methods are never changed, except that a skipped test is replaced when real tests are added for the member.
- Overloads are told apart by parameter types in the test name (`Add_Int_...`, `Add_Decimal_...`).
- Methods with `ref`, `out` and `in` parameters are tested: `out` arguments are received with `out _`, `ref` arguments are passed from the test parameter (or as `ref new int[] { 0 }[0]`), and `in` arguments are passed as values. Only the return value is checked.

Generated tests:

- Exception tests from argument guards (`if (...) throw`, `ThrowIf...`, `?? throw`), including `ParamName` checks. Guard conditions may use `is` patterns (`x is < 0 or > 10`, `s is null or ""`, `x is not (>= 1 and <= 3)`). Guards that compare arguments with each other (`if (min > max) throw`, `ThrowIfGreaterThan(from, to)`) or use an argument's property (`if (date.Year < 1900) throw`, `text.Length > 3`) are evaluated statically to find an input that throws, in methods and constructors; the instance under test is created with arguments that pass such constructor guards.
- Boundary-value tests whose expected values are computed from the code by a static interpreter, including string interpolation and `string.Join` / `Concat` / `Format` with culture-independent values (strings, integers, enums), enum parameters and return values, fields and properties of the instance under test (their values right after the constructor, computed from field initializers and the constructor body), class (DTO) arguments built with an object initializer or a constructor (`new Customer { Age = 19 }`; only the properties the method reads are varied), lists, arrays, loops (`foreach`, `for`, `while`) and common LINQ methods (`Where`, `Select`, `Sum`, `Count`, `Any`, `First`, ...) with collections varied to empty and to elements around the values they are compared with, `Path.Combine` / `GetFileName` and similar path operations (with the directory separator of the OS that generated the test), the midpoint between boundaries and each type's extremes (`int.MinValue`, `int.MaxValue`, `null`, ...). Runtime exceptions at the extremes (for example `NullReferenceException`) are recorded as exception tests. `DateTime`, `DateOnly` and `TimeSpan` are supported: dates compared in the code (`date < new DateTime(2026, 6, 1)`) are tested one day and one second before and after, and comparisons of `date.Year`, `date.Month`, `date.DayOfWeek` and similar properties produce matching dates.
- Inputs aimed at condition coverage: when the boundary values leave a decision (`if`, `?:`, loop condition) always true or always false, or leave an element of `&&` / `||` without a pair of inputs where only that element changes and the result changes (MC/DC), inputs that fill the gap are searched for among the values already used, the numeric constants in the method and their neighbors, and added to the `Assert.Equal` tests. For each numeric argument, the point where a decision changes is found by bisection, so comparisons through local variables (`var adjusted = score / 2; if (adjusted >= 40)`) are tested at the argument's real boundary (79 and 80). When a method has two or more `bool` arguments, rows are added so that every pair of them takes all four combinations. Expressions that return `bool` (`return a > 5 && b;`) are treated as decisions too, so both `true` and `false` results are tested. Conditions that could not be satisfied are listed in a comment above the test, so you can see what is not tested. Exception tests make exactly one argument invalid, and runtime exceptions found with the boundary values (for example division by zero) become exception tests.
- Snapshot tests (Verify) for values that can't be computed statically, with dependencies mocked by Moq (when the test project references Verify.Xunit and Moq). Each snapshot test has a comment saying why the value could not be computed statically.
- No-exception checks for methods without a return value.

Review the generated tests before committing them. Expected values are what the current code does, not what the specification says; if the code has a bug, the generated test expects the buggy result.

- `Assert.Equal` tests: check that the expected values (the last column of `InlineData` / `TheoryData`) match the specification.
- Verify tests: the first run creates the `.verified.txt` files automatically. Check their contents against the specification before committing them.
- When a later run produces a different result, Verify writes a `.received.txt` file and the test fails. Compare the two; accept the new result if the change is intended, otherwise fix the code. On CI (build servers), results are never accepted automatically, so a missing `.verified.txt` fails the build.

To check how much of the code the tests run, the CLI in this repository runs the test project with code coverage (Microsoft Code Coverage, included in `Microsoft.NET.Test.Sdk`; no extra package) and prints the line and branch coverage of each method with the lines not run:

```shell
dotnet run --project XUnit_IngeniousGenerator.Cli -- coverage --project path/to/Product.Tests.csproj [--threshold 80]
dotnet run --project XUnit_IngeniousGenerator.Cli -- coverage --report path/to/coverage.cobertura.xml
```

With `--threshold`, the command exits with code 2 when the line coverage is below the given percentage.

The test project is looked up next to the project as `<ProjectName>.Tests` by default; set the `IngeniousTestProjectDirectory` property in the csproj to use another folder. Product code is never rewritten.

This package is independent of [CSharp_IngeniousAnalyzer](https://github.com/kanda-IngeniousDesign/CSharp_IngeniousAnalyzer) (static analysis rules); install either or both.

## 日本語

C#のコードから、xUnitのテストコードをRoslynだけで決定的に生成します（LLMや外部サービスは使いません）。

テスト対象のプロジェクトに `XUnit_IngeniousGenerator` パッケージを追加してください。このパッケージを入れること自体がテスト生成を使う意思表示なので、2つのルールはどちらも**既定で有効**です。止めたい場合は、`.editorconfig` で上記のように重大度を `none` にしてください。無効の間は解析もしません。

クラスの中の処理を持つ関数は、すべてテストプロジェクトに「テスト」か「**テストしていない一覧（スキップのテスト）**」のどちらかとして、理由つきで載ります（`[Fact(Skip = "未テスト: ...")] public void Load_NotTested() { }`）。警告のメッセージ・理由・生成したテストのコメント・クイックフィックスのタイトルは、IDE や OS の表示言語が日本語なら日本語、それ以外なら英語で書かれます。言語を決めたい場合は、テスト対象のプロジェクトの csproj の `PropertyGroup` に `<IngeniousLanguage>ja</IngeniousLanguage>`（英語なら `en`）を書いてください。テストを実行すると、テストエクスプローラー（と `dotnet test`）に、合格・失敗と並んでテストしていない関数が表示されます。

- **TEST001** は、クイックフィックスでテストプロジェクトに追加できるものがあるときに報告します。テストファイル（`XxxTests.cs`）がなければクラス名に報告し、クイックフィックスで、生成したテストとスキップのテストをまとめたテストファイルを作ります（テストプロジェクトがなければ作成し、ソリューションにも追加します）。テストファイルがあれば、テストを生成できるのにまだない関数に報告し、クイックフィックスでその関数のテストだけを追加します（スキップのテストがあれば置き換えます）。ドキュメント・プロジェクト・ソリューション全体の Fix も使えます（テストプロジェクトは、先に1つ個別の Fix で作ってください）。
- **TEST002** は、テストを生成できず、まだ一覧に載っていない関数に、理由とともに報告します。クイックフィックスで一覧にスキップのテスト（`関数名_NotTested`）を追加するか、`// Ignore TEST002` を挿入します。
- 自動生成はしないが、テストしていない一覧には載るもの：public でないクラスの関数（アクセス修飾子のないクラスは internal です。テストを生成するには public にしてください）、public でない関数、処理のあるプロパティの get、演算子、インデクサー、静的コンストラクタ、入れ子のクラスの関数。private の関数は、それを呼ぶ public の関数のテストで確かめます。
- record と構造体は解析の対象外です。既存のテストメソッドは変更しません（本物のテストを追加した関数のスキップのテストだけは置き換えます）。
- オーバーロードは、テスト名に引数の型を付けて区別します（`Add_Int_...`、`Add_Decimal_...`）。
- `ref`・`out`・`in` の引数を持つ関数もテストします。`out` は `out _` で受け、`ref` はテストの引数（または `ref new int[] { 0 }[0]`）を渡し、`in` は値をそのまま渡します。確かめるのは戻り値だけです。

生成するテストの入力は、条件の網羅も目指します。境界値だけでは、判定（`if`・三項演算子・ループの条件）がいつも成立またはいつも不成立になる場合や、`&&`・`||` の要素ごとに「その要素だけが変わって結果が変わる」入力の組（MC/DC）がない場合は、すでに使っている値・関数の中の数値の定数とその前後から、足りない入力を探して `Assert.Equal` のテストに加えます。数値の引数ごとに、判定の結果が切り替わる点を二分探索で探すので、ローカル変数を通した比較（`var adjusted = score / 2; if (adjusted >= 40)`）も、引数の本当の境界（79 と 80）で確かめます。`bool` の引数が2つ以上あれば、どの2つの組でも true / false の4通りがそろうように行を加えます。`bool` を返す式（`return a > 5 && b;`）も判定として扱い、結果が true と false の両方になる入力を探します。探しても満たせなかった条件は、テストの上にコメントで書くので、何をテストしていないかが分かります。例外のテストでは、無効な引数を1つだけにします。境界値の入力で起きた実行時の例外（0 での割り算など）も、例外のテストにします。

生成されたテストは、コミットする前に必ず人が確認してください。期待値は「今のコードの実行結果」で、仕様として正しいかは保証しません。コードにバグがあれば、そのバグを正解とするテストが生成されます。

- `Assert.Equal` のテスト：期待値（`InlineData`・`TheoryData` の最後の列）が仕様どおりかを確認します。
- Verify のテスト：初回の実行で `.verified.txt` が自動で作られます。その中身が仕様どおりかを確認してからコミットします。
- 2回目以降に結果が変わると、`.received.txt` ができてテストが失敗します。差分を見て、正しい変更なら承認し、誤りならコードを直します。CI（ビルドサーバー）では自動で承認しないため、`.verified.txt` のコミット漏れは失敗として見つかります。

テストがコードのどこまでを通っているかは、このリポジトリの CLI で確かめられます。テストプロジェクトをカバレッジを集めながら実行し（`Microsoft.NET.Test.Sdk` に含まれる Microsoft Code Coverage を使うので、パッケージの追加は不要です）、関数ごとの行・分岐のカバー率と、通っていない行を表示します（コマンドは上記のとおりです）。`--threshold` を付けると、行のカバー率がその値（%）を下回ったときに終了コード 2 で終わります。

テストプロジェクトは、既定では隣のフォルダの「プロジェクト名.Tests」を探します。別の場所にある場合は、csprojで `IngeniousTestProjectDirectory` プロパティを指定してください。プロダクトコードは書き換えません。

静的解析の [CSharp_IngeniousAnalyzer](https://github.com/kanda-IngeniousDesign/CSharp_IngeniousAnalyzer) とは独立したパッケージです。どちらか一方だけでも、両方でも使えます。

## License

MIT
