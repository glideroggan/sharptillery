#pragma warning disable CA1848
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging;
using SharpArtillery.Reporting.Excel;

namespace SharpArtillery.Reporting
{
    internal class MyExcel
    {
        private readonly ILogger<MyExcel> _logger;

        public MyExcel(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<MyExcel>();
        }
        public void Create(string outputPath, List<DataPoint> requestData)
        {
            // create an excel https://docs.microsoft.com/en-us/office/open-xml/how-to-insert-a-chart-into-a-spreadsheet
            requestData.Sort((a, b) =>
                a.RequestSentTime < b.RequestSentTime ? -1 :
                a.RequestSentTime == b.RequestSentTime ? 0 : 1);

            // copy template to new file to edit the report
            using var stream =
                ReportHelper.GetContentFromManifest(ReportHelper.ReportingTemplatesLocations[ReportingTemplates.Excel]);
            Debug.Assert(stream != null);

            try
            {
                var dest = OpenTemplateExcel(outputPath, stream);
                if (dest == null) return;

                using var spreadSheet = SpreadsheetDocument.Open(outputPath, true);
                var row = 2U;
                var workSheetPart = spreadSheet.GetSheet("report")!;
                var sheetData = workSheetPart.Worksheet.GetFirstChild<SheetData>();
                foreach (var data in requestData)
                {
                    spreadSheet.InsertData(sheetData, $"{data.RequestSentTime:O}", "A", row, CellValues.String);
                    spreadSheet.InsertData(sheetData, $"{data.RequestReceivedTime:O}", "B", row, CellValues.String);
                    spreadSheet.InsertData(sheetData, data.Latency.TotalMilliseconds, "C", row, CellValues.Number);
                    spreadSheet.InsertData(sheetData, data.Status.ToString(), "D", row, CellValues.String);
                    spreadSheet.InsertData(sheetData, data.RequestRate, "E", row, CellValues.Number);
                    row++;
                }

                spreadSheet.Save();
            }
            catch (Exception e)
            {
                _logger.LogError("Exception message {Message}", e.Message);
                throw;
            }

            Console.Out.WriteLine("Excel saved!");
        }

        private FileStream? OpenTemplateExcel(string outputPath, Stream stream)
        {
            Start:
            try
            {
                var dest = File.Create(outputPath);
                stream.CopyToAsync(dest);
                dest.FlushAsync();
                dest.Close();
                return dest;
            }
            catch (IOException e)
            {
                _logger.LogError("IO Exception message {Message}", e.Message);
                Console.Out.WriteLine(e.Message);
                Console.Out.Write("Close opened file and try again? (y/n) ");
                // TODO: need flag "quiet" here, to be able to have automatic process here
                // BUG: fix below problem, as this wouldn't work if being fed a file 
                var t = Console.ReadKey();
                Console.Out.WriteLine();
                if (t.Key == ConsoleKey.Y) goto Start;
            }

            return null;
        }
    }
}