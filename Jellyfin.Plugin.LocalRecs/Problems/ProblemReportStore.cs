using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.Problems
{
    /// <summary>
    /// Problemberichte aus den Apps („Ups, gab es hier ein technisches Problem?“): Gerät, Titel, Ton/Untertitel,
    /// Direktwiedergabe oder Umwandlung, Fehlermeldung und letzte Log-Zeilen. Bleibt nur auf dem Server
    /// (problemberichte.json); ein Administrator arbeitet die Liste ab (offen/erledigt).
    /// </summary>
    public class ProblemReportStore
    {
        /// <summary>Status of a new report.</summary>
        public const string Open = "offen";

        /// <summary>Status of a handled report.</summary>
        public const string Done = "erledigt";

        /// <summary>Maximum number of stored reports.</summary>
        public const int MaxReports = 500;

        /// <summary>Maximum reports per user and hour.</summary>
        public const int MaxPerUserAndHour = 10;

        /// <summary>Days after which handled reports are removed.</summary>
        public const int KeepDoneDays = 90;

        private readonly ILogger<ProblemReportStore> _logger;
        private readonly string _filePath;
        private readonly object _lock = new object();
        private List<ProblemReport> _reports = new List<ProblemReport>();

        /// <summary>
        /// Initializes a new instance of the <see cref="ProblemReportStore"/> class.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="filePath">Path of the JSON file.</param>
        public ProblemReportStore(ILogger<ProblemReportStore> logger, string filePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            Load();
        }

        /// <summary>
        /// Adds a report (already sanitized). Returns null if the user sent too many reports in the last hour.
        /// </summary>
        /// <param name="report">The report.</param>
        /// <param name="now">Current time (UTC).</param>
        /// <returns>The stored report, or null.</returns>
        public ProblemReport? Add(ProblemReport report, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(report);

            lock (_lock)
            {
                if (_reports.Count(r => r.UserId == report.UserId && now - r.Created < TimeSpan.FromHours(1)) >= MaxPerUserAndHour)
                {
                    return null;
                }

                report.Id = Guid.NewGuid().ToString("N");
                report.Created = now;
                report.Status = Open;
                _reports.Add(report);
                Prune(now);
                Save();
            }

            _logger.LogWarning("Problembericht von {User} ({Device}): {Item} – {Summary}", report.UserName, report.Device, report.ItemName, report.Summary);
            return report;
        }

        /// <summary>
        /// Gets the reports, newest first.
        /// </summary>
        /// <param name="status">Only this status (null = all).</param>
        /// <returns>The reports.</returns>
        public List<ProblemReport> Get(string? status)
        {
            lock (_lock)
            {
                return _reports.Where(r => status == null || r.Status == status).OrderByDescending(r => r.Created).ToList();
            }
        }

        /// <summary>
        /// Sets status and note of a report.
        /// </summary>
        /// <param name="id">Report ID.</param>
        /// <param name="status">New status (<see cref="Open"/> or <see cref="Done"/>), null = unchanged.</param>
        /// <param name="note">Note, null = unchanged.</param>
        /// <param name="now">Current time (UTC).</param>
        /// <returns>True if the report exists.</returns>
        public bool Update(string id, string? status, string? note, DateTime now)
        {
            lock (_lock)
            {
                var report = _reports.FirstOrDefault(r => r.Id == id);
                if (report == null)
                {
                    return false;
                }

                if (status == Open || status == Done)
                {
                    report.Status = status;
                    report.Handled = status == Done ? now : null;
                }

                if (note != null)
                {
                    report.Note = note.Length > 2000 ? note.Substring(0, 2000) : note;
                }

                Save();
                return true;
            }
        }

        /// <summary>
        /// Deletes a report.
        /// </summary>
        /// <param name="id">Report ID.</param>
        /// <returns>True if it existed.</returns>
        public bool Delete(string id)
        {
            lock (_lock)
            {
                var removed = _reports.RemoveAll(r => r.Id == id) > 0;
                if (removed)
                {
                    Save();
                }

                return removed;
            }
        }

        // Erledigte nach 90 Tagen weg, insgesamt höchstens MaxReports (älteste erledigte zuerst, dann älteste offene)
        private void Prune(DateTime now)
        {
            _reports.RemoveAll(r => r.Status == Done && r.Handled is DateTime handled && now - handled > TimeSpan.FromDays(KeepDoneDays));
            var excess = _reports.Count - MaxReports;
            if (excess > 0)
            {
                foreach (var old in _reports.OrderBy(r => r.Status == Open).ThenBy(r => r.Created).Take(excess).ToList())
                {
                    _reports.Remove(old);
                }
            }
        }

        private void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(_reports));
                File.Move(tempPath, _filePath, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Problemberichte konnten nicht gespeichert werden: {Path}", _filePath);
            }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    _reports = JsonSerializer.Deserialize<List<ProblemReport>>(File.ReadAllText(_filePath)) ?? new List<ProblemReport>();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Problemberichte konnten nicht gelesen werden: {Path}", _filePath);
            }
        }
    }
}
