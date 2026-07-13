using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinLog
{
    public partial class MainWindow : Window
    {
        private List<ImportedLogEntry> _allEvents = new();
        private List<ImportedLogEntry> _filteredEvents = new();
        private List<string> _currentFilePaths = new();

        public MainWindow()
        {
            InitializeComponent();
            MachineNameText.Text = Environment.MachineName;
            UpdateActiveFileLabel();
        }

        private void OnImportClicked(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = "Select Log File(s) to Analyze",
                Filter = "All Supported Logs|*.evtx;*.json;*.xml;*.csv;*.xlsx;*.xls;*.log;*.txt" +
                         "|Windows Event Log (*.evtx)|*.evtx" +
                         "|Web & Application Text Logs (*.log; *.txt)|*.log;*.txt" +
                         "|JSON Log Array (*.json)|*.json" +
                         "|XML Log File (*.xml)|*.xml" +
                         "|CSV Spreadsheets (*.csv)|*.csv" +
                         "|Excel Workbooks (*.xlsx; *.xls)|*.xlsx;*.xls" +
                         "|All Files (*.*)|*.*",
                Multiselect = true
            };

            if (ofd.ShowDialog() == true)
            {
                this.Cursor = Cursors.Wait;
                var sw = Stopwatch.StartNew();

                try
                {
                    var combinedEvents = new List<ImportedLogEntry>();
                    var loadedPaths = new List<string>();

                    foreach (string filePath in ofd.FileNames)
                    {
                        var parsed = LogParser.ParseFile(filePath);
                        combinedEvents.AddRange(parsed);
                        loadedPaths.Add(filePath);
                    }

                    // Sort chronologically (newest first)
                    _allEvents = combinedEvents.OrderByDescending(ev => ev.TimeCreated).ToList();
                    _currentFilePaths = loadedPaths;
                    
                    sw.Stop();
                    StatusLoadTimeText.Text = $"Parsed {ofd.FileNames.Length} files in: {sw.ElapsedMilliseconds} ms";
                    UpdateActiveFileLabel();
                    UpdateLogFileComboBox();
                    UpdatePresetButtonsState();
                    UpdateDynamicQuickFilters();
                    ApplyFilters();
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    try
                    {
                        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error_log.txt"), ex.ToString());
                    }
                    catch { }
                    MessageBox.Show($"Failed to parse log file(s):\n{ex.Message}\n\nFull details written to error_log.txt in your workspace.", "Parser Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    this.Cursor = Cursors.Arrow;
                }
            }
        }

        private void ApplyFilters()
        {
            if (SearchTextBox == null || FilterEventIdTextBox == null || FilterLevelComboBox == null || EventsDataGrid == null || StatusCountText == null)
            {
                return;
            }

            var filtered = _allEvents.AsEnumerable();

            // 1. Text Search (Matches Message, Source, User, Computer)
            string searchText = SearchTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(searchText))
            {
                filtered = filtered.Where(e =>
                    (e.Message != null && e.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase)) ||
                    (e.Source != null && e.Source.Contains(searchText, StringComparison.OrdinalIgnoreCase)) ||
                    (e.User != null && e.User.Contains(searchText, StringComparison.OrdinalIgnoreCase)) ||
                    (e.Computer != null && e.Computer.Contains(searchText, StringComparison.OrdinalIgnoreCase)) ||
                    e.EventId.Contains(searchText)
                );
            }

            // 2. Event ID Filter (supports multiple IDs separated by comma, space, or pipe)
            string idFilter = FilterEventIdTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(idFilter))
            {
                var ids = idFilter.Split(new[] { ',', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(id => id.Trim())
                                  .ToList();
                if (ids.Any())
                {
                    filtered = filtered.Where(e => ids.Any(id => e.EventId.Equals(id, StringComparison.OrdinalIgnoreCase)));
                }
            }

            // 3. Level Filter
            if (FilterLevelComboBox.SelectedItem is ComboBoxItem levelItem)
            {
                string? levelStr = levelItem.Content?.ToString();
                if (levelStr != null && levelStr != "All Levels")
                {
                    filtered = filtered.Where(e => e.Level.Equals(levelStr, StringComparison.OrdinalIgnoreCase));
                }
            }

            // 4. Log File Source Filter
            if (FilterLogFileComboBox != null && FilterLogFileComboBox.SelectedItem is ComboBoxItem logFileItem)
            {
                string? selectedLogFile = logFileItem.Content?.ToString();
                if (selectedLogFile != null && selectedLogFile != "All Files")
                {
                    filtered = filtered.Where(e => e.LogName.Equals(selectedLogFile, StringComparison.OrdinalIgnoreCase));
                }
            }

            // 5. Date and Time Range Filter
            if (StartDatePicker != null && StartDatePicker.SelectedDate.HasValue)
            {
                DateTime startDate = StartDatePicker.SelectedDate.Value;
                string startTimeStr = StartTimeTextBox != null ? StartTimeTextBox.Text.Trim() : "00:00:00";
                if (TimeSpan.TryParse(startTimeStr, out TimeSpan startTime))
                {
                    startDate = startDate.Date + startTime;
                }
                filtered = filtered.Where(e => e.TimeCreated >= startDate);
            }

            if (EndDatePicker != null && EndDatePicker.SelectedDate.HasValue)
            {
                DateTime endDate = EndDatePicker.SelectedDate.Value;
                string endTimeStr = EndTimeTextBox != null ? EndTimeTextBox.Text.Trim() : "23:59:59";
                if (TimeSpan.TryParse(endTimeStr, out TimeSpan endTime))
                {
                    endDate = endDate.Date + endTime;
                }
                filtered = filtered.Where(e => e.TimeCreated <= endDate);
            }

            _filteredEvents = filtered.ToList();
            EventsDataGrid.ItemsSource = _filteredEvents;
            StatusCountText.Text = (_currentFilePaths == null || _currentFilePaths.Count == 0) 
                ? "No file loaded." 
                : $"Loaded {_allEvents.Count:N0} logs. Showing {_filteredEvents.Count:N0} filtered.";

            UpdatePresetVisualStates();
        }

        private void UpdateActiveFileLabel()
        {
            if (_currentFilePaths == null || _currentFilePaths.Count == 0)
            {
                ActiveFileText.Text = "Active File: None";
            }
            else if (_currentFilePaths.Count == 1)
            {
                string path = _currentFilePaths[0];
                ActiveFileText.Text = $"Active File: {Path.GetFileName(path)} ({GetFileSizeString(path)})";
            }
            else
            {
                long totalSize = 0;
                foreach (var path in _currentFilePaths)
                {
                    try { totalSize += new FileInfo(path).Length; } catch { }
                }
                string sizeStr = GetFileSizeStringFromLength(totalSize);
                string names = string.Join(", ", _currentFilePaths.Select(Path.GetFileName));
                if (names.Length > 60) names = names.Substring(0, 57) + "...";
                ActiveFileText.Text = $"Active File: [Multiple Files] ({names}) - {sizeStr}";
            }
        }

        private void UpdateLogFileComboBox()
        {
            if (FilterLogFileComboBox == null) return;

            FilterLogFileComboBox.Items.Clear();
            FilterLogFileComboBox.Items.Add(new ComboBoxItem { Content = "All Files", IsSelected = true });

            if (_currentFilePaths != null && _currentFilePaths.Count > 0)
            {
                foreach (var path in _currentFilePaths)
                {
                    FilterLogFileComboBox.Items.Add(new ComboBoxItem { Content = Path.GetFileName(path) });
                }
            }
        }

        private string GetFileSizeString(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                return GetFileSizeStringFromLength(fi.Length);
            }
            catch
            {
                return "Unknown Size";
            }
        }

        private string GetFileSizeStringFromLength(long len)
        {
            double dLen = len;
            if (dLen < 1024) return $"{dLen} B";
            if (dLen < 1024 * 1024) return $"{dLen / 1024:F1} KB";
            return $"{dLen / (1024 * 1024):F1} MB";
        }

        private void OnFilterChanged(object sender, RoutedEventArgs e)
        {
            ApplyFilters();
        }

        private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void OnResetFiltersClicked(object sender, RoutedEventArgs e)
        {
            if (SearchTextBox != null) SearchTextBox.Text = string.Empty;
            if (FilterEventIdTextBox != null) FilterEventIdTextBox.Text = string.Empty;
            if (FilterLevelComboBox != null) FilterLevelComboBox.SelectedIndex = 0; // All Levels
            if (FilterLogFileComboBox != null) FilterLogFileComboBox.SelectedIndex = 0; // All Files
            if (StartDatePicker != null) StartDatePicker.SelectedDate = null;
            if (StartTimeTextBox != null) StartTimeTextBox.Text = "00:00:00";
            if (EndDatePicker != null) EndDatePicker.SelectedDate = null;
            if (EndTimeTextBox != null) EndTimeTextBox.Text = "23:59:59";
            ApplyFilters();
        }

        private void OnClearDataClicked(object sender, RoutedEventArgs e)
        {
            _allEvents.Clear();
            _filteredEvents.Clear();
            _currentFilePaths.Clear();
            StatusLoadTimeText.Text = string.Empty;
            
            OnResetFiltersClicked(sender, e);
            UpdateActiveFileLabel();
            UpdateLogFileComboBox();
            UpdatePresetButtonsState();
            UpdateDynamicQuickFilters();
            ApplyFilters();
        }

        // Row selection detail display
        private void OnEventSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EventsDataGrid.SelectedItem is ImportedLogEntry entry)
            {
                DetailEventIdText.Text = entry.EventId;
                DetailProviderText.Text = entry.Source;

                // Load forensic guidance if Event ID matches Windows security presets
                int.TryParse(entry.EventId, out int evId);
                var advice = ForensicAdvisor.GetAdvice(evId);

                ForensicTitleText.Text = advice.Title;
                ForensicCategoryText.Text = advice.Category.ToUpper();
                ForensicDescText.Text = advice.Description;
                ForensicStepsText.Text = advice.InvestigationSteps;

                // Set soft color badge border for warnings/errors (no heavy backgrounds)
                if (entry.IsForensicAlert)
                {
                    ForensicBadge.BorderBrush = advice.Severity switch
                    {
                        "Critical" => new SolidColorBrush(Color.FromRgb(185, 28, 28)), // Red border
                        "Warning" => new SolidColorBrush(Color.FromRgb(217, 119, 6)),  // Orange border
                        _ => new SolidColorBrush(Color.FromRgb(2, 132, 199))           // Blue border
                    };
                    ForensicCategoryText.Foreground = advice.Severity switch
                    {
                        "Critical" => new SolidColorBrush(Color.FromRgb(185, 28, 28)),
                        "Warning" => new SolidColorBrush(Color.FromRgb(217, 119, 6)),
                        _ => new SolidColorBrush(Color.FromRgb(2, 132, 199))
                    };
                }
                else
                {
                    ForensicBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)); // Slate-200 border
                    ForensicCategoryText.Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)); // slate-600
                }

                DetailMessageTextBox.Text = entry.Message;
                DetailXmlTextBox.Text = entry.RawData;

                // Populate Microsoft Event Viewer metadata layout
                MetaLogNameText.Text = entry.LogName;
                MetaLoggedText.Text = entry.TimeCreated.ToString("yyyy-MM-dd HH:mm:ss");
                MetaSourceText.Text = entry.Source;
                MetaTaskCategoryText.Text = entry.TaskCategory;
                MetaEventIdText.Text = entry.EventId;
                MetaKeywordsText.Text = entry.Keywords;
                MetaLevelText.Text = entry.Level;
                MetaComputerText.Text = entry.Computer;
                MetaUserText.Text = entry.User;
                MetaOpcodeText.Text = entry.Opcode;
            }
            else
            {
                DetailEventIdText.Text = "None";
                DetailProviderText.Text = "None";
                ForensicTitleText.Text = "Generic Event";
                ForensicCategoryText.Text = "UNSPECIFIED CATEGORY";
                ForensicCategoryText.Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105));
                ForensicBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225));
                ForensicDescText.Text = "No pre-configured security alert definitions for this Event ID.";
                ForensicStepsText.Text = "Analyze event parameters and message context.";
                DetailMessageTextBox.Text = string.Empty;
                DetailXmlTextBox.Text = string.Empty;

                // Clear Microsoft Event Viewer metadata layout
                MetaLogNameText.Text = "None";
                MetaLoggedText.Text = "None";
                MetaSourceText.Text = "None";
                MetaTaskCategoryText.Text = "None";
                MetaEventIdText.Text = "None";
                MetaKeywordsText.Text = "None";
                MetaLevelText.Text = "None";
                MetaComputerText.Text = "None";
                MetaUserText.Text = "None";
                MetaOpcodeText.Text = "None";
            }
        }

        // Forensic Presets Click Triggers
        private void OnPresetClearLogs(object sender, RoutedEventArgs e)
        {
            TogglePresetEventIds("104, 1102");
        }

        private void OnPresetFailedLogons(object sender, RoutedEventArgs e)
        {
            TogglePresetEventIds("4625");
        }

        private void OnPresetSuccessfulLogons(object sender, RoutedEventArgs e)
        {
            TogglePresetEventIds("4624");
        }

        private void OnPresetServiceInstalls(object sender, RoutedEventArgs e)
        {
            TogglePresetEventIds("7045");
        }

        private void OnPresetAccountChanges(object sender, RoutedEventArgs e)
        {
            TogglePresetEventIds("4720, 4722, 4724, 4738, 4732");
        }

        private void OnPresetPowerShell(object sender, RoutedEventArgs e)
        {
            TogglePresetEventIds("4104");
        }

        private void OnPresetProcessExecution(object sender, RoutedEventArgs e)
        {
            TogglePresetEventIds("4688");
        }

        private void OnPresetSystemPower(object sender, RoutedEventArgs e)
        {
            TogglePresetEventIds("6005, 6006, 1074");
        }

        private void TogglePresetEventIds(string targetIds)
        {
            if (FilterEventIdTextBox == null) return;

            string currentText = FilterEventIdTextBox.Text.Trim();
            if (string.IsNullOrEmpty(currentText))
            {
                FilterEventIdTextBox.Text = targetIds;
            }
            else
            {
                var existing = currentText.Split(new[] { ',', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(id => id.Trim())
                                           .ToList();

                var targets = targetIds.Split(new[] { ',', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(id => id.Trim())
                                        .ToList();

                bool allPresent = targets.All(t => existing.Any(e => e.Equals(t, StringComparison.OrdinalIgnoreCase)));

                if (allPresent)
                {
                    existing = existing.Where(e => !targets.Any(t => t.Equals(e, StringComparison.OrdinalIgnoreCase))).ToList();
                }
                else
                {
                    foreach (var t in targets)
                    {
                        if (!existing.Any(e => e.Equals(t, StringComparison.OrdinalIgnoreCase)))
                        {
                            existing.Add(t);
                        }
                    }
                }

                FilterEventIdTextBox.Text = string.Join(", ", existing);
            }

            if (FilterLevelComboBox != null) FilterLevelComboBox.SelectedIndex = 0;
            ApplyFilters();
        }

        private void UpdatePresetVisualStates()
        {
            if (FilterEventIdTextBox == null) return;
            string currentText = FilterEventIdTextBox.Text.Trim();
            var existing = currentText.Split(new[] { ',', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(id => id.Trim())
                                       .ToList();

            HighlightButton(PresetClearLogsBtn, existing.Contains("104") || existing.Contains("1102"));
            HighlightButton(PresetFailedLogonsBtn, existing.Contains("4625"));
            HighlightButton(PresetSuccessfulLogonsBtn, existing.Contains("4624"));
            HighlightButton(PresetServiceInstallsBtn, existing.Contains("7045"));
            HighlightButton(PresetAccountChangesBtn, existing.Contains("4720") || existing.Contains("4722") || existing.Contains("4724") || existing.Contains("4738") || existing.Contains("4732"));
            HighlightButton(PresetPowerShellBtn, existing.Contains("4104"));
            HighlightButton(PresetProcessExecutionBtn, existing.Contains("4688"));
            HighlightButton(PresetSystemPowerBtn, existing.Contains("6005") || existing.Contains("6006") || existing.Contains("1074"));
        }

        private void HighlightButton(Button btn, bool highlight)
        {
            if (btn == null) return;
            if (highlight)
            {
                btn.Background = new SolidColorBrush(Color.FromRgb(224, 242, 254)); // sky-100
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // sky-400
                btn.BorderThickness = new Thickness(1);
            }
            else
            {
                btn.Background = SystemParameters.HighContrast ? SystemColors.ControlBrush : new SolidColorBrush(Color.FromRgb(241, 245, 249)); // slate-100
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)); // slate-200
                btn.BorderThickness = new Thickness(1);
            }
        }

        // Export active grid rows
        private void OnExportCsvClicked(object sender, RoutedEventArgs e)
        {
            if (_filteredEvents.Count == 0)
            {
                MessageBox.Show("No records in current view to export.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sfd = new SaveFileDialog
            {
                Filter = "CSV Spreadsheet (*.csv)|*.csv",
                FileName = $"Forensic_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Index,TimeCreated,EventId,Level,LogName,Source,User,Computer,Message");

                    foreach (var entry in _filteredEvents)
                    {
                        sb.AppendLine(string.Format("{0},\"{1}\",\"{2}\",\"{3}\",\"{4}\",\"{5}\",\"{6}\",\"{7}\",\"{8}\"",
                            entry.RecordId,
                            entry.TimeCreated.ToString("yyyy-MM-dd HH:mm:ss"),
                            EscapeCsvField(entry.EventId),
                            EscapeCsvField(entry.Level),
                            EscapeCsvField(entry.LogName),
                            EscapeCsvField(entry.Source),
                            EscapeCsvField(entry.User),
                            EscapeCsvField(entry.Computer),
                            EscapeCsvField(entry.Message)));
                    }

                    File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                    MessageBox.Show($"Exported {_filteredEvents.Count} rows to CSV successfully.", "Export Succeeded", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to export to CSV:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OnExportJsonClicked(object sender, RoutedEventArgs e)
        {
            if (_filteredEvents.Count == 0)
            {
                MessageBox.Show("No records in current view to export.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sfd = new SaveFileDialog
            {
                Filter = "JSON File (*.json)|*.json",
                FileName = $"Forensic_Export_{DateTime.Now:yyyyMMdd_HHmmss}.json"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("[");
                    for (int i = 0; i < _filteredEvents.Count; i++)
                    {
                        var entry = _filteredEvents[i];
                        sb.AppendLine("  {");
                        sb.AppendLine($"    \"index\": {entry.RecordId},");
                        sb.AppendLine($"    \"timeCreated\": \"{entry.TimeCreated:yyyy-MM-dd HH:mm:ss}\",");
                        sb.AppendLine($"    \"eventId\": \"{EscapeJsonField(entry.EventId)}\",");
                        sb.AppendLine($"    \"level\": \"{EscapeJsonField(entry.Level)}\",");
                        sb.AppendLine($"    \"logName\": \"{EscapeJsonField(entry.LogName)}\",");
                        sb.AppendLine($"    \"source\": \"{EscapeJsonField(entry.Source)}\",");
                        sb.AppendLine($"    \"user\": \"{EscapeJsonField(entry.User)}\",");
                        sb.AppendLine($"    \"computer\": \"{EscapeJsonField(entry.Computer)}\",");
                        sb.AppendLine($"    \"message\": \"{EscapeJsonField(entry.Message)}\"");
                        sb.Append("  }");
                        if (i < _filteredEvents.Count - 1)
                        {
                            sb.AppendLine(",");
                        }
                        else
                        {
                            sb.AppendLine();
                        }
                    }
                    sb.AppendLine("]");

                    File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                    MessageBox.Show($"Exported {_filteredEvents.Count} rows to JSON successfully.", "Export Succeeded", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to export to JSON:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field)) return string.Empty;
            return field.Replace("\"", "\"\"");
        }

        private string EscapeJsonField(string field)
        {
            if (string.IsNullOrEmpty(field)) return string.Empty;
            return field
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        private void UpdatePresetButtonsState()
        {
            if (_allEvents == null || _allEvents.Count == 0)
            {
                if (PresetClearLogsBtn != null) PresetClearLogsBtn.IsEnabled = false;
                if (PresetFailedLogonsBtn != null) PresetFailedLogonsBtn.IsEnabled = false;
                if (PresetSuccessfulLogonsBtn != null) PresetSuccessfulLogonsBtn.IsEnabled = false;
                if (PresetServiceInstallsBtn != null) PresetServiceInstallsBtn.IsEnabled = false;
                if (PresetAccountChangesBtn != null) PresetAccountChangesBtn.IsEnabled = false;
                if (PresetPowerShellBtn != null) PresetPowerShellBtn.IsEnabled = false;
                if (PresetProcessExecutionBtn != null) PresetProcessExecutionBtn.IsEnabled = false;
                if (PresetSystemPowerBtn != null) PresetSystemPowerBtn.IsEnabled = false;
                UpdatePresetVisualStates();
                return;
            }

            // Inspect event list for specific event IDs
            bool hasClearLogs = _allEvents.Any(e => e.EventId == "104" || e.EventId == "1102");
            bool hasFailedLogons = _allEvents.Any(e => e.EventId == "4625");
            bool hasSuccessfulLogons = _allEvents.Any(e => e.EventId == "4624");
            bool hasServiceInstalls = _allEvents.Any(e => e.EventId == "7045");
            bool hasAccountChanges = _allEvents.Any(e => e.EventId.StartsWith("47"));
            bool hasPowerShell = _allEvents.Any(e => e.EventId == "4104");
            bool hasProcessExecution = _allEvents.Any(e => e.EventId == "4688");
            bool hasSystemPower = _allEvents.Any(e => e.EventId == "6005" || e.EventId == "6006" || e.EventId == "1074");

            if (PresetClearLogsBtn != null)
            {
                PresetClearLogsBtn.IsEnabled = hasClearLogs;
                PresetClearLogsBtn.ToolTip = hasClearLogs ? "Filter for log clearing events (104/1102)" : "No log clearing events (104/1102) found in this file";
            }
            if (PresetFailedLogonsBtn != null)
            {
                PresetFailedLogonsBtn.IsEnabled = hasFailedLogons;
                PresetFailedLogonsBtn.ToolTip = hasFailedLogons ? "Filter for failed logons (4625)" : "No failed logon events (4625) found in this file";
            }
            if (PresetSuccessfulLogonsBtn != null)
            {
                PresetSuccessfulLogonsBtn.IsEnabled = hasSuccessfulLogons;
                PresetSuccessfulLogonsBtn.ToolTip = hasSuccessfulLogons ? "Filter for successful logons (4624)" : "No successful logon events (4624) found in this file";
            }
            if (PresetServiceInstallsBtn != null)
            {
                PresetServiceInstallsBtn.IsEnabled = hasServiceInstalls;
                PresetServiceInstallsBtn.ToolTip = hasServiceInstalls ? "Filter for new service installations (7045)" : "No service installation events (7045) found in this file";
            }
            if (PresetAccountChangesBtn != null)
            {
                PresetAccountChangesBtn.IsEnabled = hasAccountChanges;
                PresetAccountChangesBtn.ToolTip = hasAccountChanges ? "Filter for account management events (4720+)" : "No account management events (47xx) found in this file";
            }
            if (PresetPowerShellBtn != null)
            {
                PresetPowerShellBtn.IsEnabled = hasPowerShell;
                PresetPowerShellBtn.ToolTip = hasPowerShell ? "Filter for PowerShell script block logs (4104)" : "No PowerShell script block events (4104) found in this file";
            }
            if (PresetProcessExecutionBtn != null)
            {
                PresetProcessExecutionBtn.IsEnabled = hasProcessExecution;
                PresetProcessExecutionBtn.ToolTip = hasProcessExecution ? "Filter for Process Execution events (4688)" : "No Process Execution events (4688) found in this file";
            }
            if (PresetSystemPowerBtn != null)
            {
                PresetSystemPowerBtn.IsEnabled = hasSystemPower;
                PresetSystemPowerBtn.ToolTip = hasSystemPower ? "Filter for System Startup/Shutdown events (6005, 6006, 1074)" : "No System Startup/Shutdown events (6005, 6006, 1074) found in this file";
            }

            UpdatePresetVisualStates();
        }

        private void UpdateDynamicQuickFilters()
        {
            if (TopEventIdsPanel == null || TopSourcesPanel == null || DynamicFiltersSeparator == null || DynamicQuickFiltersHeader == null || DynamicQuickFiltersPanel == null)
                return;

            TopEventIdsPanel.Children.Clear();
            TopSourcesPanel.Children.Clear();

            if (_allEvents == null || _allEvents.Count == 0)
            {
                DynamicFiltersSeparator.Visibility = Visibility.Collapsed;
                DynamicQuickFiltersHeader.Visibility = Visibility.Collapsed;
                DynamicQuickFiltersPanel.Visibility = Visibility.Collapsed;
                return;
            }

            // Extract top Event IDs (excluding empty/0)
            var topEventIds = _allEvents
                .Where(e => !string.IsNullOrEmpty(e.EventId) && e.EventId != "0" && e.EventId != "N/A")
                .GroupBy(e => e.EventId)
                .Select(g => new { EventId = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList();

            // Extract top Sources (excluding empty/N/A)
            var topSources = _allEvents
                .Where(e => !string.IsNullOrEmpty(e.Source) && e.Source != "N/A")
                .GroupBy(e => e.Source)
                .Select(g => new { Source = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList();

            if (topEventIds.Any() || topSources.Any())
            {
                DynamicFiltersSeparator.Visibility = Visibility.Visible;
                DynamicQuickFiltersHeader.Visibility = Visibility.Visible;
                DynamicQuickFiltersPanel.Visibility = Visibility.Visible;
            }
            else
            {
                DynamicFiltersSeparator.Visibility = Visibility.Collapsed;
                DynamicQuickFiltersHeader.Visibility = Visibility.Collapsed;
                DynamicQuickFiltersPanel.Visibility = Visibility.Collapsed;
                return;
            }

            // Create buttons for top Event IDs
            foreach (var item in topEventIds)
            {
                var btn = new Button
                {
                    Content = $"🔢 ID {item.EventId} ({item.Count:N0} events)",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 2, 0, 2),
                    FontSize = 10,
                    Tag = item.EventId
                };
                btn.Click += (s, e) =>
                {
                    if (FilterEventIdTextBox != null)
                    {
                        FilterEventIdTextBox.Text = btn.Tag.ToString();
                        ApplyFilters();
                    }
                };
                TopEventIdsPanel.Children.Add(btn);
            }

            // Create buttons for top Sources/Providers
            foreach (var item in topSources)
            {
                string displayName = item.Source.Length > 28 ? item.Source.Substring(0, 25) + "..." : item.Source;
                var btn = new Button
                {
                    Content = $"🔌 {displayName} ({item.Count:N0})",
                    ToolTip = item.Source,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 2, 0, 2),
                    FontSize = 10,
                    Tag = item.Source
                };
                btn.Click += (s, e) =>
                {
                    if (SearchTextBox != null)
                    {
                        SearchTextBox.Text = btn.Tag.ToString();
                        ApplyFilters();
                    }
                };
                TopSourcesPanel.Children.Add(btn);
            }
        }

        private void OnExportHtmlReportClicked(object sender, RoutedEventArgs e)
        {
            if (_filteredEvents.Count == 0)
            {
                MessageBox.Show("No log events are loaded or visible. Please import a file and apply filters before generating a report.", "No Data", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Get default hostname from loaded logs if available
            string defaultHost = _filteredEvents.FirstOrDefault(ev => !string.IsNullOrEmpty(ev.Computer) && ev.Computer != "N/A" && ev.Computer != "None")?.Computer ?? Environment.MachineName;

            var inputDlg = new ReportInputDialog(defaultHost) { Owner = this };
            inputDlg.ShowDialog();

            if (!inputDlg.Success)
            {
                return; // User cancelled
            }

            var sfd = new SaveFileDialog
            {
                Title = "Save HTML Forensic Report",
                Filter = "HTML Document (*.html)|*.html",
                FileName = $"Forensic_Report_{inputDlg.CaseId.Replace(" ", "_")}.html"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    // Generate report HTML content
                    string html = GenerateHtmlReportContent(inputDlg);
                    File.WriteAllText(sfd.FileName, html, Encoding.UTF8);
                    MessageBox.Show($"Forensic report generated successfully:\n{sfd.FileName}", "Report Generated", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to generate HTML report:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private string GenerateHtmlReportContent(ReportInputDialog metadata)
        {
            var sb = new StringBuilder();

            // Calculate Severity Statistics
            int criticalCount = _filteredEvents.Count(ev => ev.Level.Equals("Critical", StringComparison.OrdinalIgnoreCase));
            int errorCount = _filteredEvents.Count(ev => ev.Level.Equals("Error", StringComparison.OrdinalIgnoreCase));
            int warningCount = _filteredEvents.Count(ev => ev.Level.Equals("Warning", StringComparison.OrdinalIgnoreCase));
            int infoCount = _filteredEvents.Count(ev => ev.Level.Equals("Information", StringComparison.OrdinalIgnoreCase));
            int successAuditCount = _filteredEvents.Count(ev => ev.Level.Equals("SuccessAudit", StringComparison.OrdinalIgnoreCase));
            int failureAuditCount = _filteredEvents.Count(ev => ev.Level.Equals("FailureAudit", StringComparison.OrdinalIgnoreCase));
            int otherCount = _filteredEvents.Count - (criticalCount + errorCount + warningCount + infoCount + successAuditCount + failureAuditCount);

            // Calculate Top 5 Event IDs in filtered list
            var topEventIds = _filteredEvents
                .GroupBy(ev => ev.EventId)
                .Select(g => new { EventId = g.Key, Count = g.Count(), Source = g.First().Source })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList();

            // ------------------ Timeline Intelligence & Correlation Analysis (RTL / Arabic) ------------------
            var chronologicalEvents = _filteredEvents.OrderBy(ev => ev.TimeCreated).ToList();
            var findings = new List<string>();

            for (int i = 0; i < chronologicalEvents.Count; i++)
            {
                var ev = chronologicalEvents[i];

                // 1. Log Cleared (104, 1102)
                if (ev.EventId == "104" || ev.EventId == "1102")
                {
                    findings.Add($"<strong>[Defense Evasion - Log Cleared]</strong> A complete clearing of event logs was detected at {ev.TimeCreated:yyyy-MM-dd HH:mm:ss} by source ({ev.Source}). Attackers often clear logs to hide their activities.");
                }

                // 2. Brute Force Detection: Multiple Failed Logons (4625) followed by a Success (4624)
                if (ev.EventId == "4625")
                {
                    int failedCount = 1;
                    int j = i + 1;
                    DateTime windowStart = ev.TimeCreated;
                    while (j < chronologicalEvents.Count && (chronologicalEvents[j].TimeCreated - windowStart).TotalMinutes <= 5)
                    {
                        if (chronologicalEvents[j].EventId == "4625") failedCount++;
                        else if (chronologicalEvents[j].EventId == "4624")
                        {
                            findings.Add($"<strong>[Brute Force Attack - Password Guessing]</strong> Detected {failedCount} failed logon attempts followed by a successful logon for account ({EscapeHtml(chronologicalEvents[j].User)}) within 5 minutes (Successful Logon Time: {chronologicalEvents[j].TimeCreated:yyyy-MM-dd HH:mm:ss}). This pattern indicates a successful password brute-force attempt.");
                            i = j; // Advance outer loop
                            break;
                        }
                        j++;
                    }
                }

                // 3. Post-Exploitation Persistence: Success Logon (4624) followed by Service Persistence (7045)
                if (ev.EventId == "4624")
                {
                    int j = i + 1;
                    while (j < chronologicalEvents.Count && (chronologicalEvents[j].TimeCreated - ev.TimeCreated).TotalMinutes <= 10)
                    {
                        if (chronologicalEvents[j].EventId == "7045")
                        {
                            findings.Add($"<strong>[Persistence Mechanism - New Service]</strong> A new system service ({EscapeHtml(chronologicalEvents[j].MessageSummary)}) was created at {chronologicalEvents[j].TimeCreated:yyyy-MM-dd HH:mm:ss}, just {Math.Round((chronologicalEvents[j].TimeCreated - ev.TimeCreated).TotalSeconds)} seconds after a successful logon for account ({EscapeHtml(ev.User)}). This suggests that an attacker may have installed a backdoor immediately post-logon.");
                        }
                        j++;
                    }
                }

                // 4. Post-Exploitation Script Execution: Success Logon (4624) followed by PowerShell execution (4104)
                if (ev.EventId == "4624")
                {
                    int j = i + 1;
                    while (j < chronologicalEvents.Count && (chronologicalEvents[j].TimeCreated - ev.TimeCreated).TotalMinutes <= 10)
                    {
                        if (chronologicalEvents[j].EventId == "4104")
                        {
                            findings.Add($"<strong>[Script-Based Execution - Suspicious Script]</strong> A PowerShell script was executed at {chronologicalEvents[j].TimeCreated:yyyy-MM-dd HH:mm:ss}, {Math.Round((chronologicalEvents[j].TimeCreated - ev.TimeCreated).TotalSeconds)} seconds after user ({EscapeHtml(ev.User)}) logged on. It is recommended to review the script content for potential malicious commands.");
                        }
                        j++;
                    }
                }

                // 5. System Shutdown / Restart initiated by a process or user
                if (ev.EventId == "1074")
                {
                    findings.Add($"<strong>[System Reboot - Shutdown/Restart]</strong> A system shutdown or restart was initiated at {ev.TimeCreated:yyyy-MM-dd HH:mm:ss}. Details: {EscapeHtml(ev.MessageSummary)}.");
                }
            }

            // Build HTML — Autopsy-style forensic report layout
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"UTF-8\">");
            sb.AppendLine("  <title>WinLog Forensic Report</title>");
            sb.AppendLine("  <style>");
            sb.AppendLine("    @import url('https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700&display=swap');");
            sb.AppendLine("    * { margin: 0; padding: 0; box-sizing: border-box; }");
            sb.AppendLine("    body { font-family: 'Inter', 'Segoe UI', Arial, sans-serif; font-size: 13px; color: #1e293b; background: #f1f5f9; }");
            sb.AppendLine("    a { color: #0284c7; text-decoration: none; }");
            sb.AppendLine("    a:hover { text-decoration: underline; }");
            // Sidebar
            sb.AppendLine("    .layout { display: flex; min-height: 100vh; }");
            sb.AppendLine("    .sidebar {");
            sb.AppendLine("      width: 230px; min-width: 230px; background: #ffffff; border-right: 1px solid #e2e8f0;");
            sb.AppendLine("      padding: 20px 0; position: fixed; top: 0; bottom: 0; overflow-y: auto;");
            sb.AppendLine("    }");
            sb.AppendLine("    .sidebar-title { font-size: 14px; font-weight: 600; color: #0284c7; padding: 0 20px 15px 20px; border-bottom: 1px solid #e2e8f0; margin-bottom: 10px; }");
            sb.AppendLine("    .nav-section { padding: 0 10px; }");
            sb.AppendLine("    .nav-item {");
            sb.AppendLine("      display: flex; align-items: center; gap: 8px; padding: 7px 12px; border-radius: 6px;");
            sb.AppendLine("      font-size: 12.5px; color: #334155; cursor: pointer; margin-bottom: 2px; transition: background 0.15s;");
            sb.AppendLine("    }");
            sb.AppendLine("    .nav-item:hover { background: #f1f5f9; color: #0284c7; }");
            sb.AppendLine("    .nav-icon { font-size: 14px; width: 20px; text-align: center; }");
            sb.AppendLine("    .nav-count { margin-left: auto; font-size: 11px; color: #94a3b8; }");
            // Main content area
            sb.AppendLine("    .main { margin-left: 230px; flex: 1; padding: 0; }");
            // Report header
            sb.AppendLine("    .report-header {");
            sb.AppendLine("      padding: 25px 40px; border-bottom: 2px solid #0284c7; background: #ffffff;");
            sb.AppendLine("    }");
            sb.AppendLine("    .report-title { font-size: 28px; font-weight: 300; color: #0284c7; letter-spacing: -0.02em; }");
            sb.AppendLine("    .report-subtitle { font-size: 12px; color: #94a3b8; margin-top: 4px; }");
            // Sections
            sb.AppendLine("    .content-area { padding: 25px 40px; }");
            sb.AppendLine("    .section { margin-bottom: 30px; }");
            sb.AppendLine("    .section-heading {");
            sb.AppendLine("      font-size: 16px; font-weight: 400; color: #0284c7; margin-bottom: 15px;");
            sb.AppendLine("      padding-bottom: 5px; border-bottom: 1px solid #e2e8f0;");
            sb.AppendLine("    }");
            // Info tables (Autopsy-style key-value)
            sb.AppendLine("    .info-table { width: 100%; border-collapse: collapse; margin-bottom: 15px; }");
            sb.AppendLine("    .info-table td { padding: 6px 12px; vertical-align: top; font-size: 13px; }");
            sb.AppendLine("    .info-table td:first-child { width: 200px; color: #475569; font-weight: 500; }");
            sb.AppendLine("    .info-table td:last-child { color: #1e293b; }");
            sb.AppendLine("    .info-table tr:nth-child(even) { background: #f8fafc; }");
            // Dark header bar (like Autopsy Image Info bar)
            sb.AppendLine("    .dark-bar { background: #334155; color: #f8fafc; padding: 8px 14px; font-size: 12.5px; font-weight: 500; border-radius: 4px 4px 0 0; margin-top: 10px; }");
            // Data table for events
            sb.AppendLine("    .data-table { width: 100%; border-collapse: collapse; font-size: 12px; }");
            sb.AppendLine("    .data-table th { background: #e2e8f0; color: #334155; font-weight: 600; text-align: left; padding: 8px 10px; border: 1px solid #cbd5e1; }");
            sb.AppendLine("    .data-table td { padding: 7px 10px; border: 1px solid #e2e8f0; vertical-align: top; }");
            sb.AppendLine("    .data-table tr:nth-child(even) { background: #f8fafc; }");
            sb.AppendLine("    .data-table tr:hover { background: #e0f2fe; }");
            // Severity badges
            sb.AppendLine("    .badge { display: inline-block; padding: 2px 8px; font-size: 10px; font-weight: 600; border-radius: 3px; color: white; }");
            sb.AppendLine("    .b-critical { background: #dc2626; }");
            sb.AppendLine("    .b-error { background: #ef4444; }");
            sb.AppendLine("    .b-warning { background: #f59e0b; color: #1e293b; }");
            sb.AppendLine("    .b-info { background: #3b82f6; }");
            sb.AppendLine("    .b-success { background: #16a34a; }");
            sb.AppendLine("    .b-other { background: #64748b; }");
            // Alert box for findings
            sb.AppendLine("    .alert-box { background: #fef3c7; border: 1px solid #fbbf24; border-left: 4px solid #f59e0b; padding: 14px 18px; border-radius: 4px; margin-bottom: 15px; }");
            sb.AppendLine("    .alert-title { font-weight: 700; color: #92400e; font-size: 13px; margin-bottom: 8px; }");
            sb.AppendLine("    .alert-list { padding-left: 20px; color: #78350f; line-height: 1.8; }");
            sb.AppendLine("    .alert-list li { margin-bottom: 6px; font-size: 12.5px; }");
            // Investigator notes box
            sb.AppendLine("    .notes-box { background: #f0f9ff; border: 1px solid #bae6fd; border-left: 4px solid #0284c7; padding: 12px 16px; font-size: 12.5px; color: #0c4a6e; white-space: pre-wrap; border-radius: 4px; }");
            // Playbook
            sb.AppendLine("    .playbook { background: #f0fdf4; border: 1px solid #86efac; border-left: 4px solid #16a34a; padding: 12px 16px; border-radius: 4px; margin-top: 8px; }");
            sb.AppendLine("    .playbook-title { font-weight: 700; color: #166534; font-size: 12px; margin-bottom: 5px; }");
            sb.AppendLine("    .playbook-steps { font-family: 'Cascadia Code', Consolas, monospace; font-size: 11px; color: #166534; white-space: pre-wrap; background: #dcfce7; padding: 8px 12px; border-radius: 3px; }");
            // Event detail expander
            sb.AppendLine("    .detail-box {");
            sb.AppendLine("      font-family: 'Cascadia Code', Consolas, monospace; font-size: 11px; color: #334155;");
            sb.AppendLine("      background: #f8fafc; border: 1px solid #e2e8f0; padding: 10px 14px; border-radius: 4px;");
            sb.AppendLine("      max-height: 160px; overflow-y: auto; white-space: pre-wrap; word-break: break-all; margin-top: 6px;");
            sb.AppendLine("    }");
            // Footer
            sb.AppendLine("    .report-footer { text-align: center; font-size: 11px; color: #94a3b8; padding: 20px 40px; border-top: 1px solid #e2e8f0; margin-top: 30px; }");
            // Print styling
            sb.AppendLine("    @media print { .sidebar { display: none; } .main { margin-left: 0; } .layout { display: block; } }");
            sb.AppendLine("  </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class=\"layout\">");

            // ─── Sidebar Navigation ───
            sb.AppendLine("  <nav class=\"sidebar\">");
            sb.AppendLine("    <div class=\"sidebar-title\">Report Navigation</div>");
            sb.AppendLine("    <div class=\"nav-section\">");
            sb.AppendLine("      <a href=\"#case-summary\" class=\"nav-item\"><span class=\"nav-icon\">📋</span> Case Summary</a>");
            sb.AppendLine("      <a href=\"#evidence-sources\" class=\"nav-item\"><span class=\"nav-icon\">📁</span> Evidence Sources</a>");
            if (!string.IsNullOrWhiteSpace(metadata.InvestigatorNotes))
                sb.AppendLine("      <a href=\"#investigator-notes\" class=\"nav-item\"><span class=\"nav-icon\">📝</span> Investigator Notes</a>");
            sb.AppendLine("      <a href=\"#threat-findings\" class=\"nav-item\"><span class=\"nav-icon\">⚠️</span> Threat Findings <span class=\"nav-count\">(" + findings.Distinct().Count() + ")</span></a>");
            sb.AppendLine("      <a href=\"#statistics\" class=\"nav-item\"><span class=\"nav-icon\">📊</span> Statistics</a>");
            sb.AppendLine("      <a href=\"#top-events\" class=\"nav-item\"><span class=\"nav-icon\">🔢</span> Top Event IDs</a>");
            sb.AppendLine("      <a href=\"#event-log\" class=\"nav-item\"><span class=\"nav-icon\">🕒</span> Event Log <span class=\"nav-count\">(" + _filteredEvents.Count.ToString("N0") + ")</span></a>");
            sb.AppendLine("    </div>");
            sb.AppendLine("  </nav>");

            // ─── Main Content Area ───
            sb.AppendLine("  <div class=\"main\">");

            // Report Header
            sb.AppendLine("    <div class=\"report-header\">");
            sb.AppendLine("      <div class=\"report-title\">WinLog Forensic Report</div>");
            sb.AppendLine($"      <div class=\"report-subtitle\">HTML Report Generated on {DateTime.Now:yyyy/MM/dd HH:mm:ss}</div>");
            sb.AppendLine("    </div>");

            sb.AppendLine("    <div class=\"content-area\">");

            // ─── Section: Case Summary ───
            sb.AppendLine("      <div class=\"section\" id=\"case-summary\">");
            sb.AppendLine("        <div class=\"section-heading\">Case Summary:</div>");
            sb.AppendLine("        <table class=\"info-table\">");
            sb.AppendLine($"          <tr><td>Case Reference:</td><td>{EscapeHtml(metadata.CaseId)}</td></tr>");
            sb.AppendLine($"          <tr><td>Lead Investigator:</td><td>{EscapeHtml(metadata.InvestigatorName)}</td></tr>");
            sb.AppendLine($"          <tr><td>Target Machine:</td><td>{EscapeHtml(metadata.TargetHost)}</td></tr>");
            sb.AppendLine($"          <tr><td>Total Events Analyzed:</td><td>{_filteredEvents.Count:N0}</td></tr>");
            sb.AppendLine($"          <tr><td>Report Generated:</td><td>{DateTime.Now:yyyy/MM/dd HH:mm:ss}</td></tr>");
            if (_filteredEvents.Count > 0)
            {
                var earliest = _filteredEvents.Min(e => e.TimeCreated);
                var latest = _filteredEvents.Max(e => e.TimeCreated);
                sb.AppendLine($"          <tr><td>Time Range (Earliest):</td><td>{earliest:yyyy-MM-dd HH:mm:ss}</td></tr>");
                sb.AppendLine($"          <tr><td>Time Range (Latest):</td><td>{latest:yyyy-MM-dd HH:mm:ss}</td></tr>");
            }
            sb.AppendLine("        </table>");
            sb.AppendLine("      </div>");

            // ─── Section: Evidence Sources ───
            sb.AppendLine("      <div class=\"section\" id=\"evidence-sources\">");
            sb.AppendLine("        <div class=\"section-heading\">Evidence Sources:</div>");
            foreach (var fp in _currentFilePaths)
            {
                string fname = Path.GetFileName(fp);
                sb.AppendLine($"        <div class=\"dark-bar\">{EscapeHtml(fname)}</div>");
                sb.AppendLine("        <table class=\"info-table\">");
                sb.AppendLine($"          <tr><td>Full Path:</td><td>{EscapeHtml(fp)}</td></tr>");
                sb.AppendLine("        </table>");
            }
            sb.AppendLine("      </div>");

            // ─── Section: Investigator Notes ───
            if (!string.IsNullOrWhiteSpace(metadata.InvestigatorNotes))
            {
                sb.AppendLine("      <div class=\"section\" id=\"investigator-notes\">");
                sb.AppendLine("        <div class=\"section-heading\">Investigator Notes:</div>");
                sb.AppendLine($"        <div class=\"notes-box\">{EscapeHtml(metadata.InvestigatorNotes)}</div>");
                sb.AppendLine("      </div>");
            }

            // ─── Section: Threat Findings ───
            sb.AppendLine("      <div class=\"section\" id=\"threat-findings\">");
            sb.AppendLine("        <div class=\"section-heading\">Incident Timeline Correlation Analysis:</div>");
            if (findings.Any())
            {
                sb.AppendLine("        <div class=\"alert-box\">");
                sb.AppendLine("          <div class=\"alert-title\">⚠ Detected Attack Patterns &amp; Threat Intelligence Findings:</div>");
                sb.AppendLine("          <ul class=\"alert-list\">");
                foreach (var finding in findings.Distinct())
                {
                    sb.AppendLine($"            <li>{finding}</li>");
                }
                sb.AppendLine("          </ul>");
                sb.AppendLine("        </div>");
            }
            else
            {
                sb.AppendLine("        <p style=\"color:#64748b;\">ℹ No predefined correlation threat patterns were detected automatically. Manual analysis is recommended.</p>");
            }
            sb.AppendLine("      </div>");

            // ─── Section: Statistics ───
            sb.AppendLine("      <div class=\"section\" id=\"statistics\">");
            sb.AppendLine("        <div class=\"section-heading\">Severity Level Statistics:</div>");
            sb.AppendLine("        <table class=\"data-table\">");
            sb.AppendLine("          <tr><th>Severity Level</th><th>Count</th><th>Percentage</th></tr>");
            AddStatRowAutopsy(sb, "Critical", criticalCount, "b-critical");
            AddStatRowAutopsy(sb, "Error", errorCount, "b-error");
            AddStatRowAutopsy(sb, "Warning", warningCount, "b-warning");
            AddStatRowAutopsy(sb, "Information", infoCount, "b-info");
            AddStatRowAutopsy(sb, "SuccessAudit", successAuditCount, "b-success");
            AddStatRowAutopsy(sb, "FailureAudit", failureAuditCount, "b-critical");
            if (otherCount > 0) AddStatRowAutopsy(sb, "Other", otherCount, "b-other");
            sb.AppendLine("        </table>");
            sb.AppendLine("      </div>");

            // ─── Section: Top Event IDs ───
            sb.AppendLine("      <div class=\"section\" id=\"top-events\">");
            sb.AppendLine("        <div class=\"section-heading\">Top Event IDs:</div>");
            sb.AppendLine("        <table class=\"data-table\">");
            sb.AppendLine("          <tr><th>Event ID</th><th>Provider / Source</th><th>Count</th></tr>");
            foreach (var item in topEventIds)
            {
                sb.AppendLine($"          <tr><td><strong>{item.EventId}</strong></td><td>{EscapeHtml(item.Source)}</td><td>{item.Count:N0}</td></tr>");
            }
            sb.AppendLine("        </table>");
            sb.AppendLine("      </div>");

            // ─── Section: Chronological Event Log ───
            sb.AppendLine("      <div class=\"section\" id=\"event-log\">");
            sb.AppendLine("        <div class=\"section-heading\">Chronological Event Log:</div>");
            sb.AppendLine("        <table class=\"data-table\">");
            sb.AppendLine("          <tr><th>#</th><th>Date &amp; Time</th><th>Event ID</th><th>Level</th><th>Source</th><th>Computer</th><th>User</th><th>Task Category</th></tr>");

            int idx = 1;
            foreach (var ev in _filteredEvents)
            {
                string badgeClass = ev.Level.ToLower() switch
                {
                    "critical" => "b-critical",
                    "error" => "b-error",
                    "warning" => "b-warning",
                    "information" => "b-info",
                    "successaudit" => "b-success",
                    "failureaudit" => "b-critical",
                    _ => "b-other"
                };

                sb.AppendLine("          <tr>");
                sb.AppendLine($"            <td>{idx}</td>");
                sb.AppendLine($"            <td>{ev.TimeCreated:yyyy-MM-dd HH:mm:ss}</td>");
                sb.AppendLine($"            <td><strong>{ev.EventId}</strong></td>");
                sb.AppendLine($"            <td><span class=\"badge {badgeClass}\">{ev.Level}</span></td>");
                sb.AppendLine($"            <td>{EscapeHtml(ev.Source)}</td>");
                sb.AppendLine($"            <td>{EscapeHtml(ev.Computer)}</td>");
                sb.AppendLine($"            <td>{EscapeHtml(ev.User)}</td>");
                sb.AppendLine($"            <td>{EscapeHtml(ev.TaskCategory)}</td>");
                sb.AppendLine("          </tr>");

                // Detail row with full message + playbook
                bool hasMessage = !string.IsNullOrEmpty(ev.Message);
                int evIdVal = 0;
                int.TryParse(ev.EventId, out evIdVal);
                bool hasPlaybook = evIdVal > 0 && ForensicAdvisor.IsForensicHighlight(evIdVal);

                if (hasMessage || hasPlaybook)
                {
                    sb.AppendLine($"          <tr><td colspan=\"8\" style=\"padding: 4px 10px 12px 30px; border-top: none;\">");
                    if (hasMessage)
                    {
                        sb.AppendLine($"            <div class=\"detail-box\">{EscapeHtml(ev.Message)}</div>");
                    }
                    if (hasPlaybook)
                    {
                        var advice = ForensicAdvisor.GetAdvice(evIdVal);
                        sb.AppendLine("            <div class=\"playbook\">");
                        sb.AppendLine($"              <div class=\"playbook-title\">💡 Forensic Playbook: {EscapeHtml(advice.Title)} ({advice.Category})</div>");
                        sb.AppendLine($"              <div style=\"margin-bottom:6px;\"><strong>Security Implication:</strong> {EscapeHtml(advice.Description)}</div>");
                        sb.AppendLine($"              <div class=\"playbook-steps\">{EscapeHtml(advice.InvestigationSteps)}</div>");
                        sb.AppendLine("            </div>");
                    }
                    sb.AppendLine("          </td></tr>");
                }

                idx++;
            }

            sb.AppendLine("        </table>");
            sb.AppendLine("      </div>");

            // Footer
            sb.AppendLine("      <div class=\"report-footer\">");
            sb.AppendLine("        WinLog Forensic Report | Developed by Wadhah Anaam | Digital Forensics &amp; Incident Response");
            sb.AppendLine("      </div>");

            sb.AppendLine("    </div>"); // content-area
            sb.AppendLine("  </div>"); // main
            sb.AppendLine("</div>"); // layout

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        private void AddStatRow(StringBuilder sb, string level, int count, string badgeClass)
        {
            if (_filteredEvents.Count == 0) return;
            double pct = (double)count / _filteredEvents.Count * 100;
            sb.AppendLine($"          <tr><td><span class=\"badge {badgeClass}\">{level}</span></td><td>{count:N0}</td><td>{pct:F1}%</td></tr>");
        }

        private void AddStatRowAutopsy(StringBuilder sb, string level, int count, string badgeClass)
        {
            if (_filteredEvents.Count == 0) return;
            double pct = (double)count / _filteredEvents.Count * 100;
            sb.AppendLine($"          <tr><td><span class=\"badge {badgeClass}\">{level}</span></td><td>{count:N0}</td><td>{pct:F1}%</td></tr>");
        }

        private string EscapeHtml(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return System.Net.WebUtility.HtmlEncode(text);
        }
    }

    public class ReportInputDialog : Window
    {
        public string InvestigatorName { get; private set; } = string.Empty;
        public string CaseId { get; private set; } = string.Empty;
        public string TargetHost { get; private set; } = string.Empty;
        public string InvestigatorNotes { get; private set; } = string.Empty;
        public bool Success { get; private set; } = false;

        private TextBox txtInvestigator;
        private TextBox txtCaseId;
        private TextBox txtHost;
        private TextBox txtNotes;

        public ReportInputDialog(string defaultHost)
        {
            Title = "Forensic Report Metadata Input";
            Width = 420;
            Height = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)); // slate-50

            var grid = new Grid { Margin = new Thickness(15) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Investigator Name
            var lblInvestigator = new TextBlock { Text = "Investigator Name:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 3), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)) };
            txtInvestigator = new TextBox { Height = 23, Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(lblInvestigator, 0);
            Grid.SetRow(txtInvestigator, 0);
            txtInvestigator.Margin = new Thickness(0, 22, 0, 10);
            grid.Children.Add(lblInvestigator);
            grid.Children.Add(txtInvestigator);

            // Case ID
            var lblCaseId = new TextBlock { Text = "Case ID / Reference:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 3), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)) };
            txtCaseId = new TextBox { Height = 23, Margin = new Thickness(0, 0, 0, 10), Text = "CASE-" + DateTime.Now.ToString("yyyyMMdd") };
            Grid.SetRow(lblCaseId, 1);
            Grid.SetRow(txtCaseId, 1);
            txtCaseId.Margin = new Thickness(0, 22, 0, 10);
            grid.Children.Add(lblCaseId);
            grid.Children.Add(txtCaseId);

            // Target Host
            var lblHost = new TextBlock { Text = "Target Hostname / System:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 3), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)) };
            txtHost = new TextBox { Height = 23, Margin = new Thickness(0, 0, 0, 10), Text = defaultHost };
            Grid.SetRow(lblHost, 2);
            Grid.SetRow(txtHost, 2);
            txtHost.Margin = new Thickness(0, 22, 0, 10);
            grid.Children.Add(lblHost);
            grid.Children.Add(txtHost);

            // Notes
            var lblNotes = new TextBlock { Text = "Investigator Notes:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 3), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)) };
            txtNotes = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 22, 0, 10), Height = 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(lblNotes, 3);
            Grid.SetRow(txtNotes, 3);
            grid.Children.Add(lblNotes);
            grid.Children.Add(txtNotes);

            // Buttons panel
            var pnlButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            Grid.SetRow(pnlButtons, 5);
            grid.Children.Add(pnlButtons);

            var btnOk = new Button { Content = "Generate", Width = 80, Height = 24, IsDefault = true };
            btnOk.Click += (s, e) => {
                InvestigatorName = txtInvestigator.Text;
                CaseId = txtCaseId.Text;
                TargetHost = txtHost.Text;
                InvestigatorNotes = txtNotes.Text;
                Success = true;
                Close();
            };

            var btnCancel = new Button { Content = "Cancel", Width = 80, Height = 24, Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
            btnCancel.Click += (s, e) => {
                Close();
            };

            pnlButtons.Children.Add(btnOk);
            pnlButtons.Children.Add(btnCancel);

            Content = grid;
        }
    }
}