using System;
using System.Collections.Generic;

namespace WinLog
{
    public class ForensicAdvice
    {
        public int EventId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Severity { get; set; } = "Info"; // Critical, Error, Warning, Info
        public string Description { get; set; } = string.Empty;
        public string InvestigationSteps { get; set; } = string.Empty;
    }

    public static class ForensicAdvisor
    {
        private static readonly Dictionary<int, ForensicAdvice> Advices = new()
        {
            {
                1102, new ForensicAdvice
                {
                    EventId = 1102,
                    Title = "Audit Log Cleared (Security)",
                    Category = "Anti-Forensics / Defense Evasion",
                    Severity = "Critical",
                    Description = "The Windows Security event log was cleared. This is a common action performed by attackers to erase their activity logs and evade detection.",
                    InvestigationSteps = "1. Identify the user account that executed the clear action (look at the Subject/User SID in the event message).\n2. Cross-reference this timestamp with active logon sessions to trace host-compromise origins.\n3. Investigate if this was a legitimate administrative task or an unauthorized cover-up."
                }
            },
            {
                104, new ForensicAdvice
                {
                    EventId = 104,
                    Title = "Log File Cleared (System)",
                    Category = "Anti-Forensics / Defense Evasion",
                    Severity = "Critical",
                    Description = "A log file (usually System or Application) was cleared. Intruder activity is highly likely if performed during anomalous hours.",
                    InvestigationSteps = "1. Check the User or SID listed in the event details to find who cleared it.\n2. Correlate with concurrent remote logins (RDP/Network share).\n3. Check backups to recover lost event logs if possible."
                }
            },
            {
                4625, new ForensicAdvice
                {
                    EventId = 4625,
                    Title = "Failed Account Logon (Security)",
                    Category = "Authentication / Brute Force",
                    Severity = "Warning",
                    Description = "An account failed to log on. Multiple occurrences of this event within a short timeframe strongly indicate a brute-force or credential stuffing attack.",
                    InvestigationSteps = "1. Examine the 'Logon Type' to determine the attack vector:\n   - Type 2: Local interactive console (Physical/Console access)\n   - Type 3: Network logon (IIS, shared folders, IPC$)\n   - Type 10: RDP / Remote interactive (Remote Desktop)\n2. Note the 'Source Network Address' to block the attacking IP.\n3. Verify if the target account (TargetUserName) is a critical administrative account."
                }
            },
            {
                4624, new ForensicAdvice
                {
                    EventId = 4624,
                    Title = "Successful Account Logon (Security)",
                    Category = "Authentication",
                    Severity = "Info",
                    Description = "An account successfully logged on. Monitoring successful logons helps track lateral movement and session hijacking.",
                    InvestigationSteps = "1. Confirm if the logon was authorized. Pay close attention to Logon Type 10 (RDP) or Type 3 (Network access from unexpected IPs).\n2. Inspect the 'Workstation Name' and 'Source Network Address' to verify geographic/IP validity.\n3. Look for administrative accounts logging onto non-standard workstations."
                }
            },
            {
                7045, new ForensicAdvice
                {
                    EventId = 7045,
                    Title = "New Service Installed (System)",
                    Category = "Persistence / Privilege Escalation",
                    Severity = "Warning",
                    Description = "A new service was registered in the system. Attackers frequently install malicious binaries as services to achieve persistent privilege-level command execution.",
                    InvestigationSteps = "1. Check the Service Name and Image Path (executable file).\n2. Inspect the file path of the executable. Look for non-standard paths (e.g., Temp, AppData, C:\\Users\\Public).\n3. Verify the signature and source of the executable."
                }
            },
            {
                4720, new ForensicAdvice
                {
                    EventId = 4720,
                    Title = "User Account Created (Security)",
                    Category = "Persistence / Account Creation",
                    Severity = "Warning",
                    Description = "A new user account was created. Unannounced account creations are a common backdoor method used to maintain access after credentials are changed.",
                    InvestigationSteps = "1. Identify who created the account (SubjectUserName) and who the account is for (TargetUserName).\n2. Determine if the creation followed standard HR/IT ticket processes.\n3. Monitor this new account closely for group inclusion (e.g., added to Administrators group)."
                }
            },
            {
                4732, new ForensicAdvice
                {
                    EventId = 4732,
                    Title = "User Added to Local Administrators (Security)",
                    Category = "Privilege Escalation",
                    Severity = "Critical",
                    Description = "A user account was added to the local Administrators group, granting them full administrative rights on the system.",
                    InvestigationSteps = "1. Verify if this privilege assignment was authorized by IT operations.\n2. Trace the action to the Subject account to see who delegated the rights.\n3. Inspect concurrent logs from the newly elevated user to see if they started executing high-privilege operations."
                }
            },
            {
                4104, new ForensicAdvice
                {
                    EventId = 4104,
                    Title = "PowerShell Script Block Logged",
                    Category = "Execution / Scripting",
                    Severity = "Warning",
                    Description = "A PowerShell script block was executed and logged. This captures the actual source code of scripts run on the machine, which is highly useful for detecting obfuscated malware scripts, power-sploit payloads, and command-and-control operations.",
                    InvestigationSteps = "1. Look at the ScriptBlock Text to inspect the commands executed.\n2. Watch for indicators of obfuscation (e.g., base64 encoding, System.Convert, DownloadString, IEX, Invoke-Expression).\n3. Check which user spawned PowerShell and verify if script-based execution is typical for their role."
                }
            },
            {
                4688, new ForensicAdvice
                {
                    EventId = 4688,
                    Title = "New Process Created (Security)",
                    Category = "Execution",
                    Severity = "Info",
                    Description = "A new process was spawned. This tracks every command line and executable running on the system.",
                    InvestigationSteps = "1. Check the 'New Process Name' and 'Creator Process Name' to map parent-child relationships (e.g., `cmd.exe` or `powershell.exe` spawned by `w3wp.exe` or `winword.exe` is highly anomalous).\n2. Look at the 'Process Command Line' to see what arguments were passed (e.g., encoded command lines, credentials in plaintext, network downloads)."
                }
            },
            {
                6005, new ForensicAdvice
                {
                    EventId = 6005,
                    Title = "Event Log Service Started (System Boot)",
                    Category = "System Uptime",
                    Severity = "Info",
                    Description = "The Event Log service was started. This occurs at system startup, indicating when the computer booted up.",
                    InvestigationSteps = "1. Match the timestamp with authorized boot-up/reboot logs.\n2. Look for any preceding unexpected shutdown events (6008) to detect power cuts or force reboots."
                }
            },
            {
                6006, new ForensicAdvice
                {
                    EventId = 6006,
                    Title = "Event Log Service Stopped (System Shutdown)",
                    Category = "System Uptime",
                    Severity = "Info",
                    Description = "The Event Log service was stopped, indicating a clean system shutdown.",
                    InvestigationSteps = "1. Verify if this shutdown corresponds to a planned maintenance window.\n2. Cross-reference with Event ID 1074 to find the initiating user or process."
                }
            },
            {
                1074, new ForensicAdvice
                {
                    EventId = 1074,
                    Title = "System Shutdown/Reboot Initiated",
                    Category = "System Uptime",
                    Severity = "Warning",
                    Description = "A shutdown or reboot was initiated by a user or an active process (such as a Windows Update or a script).",
                    InvestigationSteps = "1. Inspect the 'User' and 'Process' fields to see who initiated the shutdown.\n2. Verify if this reboot was scheduled, or if an attacker is trying to disrupt logging, trigger boot persistence, or clear volatile memory."
                }
            },
            {
                8001, new ForensicAdvice
                {
                    EventId = 8001,
                    Title = "Apache Web Server Error Log",
                    Category = "Web Application / Server Error",
                    Severity = "Warning",
                    Description = "An entry from the Apache HTTP Server error log containing diagnostics, runtime warnings, or exceptions.",
                    InvestigationSteps = "1. Look at the error message details for software exceptions or misconfigurations.\n2. Cross-reference the timestamp with Web Access logs to trace the client IP request that triggered this error.\n3. Check if the error indicates resource exhaustion or DoS/DDoS patterns."
                }
            },
            {
                8002, new ForensicAdvice
                {
                    EventId = 8002,
                    Title = "Nginx Web Server Error Log",
                    Category = "Web Application / Server Error",
                    Severity = "Warning",
                    Description = "An entry from the Nginx error log, typically recording upstream processing timeouts, connection failures, or security checks.",
                    InvestigationSteps = "1. Analyze the upstream response or local path permission issues.\n2. Extract client IP and virtual host to determine target scope.\n3. Check if it points to scanning or automated directory enumeration scripts."
                }
            },
            {
                8003, new ForensicAdvice
                {
                    EventId = 8003,
                    Title = "Web Server Access Log (HTTP Request)",
                    Category = "Web Application / Access Log",
                    Severity = "Info",
                    Description = "A standard HTTP request logged by Apache or Nginx.",
                    InvestigationSteps = "1. Check the HTTP response status code (e.g. 200, 301, 404).\n2. Inspect the request URL path and query parameters for unusual characters.\n3. Verify if the User-Agent is a legitimate browser or an automated scanner."
                }
            },
            {
                8004, new ForensicAdvice
                {
                    EventId = 8004,
                    Title = "Potential Web Application Exploit Attempt",
                    Category = "Web Application / Exploit Attempt",
                    Severity = "Critical",
                    Description = "A web access request contains indicators of common web attacks (SQLi, XSS, Path Traversal, RCE, web shell access, or sensitive file exposure).",
                    InvestigationSteps = "1. Check the HTTP response status code. If 200, check the server files for unauthorized modification; if 403/404, it was likely unsuccessful.\n2. Examine request parameters and HTTP payload for attack signatures (e.g., SELECT/UNION, ../../, cmd.exe, eval()).\n3. Scan the host directory for web shells or newly modified files around the event time."
                }
            },
            {
                9001, new ForensicAdvice
                {
                    EventId = 9001,
                    Title = "Generic Text Log Entry",
                    Category = "Application Logs / General",
                    Severity = "Info",
                    Description = "An entry imported from a general application or service text-based log file.",
                    InvestigationSteps = "1. Review the full text message to find the source application context.\n2. Filter for failure keywords (e.g., error, exception, fail, unauthorized).\n3. Correlate with system/security logs around the same time range."
                }
            }
        };

        public static ForensicAdvice GetAdvice(int eventId)
        {
            if (Advices.TryGetValue(eventId, out var advice))
            {
                return advice;
            }

            return new ForensicAdvice
            {
                EventId = eventId,
                Title = $"Event ID {eventId}",
                Category = "Generic Security Event",
                Severity = "Info",
                Description = "No specific forensic advice is pre-configured for this Event ID.",
                InvestigationSteps = "1. Search Microsoft documentation or security portals (like UltimateWindowsSecurity) for details on this Event ID.\n2. Analyze the Provider name and event description to identify the context."
            };
        }

        public static bool IsForensicHighlight(int eventId)
        {
            return Advices.ContainsKey(eventId);
        }
    }
}
