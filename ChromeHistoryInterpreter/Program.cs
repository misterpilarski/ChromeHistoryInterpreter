using System.Diagnostics;
using System.Globalization;
using System.Text;
using CsvHelper;

var interpreterArgs = args;

var interpreter = ChromeHistoryInterpreterCli.InitializeInterpreter(interpreterArgs);

interpreter.Run();

interpreter.EvaluateAll();

public class ChromeHistoryInterpreterCli
{
    public static ChromeHistoryInterpreter InitializeInterpreter(string[] args)
    {
        var configuration = ParseCliArguments(args);

        return new ChromeHistoryInterpreter(configuration.Path);
    }

    private static Configuration ParseCliArguments(string[] args)
    {
        if (!args.Any())
        {
            var defaultFile = new DirectoryInfo(Directory.GetCurrentDirectory()).GetFiles("history*.csv").LastOrDefault();

            if (defaultFile?.Exists == true)
            {
                return new Configuration(defaultFile.FullName);
            }

            throw new ArgumentNullException("Keinen Pfad gefunden");
        }

        var path = args[0];


        var ensuredPath = (new FileInfo(path)).Exists
            ? path
            : "";

        if (string.IsNullOrWhiteSpace(ensuredPath))
        {
            throw new FileNotFoundException($"Datei existiert nicht: {path}");
        }


        return new Configuration(ensuredPath);
    }

    private record Configuration(string Path);


}
public class ChromeHistoryInterpreter
{
    private readonly string _csvPath;
    private List<HistoryEntry> _history;

    public ChromeHistoryInterpreter(string CsvPath)
    {
        _csvPath = CsvPath;
    }
    public void Run()
    {
        using (var reader = new StreamReader(_csvPath))
        using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
        {
            var records = csv.GetRecords<RawBowserData>();
            var history = records.Select(x => new HistoryEntry
            {
                DateStamp = x.date.ToDateTime(x.time),
                Title = x.title,
                Url = x.url
            });

            var orderedHistory = history.OrderBy(x => x.DateStamp).ToList();

            using (var iterator = orderedHistory.GetEnumerator())
            {
                while (iterator.MoveNext())
                {
                    var first = iterator.Current;
                    var second = iterator.MoveNext() ? iterator.Current : null;

                    if (second != null)
                    {
                        first.Duration = second.DateStamp.Subtract(first.DateStamp);
                    }
                }
            }

            _history = orderedHistory.ToList();
        }
    }

    public void EvaluateAll()
    {
        var start = _history.Select(x => x.Day).Min();
        var end = _history.Select(x => x.Day).Max();

        EvaluateTimeRecords(start, end);
    }
    public void EvaluateTimeRecords(DateOnly start, DateOnly end)
    {
        var evaluationRecords = _history.Where(x => x.Day >= start && x.Day <= end).ToList();

        var timeRecordDays = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1)
            .Select(evaluationDay => DateOnly.FromDayNumber(start.DayNumber + evaluationDay)).ToList();

        var timeRecords = timeRecordDays
            .Select(evaluationDate => new TimeRecordDay()
            {
                Records = evaluationRecords
                    .Where(x => x.Day == evaluationDate)
                    .Select(x => TimeRecord.fromHistoryEntry(x))
                    .Where(x => x != null)
                    .ToList()
            }).ToList();
        
        foreach (var timeRecordDay in timeRecords)
        {
            timeRecordDay.ComputeHoursOfWorks();
        }

        var sb = new StringBuilder();
        foreach (var timeRecordDay in timeRecords)
        {
            if (!(timeRecordDay.StartOfWork.HasValue && timeRecordDay.EndOfWork.HasValue &&
                  timeRecordDay.Records.Any()))
            {
                continue;
            }

            var dayTitle = $"Tag: {timeRecordDay.StartOfWork.Value.Date:dd.MM.yyyy}";

            var timeRangeDisplay = $"{timeRecordDay.StartOfWork:HH:mm} - {timeRecordDay.EndOfWork:HH:mm}";
            var workDisplay = $"Browserzeit: {timeRangeDisplay}; Dauer: {timeRecordDay.HoursOfWork:g}";

            sb.AppendLine(dayTitle + " " + workDisplay);
        }


