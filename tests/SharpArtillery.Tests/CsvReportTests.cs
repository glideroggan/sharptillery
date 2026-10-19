using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SharpArtillery.Configs;
using SharpArtillery.Models;
using SharpArtillery.Reporting;
using SharpArtillery.Tests.Stubs;
using SharpArtillery.YamlConfig;
using Xunit;

namespace SharpArtillery.Tests;

public class CsvReportTests
{
    private const string Header = "timeline_ms,phase,status,latency_ms,error_type,response_bytes";
    private const string Target = "http://stub.test/x";

    // Every wait in these tests is bounded by this; a run that takes longer is a hang, not slowness.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>A writer the test can read while the run is still going.</summary>
    private sealed class RecordingWriter : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_text) _text.Append(value);
        }

        public override void Write(string? value)
        {
            lock (_text) _text.Append(value);
        }

        public string Text
        {
            get { lock (_text) return _text.ToString(); }
        }

        /// <summary>The lines after the header row, counting only complete lines.</summary>
        public int DataRows => Math.Max(0, Text.Split("\r\n").Length - 2);
    }

    /// <summary>A writer whose every write fails, as a full disk does.</summary>
    private sealed class FailingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => throw new IOException("disk full");

        public override void Write(string? value) => throw new IOException("disk full");

        public override void Flush() => throw new IOException("disk full");
    }

    private static async Task RunPhaseAsync(Manager manager, Settings settings)
    {
        await manager.PrepareForTest(settings);
        await manager.RunTest().WaitAsync(Patience);
        await manager.ProcessData();
    }

    private static DataPoint Point(double atMs, double latencyMs, string? phase = null, HttpStatusCode status = HttpStatusCode.OK,
        string? errorType = null, long? bytes = null) => new()
    {
        RequestTimeLine = TimeSpan.FromMilliseconds(atMs),
        ResponseTime = TimeSpan.FromMilliseconds(latencyMs),
        PhaseName = phase,
        Status = status,
        HasResponse = status != 0,
        ErrorType = errorType,
        ResponseContentLengthBytes = bytes
    };

    [Fact]
    [Trait("slice", "6")]
    public void The_header_row_is_written_first_and_exactly()
    {
        var writer = new RecordingWriter();
        using var csv = new CsvReport(writer, "report.csv");

        csv.Flush();

        Assert.Equal(Header + "\r\n", writer.Text);
    }

    [Fact]
    [Trait("slice", "6")]
    public void Each_result_is_one_row_in_the_column_order()
    {
        var writer = new RecordingWriter();
        using var csv = new CsvReport(writer, "report.csv");

        csv.Write(Point(1500, 12.5, "warm", HttpStatusCode.OK, bytes: 42));
        csv.Write(Point(1600, 3, "warm", HttpStatusCode.InternalServerError));

        Assert.Equal(
            Header + "\r\n" +
            "1500.000,warm,200,12.500,,42\r\n" +
            "1600.000,warm,500,3.000,,\r\n",
            writer.Text);
    }

    // A timeout has no response: status 0, and no error type unless one is set.
    [Fact]
    [Trait("slice", "6")]
    public void Missing_fields_are_empty_and_a_timeout_is_status_zero()
    {
        var writer = new RecordingWriter();
        using var csv = new CsvReport(writer, "report.csv");

        csv.Write(new DataPoint { ResponseTime = TimeSpan.FromSeconds(5), IsTimeout = true });

        Assert.Equal(Header + "\r\n" + "0.000,,0,5000.000,,\r\n", writer.Text);
    }

    [Fact]
    [Trait("slice", "6")]
    public void A_field_with_a_comma_quote_or_newline_is_quoted_and_its_quotes_doubled()
    {
        var writer = new RecordingWriter();
        using var csv = new CsvReport(writer, "report.csv");

        csv.Write(Point(0, 1, "ramp, up", HttpStatusCode.OK, errorType: "say \"hi\"\nagain"));

        Assert.Equal(
            Header + "\r\n" + "0.000,\"ramp, up\",200,1.000,\"say \"\"hi\"\"\nagain\",\r\n",
            writer.Text);
    }

    // The culture is sv-SE, whose decimal separator is a comma: the file still uses a dot.
    [Fact]
    [Trait("slice", "6")]
    public void Numbers_are_written_with_a_dot_whatever_the_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            Assert.Equal("12,250", 12.25.ToString("F3", CultureInfo.CurrentCulture));

            var writer = new RecordingWriter();
            using var csv = new CsvReport(writer, "report.csv");
            csv.Write(Point(1500.5, 12.25, "a", HttpStatusCode.OK, bytes: 1234));

            Assert.Equal(Header + "\r\n" + "1500.500,a,200,12.250,,1234\r\n", writer.Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // A file report has no byte order mark, and its name is the -o name with .csv.
    [Fact]
    [Trait("slice", "6")]
    public void Open_writes_the_header_as_utf8_without_a_byte_order_mark()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sharptillery-csv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "report.csv");
            using (var csv = CsvReport.Open(path))
            {
                csv.Write(Point(1, 2, "p", HttpStatusCode.OK));
            }

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal(Header + "\r\n" + "1.000,p,200,2.000,,\r\n", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The file is opened before the first request, so a bad path fails before anything is sent.
    [Fact]
    [Trait("slice", "6")]
    public void A_report_file_that_cannot_be_created_fails_before_the_run()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sharptillery-missing-" + Guid.NewGuid().ToString("N"), "report.csv");

        Assert.ThrowsAny<IOException>(() => CsvReport.Open(missing));
    }

    // Rows reach the writer while the run is still going: the fifth request is held until the
    // first four rows are in the writer.
    [Fact]
    [Trait("slice", "6")]
    public async Task Each_row_is_written_while_the_run_is_still_going()
    {
        var writer = new RecordingWriter();
        using var csv = new CsvReport(writer, "stream.csv");
        var calls = 0;
        var sawFourRowsDuringTheRun = false;
        var stub = new StubHttpHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 5)
            {
                var deadline = DateTime.UtcNow + Patience;
                while (writer.DataRows < 4 && DateTime.UtcNow < deadline)
                    await Task.Delay(1, ct).ConfigureAwait(false);
                sawFourRowsDuringTheRun = writer.DataRows >= 4;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance) { Csv = csv };

        await RunPhaseAsync(manager, new Settings { Target = Target, Vu = 1, MaxRequests = 5 });
        csv.Close();

        Assert.True(sawFourRowsDuringTheRun);
        Assert.Equal(5, writer.DataRows);
        Assert.Equal(5, manager.Results.Count);
    }

    // Flow mode drains its queue while the phase runs, so rows are written before the phase ends.
    [Fact]
    [Trait("slice", "6")]
    public async Task Each_flow_row_is_written_while_the_run_is_still_going()
    {
        var writer = new RecordingWriter();
        using var csv = new CsvReport(writer, "flow.csv");
        var calls = 0;
        var sawFourRowsDuringTheRun = false;
        var stub = new StubHttpHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 5)
            {
                var deadline = DateTime.UtcNow + Patience;
                while (writer.DataRows < 4 && DateTime.UtcNow < deadline)
                    await Task.Delay(1, ct).ConfigureAwait(false);
                sawFourRowsDuringTheRun = writer.DataRows >= 4;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var steps = new List<FlowStep> { new() { Get = new RequestSpec { Url = "/a" } } };
        var phases = new List<Phase> { new() { Name = "flow", Vu = 1, Requests = 5 } };

        await MainService.RunFlowModeAsync(steps, "http://stub.test", TimeSpan.FromSeconds(5), phases,
            new Models.ReportSettings(), stub, csv).WaitAsync(Patience);

        Assert.True(sawFourRowsDuringTheRun);
        Assert.Equal(5, writer.DataRows);
    }

    // A write that fails must not stop the run: every result is still collected, and the failure
    // is reported when the report closes.
    [Fact]
    [Trait("slice", "6")]
    public async Task A_failing_write_does_not_stop_the_run_and_is_reported_when_the_report_closes()
    {
        using var csv = new CsvReport(new FailingWriter(), "broken.csv");
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance) { Csv = csv };

        await RunPhaseAsync(manager, new Settings { Target = Target, Vu = 2, MaxRequests = 20 });

        Assert.Equal(20, manager.Results.Count);
        var error = Assert.Throws<IOException>(() => csv.Close());
        Assert.Contains("broken.csv", error.Message);
    }

    [Fact]
    [Trait("slice", "6")]
    public void The_csv_extension_is_dot_csv_whatever_the_case()
    {
        Assert.Equal(".csv", YamlHelper.GetReportExtension("csv"));
        Assert.Equal(".csv", YamlHelper.GetReportExtension("CSV"));
    }
}
