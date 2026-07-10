using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ExcelDataReader;

namespace WinLog
{
    public class ImportedLogEntry
    {
        public long? RecordId { get; set; }
        public string EventId { get; set; } = string.Empty;
        public DateTime TimeCreated { get; set; } = DateTime.Now;
        public string LogName { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Level { get; set; } = "Information";
        public string User { get; set; } = string.Empty;
        public string Computer { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string RawData { get; set; } = string.Empty;
        public bool IsForensicAlert { get; set; }
        public string ForensicCategory { get; set; } = string.Empty;
        public string ForensicTitle { get; set; } = string.Empty;
        public string TaskCategory { get; set; } = "None";
        public string Keywords { get; set; } = "None";
        public string Opcode { get; set; } = "Info";

        public string MessageSummary
        {
            get
            {
                if (string.IsNullOrEmpty(Message)) return string.Empty;
                int nlIdx = Message.IndexOf('\n');
                string firstLine = nlIdx > 0 ? Message.Substring(0, nlIdx) : Message;
                return firstLine.Replace("\r", "").Trim();
            }
        }
    }

    public static class LogParser
    {
        public static List<ImportedLogEntry> ParseFile(string filePath)
        {
            // Bypass WoW64 File System Redirector for 32-bit processes on 64-bit OS
            if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
            {
                if (filePath.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase))
                {
                    string sysnativePath = filePath.Replace(@"\System32\", @"\Sysnative\", StringComparison.OrdinalIgnoreCase);
                    if (File.Exists(sysnativePath))
                    {
                        filePath = sysnativePath;
                    }
                }
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("The specified file does not exist.", filePath);
            }

            string ext = Path.GetExtension(filePath).ToLower();
            return ext switch
            {
                ".evtx" => ParseEvtx(filePath),
                ".json" => ParseJson(filePath),
                ".xml" => ParseXml(filePath),
                ".csv" => ParseCsv(filePath),
                ".xlsx" or ".xls" => ParseExcel(filePath),
                _ => throw new NotSupportedException($"The file extension '{ext}' is not supported. Supported: .evtx, .json, .xml, .csv, .xlsx, .xls")
            };
        }

        private static List<ImportedLogEntry> ParseEvtx(string filePath)
        {
            var entries = new List<ImportedLogEntry>();
            using var reader = new EventLogReader(filePath, PathType.FilePath);
            EventRecord record;

            while ((record = reader.ReadEvent()) != null)
            {
                using (record)
                {
                    int eventId = record.Id;
                    var advice = ForensicAdvisor.GetAdvice(eventId);
                    var isAlert = ForensicAdvisor.IsForensicHighlight(eventId);

                    string message = string.Empty;
                    try
                    {
                        message = record.FormatDescription();
                    }
                    catch
                    {
                        message = "Event parameters: " + string.Join(", ", record.Properties.Select(p => p.Value));
                    }

                    string xml = string.Empty;
                    try
                    {
                        xml = record.ToXml();
                    }
                    catch
                    {
                        xml = "<ErrorLoadingXml />";
                    }

                    string levelDisplayName = "Information";
                    try
                    {
                        levelDisplayName = record.LevelDisplayName ?? "Information";
                    }
                    catch
                    {
                        int? levelVal = record.Level;
                        levelDisplayName = levelVal switch
                        {
                            1 => "Critical",
                            2 => "Error",
                            3 => "Warning",
                            4 => "Information",
                            5 => "Verbose",
                            _ => "Information"
                        };
                    }

                    string taskCat = "None";
                    try { taskCat = record.TaskDisplayName ?? "None"; } catch { }

                    string keywords = "None";
                    try 
                    { 
                        if (record.KeywordsDisplayNames != null && record.KeywordsDisplayNames.Any())
                            keywords = string.Join(", ", record.KeywordsDisplayNames); 
                    } 
                    catch { }

                    string opcode = "Info";
                    try { opcode = record.OpcodeDisplayName ?? "Info"; } catch { }

                    entries.Add(new ImportedLogEntry
                    {
                        RecordId = record.RecordId,
                        EventId = eventId.ToString(),
                        TimeCreated = record.TimeCreated ?? DateTime.Now,
                        LogName = Path.GetFileName(filePath),
                        Source = record.ProviderName,
                        Level = levelDisplayName,
                        User = record.UserId?.Value ?? "N/A",
                        Computer = record.MachineName,
                        Message = message,
                        RawData = xml,
                        IsForensicAlert = isAlert,
                        ForensicCategory = isAlert ? advice.Category : string.Empty,
                        ForensicTitle = isAlert ? advice.Title : string.Empty,
                        TaskCategory = taskCat,
                        Keywords = keywords,
                        Opcode = opcode
                    });
                }
            }

            // Return in reverse chronological order (newest first)
            entries.Sort((x, y) => y.TimeCreated.CompareTo(x.TimeCreated));
            return entries;
        }

        private static List<ImportedLogEntry> ParseJson(string filePath)
        {
            var entries = new List<ImportedLogEntry>();
            string content = File.ReadAllText(filePath);
            
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                long idx = 1;
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    entries.Add(ParseJsonObject(elem, idx++, Path.GetFileName(filePath)));
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                entries.Add(ParseJsonObject(doc.RootElement, 1, Path.GetFileName(filePath)));
            }

            entries.Sort((x, y) => y.TimeCreated.CompareTo(x.TimeCreated));
            return entries;
        }

        private static ImportedLogEntry ParseJsonObject(JsonElement elem, long recordId, string fileName)
        {
            string timeStr = GetJsonString(elem, "TimeCreated", "Time", "Timestamp", "Date", "Created") ?? DateTime.Now.ToString();
            DateTime.TryParse(timeStr, out DateTime time);

            string eventId = GetJsonString(elem, "EventId", "Id", "EventID", "Event_ID") ?? "0";
            string level = GetJsonString(elem, "Level", "Severity", "Type", "LevelDisplayName") ?? "Information";
            string source = GetJsonString(elem, "Source", "Provider", "ProviderName") ?? "N/A";
            string user = GetJsonString(elem, "User", "UserId", "Username", "Account") ?? "N/A";
            string computer = GetJsonString(elem, "Computer", "MachineName", "Host") ?? "N/A";
            string message = GetJsonString(elem, "Message", "Description", "Detail") ?? elem.ToString();

            int.TryParse(eventId, out int evIdNum);
            var advice = ForensicAdvisor.GetAdvice(evIdNum);
            var isAlert = ForensicAdvisor.IsForensicHighlight(evIdNum);

            return new ImportedLogEntry
            {
                RecordId = recordId,
                EventId = eventId,
                TimeCreated = time,
                LogName = fileName,
                Source = source,
                Level = level,
                User = user,
                Computer = computer,
                Message = message,
                RawData = elem.ToString(),
                IsForensicAlert = isAlert,
                ForensicCategory = isAlert ? advice.Category : string.Empty,
                ForensicTitle = isAlert ? advice.Title : string.Empty
            };
        }

        private static string? GetJsonString(JsonElement elem, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                if (elem.TryGetProperty(name, out var prop)) return prop.ToString();
                // Check case-insensitively
                foreach (var p in elem.EnumerateObject())
                {
                    if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value.ToString();
                }
            }
            return null;
        }

