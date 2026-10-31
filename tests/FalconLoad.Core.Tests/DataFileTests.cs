using System.Net;
using System.Text.Json;
using FalconLoad.Core.Distributed;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class DataFileTests
{
    private const string Users = "email,password\nana@example.test,pw-ana\nbo@example.test,pw-bo\ncy@example.test,pw-cy\n";

    private static DataFile Csv(string content, string name = "users.csv", ParameterSelection selection = ParameterSelection.Sequential) =>
        new() { Name = name, Content = content, Selection = selection };

    private static DataTable Parse(string csv) => DataTable.ParseCsv(csv);

    private static string[] Row(DataTable table, int index) => table.Rows[index];

    // ---- CSV ----

    [Fact]
    public void Csv_ReadsHeaderAndRows()
    {
        var table = Parse(Users);

        Assert.Equal(["email", "password"], table.Columns);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(["bo@example.test", "pw-bo"], Row(table, 1));
    }

    [Theory]
    [InlineData("a,b\r\n1,2\r\n3,4\r\n")]
    [InlineData("a,b\r1,2\r3,4")]
    [InlineData("﻿a,b\n1,2\n\n3,4\n\n")]
    public void Csv_AcceptsAnyLineEndingByteOrderMarkAndBlankLines(string text)
    {
        var table = Parse(text);

        Assert.Equal(["a", "b"], table.Columns);
        Assert.Equal([["1", "2"], ["3", "4"]], table.Rows);
    }

    [Fact]
    public void Csv_ReadsQuotedValuesWithCommasQuotesAndLineBreaks()
    {
        var table = Parse("name,note\n\"Smith, Ana\",\"said \"\"hi\"\"\"\nBo,\"two\nlines\"\n");

        Assert.Equal(["Smith, Ana", "said \"hi\""], Row(table, 0));
        Assert.Equal(["Bo", "two\nlines"], Row(table, 1));
    }

    [Theory]
    [InlineData("a;b\n1;2")]
    [InlineData("a\tb\n1\t2")]
    public void Csv_DetectsSemicolonAndTabSeparators(string text)
    {
        var table = Parse(text);

        Assert.Equal(["a", "b"], table.Columns);
        Assert.Equal(["1", "2"], Row(table, 0));
    }

    [Fact]
    public void Csv_KeepsSpacesInValuesAndEmptyValues()
    {
        var table = Parse("a,b,c\n x ,,\"\"\n");

        Assert.Equal([" x ", "", ""], Row(table, 0));
    }

    [Fact]
    public void Csv_ATrailingQuoteInsideAnUnquotedValueIsKept()
    {
        var table = Parse("size\n5\" pipe\n");

        Assert.Equal("5\" pipe", Row(table, 0)[0]);
    }

    [Theory]
    [InlineData("a,b\n1,2,3\n", "line 2 has 3 values but the header has 2 columns")]
    [InlineData("a,b\n1,2\n3\n", "line 3 has 1 value but the header has 2 columns")]
    [InlineData("a,b\n\"1\"x,2\n", "line 2 has text after a closing quote")]
    [InlineData("a,b\n\"1,2\n", "the quote that opens on line 2 is never closed")]
    [InlineData("", "the file is empty")]
    [InlineData("\n\n", "the file is empty")]
    public void Csv_ReportsTheLineOfAProblem(string text, string message)
    {
        var file = Csv(text);

        var problem = Assert.Single(file.Validate());
        Assert.Contains(message, problem);
    }

    [Fact]
    public void Csv_LineNumbersCountLineBreaksInsideQuotes()
    {
        var problem = Assert.Single(Csv("a,b\n\"x\ny\",2\n3\n").Validate());

        Assert.Contains("line 4 has 1 value", problem);
    }

    [Fact]
    public void Csv_RoundTripsThroughToCsv()
    {
        var table = Parse("a,b\n\"x,y\",\"q\"\"r\"\n\"two\nlines\",\n");

        var again = Parse(table.ToCsv());

        Assert.Equal(table.Columns, again.Columns);
        Assert.Equal(table.Rows, again.Rows);
    }

    [Fact]
    public void Csv_RoundTripKeepsEmptyValuesOfAOneColumnTable()
    {
        var table = Parse("a\nx\n\"\"\ny\n");

        Assert.Equal(["x", "", "y"], Parse(table.ToCsv()).Rows.Select(r => r[0]));
    }

    // ---- JSON ----

    [Fact]
    public void Json_ReadsObjectsAsRows()
    {
        var table = DataTable.ParseJson("""[{"id": 17, "name": "Ana", "admin": true, "note": null}, {"id": 42, "name": "Bo", "tags": ["a", "b"]}]""");

        Assert.Equal(["id", "name", "admin", "note", "tags"], table.Columns);
        Assert.Equal(["17", "Ana", "true", "", ""], Row(table, 0));
        Assert.Equal(["42", "Bo", "", "", "[\"a\", \"b\"]"], Row(table, 1));
    }

    [Theory]
    [InlineData("""{"id": 1}""", "has to be an array of objects")]
    [InlineData("""["a", "b"]""", "item 1 of the array isn't an object")]
    [InlineData("""[{"id": 1}, 5]""", "item 2 of the array isn't an object")]
    [InlineData("[{\"id\": 1},", "not valid JSON")]
    public void Json_ReportsWhatIsWrong(string text, string message)
    {
        var problem = Assert.Single(new DataFile { Name = "d.json", Format = DataFormat.Json, Content = text }.Validate());

        Assert.Contains(message, problem);
    }

    [Fact]
    public void Json_AcceptsCommentsAndTrailingCommas()
    {
        var table = DataTable.ParseJson("[\n// first\n{\"id\": 1},\n]");

        Assert.Single(table.Rows);
    }

    [Theory]
    [InlineData("users.json", "a,b", DataFormat.Json)]
    [InlineData("USERS.CSV", "[1]", DataFormat.Csv)]
    [InlineData("users.tsv", "a\tb", DataFormat.Csv)]
    [InlineData("users", "  [{\"a\": 1}]", DataFormat.Json)]
    [InlineData(null, "a,b\n1,2", DataFormat.Csv)]
    public void DetectFormat_UsesTheExtensionThenTheText(string? name, string content, DataFormat expected) =>
        Assert.Equal(expected, DataFile.DetectFormat(name, content));

    // ---- Validation ----

    [Fact]
    public void Validate_AcceptsAWellFormedFile() => Assert.Empty(Csv(Users).Validate());

    [Fact]
    public void Validate_RejectsHeaderOnlyFilesAndBadColumnNames()
    {
        Assert.Contains("no rows", Assert.Single(Csv("a,b\n").Validate()));

        var problems = Csv("first name,2nd,ok,OK,\n1,2,3,4,5\n").Validate().ToList();
        Assert.Contains(problems, p => p.Contains("'first name'") && p.Contains("variable name"));
        Assert.Contains(problems, p => p.Contains("'2nd'"));
        Assert.Contains(problems, p => p.Contains("more than one column named 'OK'"));
        Assert.Contains(problems, p => p.Contains("Column 5") && p.Contains("no name"));
    }

    [Fact]
    public void Validate_RejectsFilesWithoutANameOrOverTheSizeLimit()
    {
        Assert.Contains(Csv(Users, name: " ").Validate(), p => p.Contains("needs a name"));

        var big = Csv("a\n" + new string('x', DataFile.MaxContentLength));
        Assert.Contains("larger than", Assert.Single(big.Validate()));
    }

    [Fact]
    public void Plan_RejectsDuplicateFilesAndClashingNames()
    {
        var plan = Plan("http://api.test/{{email}}");
        plan.DataFiles.Add(Csv(Users));
        plan.DataFiles.Add(Csv("email,id\nx,1\n", name: "more.csv"));
        plan.Parameters.Add(new ParameterList { Name = "password", Values = ["1"] });
        plan.Requests[0].Extractions.Add(new ExtractionRule { Variable = "id", Source = ExtractionSource.JsonPath, Expression = "$.id" });

        var problems = plan.Validate();

        Assert.Contains(problems, p => p.Contains("Column 'password'") && p.Contains("parameter list"));
        Assert.Contains(problems, p => p.Contains("Column 'email' is in both"));
        Assert.Contains(problems, p => p.Contains("'id' is both a column of data file 'more.csv' and an extracted variable"));

        plan = Plan("http://api.test/{{email}}");
        plan.DataFiles.Add(Csv(Users));
        plan.DataFiles.Add(Csv(Users));
        Assert.Contains(plan.Validate(), p => p.Contains("more than one data file named 'users.csv'"));
    }

    [Fact]
    public void Plan_ChecksTheUrlWithTheFirstRow()
    {
        var plan = Plan("{{host}}/login");
        plan.DataFiles.Add(Csv("host\nhttp://api.test\n"));

        Assert.Empty(plan.ValidateForRun(null));
    }

    // ---- Running ----

    private static LoadTestPlan Plan(string url, int total = 6, int concurrency = 1) => new()
    {
        Requests = [new RequestDefinition { Method = "POST", Url = url, Body = "{ \"email\": \"{{email}}\", \"password\": \"{{password}}\" }" }],
        Profile = new LoadProfile { Concurrency = concurrency, StopCondition = StopCondition.RequestCount, TotalRequests = total },
    };

    private static async Task<(List<(HttpRequestMessage Request, string? Body)> Received, LoadTestRunner Runner)> RunAsync(LoadTestPlan plan, TestEnvironment? environment = null)
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler), environment);
        await runner.RunAsync(cancellationToken: CancellationToken.None);
        return ([.. handler.Received], runner);
    }

    private static (string Email, string Password) Credentials(string? body)
    {
        using var json = JsonDocument.Parse(body!);
        return (json.RootElement.GetProperty("email").GetString()!, json.RootElement.GetProperty("password").GetString()!);
    }

    [Fact]
    public async Task Sequential_HandsOutRowsInOrder_AndWraps()
    {
        var plan = Plan("http://api.test/login");
        plan.DataFiles.Add(Csv(Users));

        var (received, runner) = await RunAsync(plan);
        await runner.DisposeAsync();

        Assert.Equal(
            [("ana@example.test", "pw-ana"), ("bo@example.test", "pw-bo"), ("cy@example.test", "pw-cy"), ("ana@example.test", "pw-ana"), ("bo@example.test", "pw-bo"), ("cy@example.test", "pw-cy")],
            received.Select(r => Credentials(r.Body)));
    }

    [Fact]
    public async Task AllColumnsOfARow_AreUsedTogether_EvenWithManyVirtualUsers()
    {
        var plan = Plan("http://api.test/login", total: 300, concurrency: 4);
        plan.DataFiles.Add(Csv(Users));

        var (received, runner) = await RunAsync(plan);
        await runner.DisposeAsync();

        var pairs = received.Select(r => Credentials(r.Body)).ToList();
        Assert.All(pairs, p => Assert.Equal(p.Email.Split('@')[0], p.Password["pw-".Length..]));
        Assert.All(pairs.GroupBy(p => p.Email), g => Assert.Equal(100, g.Count()));
    }

    [Fact]
    public async Task RequestsOfAnIteration_ShareTheSameRow()
    {
        var plan = Plan("http://api.test/login/{{email}}", total: 4);
        plan.Requests.Add(new RequestDefinition { Url = "http://api.test/orders/{{email}}" });
        plan.DataFiles.Add(Csv(Users));

        var (received, runner) = await RunAsync(plan);
        await runner.DisposeAsync();

        Assert.Equal(
            ["/login/ana@example.test", "/orders/ana@example.test", "/login/bo@example.test", "/orders/bo@example.test"],
            received.Select(r => Uri.UnescapeDataString(r.Request.RequestUri!.AbsolutePath)));
    }

    [Fact]
    public async Task Random_OnlyUsesRowsFromTheFile_AndKeepsRowsWhole()
    {
        var plan = Plan("http://api.test/login", total: 60);
        plan.DataFiles.Add(Csv(Users, selection: ParameterSelection.Random));

        var (received, runner) = await RunAsync(plan);
        await runner.DisposeAsync();

        Assert.Equal(60, received.Count);
        Assert.All(received.Select(r => Credentials(r.Body)), p => Assert.Equal(p.Email.Split('@')[0], p.Password["pw-".Length..]));
    }

    [Fact]
    public async Task JsonFile_FeedsRequestsTheSameWay()
    {
        var plan = Plan("http://api.test/login", total: 2);
        plan.DataFiles.Add(new DataFile
        {
            Name = "users.json",
            Format = DataFormat.Json,
            Content = """[{"email": "ana@example.test", "password": "pw-ana"}, {"email": "bo@example.test", "password": "pw-bo"}]""",
        });

        var (received, runner) = await RunAsync(plan);
        await runner.DisposeAsync();

        Assert.Equal([("ana@example.test", "pw-ana"), ("bo@example.test", "pw-bo")], received.Select(r => Credentials(r.Body)));
    }

    [Fact]
    public async Task DataColumn_WinsOverAnEnvironmentVariableOfTheSameName()
    {
        var plan = Plan("{{baseUrl}}/login", total: 1);
        plan.DataFiles.Add(Csv(Users));
        var environment = new TestEnvironment
        {
            Name = "Staging",
            Variables =
            [
                new EnvironmentVariable { Name = "baseUrl", Value = "http://api.test" },
                new EnvironmentVariable { Name = "email", Value = "from-environment" },
            ],
        };

        var (received, runner) = await RunAsync(plan, environment);
        await runner.DisposeAsync();

        Assert.Equal("ana@example.test", Credentials(Assert.Single(received).Body).Email);
        Assert.Equal("api.test", Assert.Single(received).Request.RequestUri!.Host);
    }

    [Fact]
    public async Task Runner_RejectsAPlanWhoseDataFileIsInvalid()
    {
        var plan = Plan("http://api.test/login");
        plan.DataFiles.Add(Csv("email,password\nonly-one\n"));

        var error = Assert.Throws<ArgumentException>(() => new LoadTestRunner(plan, new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK))));

        Assert.Contains("users.csv", error.Message);
    }

    // ---- Results don't carry the rows ----

    [Fact]
    public async Task RunnerPlan_KeepsRowCountsButNotTheRows()
    {
        var plan = Plan("http://api.test/login", total: 3);
        plan.DataFiles.Add(Csv(Users));

        var (received, runner) = await RunAsync(plan);
        await runner.DisposeAsync();

        Assert.Equal(3, received.Count);
        var file = Assert.Single(runner.Plan.DataFiles);
        Assert.Equal(string.Empty, file.Content);
        Assert.Equal(3, file.OmittedRows);
        Assert.DoesNotContain("pw-ana", JsonSerializer.Serialize(runner.Plan));
        Assert.Contains("weren't kept", Assert.Single(runner.Plan.Validate()));

        // The plan that was passed in still has its rows.
        Assert.Contains("pw-ana", Assert.Single(plan.DataFiles).Content);
    }

    // ---- Splitting over agents ----

    [Fact]
    public void Split_DealsRowsRoundRobin_SoNoTwoAgentsShareARow()
    {
        var plan = Plan("http://api.test/login", total: 1000, concurrency: 4);
        plan.DataFiles.Add(Csv("email,password\n" + string.Join('\n', Enumerable.Range(1, 10).Select(i => $"u{i}@example.test,pw{i}")) + "\n"));

        var shares = LoadSplitter.Split(plan, [1, 1, 1]);

        var rows = shares.Select(s => DataTable.ParseCsv(Assert.Single(s.Plan.DataFiles).Content).Rows.Select(r => r[0]).ToList()).ToList();
        Assert.Equal(["u1@example.test", "u4@example.test", "u7@example.test", "u10@example.test"], rows[0]);
        Assert.Equal(["u2@example.test", "u5@example.test", "u8@example.test"], rows[1]);
        Assert.Equal(10, rows.Sum(r => r.Count));
        Assert.Equal(10, rows.SelectMany(r => r).Distinct().Count());
        Assert.All(shares, s => Assert.Empty(s.Plan.Validate()));
    }

    [Fact]
    public void Split_LeavesShortFilesAndRandomFilesWhole()
    {
        var plan = Plan("http://api.test/login", total: 1000, concurrency: 4);
        plan.DataFiles.Add(Csv(Users));
        plan.DataFiles.Add(Csv("a\n1\n2\n3\n4\n5\n6\n", name: "random.csv", selection: ParameterSelection.Random));
        plan.DataFiles.Add(Csv("b\n1\n2\n", name: "short.csv"));

        var shares = LoadSplitter.Split(plan, [1, 1, 1, 1]);

        // Three rows over four agents: not enough to give each its own, so each keeps all of them.
        Assert.All(shares, s => Assert.Equal(3, DataTable.ParseCsv(s.Plan.DataFiles[0].Content).Rows.Count));
        Assert.All(shares, s => Assert.Equal(6, DataTable.ParseCsv(s.Plan.DataFiles[1].Content).Rows.Count));
        Assert.All(shares, s => Assert.Equal(2, DataTable.ParseCsv(s.Plan.DataFiles[2].Content).Rows.Count));
    }

    [Fact]
    public void Split_KeepsQuotedValuesIntact()
    {
        var plan = Plan("http://api.test/{{note}}", total: 10, concurrency: 2);
        plan.DataFiles.Add(Csv("note\n\"a,b\"\n\"say \"\"hi\"\"\"\n\"two\nlines\"\nplain\n"));

        var shares = LoadSplitter.Split(plan, [1, 1]);

        var rows = shares.SelectMany(s => DataTable.ParseCsv(s.Plan.DataFiles[0].Content).Rows.Select(r => r[0])).Order().ToList();
        Assert.Equal(new[] { "a,b", "plain", "say \"hi\"", "two\nlines" }.Order(), rows);
    }

    // ---- Saved plans and exports ----

    [Fact]
    public void PlanFile_RoundTripsDataFiles()
    {
        var plan = Plan("http://api.test/login");
        plan.DataFiles.Add(Csv(Users));

        var read = PlanFile.Read(PlanFile.Write([plan]));

        Assert.True(read.Succeeded, string.Join(" ", read.Problems));
        var file = Assert.Single(Assert.Single(read.Plans).DataFiles);
        Assert.Equal("users.csv", file.Name);
        Assert.Equal(Users, file.Content);
        Assert.Equal(DataFormat.Csv, file.Format);
    }

    [Fact]
    public void PlanFile_ReadsAPlanWrittenBeforeDataFilesExisted()
    {
        const string json = """{"format": "falcon-load/plans", "version": 1, "plans": [{"name": "Old", "requests": [{"method": "GET", "url": "http://api.test/"}]}]}""";

        var read = PlanFile.Read(json);

        Assert.True(read.Succeeded, string.Join(" ", read.Problems));
        Assert.Empty(Assert.Single(read.Plans).DataFiles);
    }

    [Fact]
    public void PlanFile_ReportsAnInvalidDataFileInAnImport()
    {
        var plan = Plan("http://api.test/login");
        plan.DataFiles.Add(Csv("a,b\n1\n"));

        var read = PlanFile.Read(PlanFile.Write([plan]));

        Assert.False(read.Succeeded);
        Assert.Contains("users.csv", Assert.Single(read.Problems));
    }

    [Fact]
    public void PlanFile_TolerantOfNullsInHandEditedFiles()
    {
        const string json = """{"format": "falcon-load/plans", "version": 1, "plans": [{"name": "X", "requests": [{"url": "http://api.test/{{a}}"}], "dataFiles": [null, {"name": "d.csv", "content": "a\n1\n"}]}]}""";

        var read = PlanFile.Read(json);

        Assert.True(read.Succeeded, string.Join(" ", read.Problems));
        Assert.Single(Assert.Single(read.Plans).DataFiles);
    }

    // ---- Send once ----

    [Fact]
    public async Task Probe_UsesTheFirstRow()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        var definition = new RequestDefinition { Method = "POST", Url = "http://api.test/login", Body = "{{email}}/{{password}}" };

        var result = await RequestProbe.SendAsync(definition, new LoadProfile(), new HttpMessageInvoker(handler), dataFiles: [Csv(Users)]);

        Assert.Null(result.Error);
        Assert.Equal("ana@example.test/pw-ana", Assert.Single(handler.Received).Body);
    }
}
