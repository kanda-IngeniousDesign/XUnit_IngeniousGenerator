# XUnit_IngeniousGenerator

Generates xUnit tests from C# code deterministically with Roslyn — no LLM, no external services.

Install the `XUnit_IngeniousGenerator` package in the project under test. Both rules are **enabled by default**, since installing this package is the opt-in. To turn a rule off, set its severity to `none` in `.editorconfig`; while it is off, nothing is analyzed.

```
dotnet_diagnostic.TEST001.severity = none
dotnet_diagnostic.TEST002.severity = none
```

- **TEST001** is reported only when the Quick Fix can actually generate tests. If the test project has no test file (`XxxTests.cs`) for the class, it is reported on the class name and the Quick Fix generates the test file (creating the test project if needed and adding it to the solution). If the test file exists, it is reported on each public method, constructor or property setter that has no test (no test method named `MemberName_...`), and the Quick Fix adds the tests for that member only. Existing test methods are never changed.
- **TEST002** is reported on members without tests whose tests cannot be generated automatically (for example, members that use files, databases or the network), with the reason. Verify such members another way, then suppress the warning with the Quick Fix, which inserts `// Ignore TEST002` before the member.

Generated tests:

- Exception tests from argument guards (`if (...) throw`, `ThrowIf...`, `?? throw`), including `ParamName` checks.
- Boundary-value tests whose expected values are computed from the code by a static interpreter.
- Snapshot tests (Verify) for values that can't be computed statically, with dependencies mocked by Moq (when the test project references Verify.Xunit and Moq).
- No-exception checks for methods without a return value.

The test project is looked up next to the project as `<ProjectName>.Tests` by default; set the `IngeniousTestProjectDirectory` property in the csproj to use another folder. Product code is never rewritten.

This package is independent of [CSharp_IngeniousAnalyzer](https://github.com/kanda-IngeniousDesign/CSharp_IngeniousAnalyzer) (static analysis rules); install either or both.

## 日本語

C#のコードから、xUnitのテストコードをRoslynだけで決定的に生成します（LLMや外部サービスは使いません）。

テスト対象のプロジェクトに `XUnit_IngeniousGenerator` パッケージを追加してください。このパッケージを入れること自体がテスト生成を使う意思表示なので、2つのルールはどちらも**既定で有効**です。止めたい場合は、`.editorconfig` で上記のように重大度を `none` にしてください。無効の間は解析もしません。

- **TEST001** は、クイックフィックスで実際にテストを生成できる場合だけ報告します。テストファイル（`XxxTests.cs`）がなければクラス名に報告し、クイックフィックスでテストファイルを生成します（テストプロジェクトがなければ作成し、ソリューションにも追加します）。テストファイルがあれば、テストのない（「メンバー名_」で始まるテストメソッドがない）publicなメソッド・コンストラクタ・プロパティのsetterに報告し、クイックフィックスでそのメンバーのテストだけを追加します。既存のテストメソッドは変更しません。
- **TEST002** は、テストがなく、自動では生成できないメンバー（ファイル・DB・通信を使うものなど）に、理由とともに報告します。別の方法で確認したうえで、クイックフィックスで `// Ignore TEST002` を挿入して警告を消します。

テストプロジェクトは、既定では隣のフォルダの「プロジェクト名.Tests」を探します。別の場所にある場合は、csprojで `IngeniousTestProjectDirectory` プロパティを指定してください。プロダクトコードは書き換えません。

静的解析の [CSharp_IngeniousAnalyzer](https://github.com/kanda-IngeniousDesign/CSharp_IngeniousAnalyzer) とは独立したパッケージです。どちらか一方だけでも、両方でも使えます。

## License

MIT
