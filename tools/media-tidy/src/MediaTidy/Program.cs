using MediaTidy;

var options = Options.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

// qBittorrent and the scheduled task can fire at the same moment; let one finish first.
using var mutex = new Mutex(initiallyOwned: false, @"Local\MediaTidy");
try
{
    if (!mutex.WaitOne(TimeSpan.FromMinutes(5)))
    {
        return 3;
    }
}
catch (AbandonedMutexException)
{
    // The previous run died; carry on.
}

try
{
    var ledger = Ledger.Load(options.LedgerFile);
    var planner = new Planner(options, ledger, TimeProvider.System);

    IEnumerable<Plan> plans;
    if (options.Target is null)
    {
        plans = planner.PlanAll();
    }
    else
    {
        // qBittorrent passes the path of a torrent's content, which may sit deeper than the top-level entry.
        var relative = Path.GetRelativePath(options.Source, options.Target);
        var top = relative.Split(Path.DirectorySeparatorChar)[0];
        plans = relative.StartsWith("..") || Path.IsPathRooted(relative)
            ? [Plan.Skip(options.Target, PlanStatus.NothingToDo, $"outside {options.Source}")]
            : [planner.PlanEntry(Path.Combine(options.Source, top), settle: false)];
    }

    foreach (var plan in plans)
    {
        var name = Path.GetFileName(plan.Entry);
        switch (plan.Status)
        {
            case PlanStatus.Ready:
                Log.Write(options, $"{(options.Apply ? "FILED" : "WOULD FILE")}  {name}");
                foreach (var link in plan.Links)
                {
                    Log.Write(options, $"    -> {planner.Relative(link.To)}");
                }

                if (options.Apply)
                {
                    Linker.Apply(plan);
                    ledger.MarkFiled(plan.Entry, DateTimeOffset.Now);
                }

                break;

            case PlanStatus.Unrecognised or PlanStatus.Conflict:
                if (!options.Apply || ledger.ShouldReport(plan.Entry, plan.Reason))
                {
                    Log.Write(options, $"NEEDS YOU  {name}: {plan.Reason}");
                }

                break;

            default:
                if (!options.Apply)
                {
                    Log.Write(options, $"skip       {name}: {plan.Reason}");
                }

                break;
        }
    }

    if (options.Apply)
    {
        ledger.Save();
    }
    else
    {
        Console.WriteLine("\nDry run: nothing was changed. Add --apply to file these.");
    }

    return 0;
}
catch (Exception ex)
{
    Log.Write(options, $"FAILED  {ex}");
    return 1;
}
finally
{
    mutex.ReleaseMutex();
}

internal static class Log
{
    /// <summary>Prints the line, and when applying also appends it to the log file (kept under 1 MB).</summary>
    public static void Write(Options options, string line)
    {
        Console.WriteLine(line);
        if (!options.Apply)
        {
            return;
        }

        Directory.CreateDirectory(options.DataDirectory);
        var log = new FileInfo(options.LogFile);
        if (log.Exists && log.Length > 1024 * 1024)
        {
            log.MoveTo(options.LogFile + ".old", overwrite: true);
        }

        File.AppendAllText(options.LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
    }
}
