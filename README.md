# XUnit_IngeniousGenerator

Generates xUnit tests from C# code deterministically with Roslyn — no LLM, no external services.

Install the `XUnit_IngeniousGenerator` package in the project under test. Both rules are **enabled by default**, since installing this package is the opt-in. To turn a rule off, set its severity to `none` in `.editorconfig`; while it is off, nothing is analyzed.

```
dotnet_diagnostic.TEST001.severity = none
dotnet_diagnostic.TEST002.severity = none
```

Every member with logic in a class ends up in the test project either as tests or as a **skipped test that lists it as not tested**, with the reason (`[Fact(Skip = "未テスト: ...")] public void Load_NotTested() { }`). After a test run, Test Explorer (and `dotnet test`) shows the members that are not tested next to the passed and failed tests.

- **TEST001** is reported when the Quick Fix can add something to the test project. If the class has no test file (`XxxTests.cs`), it is reported on the class name, and the Quick Fix creates the test file with the generated tests and the skipped tests (creating the test project if needed and adding it to the solution). If the test file exists, it is reported on each member whose tests can be generated but are missing, and the Quick Fix adds the tests for that member only (replacing its skipped test, if any).
- **TEST002** is reported on members whose tests cannot be generated and that are not yet in the list, with the reason. The Quick Fix adds the skipped test (`MemberName_NotTested`) to the list, or inserts `// Ignore TEST002`.
- Members that are not generated automatically but still listed as not tested: members of non-public classes (classes without an access modifier are `internal`; make the class `public` to generate tests), non-public members, property getters with logic, operators, indexers, static constructors and members of nested classes. Private methods are verified through the tests of the public methods that call them.
- Records and structs are not analyzed. Existing test methods are never changed, except that a skipped test is replaced when real tests are added for the member.
- Overloads are told apart by parameter types in the test name (`Add_Int_...`, `Add_Decimal_...`).

Generated tests:

- Exception tests from argument guards (`if (...) throw`, `ThrowIf...`, `?? throw`), including `ParamName` checks.
- Boundary-value tests whose expected values are computed from the code by a static interpreter, including the midpoint between boundaries and each type's extremes (`int.MinValue`, `int.MaxValue`, `null`, ...). Runtime exceptions at the extremes (for example `NullReferenceException`) are recorded as exception tests.
- Snapshot tests (Verify) for values that can't be computed statically, with dependencies mocked by Moq (when the test project references Verify.Xunit and Moq).
- No-exception checks for methods without a return value.

The test project is looked up next to the project as `<ProjectName>.Tests` by default; set the `IngeniousTestProjectDirectory` property in the csproj to use another folder. Product code is never rewritten.

This package is independent of [CSharp_IngeniousAnalyzer](https://github.com/kanda-IngeniousDesign/CSharp_IngeniousAnalyzer) (static analysis rules); install either or both.

## 日本語

C#のコードから、xUnitのテストコードをRoslynだけで決定的に生成します（LLMや外部サービスは使いません）。

テスト対象のプロジェクトに `XUnit_IngeniousGenerator` パッケージを追加してください。このパッケージを入れること自体がテスト生成を使う意思表示なので、2つのルールはどちらも**既定で有効**です。止めたい場合は、`.editorconfig` で上記のように重大度を `none` にしてください。無効の間は解析もしません。

クラスの中の処理を持つ関数は、すべてテストプロジェクトに「テスト」か「**テストしていない一覧（スキップのテスト）**」のどちらかとして、理由つきで載ります（`[Fact(Skip = "未テスト: ...")] public void Load_NotTested() { }`）。テストを実行すると、テストエクスプローラー（と `dotnet test`）に、合格・失敗と並んでテストしていない関数が表示されます。

- **TEST001** は、クイックフィックスでテストプロジェクトに追加できるものがあるときに報告します。テストファイル（`XxxTests.cs`）がなければクラス名に報告し、クイックフィックスで、生成したテストとスキップのテストをまとめたテストファイルを作ります（テストプロジェクトがなければ作成し、ソリューションにも追加します）。テストファイルがあれば、テストを生成できるのにまだない関数に報告し、クイックフィックスでその関数のテストだけを追加します（スキップのテストがあれば置き換えます）。
- **TEST002** は、テストを生成できず、まだ一覧に載っていない関数に、理由とともに報告します。クイックフィックスで一覧にスキップのテスト（`関数名_NotTested`）を追加するか、`// Ignore TEST002` を挿入します。
- 自動生成はしないが、テストしていない一覧には載るもの：public でないクラスの関数（アクセス修飾子のないクラスは internal です。テストを生成するには public にしてください）、public でない関数、処理のあるプロパティの get、演算子、インデクサー、静的コンストラクタ、入れ子のクラスの関数。private の関数は、それを呼ぶ public の関数のテストで確かめます。
- record と構造体は解析の対象外です。既存のテストメソッドは変更しません（本物のテストを追加した関数のスキップのテストだけは置き換えます）。
- オーバーロードは、テスト名に引数の型を付けて区別します（`Add_Int_...`、`Add_Decimal_...`）。

テストプロジェクトは、既定では隣のフォルダの「プロジェクト名.Tests」を探します。別の場所にある場合は、csprojで `IngeniousTestProjectDirectory` プロパティを指定してください。プロダクトコードは書き換えません。

静的解析の [CSharp_IngeniousAnalyzer](https://github.com/kanda-IngeniousDesign/CSharp_IngeniousAnalyzer) とは独立したパッケージです。どちらか一方だけでも、両方でも使えます。

## License

MIT