        private static List<ImportedLogEntry> ParseXml(string filePath)
        {
            var entries = new List<ImportedLogEntry>();
            var doc = XDocument.Load(filePath);
            
            var eventElements = doc.Descendants().Where(d => d.Name.LocalName.Equals("Event", StringComparison.OrdinalIgnoreCase) 
                                                          || d.Name.LocalName.Equals("LogEntry", StringComparison.OrdinalIgnoreCase));
            
            long idx = 1;
            foreach (var elem in eventElements)
            {
                string timeStr = elem.Descendants().FirstOrDefault(d => d.Name.LocalName.Equals("TimeCreated", StringComparison.OrdinalIgnoreCase))?.Attribute("SystemTime")?.Value 
                                 ?? elem.Element("Time")?.Value 
                                 ?? elem.Element("Timestamp")?.Value 
                                 ?? DateTime.Now.ToString();
                DateTime.TryParse(timeStr, out DateTime time);

                string eventId = elem.Descendants().FirstOrDefault(d => d.Name.LocalName.Equals("EventID", StringComparison.OrdinalIgnoreCase))?.Value 
                                 ?? elem.Element("Id")?.Value 
                                 ?? "0";

                string level = elem.Descendants().FirstOrDefault(d => d.Name.LocalName.Equals("Level", StringComparison.OrdinalIgnoreCase))?.Value 
                               ?? elem.Element("Severity")?.Value 
                               ?? "Information";

                string source = elem.Descendants().FirstOrDefault(d => d.Name.LocalName.Equals("Provider", StringComparison.OrdinalIgnoreCase))?.Attribute("Name")?.Value 
                                ?? elem.Element("Source")?.Value 
                                ?? "N/A";

                string user = elem.Descendants().FirstOrDefault(d => d.Name.LocalName.Equals("Security", StringComparison.OrdinalIgnoreCase))?.Attribute("UserId")?.Value 
                              ?? elem.Element("User")?.Value 
                              ?? "N/A";

                string computer = elem.Descendants().FirstOrDefault(d => d.Name.LocalName.Equals("Computer", StringComparison.OrdinalIgnoreCase))?.Value 
                                  ?? elem.Element("Host")?.Value 
                                  ?? "N/A";

                string message = elem.Element("Message")?.Value 
                                 ?? elem.Element("Description")?.Value 
                                 ?? elem.ToString();

                int.TryParse(eventId, out int evIdNum);
                var advice = ForensicAdvisor.GetAdvice(evIdNum);
                var isAlert = ForensicAdvisor.IsForensicHighlight(evIdNum);

                entries.Add(new ImportedLogEntry
                {
                    RecordId = idx++,
                    EventId = eventId,
                    TimeCreated = time,
                    LogName = Path.GetFileName(filePath),
                    Source = source,
                    Level = level,
                    User = user,
                    Computer = computer,
                    Message = message,
                    RawData = elem.ToString(),
                    IsForensicAlert = isAlert,
                    ForensicCategory = isAlert ? advice.Category : string.Empty,
                    ForensicTitle = isAlert ? advice.Title : string.Empty
                });
            }

            if (entries.Count == 0)
            {
                entries.Add(new ImportedLogEntry
                {
                    RecordId = 1,
                    EventId = "0",
                    TimeCreated = DateTime.Now,
                    LogName = Path.GetFileName(filePath),
                    Source = "XML Parser",
                    Level = "Information",
                    Message = "Raw XML imported (no structured Event nodes detected).",
                    RawData = doc.ToString()
                });
            }

            entries.Sort((x, y) => y.TimeCreated.CompareTo(x.TimeCreated));
            return entries;
        }

