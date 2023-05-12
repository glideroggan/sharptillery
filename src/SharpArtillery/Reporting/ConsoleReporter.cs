using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using SharpArtillery.Models;

namespace SharpArtillery.Reporting;

[Obsolete]
internal static class ConsoleReporter
{
    private static (int Left, int Top) cursor;
    private static (int Left, int Top) emittingCursor;
    private static List<string> emitBuffer = new();

    internal static void Report(Stopwatch durationTimer, List<DataPoint> completedTasks, List<Task<DataPoint>> tasks,
        int requestRate)
    {
        // Console.SetCursorPosition(cursor.Left, cursor.Top);
        Console.Write($"[{durationTimer.Elapsed}] - ");
        Console.Write($"Completed# {completedTasks.Count} - ");
        Console.Write($"Concurrent# {tasks.Count} - ");
        Console.Write($"Rate# {requestRate}\n");
    }

    public static void Init((int Left, int Top) initPos)
    {
        cursor = initPos;
        emittingCursor = (initPos.Left, initPos.Top + 10);
    }

    public static void Emit(string s)
    {
        Debug.WriteLine(s);
    }
}