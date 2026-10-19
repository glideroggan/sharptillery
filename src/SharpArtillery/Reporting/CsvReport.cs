using System;
using System.Globalization;
using System.IO;
using System.Text;
using SharpArtillery.Models;

namespace SharpArtillery.Reporting;

/// <summary>
/// The CSV report: one row per request, written as the run reads each result. Its time column is the
/// run's timeline (when the answer was processed), the same one the HTML report plots.
/// </summary>
/// <remarks>
/// A write that fails does not stop the run: the run loop reads results on the same thread that writes
/// rows, so an exception there would leave the clients blocked on a full channel. The first failure is
/// printed, later rows are dropped, and <see cref="Close"/> reports it once the run has finished.
/// </remarks>
internal sealed class CsvReport : IDisposable
{
    public const string Header = "timeline_ms,phase,status,latency_ms,error_type,response_bytes";

    // RFC 4180 asks for CRLF; written explicitly so the file is the same on every platform.
    private const string LineEnd = "\r\n";

    private readonly TextWriter _writer;
    private readonly string _name;
    private IOException? _failure;
    private bool _closed;

    /// <summary>
    /// Creates the report file, UTF-8 without a byte order mark, and writes the header. Fails before any
    /// request is sent when the file cannot be created.
    /// </summary>
    public static CsvReport Open(string path)
    {
        var stream = File.Create(path);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new CsvReport(writer, path);
    }

    public CsvReport(TextWriter writer, string name)
    {
        _writer = writer;
        _name = name;
        try
        {
            _writer.Write(Header + LineEnd);
        }
        catch (IOException e)
        {
            Fail(e);
        }
    }

    /// <summary>
    /// Writes one row. Once a write has failed, rows are no longer written.
    /// </summary>
    public void Write(DataPoint data)
    {
        if (_failure != null || _closed) return;

        var row = new StringBuilder(96)
            .Append(data.RequestTimeLine.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
            .Append(Quote(data.PhaseName)).Append(',')
            .Append(((int)data.Status).ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(data.ResponseTime.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
            .Append(Quote(data.ErrorType)).Append(',')
            .Append(data.ResponseContentLengthBytes?.ToString(CultureInfo.InvariantCulture) ?? "")
            .Append(LineEnd);
        try
        {
            _writer.Write(row.ToString());
        }
        catch (IOException e)
        {
            Fail(e);
        }
    }

    /// <summary>
    /// Pushes the rows written so far to the file. The run calls this once per batch of results, not per row.
    /// </summary>
    public void Flush()
    {
        if (_failure != null || _closed) return;
        try
        {
            _writer.Flush();
        }
        catch (IOException e)
        {
            Fail(e);
        }
    }

    /// <summary>
    /// Flushes and closes the file. Throws an <see cref="IOException"/> naming the file when a write failed
    /// during the run. The file is closed either way.
    /// </summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            _writer.Flush();
        }
        catch (IOException e)
        {
            Fail(e);
        }
        finally
        {
            _writer.Dispose();
        }

        if (_failure != null)
            throw new IOException($"CSV report '{_name}' is incomplete: {_failure.Message}", _failure);
    }

    /// <summary>
    /// Closes the file without reporting a failure, for the cleanup after a run that already ended.
    /// Close reports a failure to the caller; this runs when the caller did not reach Close (an exception
    /// in the run), and that exception is the one that matters, so a failed flush here is not thrown.
    /// </summary>
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            _writer.Dispose();
        }
        catch (IOException)
        {
        }
    }

    private void Fail(IOException e)
    {
        if (_failure != null) return;
        _failure = e;
        Console.Error.WriteLine($"CSV report '{_name}': a row could not be written ({e.Message}). The run goes on; the report is marked incomplete.");
    }

    /// <summary>
    /// RFC 4180: a field with a comma, a quote or a line break is wrapped in quotes, and each quote inside it is doubled.
    /// </summary>
    private static string Quote(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0) return field;
        return "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