        private static List<ImportedLogEntry> ParseCsv(string filePath)
        {
            var entries = new List<ImportedLogEntry>();
            var lines = File.ReadAllLines(filePath);
            if (lines.Length <= 1) return entries;

            string[] headers = SplitCsvLine(lines[0]);
            
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                string[] fields = SplitCsvLine(lines[i]);
                
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int j = 0; j < headers.Length; j++)
                {
                    if (j < fields.Length)
                    {
                        dict[headers[j]] = fields[j];
                    }
                }

                string timeStr = GetDictValue(dict, "TimeCreated", "Time", "Timestamp", "Date", "Created") ?? DateTime.Now.ToString();
                DateTime.TryParse(timeStr, out DateTime time);

                string eventId = GetDictValue(dict, "EventId", "Id", "EventID", "Event_ID") ?? "0";
                string level = GetDictValue(dict, "Level", "Severity", "Type") ?? "Information";
                string source = GetDictValue(dict, "Source", "Provider", "ProviderName") ?? "N/A";
                string user = GetDictValue(dict, "User", "UserId", "Username", "Account") ?? "N/A";
                string computer = GetDictValue(dict, "Computer", "MachineName", "Host") ?? "N/A";
                string message = GetDictValue(dict, "Message", "Description", "Detail") ?? string.Join(", ", fields);

                int.TryParse(eventId, out int evIdNum);
                var advice = ForensicAdvisor.GetAdvice(evIdNum);
                var isAlert = ForensicAdvisor.IsForensicHighlight(evIdNum);

