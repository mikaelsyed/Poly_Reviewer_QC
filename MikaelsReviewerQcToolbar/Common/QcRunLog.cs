using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MikaelsReviewerQcToolbar.Common
{
    /// <summary>Status text shared by the writer and the readers of the log file.</summary>
    internal static class QcStatus
    {
        public const string NotRun = "Not run";
        public const string Skipped = "Skipped";
        public const string Completed = "Completed";
        public const string FpMatched = "Completed - every result matched the expected false-positive count";
        public const string FpMismatch = "Stopped - false-positive count did not match";
        public const string Cancelled = "Cancelled";
        public const string Error = "Stopped by an error";
    }

    internal sealed class QcLogLayerInfo
    {
        public string Role { get; set; } = "";
        public string Name { get; set; } = "";
        public string Source { get; set; } = "";
        public long Features { get; set; }
    }

    /// <summary>One check's result on one layer.</summary>
    internal sealed class QcLogLayerResult
    {
        public string Layer { get; set; } = "";
        /// <summary>Feature class / shapefile name of the layer, used to match the log to a dataset later.</summary>
        public string Dataset { get; set; } = "";
        public int Found { get; set; }
        /// <summary>How many of Found were expected false positives (all of them when the false-positive check matched, otherwise 0).</summary>
        public int FalsePositives { get; set; }
        public double Seconds { get; set; }
        public string ExportedTo { get; set; } = "";
        public string Note { get; set; } = "";

        public const string NotCheckedPrefix = "NOT CHECKED";
        public const string ErrorPrefix = "ERROR";

        /// <summary>False when the check could not run on this layer (so Found = 0 does not mean "no errors").</summary>
        public bool IsValid => !(Note ?? "").StartsWith(NotCheckedPrefix, StringComparison.Ordinal)
                            && !(Note ?? "").StartsWith(ErrorPrefix, StringComparison.Ordinal);
    }

    internal sealed class QcLogStep
    {
        public int Number { get; set; }
        /// <summary>Stable identifier (e.g. "IsolatedLines") so other tools can read the log without depending on display text.</summary>
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Parameters { get; set; } = "";
        public bool Skipped { get; set; }
        public bool FpEnabled { get; set; }
        public int ExpectedFp { get; set; }
        public bool ExportRequested { get; set; }
        public string Status { get; set; } = QcStatus.NotRun;
        public double Seconds { get; set; }
        public List<QcLogLayerResult> Layers { get; } = new List<QcLogLayerResult>();

        public int TotalFound => Layers.Sum(l => l.Found);
        public int TotalFalsePositives => Layers.Sum(l => l.FalsePositives);

        /// <summary>Records the outcome of the false-positive gate: a match makes every hit a known false positive.</summary>
        public void ApplyGate(bool matched)
        {
            foreach (var layer in Layers)
                layer.FalsePositives = matched ? layer.Found : 0;
            Status = matched ? QcStatus.FpMatched : QcStatus.Completed;
        }
    }

    /// <summary>
    /// The record of one batch QC run -- layers, parameters, per-layer results, false-positive
    /// handling, run times and the run's messages -- written as a human-readable text file that also
    /// carries "QC|..." lines the Metadata Editor can read back. Re-running the batch replaces the file.
    /// </summary>
    internal sealed class QcRunLog
    {
        public const int FormatVersion = 1;
        public const string PolylineToolbarId = "POLYLINE_QC";
        public const string ReviewerToolbarId = "REVIEWER_QC";
        public const string PolylineDefaultFileName = "Polyline_QC_Output_Log.txt";
        public const string ReviewerDefaultFileName = "Reviewer_QC_Output_Log.txt";

        public string ToolbarId { get; set; } = "";
        public string ToolbarName { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime Started { get; set; } = DateTime.Now;
        public DateTime? Finished { get; set; }
        public string Outcome { get; set; } = "";
        public string RunBy { get; set; } = Environment.UserName;
        public string Project { get; set; } = "";
        public string DefaultGeodatabase { get; set; } = "";
        public List<QcLogLayerInfo> Layers { get; } = new List<QcLogLayerInfo>();
        public List<QcLogStep> Steps { get; } = new List<QcLogStep>();
        public List<string> Messages { get; } = new List<string>();

        public double TotalSeconds => ((Finished ?? DateTime.Now) - Started).TotalSeconds;

        // ------------------------------------------------------------------
        // Writing
        // ------------------------------------------------------------------

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", Inv);
        private static string Clock(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        /// <summary>Writes the log, replacing any earlier file. If the file is locked (open in another program) a timestamped sibling is written instead; the path actually used is returned.</summary>
        public string Write(string path)
        {
            string text = ToText();
            var enc = new UTF8Encoding(false);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(path, text, enc);
                return path;
            }
            catch (IOException)
            {
                string alt = Path.Combine(dir ?? "", Path.GetFileNameWithoutExtension(path) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv) + Path.GetExtension(path));
                File.WriteAllText(alt, text, enc);
                return alt;
            }
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            string bar = new string('=', 100);
            string thin = new string('-', 100);

            sb.AppendLine(bar);
            sb.AppendLine(" " + (string.IsNullOrEmpty(Title) ? ToolbarName + " Output Log" : Title).ToUpperInvariant());
            sb.AppendLine(bar);
            sb.AppendLine($"Toolbar            : {ToolbarName}");
            sb.AppendLine($"Run started        : {Stamp(Started)}");
            sb.AppendLine($"Run finished       : {(Finished.HasValue ? Stamp(Finished.Value) : "(did not finish)")}");
            sb.AppendLine($"Total run time     : {Clock(TotalSeconds)}");
            sb.AppendLine($"Outcome            : {Outcome}");
            sb.AppendLine($"Run by             : {RunBy}");
            if (!string.IsNullOrEmpty(Project)) sb.AppendLine($"Project            : {Project}");
            if (!string.IsNullOrEmpty(DefaultGeodatabase)) sb.AppendLine($"Default geodatabase: {DefaultGeodatabase}");
            sb.AppendLine();

            sb.AppendLine("INPUT LAYERS");
            sb.AppendLine(thin);
            foreach (var l in Layers)
                sb.AppendLine($"{l.Role,-18} {l.Name}  ({l.Features:N0} features)" + (string.IsNullOrEmpty(l.Source) ? "" : $"  [{l.Source}]"));
            if (Layers.Count == 0) sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("RESULTS BY CHECK");
            sb.AppendLine(thin);
            sb.AppendLine($"{"#",-3} {"Check",-34} {"Layer",-24} {"Found",8} {"False pos.",10} {"Remaining",10}  {"Time",8}  Status");
            foreach (var step in Steps)
            {
                if (step.Layers.Count == 0)
                {
                    sb.AppendLine($"{step.Number,-3} {Cut(step.Name, 34),-34} {"-",-24} {"-",8} {"-",10} {"-",10}  {Clock(step.Seconds),8}  {step.Status}");
                    continue;
                }
                bool first = true;
                foreach (var r in step.Layers)
                {
                    string found = r.IsValid ? r.Found.ToString("N0") : "n/a";
                    string fp = r.IsValid ? r.FalsePositives.ToString("N0") : "n/a";
                    string remaining = r.IsValid ? (r.Found - r.FalsePositives).ToString("N0") : "n/a";
                    sb.AppendLine($"{(first ? step.Number.ToString() : ""),-3} {(first ? Cut(step.Name, 34) : ""),-34} {Cut(r.Layer, 24),-24} {found,8} {fp,10} {remaining,10}  {Clock(r.Seconds),8}  {(first ? step.Status : "")}");
                    first = false;
                }
            }
            var ran = Steps.Where(s => !s.Skipped && s.Layers.Count > 0).ToList();
            int totFound = ran.Sum(s => s.TotalFound), totFp = ran.Sum(s => s.TotalFalsePositives);
            sb.AppendLine($"{"",-3} {"TOTAL",-34} {"",-24} {totFound,8:N0} {totFp,10:N0} {totFound - totFp,10:N0}  {Clock(TotalSeconds),8}");
            sb.AppendLine();

            sb.AppendLine("SETTINGS USED");
            sb.AppendLine(thin);
            foreach (var step in Steps)
            {
                sb.AppendLine($"{step.Number}. {step.Name}");
                if (step.Skipped) { sb.AppendLine("     Skipped."); continue; }
                if (!string.IsNullOrEmpty(step.Parameters)) sb.AppendLine("     Parameters      : " + step.Parameters);
                sb.AppendLine("     False-pos. check: " + (step.FpEnabled ? $"ON, expecting {step.ExpectedFp:N0}" : "off"));
                sb.AppendLine("     Export requested: " + (step.ExportRequested ? "yes" : "no"));
                foreach (var r in step.Layers.Where(r => !string.IsNullOrEmpty(r.ExportedTo) || !string.IsNullOrEmpty(r.Note)))
                {
                    if (!string.IsNullOrEmpty(r.ExportedTo)) sb.AppendLine($"     Exported ({r.Layer}): {r.ExportedTo}");
                    if (!string.IsNullOrEmpty(r.Note)) sb.AppendLine($"     Note ({r.Layer}): {r.Note}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("RUN MESSAGES");
            sb.AppendLine(thin);
            foreach (var m in Messages) sb.AppendLine(m);
            if (Messages.Count == 0) sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("MACHINE-READABLE DATA (read by the Metadata Editor's \"Load QC log\" - please do not edit)");
            sb.AppendLine(thin);
            sb.AppendLine(Line("QC", "FORMAT", FormatVersion.ToString(Inv), ToolbarId));
            sb.AppendLine(Line("QC", "RUN", "started", Stamp(Started)));
            sb.AppendLine(Line("QC", "RUN", "finished", Finished.HasValue ? Stamp(Finished.Value) : ""));
            sb.AppendLine(Line("QC", "RUN", "seconds", TotalSeconds.ToString("0.0", Inv)));
            sb.AppendLine(Line("QC", "RUN", "outcome", Outcome));
            sb.AppendLine(Line("QC", "RUN", "user", RunBy));
            sb.AppendLine(Line("QC", "RUN", "project", Project));
            foreach (var l in Layers)
                sb.AppendLine(Line("QC", "LAYER", l.Role, l.Name, l.Source, l.Features.ToString(Inv)));
            foreach (var s in Steps)
            {
                sb.AppendLine(Line("QC", "STEP", s.Number.ToString(Inv), s.Key, s.Name, s.Skipped ? "1" : "0", s.FpEnabled ? "1" : "0",
                    s.ExpectedFp.ToString(Inv), s.ExportRequested ? "1" : "0", s.Status, s.Seconds.ToString("0.0", Inv), s.Parameters));
                foreach (var r in s.Layers)
                    sb.AppendLine(Line("QC", "RESULT", s.Number.ToString(Inv), s.Key, r.Layer, r.Dataset, r.Found.ToString(Inv),
                        r.FalsePositives.ToString(Inv), r.Seconds.ToString("0.0", Inv), r.ExportedTo, r.Note));
            }
            sb.AppendLine(Line("QC", "END"));
            return sb.ToString();
        }

        private static string Cut(string s, int width) => s == null ? "" : s.Length <= width ? s : s.Substring(0, width - 1) + "~";

        // ------------------------------------------------------------------
        // Escaping: '|' separates fields, so it (and the backslash and line breaks) is escaped inside a field.
        // ------------------------------------------------------------------

        private static string Line(params string[] fields) => string.Join("|", fields.Select(Esc));

        private static string Esc(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("|", "\\p").Replace("\r", "").Replace("\n", "\\n");

        private static string Unesc(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char c = s[++i];
                    sb.Append(c == 'p' ? '|' : c == 'n' ? '\n' : c);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Reading
        // ------------------------------------------------------------------

        /// <summary>Reads a log written by Write(). Throws FormatException if the file is not a QC output log.</summary>
        public static QcRunLog Read(string path)
        {
            var log = new QcRunLog();
            bool sawFormat = false;
            QcLogStep current = null;

            foreach (var raw in File.ReadLines(path))
            {
                if (!raw.StartsWith("QC|", StringComparison.Ordinal)) continue;
                var f = raw.Split('|').Select(Unesc).ToArray();
                if (f.Length < 2) continue;

                switch (f[1])
                {
                    case "FORMAT":
                        sawFormat = true;
                        if (f.Length > 3) log.ToolbarId = f[3];
                        log.ToolbarName = log.ToolbarId == PolylineToolbarId ? "Polyline QC" : log.ToolbarId == ReviewerToolbarId ? "Reviewer QC" : log.ToolbarId;
                        break;
                    case "RUN" when f.Length >= 4:
                        switch (f[2])
                        {
                            case "started": if (DateTime.TryParse(f[3], Inv, DateTimeStyles.None, out var st)) log.Started = st; break;
                            case "finished": if (DateTime.TryParse(f[3], Inv, DateTimeStyles.None, out var fi)) log.Finished = fi; break;
                            case "outcome": log.Outcome = f[3]; break;
                            case "user": log.RunBy = f[3]; break;
                            case "project": log.Project = f[3]; break;
                        }
                        break;
                    case "LAYER" when f.Length >= 6:
                        log.Layers.Add(new QcLogLayerInfo { Role = f[2], Name = f[3], Source = f[4], Features = long.TryParse(f[5], NumberStyles.Integer, Inv, out var n) ? n : 0 });
                        break;
                    case "STEP" when f.Length >= 12:
                        current = new QcLogStep
                        {
                            Number = ParseInt(f[2]), Key = f[3], Name = f[4], Skipped = f[5] == "1", FpEnabled = f[6] == "1",
                            ExpectedFp = ParseInt(f[7]), ExportRequested = f[8] == "1", Status = f[9], Seconds = ParseDouble(f[10]), Parameters = f[11]
                        };
                        log.Steps.Add(current);
                        break;
                    case "RESULT" when f.Length >= 11:
                        var step = log.Steps.LastOrDefault(s => s.Number == ParseInt(f[2])) ?? current;
                        step?.Layers.Add(new QcLogLayerResult
                        {
                            Layer = f[4], Dataset = f[5], Found = ParseInt(f[6]), FalsePositives = ParseInt(f[7]),
                            Seconds = ParseDouble(f[8]), ExportedTo = f[9], Note = f[10]
                        });
                        break;
                }
            }

            if (!sawFormat) throw new FormatException("This file is not a QC output log written by the Polyline QC or Reviewer QC batch tools.");
            log.Title = log.ToolbarName + " Output Log";
            return log;
        }

        private static int ParseInt(string s) => int.TryParse(s, NumberStyles.Integer, Inv, out var v) ? v : 0;
        private static double ParseDouble(string s) => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : 0;
    }
}
