using System;
using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SharpArtillery.Reporting.Excel;

public static class SpreadsheetDocumentExtensions
{
    /// <summary>
    /// Only to be used when using new cells and rows
    /// </summary>
    /// <param name="spreadSheet"></param>
    /// <param name="sheetData"></param>
    /// <param name="val"></param>
    /// <param name="column"></param>
    /// <param name="row"></param>
    /// <param name="dataType"></param>
    public static void InsertData(this SpreadsheetDocument spreadSheet, SheetData sheetData, string val, string column,
        uint row, CellValues dataType)
    {
        // Insert cell A1 into the new worksheet.
        string cellReference = column + row;
        var newRow = new Row() { RowIndex = row };
        sheetData.Append(newRow);

        Cell newCell = new Cell() { CellReference = cellReference };
        newRow.InsertBefore(newCell, null);
        SetCellData(val, dataType, newCell);
    }

    private static void SetCellData<TValue>(TValue val, CellValues dataType, Cell cell)
    {
        // Set the value of cell A1.
        switch (val)
        {
            case decimal:
                cell.CellValue = new CellValue((decimal)(object)val);
                break;
            case string s:
                cell.CellValue = new CellValue(s);
                break;
            case double:
                cell.CellValue = new CellValue((double)(object)val);
                break;
            default:
                throw new ArgumentException();
        }
        
        cell.DataType = new EnumValue<CellValues>(dataType);
    }

    // Given a column name, a row index, and a WorksheetPart, inserts a cell into the worksheet. 
    // If the cell already exists, returns it. 
    // private static Cell InsertCellInWorksheet(string columnName, uint rowIndex, WorksheetPart worksheetPart)
    // {
    //     // PERF: SheetData is also something that should be sent as ref, otherwise we keep getting it here
    //     Worksheet worksheet = worksheetPart.Worksheet;
    //     SheetData sheetData = worksheet.GetFirstChild<SheetData>();
    //     string cellReference = columnName + rowIndex;
    //
    //     // If the worksheet does not contain a row with the specified row index, insert one.
    //     Row row;
    //     if (sheetData.Elements<Row>().Where(r => r.RowIndex == rowIndex).Count() != 0)
    //     {
    //         row = sheetData.Elements<Row>().Where(r => r.RowIndex == rowIndex).First();
    //     }
    //     else
    //     {
    //         row = new Row() { RowIndex = rowIndex };
    //         sheetData.Append(row);
    //     }
    //
    //     // If there is not a cell with the specified column name, insert one.  
    //     if (row.Elements<Cell>().Where(c => c.CellReference.Value == cellReference).Count() > 0)
    //     {
    //         return row.Elements<Cell>().Where(c => c.CellReference.Value == cellReference).First();
    //     }
    //     else
    //     {
    //         // Cells must be in sequential order according to CellReference. Determine where to insert the new cell.
    //         Cell refCell = null;
    //         foreach (Cell cell in row.Elements<Cell>())
    //         {
    //             if (cell.CellReference.Value.Length == cellReference.Length)
    //             {
    //                 if (string.Compare(cell.CellReference.Value, cellReference, true) > 0)
    //                 {
    //                     refCell = cell;
    //                     break;
    //                 }
    //             }
    //         }
    //
    //         Cell newCell = new Cell() { CellReference = cellReference };
    //         row.InsertBefore(newCell, refCell);
    //
    //         // worksheet.Save();
    //         return newCell;
    //     }
    // }

    // Given text and a SharedStringTablePart, creates a SharedStringItem with the specified text 
    // and inserts it into the SharedStringTablePart. If the item already exists, returns its index.
    // private static int InsertSharedStringItem(string text, SharedStringTablePart shareStringPart)
    // {
    //     // If the part does not contain a SharedStringTable, create one.
    //     if (shareStringPart.SharedStringTable == null)
    //     {
    //         shareStringPart.SharedStringTable = new SharedStringTable();
    //     }
    //
    //     int i = 0;
    //
    //     // Iterate through all the items in the SharedStringTable. If the text already exists, return its index.
    //     foreach (SharedStringItem item in shareStringPart.SharedStringTable.Elements<SharedStringItem>())
    //     {
    //         if (item.InnerText == text)
    //         {
    //             return i;
    //         }
    //
    //         i++;
    //     }
    //
    //     // The text does not exist in the part. Create the SharedStringItem and return its index.
    //     shareStringPart.SharedStringTable.AppendChild(
    //         new SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text(text)));
    //     // shareStringPart.SharedStringTable.Save();
    //
    //     return i;
    // }

    // Given a WorkbookPart, inserts a new worksheet.
    // private static WorksheetPart InsertWorksheet(WorkbookPart workbookPart)
    // {
    //     // Add a new worksheet part to the workbook.
    //     WorksheetPart newWorksheetPart = workbookPart.AddNewPart<WorksheetPart>();
    //     newWorksheetPart.Worksheet = new Worksheet(new SheetData());
    //     newWorksheetPart.Worksheet.Save();
    //
    //     Sheets sheets = workbookPart.Workbook.GetFirstChild<Sheets>();
    //     string relationshipId = workbookPart.GetIdOfPart(newWorksheetPart);
    //
    //     // Get a unique ID for the new sheet.
    //     uint sheetId = 1;
    //     if (sheets.Elements<Sheet>().Count() > 0)
    //     {
    //         sheetId = sheets.Elements<Sheet>().Select(s => s.SheetId.Value).Max() + 1;
    //     }
    //
    //     string sheetName = "Sheet" + sheetId;
    //
    //     // Append the new worksheet and associate it with the workbook.
    //     var sheet = new Sheet() { Id = relationshipId, SheetId = sheetId, Name = sheetName };
    //     sheets.Append(sheet);
    //     // workbookPart.Workbook.Save();
    //
    //     return newWorksheetPart;
    // }

    public static void InsertData(this SpreadsheetDocument spreadSheet, SheetData sheetData, double val, string column,
        uint row, CellValues dataType)
    {
        string cellReference = column + row;
        var newRow = new Row() { RowIndex = row };
        sheetData.Append(newRow);

        Cell newCell = new Cell() { CellReference = cellReference };
        newRow.InsertBefore(newCell, null);

        SetCellData(val, dataType, newCell);
    }

    public static WorksheetPart? GetSheet(this SpreadsheetDocument spreadSheet, string sheetName)
    {
        // worksheetPart = null;
        IEnumerable<Sheet> sheets = spreadSheet.WorkbookPart.Workbook.GetFirstChild<Sheets>().Elements<Sheet>()
            .Where(s => s.Name == sheetName);
        if (sheets.Count() == 0)
        {
            // The specified worksheet does not exist.
            return null;
        }

        string relationshipId = sheets.First().Id.Value;
        return (WorksheetPart)spreadSheet.WorkbookPart.GetPartById(relationshipId);
    }
}