                entries.Add(new ImportedLogEntry
                {
                    RecordId = i,
                    EventId = eventId,
                    TimeCreated = time,
                    LogName = Path.GetFileName(filePath),
                    Source = source,
                    Level = level,
                    User = user,
                    Computer = computer,
                    Message = message,
                    RawData = lines[i],
                    IsForensicAlert = isAlert,
                    ForensicCategory = isAlert ? advice.Category : string.Empty,
                    ForensicTitle = isAlert ? advice.Title : string.Empty
                });
            }

            entries.Sort((x, y) => y.TimeCreated.CompareTo(x.TimeCreated));
            return entries;
        }

        private static string[] SplitCsvLine(string line)
        {
            var result = new List<string>();
            var currentToken = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(currentToken.ToString().Trim(' ', '\"'));
                    currentToken.Clear();
                }
                else
                {
                    currentToken.Append(c);
                }
            }
            result.Add(currentToken.ToString().Trim(' ', '\"'));
            return result.ToArray();
        }

        private static List<ImportedLogEntry> ParseExcel(string filePath)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            var entries = new List<ImportedLogEntry>();
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var result = reader.AsDataSet();
            
            if (result.Tables.Count == 0) return entries;
            var table = result.Tables[0];
            if (table.Rows.Count <= 1) return entries;

            var headers = new List<string>();
            for (int col = 0; col < table.Columns.Count; col++)
            {
                headers.Add(table.Rows[0][col]?.ToString()?.Trim() ?? $"Column{col}");
            }

            for (int row = 1; row < table.Rows.Count; row++)
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int col = 0; col < headers.Count; col++)
                {
                    if (col < table.Columns.Count)
                    {
                        dict[headers[col]] = table.Rows[row][col]?.ToString() ?? string.Empty;
                    }
                }

                string timeStr = GetDictValue(dict, "TimeCreated", "Time", "Timestamp", "Date", "Created") ?? DateTime.Now.ToString();
                DateTime.TryParse(timeStr, out DateTime time);

                string eventId = GetDictValue(dict, "EventId", "Id", "EventID", "Event_ID") ?? "0";
                string level = GetDictValue(dict, "Level", "Severity", "Type") ?? "Information";
                string source = GetDictValue(dict, "Source", "Provider", "ProviderName") ?? "N/A";
                string user = GetDictValue(dict, "User", "UserId", "Username", "Account") ?? "N/A";
                string computer = GetDictValue(dict, "Computer", "MachineName", "Host") ?? "N/A";
                
                string message = GetDictValue(dict, "Message", "Description", "Detail") ?? string.Empty;
                if (string.IsNullOrEmpty(message))
                {
                    var rowValues = new List<string>();
                    for (int col = 0; col < table.Columns.Count; col++)
                    {
                        rowValues.Add(table.Rows[row][col]?.ToString() ?? "");
                    }
                    message = string.Join(" | ", rowValues.Where(x => !string.IsNullOrEmpty(x)));
                }

                int.TryParse(eventId, out int evIdNum);
                var advice = ForensicAdvisor.GetAdvice(evIdNum);
                var isAlert = ForensicAdvisor.IsForensicHighlight(evIdNum);

                entries.Add(new ImportedLogEntry
                {
                    RecordId = row,
                    EventId = eventId,
                    TimeCreated = time,
                    LogName = Path.GetFileName(filePath),
                    Source = source,
                    Level = level,
                    User = user,
                    Computer = computer,
                    Message = message,
                    RawData = string.Join("\r\n", dict.Select(kv => $"{kv.Key}: {kv.Value}")),
                    IsForensicAlert = isAlert,
                    ForensicCategory = isAlert ? advice.Category : string.Empty,
                    ForensicTitle = isAlert ? advice.Title : string.Empty
                });
            }

            entries.Sort((x, y) => y.TimeCreated.CompareTo(x.TimeCreated));
            return entries;
        }

        private static string? GetDictValue(Dictionary<string, string> dict, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (dict.TryGetValue(key, out var val)) return val;
            }
            return null;
        }
    }
}