        var report = Path.Combine(Directory.GetCurrentDirectory(), $"Auswertung vom {DateTime.Now:yyyyMMdd HH-mm-ss}.txt");

        if (File.Exists(report))
        {
            File.Delete(report);
            using var stream = File.Create(report);
        }

        File.AppendAllText(report, sb.ToString());
    }

    record RawBowserData(int order, int id, DateOnly date, TimeOnly time, string title, string url, string visitCount,
        string typedCount, string transition);

    class HistoryEntry
    {
        public DateTime DateStamp { get; set; }
        public DateOnly Day => DateOnly.FromDateTime(DateStamp);
        public TimeOnly Time => TimeOnly.FromDateTime(DateStamp);
        public string Title { get; set; }
        public string Url { get; set; }
        public TimeSpan? Duration { get; set; }

    }

    [DebuggerDisplay("Time={Time}, Type={Type}, Duration={Duration}")]
    class TimeRecord
    {
        public DateTime DateStamp { get; set; }
        public DateOnly Day => DateOnly.FromDateTime(DateStamp);
        public TimeOnly Time => TimeOnly.FromDateTime(DateStamp);
        public string Title { get; set; }
        public string Url { get; set; }
        public TimeSpan Duration { get; set; }

        public RecordType Type => Duration > TimeSpan.FromMinutes(20) ? RecordType.Absence : RecordType.Presence;

        public static TimeRecord? fromHistoryEntry(HistoryEntry entry)
        {
            if (entry.Duration.HasValue)
            {
                return new TimeRecord()
                {
                    DateStamp = entry.DateStamp,
                    Title = entry.Title,
                    Url = entry.Url,
                    Duration = entry.Duration.Value
                };
            }

            return null;
        }
    }

    [DebuggerDisplay("StartOfWork={StartOfWork}, EndOfWork={EndOfWork}, HoursOfWork={HoursOfWork}")]
    class TimeRecordDay
    {
        private ICollection<TimeRecord> _records;

        public ICollection<TimeRecord> Records
        {
            get => _records;
            set => _records = value;
        }

        public DateTime? StartOfWork => Records.FirstOrDefault(x =>
            x.Type == RecordType.Presence && x.Time >= TimeOnly.FromTimeSpan(TimeSpan.FromHours(7)))?.DateStamp;

        public DateTime? EndOfWork => Records.LastOrDefault(x =>
            x.Type == RecordType.Presence)?.DateStamp;

        public TimeSpan HoursOfWork { get; set; }

        public TimeSpan ComputeHoursOfWorks()
        {
            if (!(StartOfWork.HasValue && EndOfWork.HasValue && Records.Any()))
            {
                return TimeSpan.Zero;
            }

            var start = StartOfWork;
            var end = EndOfWork;

            var recordsQueue = Records.ToList();

            var noise = recordsQueue.Where(x => x.DateStamp < start && x.DateStamp > end);
            foreach (var trash in noise)
            {
                recordsQueue.Remove(trash);
            }

            var junkSeconds = new List<double>();

            while (recordsQueue.Any())
            {
                var currentJunk = recordsQueue.TakeWhile(x => x.Type == RecordType.Presence).ToList();

                foreach (var junk in currentJunk)
                {
                    recordsQueue.Remove(junk);
                }

                var tailAbsence = recordsQueue.TakeWhile(x => x.Type == RecordType.Absence).ToList();

                foreach (var absence in tailAbsence)
                {
                    recordsQueue.Remove(absence);
                }

                if (currentJunk.Any())
                {
                    var junkStart = currentJunk.Select(x => x.DateStamp).Min();
                    var junkEnd = currentJunk.Select(x => x.DateStamp).Max();

                    junkSeconds.Add(junkEnd.Subtract(junkStart).TotalSeconds);
                }
            }

            HoursOfWork = TimeSpan.FromSeconds(junkSeconds.Sum());

            return HoursOfWork;
        }

    }

    enum RecordType
    {
        Presence,
        Absence
    }

}


