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
                Filter = "All Supported Logs|*.evtx;*.json;*.xml;*.csv;*.xlsx;*.xls" +
                         "|Windows Event Log (*.evtx)|*.evtx" +
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
                    UpdatePresetButtonsState();
                    UpdateDynamicQuickFilters();
                    ApplyFilters();
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    try
                    {
                        File.WriteAllText(@"c:\Users\pc\Desktop\WinLog\error_log.txt", ex.ToString());
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
                    findings.Add($"<strong>[طمس الأدلة الجنائية - Defense Evasion]</strong> تم رصد مسح كامل لسجلات الأحداث في {ev.TimeCreated:yyyy-MM-dd HH:mm:ss} بواسطة المصدر ({ev.Source}). المهاجمون يمسحون السجلات لإخفاء تحركاتهم.");
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
                            findings.Add($"<strong>[محاولة اقتحام بالقوة - Brute Force Attack]</strong> تم رصد {failedCount} محاولات دخول فاشلة تلاها دخول ناجح ومؤكد للحساب ({EscapeHtml(chronologicalEvents[j].User)}) خلال 5 دقائق (وقت الدخول الناجح: {chronologicalEvents[j].TimeCreated:yyyy-MM-dd HH:mm:ss}). هذا نمط اختراق وتخمين كلمات مرور ناجح.");
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
                            findings.Add($"<strong>[تثبيت آلية بقاء خبيثة - Persistence Mechanism]</strong> تم إنشاء خدمة نظام جديدة ({EscapeHtml(chronologicalEvents[j].MessageSummary)}) في {chronologicalEvents[j].TimeCreated:yyyy-MM-dd HH:mm:ss} بعد {Math.Round((chronologicalEvents[j].TimeCreated - ev.TimeCreated).TotalSeconds)} ثانية فقط من تسجيل دخول ناجح للحساب ({EscapeHtml(ev.User)}). يشير هذا إلى احتمال قيام مهاجم بتثبيت باب خلفي (Backdoor) بعد الدخول مباشرة.");
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
                            findings.Add($"<strong>[تنفيذ سكريبتات مشبوهة - Script-Based Execution]</strong> تم تنفيذ سكريبت PowerShell في {chronologicalEvents[j].TimeCreated:yyyy-MM-dd HH:mm:ss} بعد دخول المستخدم ({EscapeHtml(ev.User)}) بـ {Math.Round((chronologicalEvents[j].TimeCreated - ev.TimeCreated).TotalSeconds)} ثانية. يوصى بمراجعة محتوى السكريبت المنفذ للتحقق من سلامته.");
                        }
                        j++;
                    }
                }

                // 5. System Shutdown / Restart initiated by a process or user
                if (ev.EventId == "1074")
                {
                    findings.Add($"<strong>[إعادة تشغيل النظام - System Reboot]</strong> تم طلب إيقاف أو إعادة تشغيل النظام في {ev.TimeCreated:yyyy-MM-dd HH:mm:ss}. التفاصيل: {EscapeHtml(ev.MessageSummary)}.");
                }
            }

            // Build HTML
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"UTF-8\">");
            sb.AppendLine("  <title>WinLog Forensic Investigation Dashboard &amp; Report</title>");
            sb.AppendLine("  <style>");
            sb.AppendLine("    @import url('https://fonts.googleapis.com/css2?family=Cairo:wght@400;600;700&family=Inter:wght@300;400;500;600;700&display=swap');");
            sb.AppendLine("    body {");
            sb.AppendLine("      font-family: 'Inter', 'Segoe UI', system-ui, sans-serif;");
            sb.AppendLine("      margin: 0;");
            sb.AppendLine("      background-color: #0f172a;");
            sb.AppendLine("      color: #cbd5e1;");
            sb.AppendLine("      line-height: 1.6;");
            sb.AppendLine("    }");
            sb.AppendLine("    .header-banner {");
            sb.AppendLine("      background: linear-gradient(135deg, #0284c7 0%, #0f172a 100%);");
            sb.AppendLine("      padding: 30px 40px;");
            sb.AppendLine("      border-bottom: 1px solid #1e293b;");
            sb.AppendLine("      box-shadow: 0 4px 6px -1px rgba(0,0,0,0.1);");
            sb.AppendLine("    }");
            sb.AppendLine("    .container {");
            sb.AppendLine("      max-width: 1250px;");
            sb.AppendLine("      margin: 0 auto;");
            sb.AppendLine("      padding: 30px 20px;");
            sb.AppendLine("    }");
            sb.AppendLine("    .title-logo { font-size: 26px; font-weight: 700; color: #f8fafc; letter-spacing: -0.02em; }");
            sb.AppendLine("    .title-sub { color: #38bdf8; font-size: 13px; font-weight: 500; margin-top: 2px; }");
            sb.AppendLine("    .dashboard-grid {");
            sb.AppendLine("      display: grid;");
            sb.AppendLine("      grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));");
            sb.AppendLine("      gap: 15px;");
            sb.AppendLine("      margin-bottom: 30px;");
            sb.AppendLine("    }");
            sb.AppendLine("    .card {");
            sb.AppendLine("      background-color: #1e293b;");
            sb.AppendLine("      border: 1px solid #334155;");
            sb.AppendLine("      border-radius: 10px;");
            sb.AppendLine("      padding: 15px 20px;");
            sb.AppendLine("      box-shadow: 0 4px 6px -1px rgba(0,0,0,0.05);");
            sb.AppendLine("    }");
            sb.AppendLine("    .card-label { font-size: 10px; text-transform: uppercase; font-weight: 700; color: #94a3b8; letter-spacing: 0.05em; }");
            sb.AppendLine("    .card-value { font-size: 16px; font-weight: 600; color: #f8fafc; margin-top: 4px; word-break: break-all; }");
            sb.AppendLine("    .section {");
            sb.AppendLine("      background-color: #1e293b;");
            sb.AppendLine("      border: 1px solid #334155;");
            sb.AppendLine("      border-radius: 10px;");
            sb.AppendLine("      padding: 20px;");
            sb.AppendLine("      margin-bottom: 25px;");
            sb.AppendLine("    }");
            sb.AppendLine("    .section-title {");
            sb.AppendLine("      font-size: 17px;");
            sb.AppendLine("      font-weight: 600;");
            sb.AppendLine("      color: #38bdf8;");
            sb.AppendLine("      border-bottom: 1px solid #334155;");
            sb.AppendLine("      padding-bottom: 8px;");
            sb.AppendLine("      margin-bottom: 15px;");
            sb.AppendLine("    }");
            sb.AppendLine("    .stats-table { width: 100%; border-collapse: collapse; font-size: 12px; }");
            sb.AppendLine("    .stats-table th, .stats-table td { border: 1px solid #334155; padding: 8px 10px; text-align: left; }");
            sb.AppendLine("    .stats-table th { background-color: #0f172a; color: #94a3b8; font-weight: 600; }");
            sb.AppendLine("    .badge { display: inline-block; padding: 2px 6px; font-size: 10px; font-weight: 700; border-radius: 4px; color: white; text-align: center; }");
            sb.AppendLine("    .badge-critical { background-color: #ef4444; }");
            sb.AppendLine("    .badge-error { background-color: #f87171; }");
            sb.AppendLine("    .badge-warning { background-color: #f59e0b; }");
            sb.AppendLine("    .badge-info { background-color: #3b82f6; }");
            sb.AppendLine("    .badge-success { background-color: #10b981; }");
            sb.AppendLine("    .badge-other { background-color: #64748b; }");
            sb.AppendLine("    .notes-box { font-style: italic; color: #cbd5e1; white-space: pre-wrap; font-size: 13px; background: #0f172a; padding: 12px; border-right: 4px solid #38bdf8; border-radius: 4px; }");
            sb.AppendLine("    .narrative-box { background-color: #451a03; border: 1px solid #78350f; border-right: 4px solid #f59e0b; padding: 15px; border-radius: 6px; }");
            sb.AppendLine("    .narrative-title { font-family: 'Cairo', sans-serif; font-weight: 700; color: #f59e0b; font-size: 14.5px; border-bottom: 1px dashed #78350f; padding-bottom: 5px; text-align: right; direction: rtl; }");
            sb.AppendLine("    .narrative-list { padding-right: 20px; line-height: 1.7; text-align: right; direction: rtl; font-family: 'Cairo', sans-serif; }");
            sb.AppendLine("    .narrative-list li { margin-bottom: 10px; font-size: 13px; color: #fef3c7; }");
            sb.AppendLine("    /* SIEM Timeline Styling */");
            sb.AppendLine("    .timeline {");
            sb.AppendLine("      position: relative;");
            sb.AppendLine("      padding-left: 25px;");
            sb.AppendLine("      border-left: 2px solid #334155;");
            sb.AppendLine("      margin-left: 15px;");
            sb.AppendLine("      margin-top: 15px;");
            sb.AppendLine("    }");
            sb.AppendLine("    .timeline-item {");
            sb.AppendLine("      position: relative;");
            sb.AppendLine("      background-color: #1e293b;");
            sb.AppendLine("      border: 1px solid #334155;");
            sb.AppendLine("      border-radius: 8px;");
            sb.AppendLine("      padding: 16px 20px;");
            sb.AppendLine("      margin-bottom: 20px;");
            sb.AppendLine("      box-shadow: 0 4px 6px -1px rgba(0,0,0,0.2);");
            sb.AppendLine("    }");
            sb.AppendLine("    .timeline-dot {");
            sb.AppendLine("      position: absolute;");
            sb.AppendLine("      left: -36px;");
            sb.AppendLine("      top: 22px;");
            sb.AppendLine("      width: 16px;");
            sb.AppendLine("      height: 16px;");
            sb.AppendLine("      border-radius: 50%;");
            sb.AppendLine("      border: 4px solid #0f172a;");
            sb.AppendLine("    }");
            sb.AppendLine("    /* Border & Dot Severities */");
            sb.AppendLine("    .border-critical { border-left: 4px solid #ef4444 !important; }");
            sb.AppendLine("    .border-error { border-left: 4px solid #f87171 !important; }");
            sb.AppendLine("    .border-warning { border-left: 4px solid #f59e0b !important; }");
            sb.AppendLine("    .border-info { border-left: 4px solid #3b82f6 !important; }");
            sb.AppendLine("    .border-success { border-left: 4px solid #10b981 !important; }");
            sb.AppendLine("    .dot-critical { background-color: #ef4444; }");
            sb.AppendLine("    .dot-error { background-color: #f87171; }");
            sb.AppendLine("    .dot-warning { background-color: #f59e0b; }");
            sb.AppendLine("    .dot-info { background-color: #3b82f6; }");
            sb.AppendLine("    .dot-success { background-color: #10b981; }");
            sb.AppendLine("    .item-header {");
            sb.AppendLine("      display: flex;");
            sb.AppendLine("      flex-wrap: wrap;");
            sb.AppendLine("      gap: 15px;");
            sb.AppendLine("      font-size: 12px;");
            sb.AppendLine("      color: #94a3b8;");
            sb.AppendLine("      border-bottom: 1px dashed #334155;");
            sb.AppendLine("      padding-bottom: 8px;");
            sb.AppendLine("      margin-bottom: 10px;");
            sb.AppendLine("    }");
            sb.AppendLine("    .item-header span strong { color: #f8fafc; }");
            sb.AppendLine("    .item-summary { font-size: 14px; font-weight: 600; color: #f1f5f9; margin-bottom: 10px; }");
            sb.AppendLine("    .item-details-box {");
            sb.AppendLine("      background-color: #0f172a;");
            sb.AppendLine("      border: 1px solid #334155;");
            sb.AppendLine("      border-radius: 6px;");
            sb.AppendLine("      padding: 12px 15px;");
            sb.AppendLine("      font-family: Consolas, Monaco, monospace;");
            sb.AppendLine("      font-size: 11.5px;");
            sb.AppendLine("      color: #cbd5e1;");
            sb.AppendLine("      white-space: pre-wrap;");
            sb.AppendLine("      max-height: 200px;");
            sb.AppendLine("      overflow-y: auto;");
            sb.AppendLine("      margin-bottom: 12px;");
            sb.AppendLine("      word-break: break-all;");
            sb.AppendLine("    }");
            sb.AppendLine("    .playbook-box {");
            sb.AppendLine("      background-color: #082f49;");
            sb.AppendLine("      border: 1px solid #0284c7;");
            sb.AppendLine("      border-radius: 6px;");
            sb.AppendLine("      padding: 12px 15px;");
            sb.AppendLine("      font-size: 12.5px;");
            sb.AppendLine("      color: #e0f2fe;");
            sb.AppendLine("    }");
            sb.AppendLine("    .playbook-title { font-weight: 700; color: #38bdf8; margin-bottom: 4px; }");
            sb.AppendLine("  </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            // Header Container Banner
            sb.AppendLine("  <div class=\"header-banner\">");
            sb.AppendLine("    <div class=\"title-logo\">WINLOG FORENSIC DASHBOARD</div>");
            sb.AppendLine("    <div class=\"title-sub\">Interactive Timeline &amp; Cyber Threat Investigation Report</div>");
            sb.AppendLine("  </div>");

            sb.AppendLine("  <div class=\"container\">");

            // Dashboard Grid
            sb.AppendLine("    <div class=\"dashboard-grid\">");
            sb.AppendLine($"      <div class=\"card\"><div class=\"card-label\">Case Reference / ID</div><div class=\"card-value\">{EscapeHtml(metadata.CaseId)}</div></div>");
            sb.AppendLine($"      <div class=\"card\"><div class=\"card-label\">Lead Investigator</div><div class=\"card-value\">{EscapeHtml(metadata.InvestigatorName)}</div></div>");
            sb.AppendLine($"      <div class=\"card\"><div class=\"card-label\">Target Machine</div><div class=\"card-value\">{EscapeHtml(metadata.TargetHost)}</div></div>");
            sb.AppendLine($"      <div class=\"card\"><div class=\"card-label\">Total Isolated Logs</div><div class=\"card-value\">{_filteredEvents.Count:N0}</div></div>");
            sb.AppendLine("    </div>");

            // Source Evidence File list
            string sourceFilesText = string.Join(", ", _currentFilePaths.Select(Path.GetFileName));
            sb.AppendLine("    <div class=\"section\">");
            sb.AppendLine("      <div class=\"section-title\">📁 Evidence Sources</div>");
            sb.AppendLine($"      <div style=\"font-size:13px; color:#cbd5e1;\">{EscapeHtml(sourceFilesText)}</div>");
            sb.AppendLine("    </div>");

            // Investigator custom notes
            if (!string.IsNullOrWhiteSpace(metadata.InvestigatorNotes))
            {
                sb.AppendLine("    <div class=\"section\">");
                sb.AppendLine("      <div class=\"section-title\">📝 Investigator Direct Notes</div>");
                sb.AppendLine($"      <div class=\"notes-box\">{EscapeHtml(metadata.InvestigatorNotes)}</div>");
                sb.AppendLine("    </div>");
            }

            // Timeline Correlation Alert Narrative (RTL / Arabic)
            sb.AppendLine("    <div class=\"section\">");
            sb.AppendLine("      <div class=\"section-title\">⚡ Incident Timeline Correlation Analysis</div>");
            if (findings.Any())
            {
                sb.AppendLine("      <div class=\"narrative-box\">");
                sb.AppendLine("        <div class=\"narrative-title\">⚠️ تحليل أنماط الهجوم والتهديدات الأمنية المرتبطة بالملف:</div>");
                sb.AppendLine("        <ul class=\"narrative-list\">");
                foreach (var finding in findings.Distinct())
                {
                    sb.AppendLine($"          <li>{finding}</li>");
                }
                sb.AppendLine("        </ul>");
                sb.AppendLine("      </div>");
            }
            else
            {
                sb.AppendLine("      <div style=\"background:#0f172a; border: 1px solid #334155; padding:15px; border-radius:6px; font-size:12.5px; color:#94a3b8;\">");
                sb.AppendLine("        ℹ️ No predefined correlation threat patterns were detected automatically. Manual checking is advised.");
                sb.AppendLine("      </div>");
            }
            sb.AppendLine("    </div>");

            // Metrics Summary Grid
            sb.AppendLine("    <div class=\"section\">");
            sb.AppendLine("      <div class=\"section-title\">📊 Statistics Breakdown</div>");
            sb.AppendLine("      <div style=\"display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 20px;\">");

            // Severity table
            sb.AppendLine("        <div>");
            sb.AppendLine("          <h4 style=\"margin: 0 0 10px 0; color:#38bdf8; font-size:13px;\">Severity Level Metrics</h4>");
            sb.AppendLine("          <table class=\"stats-table\">");
            sb.AppendLine("            <tr><th>Severity Level</th><th>Count</th><th>Percentage</th></tr>");
            AddStatRow(sb, "Critical", criticalCount, "badge-critical");
            AddStatRow(sb, "Error", errorCount, "badge-error");
            AddStatRow(sb, "Warning", warningCount, "badge-warning");
            AddStatRow(sb, "Information", infoCount, "badge-info");
            AddStatRow(sb, "SuccessAudit", successAuditCount, "badge-success");
            AddStatRow(sb, "FailureAudit", failureAuditCount, "badge-critical");
            if (otherCount > 0) AddStatRow(sb, "Other", otherCount, "badge-other");
            sb.AppendLine("          </table>");
            sb.AppendLine("        </div>");

            // Top event IDs
            sb.AppendLine("        <div>");
            sb.AppendLine("          <h4 style=\"margin: 0 0 10px 0; color:#38bdf8; font-size:13px;\">Top Event IDs present in timeline</h4>");
            sb.AppendLine("          <table class=\"stats-table\">");
            sb.AppendLine("            <tr><th>Event ID</th><th>Provider / Source</th><th>Count</th></tr>");
            foreach (var item in topEventIds)
            {
                sb.AppendLine($"            <tr><td><strong>{item.EventId}</strong></td><td>{EscapeHtml(item.Source)}</td><td>{item.Count:N0}</td></tr>");
            }
            sb.AppendLine("          </table>");
            sb.AppendLine("        </div>");

            sb.AppendLine("      </div>");
            sb.AppendLine("    </div>");

            // Chronological Timeline Cards
            sb.AppendLine("    <div class=\"section\">");
            sb.AppendLine("      <div class=\"section-title\">🕒 Chronological SIEM Timeline View</div>");
            sb.AppendLine("      <div class=\"timeline\">");

            int idx = 1;
            foreach (var ev in _filteredEvents)
            {
                string severityClass = ev.Level.ToLower() switch
                {
                    "critical" => "critical",
                    "error" => "error",
                    "warning" => "warning",
                    "information" => "info",
                    "successaudit" => "success",
                    "failureaudit" => "critical",
                    _ => "info"
                };

                string badgeClass = ev.Level.ToLower() switch
                {
                    "critical" => "badge-critical",
                    "error" => "badge-error",
                    "warning" => "badge-warning",
                    "information" => "badge-info",
                    "successaudit" => "badge-success",
                    "failureaudit" => "badge-critical",
                    _ => "badge-other"
                };

                int evIdVal = 0;
                int.TryParse(ev.EventId, out evIdVal);
                bool hasPlaybook = evIdVal > 0 && ForensicAdvisor.IsForensicHighlight(evIdVal);

                sb.AppendLine($"        <div class=\"timeline-item border-{severityClass}\">");
                sb.AppendLine($"          <div class=\"timeline-dot dot-{severityClass}\"></div>");
                
                // Card Header Metadata
                sb.AppendLine("          <div class=\"item-header\">");
                sb.AppendLine($"            <span>Index: <strong>#{idx++}</strong></span>");
                sb.AppendLine($"            <span>Date &amp; Time: <strong>{ev.TimeCreated:yyyy-MM-dd HH:mm:ss}</strong></span>");
                sb.AppendLine($"            <span>Event ID: <strong>{ev.EventId}</strong></span>");
                sb.AppendLine($"            <span>Severity: <span class=\"badge {badgeClass}\">{ev.Level}</span></span>");
                sb.AppendLine($"            <span>Computer: <strong>{EscapeHtml(ev.Computer)}</strong></span>");
                sb.AppendLine($"            <span>User Account: <strong>{EscapeHtml(ev.User)}</strong></span>");
                sb.AppendLine("          </div>");

                // Summary
                sb.AppendLine($"          <div class=\"item-summary\"><strong>Task: {EscapeHtml(ev.TaskCategory)}</strong> | Provider: {EscapeHtml(ev.Source)}</div>");

                // Details Textbox (Pre-formatted scrollable details)
                if (!string.IsNullOrEmpty(ev.Message))
                {
                    sb.AppendLine($"          <div class=\"item-details-box\">{EscapeHtml(ev.Message)}</div>");
                }

                // Inline Playbook Box (if matched)
                if (hasPlaybook)
                {
                    var advice = ForensicAdvisor.GetAdvice(evIdVal);
                    sb.AppendLine("          <div class=\"playbook-box\">");
                    sb.AppendLine($"            <div class=\"playbook-title\">💡 Forensic Playbook: {EscapeHtml(advice.Title)} ({advice.Category})</div>");
                    sb.AppendLine($"            <div style=\"margin-bottom:8px;\"><strong>Security Implication:</strong> {EscapeHtml(advice.Description)}</div>");
                    sb.AppendLine($"            <div style=\"background:#032030; border:1px solid #0284c7; padding:8px 12px; border-radius:4px; font-family:Consolas, monospace; font-size:11px; white-space:pre-wrap; color:#e0f2fe;\">{EscapeHtml(advice.InvestigationSteps)}</div>");
                    sb.AppendLine("          </div>");
                }

                sb.AppendLine("        </div>");
            }

            sb.AppendLine("      </div>");
            sb.AppendLine("    </div>");

            // Footer
            sb.AppendLine("    <div style=\"text-align: center; font-size: 11px; color: #64748b; margin-top: 50px; border-top: 1px solid #334155; padding-top: 15px;\">");
            sb.AppendLine("      WinLog Forensic Timeline Report | Developed by Wadhah Anaam | Digital Forensics &amp; Incident Response Operations");
            sb.AppendLine("    </div>");

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