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
                ".log" or ".txt" => ParseTextLog(filePath),
                _ => throw new NotSupportedException($"The file extension '{ext}' is not supported. Supported: .evtx, .json, .xml, .csv, .xlsx, .xls, .log, .txt")
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

        private static readonly System.Text.RegularExpressions.Regex AccessLogRegex = 
            new System.Text.RegularExpressions.Regex(
                @"^(?<ip>\S+)\s+(?<identd>\S+)\s+(?<user>\S+)\s+\[(?<time>[^\]]+)\]\s+""(?<request>[^""]*)""\s+(?<status>\d{3})\s+(?<size>\S+)(?:\s+""(?<referer>[^""]*)""\s+""(?<agent>[^""]*)""\s*)?$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex NginxErrorRegex = 
            new System.Text.RegularExpressions.Regex(
                @"^(?<time>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})\s+\[(?<level>[^\]]+)\]\s+(?<msg>.*)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex ApacheErrorRegex = 
            new System.Text.RegularExpressions.Regex(
                @"^\[(?<day>[A-Za-z]{3})\s+(?<time>[A-Za-z]{3}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?\s+\d{4})\]\s+\[(?:(?<module>[^:]+):)?(?<level>[^\]]+)\]\s+(?:\[client\s+(?<client>[^\]]+)\]\s+)?(?<msg>.*)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static List<ImportedLogEntry> ParseTextLog(string filePath)
        {
            var entries = new List<ImportedLogEntry>();
            if (!File.Exists(filePath)) return entries;

            var lines = File.ReadLines(filePath).ToList();
            long idx = 1;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // 1. Try Apache/Nginx Combined/Common Access Log
                var matchAccess = AccessLogRegex.Match(line);
                if (matchAccess.Success)
                {
                    string ip = matchAccess.Groups["ip"].Value;
                    string user = matchAccess.Groups["user"].Value;
                    if (user == "-") user = "N/A";
                    string rawTime = matchAccess.Groups["time"].Value;
                    DateTime time = ParseAccessLogDate(rawTime);
                    string request = matchAccess.Groups["request"].Value;
                    string status = matchAccess.Groups["status"].Value;
                    string size = matchAccess.Groups["size"].Value;
                    string referer = matchAccess.Groups["referer"].Success ? matchAccess.Groups["referer"].Value : "-";
                    string agent = matchAccess.Groups["agent"].Success ? matchAccess.Groups["agent"].Value : "-";

                    string level = "Information";
                    if (status.StartsWith("5")) level = "Error";
                    else if (status.StartsWith("4")) level = "Warning";
                    if (status == "403" || status == "401") level = "Critical";

                    bool isAttack = DetectWebAttack(request, out string attackCategory, out string attackTitle, out string attackDetails);

                    int eventIdVal = isAttack ? 8004 : 8003;
                    var advice = ForensicAdvisor.GetAdvice(eventIdVal);
                    string msg = $"Request: {request}\nStatus: {status}\nBytes Sent: {size}\nReferer: {referer}\nUser-Agent: {agent}";
                    if (isAttack)
                    {
                        msg = $"[ALERT: {attackTitle}]\nDetails: {attackDetails}\n\n{msg}";
                        level = "Critical";
                    }

                    string webSource = "Web Access Log";
                    if (filePath.Contains("nginx", StringComparison.OrdinalIgnoreCase))
                        webSource = "Nginx Access Log";
                    else if (filePath.Contains("apache", StringComparison.OrdinalIgnoreCase))
                        webSource = "Apache Access Log";

                    entries.Add(new ImportedLogEntry
                    {
                        RecordId = idx++,
                        EventId = eventIdVal.ToString(),
                        TimeCreated = time,
                        LogName = Path.GetFileName(filePath),
                        Source = webSource,
                        Level = level,
                        User = user,
                        Computer = ip,
                        Message = msg,
                        RawData = line,
                        IsForensicAlert = isAttack || status == "403" || status == "401",
                        ForensicCategory = isAttack ? attackCategory : (status == "403" || status == "401" ? "Unauthorized Access" : string.Empty),
                        ForensicTitle = isAttack ? attackTitle : (status == "403" ? "HTTP 403 Forbidden" : (status == "401" ? "HTTP 401 Unauthorized" : string.Empty)),
                        TaskCategory = "HTTP Request",
                        Keywords = status,
                        Opcode = "Access"
                    });
                    continue;
                }

                // 2. Try Nginx Error Log
                var matchNginxErr = NginxErrorRegex.Match(line);
                if (matchNginxErr.Success)
                {
                    string rawTime = matchNginxErr.Groups["time"].Value;
                    DateTime time = ParseNginxErrorDate(rawTime);
                    string levelRaw = matchNginxErr.Groups["level"].Value;
                    string msg = matchNginxErr.Groups["msg"].Value;

                    string level = levelRaw.ToLower() switch
                    {
                        "emerg" or "alert" or "crit" => "Critical",
                        "error" => "Error",
                        "warn" or "warning" => "Warning",
                        _ => "Information"
                    };

                    string clientIp = "N/A";
                    var clientMatch = System.Text.RegularExpressions.Regex.Match(msg, @"client:\s*(?<ip>[0-9a-fA-F\.:]+)");
                    if (clientMatch.Success) clientIp = clientMatch.Groups["ip"].Value;

                    entries.Add(new ImportedLogEntry
                    {
                        RecordId = idx++,
                        EventId = "8002",
                        TimeCreated = time,
                        LogName = Path.GetFileName(filePath),
                        Source = "Nginx Error Log",
                        Level = level,
                        User = "N/A",
                        Computer = clientIp,
                        Message = msg,
                        RawData = line,
                        IsForensicAlert = level == "Critical" || level == "Error",
                        ForensicCategory = level == "Critical" || level == "Error" ? "Web Server Error" : string.Empty,
                        ForensicTitle = level == "Critical" || level == "Error" ? "Nginx Server Diagnostic Alert" : string.Empty,
                        TaskCategory = "Server Diagnostics",
                        Keywords = levelRaw,
                        Opcode = "Error"
                    });
                    continue;
                }

                // 3. Try Apache Error Log
                var matchApacheErr = ApacheErrorRegex.Match(line);
                if (matchApacheErr.Success)
                {
                    string rawTime = matchApacheErr.Groups["time"].Value;
                    DateTime time = ParseApacheErrorDate(rawTime);
                    string levelRaw = matchApacheErr.Groups["level"].Value;
                    string clientIp = matchApacheErr.Groups["client"].Success ? matchApacheErr.Groups["client"].Value : "N/A";
                    string msg = matchApacheErr.Groups["msg"].Value;

                    string level = levelRaw.ToLower() switch
                    {
                        "emerg" or "alert" or "crit" => "Critical",
                        "error" => "Error",
                        "warn" or "warning" => "Warning",
                        _ => "Information"
                    };

                    entries.Add(new ImportedLogEntry
                    {
                        RecordId = idx++,
                        EventId = "8001",
                        TimeCreated = time,
                        LogName = Path.GetFileName(filePath),
                        Source = "Apache Error Log",
                        Level = level,
                        User = "N/A",
                        Computer = clientIp,
                        Message = msg,
                        RawData = line,
                        IsForensicAlert = level == "Critical" || level == "Error",
                        ForensicCategory = level == "Critical" || level == "Error" ? "Web Server Error" : string.Empty,
                        ForensicTitle = level == "Critical" || level == "Error" ? "Apache Server Diagnostic Alert" : string.Empty,
                        TaskCategory = "Server Diagnostics",
                        Keywords = levelRaw,
                        Opcode = "Error"
                    });
                    continue;
                }

                // 4. Fallback: Generic Log Entry
                DateTime genericTime = ExtractGenericTimestamp(line, filePath);
                string genericLevel = "Information";
                if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || 
                    line.Contains("FAIL", StringComparison.OrdinalIgnoreCase) || 
                    line.Contains("EXCEPTION", StringComparison.OrdinalIgnoreCase))
                {
                    genericLevel = "Error";
                }
                else if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase))
                {
                    genericLevel = "Warning";
                }
                else if (line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) || 
                         line.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase))
                {
                    genericLevel = "Critical";
                }

                entries.Add(new ImportedLogEntry
                {
                    RecordId = idx++,
                    EventId = "9001",
                    TimeCreated = genericTime,
                    LogName = Path.GetFileName(filePath),
                    Source = "Generic Log Parser",
                    Level = genericLevel,
                    User = "N/A",
                    Computer = "N/A",
                    Message = line.Trim(),
                    RawData = line,
                    IsForensicAlert = genericLevel == "Critical" || genericLevel == "Error",
                    ForensicCategory = genericLevel == "Critical" || genericLevel == "Error" ? "Generic Error Log" : string.Empty,
                    ForensicTitle = genericLevel == "Critical" || genericLevel == "Error" ? "Application Diagnostic Alert" : string.Empty,
                    TaskCategory = "Application Log",
                    Keywords = "Generic",
                    Opcode = "Log"
                });
            }

            entries.Sort((x, y) => y.TimeCreated.CompareTo(x.TimeCreated));
            return entries;
        }

        private static DateTime ParseAccessLogDate(string timeStr)
        {
            string[] formats = {
                "dd/MMM/yyyy:HH:mm:ss zzz",
                "dd/MMM/yyyy:HH:mm:ss Z",
                "dd/MMM/yyyy:HH:mm:ss"
            };
            if (DateTime.TryParseExact(timeStr, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime dt))
            {
                return dt;
            }
            int spaceIdx = timeStr.IndexOf(' ');
            if (spaceIdx > 0)
            {
                string trimmed = timeStr.Substring(0, spaceIdx);
                if (DateTime.TryParseExact(trimmed, "dd/MMM/yyyy:HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt))
                {
                    return dt;
                }
            }
            if (DateTime.TryParse(timeStr, out dt))
            {
                return dt;
            }
            return DateTime.Now;
        }

        private static DateTime ParseNginxErrorDate(string timeStr)
        {
            if (DateTime.TryParseExact(timeStr, "yyyy/MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime dt))
            {
                return dt;
            }
            if (DateTime.TryParse(timeStr, out dt))
            {
                return dt;
            }
            return DateTime.Now;
        }

        private static DateTime ParseApacheErrorDate(string timeStr)
        {
            string[] formats = {
                "MMM dd HH:mm:ss yyyy",
                "MMM  d HH:mm:ss yyyy",
                "MMM d HH:mm:ss yyyy",
                "MMM dd HH:mm:ss.ffffff yyyy",
                "MMM  d HH:mm:ss.ffffff yyyy",
                "MMM d HH:mm:ss.ffffff yyyy"
            };
            if (DateTime.TryParseExact(timeStr, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime dt))
            {
                return dt;
            }
            if (DateTime.TryParse(timeStr, out dt))
            {
                return dt;
            }
            return DateTime.Now;
        }

        private static DateTime ExtractGenericTimestamp(string line, string filePath)
        {
            var matchIso = System.Text.RegularExpressions.Regex.Match(line, @"\b(?<date>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?)\b");
            if (matchIso.Success && DateTime.TryParse(matchIso.Groups["date"].Value, out DateTime dtIso))
            {
                return dtIso;
            }

            var matchSyslog = System.Text.RegularExpressions.Regex.Match(line, @"\b(?<date>[A-Za-z]{3}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2})\b");
            if (matchSyslog.Success)
            {
                string dtStr = matchSyslog.Groups["date"].Value + " " + DateTime.Now.Year;
                if (DateTime.TryParse(dtStr, out DateTime dtSys))
                {
                    return dtSys;
                }
            }

            var matchSlash = System.Text.RegularExpressions.Regex.Match(line, @"\b(?<date>\d{2}/[A-Za-z]{3}/\d{4} \d{2}:\d{2}:\d{2})\b");
            if (matchSlash.Success && DateTime.TryParse(matchSlash.Groups["date"].Value, out DateTime dtSlash))
            {
                return dtSlash;
            }

            try
            {
                return File.GetLastWriteTime(filePath);
            }
            catch
            {
                return DateTime.Now;
            }
        }

        private static bool DetectWebAttack(string request, out string category, out string title, out string details)
        {
            category = string.Empty;
            title = string.Empty;
            details = string.Empty;

            if (string.IsNullOrEmpty(request)) return false;

            if (request.Contains("etc/passwd", StringComparison.OrdinalIgnoreCase) || 
                request.Contains("etc/shadow", StringComparison.OrdinalIgnoreCase) || 
                request.Contains("boot.ini", StringComparison.OrdinalIgnoreCase) || 
                request.Contains("win.ini", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("../", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("..\\", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("..%2f", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("..%5c", StringComparison.OrdinalIgnoreCase))
            {
                category = "Directory Traversal";
                title = "Path Traversal / File Disclosure Probe";
                details = "The request query path contains directory traversal signatures (e.g., ../ or system files like etc/passwd).";
                return true;
            }

            if (request.Contains("union ", StringComparison.OrdinalIgnoreCase) && request.Contains("select", StringComparison.OrdinalIgnoreCase) || 
                request.Contains("select ", StringComparison.OrdinalIgnoreCase) && request.Contains("from", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("insert into", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("update ", StringComparison.OrdinalIgnoreCase) && request.Contains("set", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("delete from", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("sysdatabases", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("sysobjects", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("sqlmap", StringComparison.OrdinalIgnoreCase))
            {
                category = "SQL Injection";
                title = "SQL Injection (SQLi) Attempt";
                details = "The request URI contains active SQL commands or keywords typically used to retrieve database records unauthorized.";
                return true;
            }

            if (request.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) || 
                request.Contains("/bin/sh", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("/bin/bash", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("whoami", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("wget ", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("curl ", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("nc -e", StringComparison.OrdinalIgnoreCase))
            {
                category = "Command Injection";
                title = "Remote Command Execution (RCE) / Command Injection Probe";
                details = "The request includes executable shell script parameters or calls to administrative operating system tools.";
                return true;
            }

            if (request.Contains(".git/", StringComparison.OrdinalIgnoreCase) || 
                request.Contains(".env", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("wp-config.php", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("config.php", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("database.yml", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("backup", StringComparison.OrdinalIgnoreCase) && request.Contains(".zip", StringComparison.OrdinalIgnoreCase) ||
                request.Contains(".sql", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("phpinfo.php", StringComparison.OrdinalIgnoreCase))
            {
                category = "Information Disclosure";
                title = "Sensitive File / Configuration Disclosure Probe";
                details = "The request attempts to access system configuration files, environment variables, git repositories, or database backups.";
                return true;
            }

            if (request.Contains("<script>", StringComparison.OrdinalIgnoreCase) || 
                request.Contains("onerror=", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("onload=", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("alert(", StringComparison.OrdinalIgnoreCase))
            {
                category = "Cross-Site Scripting";
                title = "Cross-Site Scripting (XSS) Inject Probe";
                details = "HTML/JavaScript payload elements were detected in the HTTP request URI parameters.";
                return true;
            }

            return false;
        }
    }
}
