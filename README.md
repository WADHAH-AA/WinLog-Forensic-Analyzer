# 🛡️ WinLog Forensic Importer & Analyzer

A premium, high-fidelity digital forensics and incident response (DFIR) application designed for security analysts, incident responders, and system administrators to import, correlate, and analyze Windows Event Logs in an interactive, visual timeline.

Developed by **Wadhah Anaam** for advanced digital forensics operations.

---

## 🎯 About the Project
During security investigations, security teams often have to examine logs from multiple different log files (e.g., Security, System, Application, PowerShell, TaskScheduler) across different machines. Analyzing each log file in isolation creates massive visibility gaps and slows down response times.

**WinLog Forensic Importer & Analyzer** solves this problem by allowing investigators to import multiple event files simultaneously, automatically merging and sorting their entries down to the millisecond in a unified timeline.

---

## 💡 Importance & Core Benefits
* **Complete Attack Chain Visibility**: Interleaves different log types (e.g., a failed logon in `Security.evtx` followed by service persistence in `System.evtx` followed by script execution in `PowerShell.evtx`) chronologically to reconstruct the attacker's timeline.
* **Rapid Triaging**: Eliminates manual log parsing. Investigators can immediately filter logs using pre-built forensic presets.
* **Explainable Analysis**: Translates complex Windows Event IDs into readable Arabic explanations and actionable mitigation playbooks.

---

## 🚀 Key Features

### 1. Multi-File Log Correlation
Import multiple Windows log files concurrently. The engine aggregates all entries, parses their XML structure, and presents them in a single, unified, chronologically-sorted event grid.

### 2. Multi-Format Import Support
WinLog supports importing Windows Event Logs exported in various formats:
* **`.evtx`** (Standard Windows Event Log files)
* **`.xml`** (Extensible Markup Language exports)
* **`.json`** (JSON formatted Windows logs)
* **`.csv`** (Comma-separated logs)
* **`.xlsx` / `.xls`** (Microsoft Excel spreadsheets)

### 3. Timeline Intelligence & Attack Correlation Findings
The correlation engine automatically scans the chronological flow of logs to detect common attacker tactics, techniques, and procedures (TTPs), presenting them in a clear threat intelligence narrative box:
* **Defense Evasion**: Detects log clearance events (`Event 104`, `1102`).
* **Brute Force Detection**: Detects multiple failed logons (`Event 4625`) preceding a successful logon (`Event 4624`).
* **Persistence Mechanism**: Detects rapid Windows service creations (`Event 7045`) immediately after successful authentication.
* **Script-Based Execution**: Detects execution of PowerShell blocks (`Event 4104`) in tight timelines.

### 4. Interactive Dark-Mode SIEM HTML Dashboard
Export the active filtered timeline into a premium **Dark-Mode SIEM Dashboard** HTML report:
* Includes executive metrics summaries and top event ID charts.
* Renders each log entry as a SIEM event card with color-coded severity borders (Red for Critical, Yellow for Warning, etc.).
* Displays full event messages inside scrollable code boxes on-screen (no hidden text).
* Interleaves automated playbooks directly under matching event cards.

### 5. Multi-Select Forensic Presets
Toggle multiple presets simultaneously (e.g. `Successful Logons (4624)` AND `Process Creation (4688)`) to interleave and compare login sessions and program executions. Active presets are visually highlighted in sky blue.

---

## 🔧 Technology Stack
* **Framework**: .NET 8.0 Windows (WPF)
* **Language**: C# 12.0
* **Log Parsing Library**: System.Diagnostics.Eventing.Reader
* **Excel Engine**: ExcelDataReader (for CSV/Excel parsing support)
* **Styling**: Modern Flat UI / HSL Color System
