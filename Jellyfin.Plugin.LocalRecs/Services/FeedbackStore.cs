using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Auswertung der Empfehlungen: merkt sich, was wem empfohlen wurde und was davon später geschaut wurde
    /// (Treffer). Daraus ergibt sich die Trefferquote je Bereich, mit der sich die Empfehlungen nach und nach
    /// verbessern lassen. Alles bleibt lokal auf dem Server (feedback.json).
    /// </summary>
    public class FeedbackStore
    {
        /// <summary>Section name for movie recommendations.</summary>
        public const string Movies = "movies";

        /// <summary>Section name for series recommendations.</summary>
        public const string Series = "series";

        /// <summary>Section name for live TV recommendations.</summary>
        public const string LiveTv = "livetv";

        private const int MaxHits = 2000;

        // Lücke, nach der ein wieder empfohlener Titel als neu gilt
        private static readonly TimeSpan ShownGap = TimeSpan.FromDays(7);

        private readonly ILogger<FeedbackStore> _logger;
        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly object _saveLock = new object();
        private FeedbackData _data = new FeedbackData();
        private bool _dirty;

        /// <summary>
        /// Initializes a new instance of the <see cref="FeedbackStore"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="filePath">Path of the JSON file.</param>
        public FeedbackStore(ILogger<FeedbackStore> logger, string filePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            Load();
        }

        /// <summary>
        /// Stable reference of a movie or series that survives a new Jellyfin item ID: TMDB ID, else IMDb ID, else title and year.
        /// </summary>
        /// <param name="tmdbId">TMDB ID.</param>
        /// <param name="imdbId">IMDb ID.</param>
        /// <param name="title">Title.</param>
        /// <param name="year">Production year.</param>
        /// <returns>The reference, or null without any usable value.</returns>
        public static string? StableRef(string? tmdbId, string? imdbId, string? title, int? year)
        {
            if (!string.IsNullOrWhiteSpace(tmdbId))
            {
                return "tmdb:" + tmdbId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                return "imdb:" + imdbId.Trim().ToLowerInvariant();
            }

            return string.IsNullOrWhiteSpace(title) ? null : "title:" + title.Trim().ToLowerInvariant() + "|" + year;
        }

        /// <summary>
        /// Records that items were recommended to a user (first recommendation date is kept).
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="section">Section (<see cref="Movies"/>, <see cref="Series"/>, <see cref="LiveTv"/>).</param>
        /// <param name="items">Item keys and display titles.</param>
        /// <param name="now">Current time (UTC).</param>
        public void RecordRecommended(Guid userId, string section, IEnumerable<(string Key, string Title)> items, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(items);
            RecordRecommended(userId, section, items.Select(i => (i.Key, i.Title, (string?)null)), now);
        }

        /// <summary>
        /// Records that items were recommended to a user. With a stable reference (<see cref="StableRef"/>) the history
        /// survives a new item ID, e.g. after a file was replaced or converted and Jellyfin re-created the item.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="section">Section.</param>
        /// <param name="items">Item keys, display titles and stable references (optional).</param>
        /// <param name="now">Current time (UTC).</param>
        public void RecordRecommended(Guid userId, string section, IEnumerable<(string Key, string Title, string? Ref)> items, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(items);

            lock (_lock)
            {
                foreach (var (key, title, reference) in items)
                {
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    var id = Id(userId, section, key);
                    if (!_data.Recommended.TryGetValue(id, out var entry))
                    {
                        entry = Rekey(userId, section, key, title, reference);
                        if (entry == null)
                        {
                            _data.Recommended[id] = new RecommendedEntry { UserId = userId, Section = section, Key = key, Title = title, Ref = reference, Date = now, ShownSince = now, LastRecommended = now };
                            _dirty = true;
                            continue;
                        }
                    }
                    else if (reference != null && entry.Ref == null)
                    {
                        entry.Ref = reference;
                    }

                    // War der Titel eine Weile nicht in der Zeile, beginnt die Zählung neu
                    var last = entry.LastRecommended == default ? entry.Date : entry.LastRecommended;
                    if (entry.ShownSince == default || now - last > ShownGap)
                    {
                        entry.ShownSince = now;
                    }

                    entry.LastRecommended = now;
                    _dirty = true;
                }
            }
        }

        /// <summary>
        /// Records a hit if the watched item (one of several possible keys) was recommended to the user before.
        /// Each recommendation counts as a hit only once.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="section">Section.</param>
        /// <param name="keys">Keys of the watched item (e.g. item ID, series ID, broadcast key, title key).</param>
        /// <param name="now">Time of watching (UTC).</param>
        /// <returns>True if a new hit was recorded.</returns>
        public bool RecordWatched(Guid userId, string section, IEnumerable<string?> keys, DateTime now)
            => RecordWatched(userId, section, keys, now, null, null);

        /// <summary>
        /// Records a hit; with a stable reference also for a recommendation stored under an older item ID.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="section">Section.</param>
        /// <param name="keys">Keys of the watched item.</param>
        /// <param name="now">Time of watching (UTC).</param>
        /// <param name="title">Display title (fallback for entries without reference).</param>
        /// <param name="reference">Stable reference (<see cref="StableRef"/>).</param>
        /// <returns>True if a new hit was recorded.</returns>
        public bool RecordWatched(Guid userId, string section, IEnumerable<string?> keys, DateTime now, string? title, string? reference)
        {
            ArgumentNullException.ThrowIfNull(keys);

            lock (_lock)
            {
                var list = keys.Where(k => !string.IsNullOrEmpty(k)).ToList();
                if (list.Count > 0 && (title != null || reference != null) && !list.Any(k => _data.Recommended.ContainsKey(Id(userId, section, k!))))
                {
                    Rekey(userId, section, list[0]!, title ?? string.Empty, reference);
                }

                foreach (var key in list)
                {
                    if (_data.Recommended.TryGetValue(Id(userId, section, key!), out var entry) && entry.WatchedAt == null && entry.Date <= now)
                    {
                        entry.WatchedAt = now;
                        _data.Hits.Add(new HitEntry
                        {
                            UserId = userId,
                            Section = section,
                            Title = entry.Title,
                            RecommendedAt = entry.Date,
                            WatchedAt = now
                        });
                        if (_data.Hits.Count > MaxHits)
                        {
                            _data.Hits.RemoveRange(0, _data.Hits.Count - MaxHits);
                        }

                        _dirty = true;
                        _logger.LogInformation("Empfehlung geschaut ({Section}): {Title}", section, entry.Title);
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Gets since when the titles currently recommended to a user have been in the row without being watched.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="section">Section.</param>
        /// <param name="now">Current time (UTC).</param>
        /// <returns>Start of the current recommendation period by item key.</returns>
        public Dictionary<string, DateTime> GetShownSince(Guid userId, string section, DateTime now)
        {
            lock (_lock)
            {
                var result = new Dictionary<string, DateTime>();
                foreach (var entry in _data.Recommended.Values)
                {
                    if (entry.UserId != userId || entry.Section != section || entry.WatchedAt != null)
                    {
                        continue;
                    }

                    var last = entry.LastRecommended == default ? entry.Date : entry.LastRecommended;
                    if (now - last <= ShownGap)
                    {
                        result[entry.Key] = entry.ShownSince == default ? entry.Date : entry.ShownSince;
                    }
                }

                return result;
            }
        }

        /// <summary>
        /// Gets the titles that stood in the row for <paramref name="graceDays"/> days without being watched and are now
        /// held back for <paramref name="pauseDays"/> days, so that the row rotates.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="section">Section.</param>
        /// <param name="now">Current time (UTC).</param>
        /// <param name="graceDays">Days in the row before the pause (0 = off).</param>
        /// <param name="pauseDays">Length of the pause in days (0 = off).</param>
        /// <returns>Keys of the paused items.</returns>
        public HashSet<string> GetPaused(Guid userId, string section, DateTime now, int graceDays, int pauseDays)
        {
            var result = new HashSet<string>();
            if (graceDays <= 0 || pauseDays <= 0)
            {
                return result;
            }

            lock (_lock)
            {
                foreach (var entry in _data.Recommended.Values)
                {
                    if (entry.UserId != userId || entry.Section != section || entry.WatchedAt != null)
                    {
                        continue;
                    }

                    var since = entry.ShownSince == default ? entry.Date : entry.ShownSince;
                    var last = entry.LastRecommended == default ? entry.Date : entry.LastRecommended;
                    var pauseStart = since.AddDays(graceDays);

                    // Nur, wenn der Titel wirklich so lange (bis auf einen Tag) ununterbrochen in der Zeile stand
                    if (last >= pauseStart.AddDays(-1) && now >= pauseStart && now < pauseStart.AddDays(pauseDays))
                    {
                        result.Add(entry.Key);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Builds the evaluation for the last days.
        /// </summary>
        /// <param name="days">Period in days.</param>
        /// <param name="now">Current time (UTC).</param>
        /// <param name="userNames">Optional user names by ID.</param>
        /// <returns>The evaluation.</returns>
        public EvaluationReport GetReport(int days, DateTime now, IReadOnlyDictionary<Guid, string>? userNames = null)
        {
            var since = now.AddDays(-Math.Max(1, days));
            lock (_lock)
            {
                var recommended = _data.Recommended.Values.Where(r => r.Date >= since).ToList();
                var report = new EvaluationReport { Days = days, Since = since };

                foreach (var section in new[] { Movies, Series, LiveTv })
                {
                    var inSection = recommended.Where(r => r.Section == section).ToList();
                    var hits = inSection.Count(r => r.WatchedAt != null);
                    report.Sections.Add(new SectionEvaluation
                    {
                        Section = section,
                        Recommended = inSection.Count,
                        Watched = hits,
                        HitRate = inSection.Count == 0 ? 0 : Math.Round(100.0 * hits / inSection.Count, 1),
                        AverageDaysToWatch = hits == 0 ? null : Math.Round(inSection.Where(r => r.WatchedAt != null).Average(r => (r.WatchedAt!.Value - r.Date).TotalDays), 1)
                    });
                }

                report.RecentHits = _data.Hits
                    .Where(h => h.WatchedAt >= since)
                    .OrderByDescending(h => h.WatchedAt)
                    .Take(50)
                    .Select(h => new HitEntry
                    {
                        UserId = h.UserId,
                        UserName = userNames != null && userNames.TryGetValue(h.UserId, out var name) ? name : null,
                        Section = h.Section,
                        Title = h.Title,
                        RecommendedAt = h.RecommendedAt,
                        WatchedAt = h.WatchedAt
                    })
                    .ToList();

                return report;
            }
        }

        /// <summary>
        /// Removes entries older than the given time.
        /// </summary>
        /// <param name="olderThan">Cut-off time (UTC).</param>
        public void Prune(DateTime olderThan)
        {
            lock (_lock)
            {
                foreach (var key in _data.Recommended.Where(kv => kv.Value.Date < olderThan).Select(kv => kv.Key).ToList())
                {
                    _data.Recommended.Remove(key);
                    _dirty = true;
                }

                if (_data.Hits.RemoveAll(h => h.WatchedAt < olderThan) > 0)
                {
                    _dirty = true;
                }
            }
        }

        /// <summary>
        /// Writes the data to disk if something changed.
        /// </summary>
        public void SaveIfDirty()
        {
            // Nur ein Schreibvorgang gleichzeitig, sonst kann ein älterer Stand einen neueren überschreiben
            lock (_saveLock)
            {
                SaveSnapshot();
            }
        }

        private static string Id(Guid userId, string section, string key) => userId.ToString("N") + "|" + section + "|" + key;

        // Eintrag unter einer früheren Item-ID (gleiche Referenz bzw. bei alten Einträgen ohne Referenz gleicher Titel)
        // auf die neue ID umziehen; mehrere alte Einträge desselben Titels werden zusammengeführt. Aufruf unter _lock.
        private RecommendedEntry? Rekey(Guid userId, string section, string key, string title, string? reference)
        {
            if (reference == null && string.IsNullOrEmpty(title))
            {
                return null;
            }

            var matches = _data.Where(userId, section, e => e.Key != key
                && ((reference != null && e.Ref == reference)
                    || (e.Ref == null && title.Length > 0 && string.Equals(e.Title, title, StringComparison.OrdinalIgnoreCase))));
            if (matches.Count == 0)
            {
                return null;
            }

            var keep = matches.OrderByDescending(m => m.Value.WatchedAt != null).ThenBy(m => m.Value.Date).First().Value;
            foreach (var (id, entry) in matches)
            {
                _data.Recommended.Remove(id);
                if (!ReferenceEquals(entry, keep))
                {
                    keep.Date = entry.Date < keep.Date ? entry.Date : keep.Date;
                    keep.LastRecommended = entry.LastRecommended > keep.LastRecommended ? entry.LastRecommended : keep.LastRecommended;
                }
            }

            keep.Key = key;
            keep.Ref ??= reference;
            _data.Recommended[Id(userId, section, key)] = keep;
            _dirty = true;
            _logger.LogDebug("Auswertung: {Title} unter neuer Item-ID weitergeführt", keep.Title);
            return keep;
        }

        private void SaveSnapshot()
        {
            string json;
            lock (_lock)
            {
                if (!_dirty)
                {
                    return;
                }

                json = JsonSerializer.Serialize(_data);
                _dirty = false;
            }

            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Auswertung konnte nicht gespeichert werden: {Path}", _filePath);
                lock (_lock)
                {
                    _dirty = true;
                }
            }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    _data = JsonSerializer.Deserialize<FeedbackData>(File.ReadAllText(_filePath)) ?? new FeedbackData();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Auswertung konnte nicht gelesen werden: {Path}", _filePath);
            }
        }

        /// <summary>
        /// A recommendation given to a user.
        /// </summary>
        public class RecommendedEntry
        {
            /// <summary>Gets or sets the user ID.</summary>
            public Guid UserId { get; set; }

            /// <summary>Gets or sets the section.</summary>
            public string Section { get; set; } = string.Empty;

            /// <summary>Gets or sets the item key.</summary>
            public string Key { get; set; } = string.Empty;

            /// <summary>Gets or sets the title.</summary>
            public string Title { get; set; } = string.Empty;

            /// <summary>Gets or sets the stable reference (TMDB/IMDb ID or title and year), see <see cref="StableRef"/>.</summary>
            public string? Ref { get; set; }

            /// <summary>Gets or sets the date of the first recommendation (UTC).</summary>
            public DateTime Date { get; set; }

            /// <summary>Gets or sets the time it was watched (UTC), if any.</summary>
            public DateTime? WatchedAt { get; set; }

            /// <summary>Gets or sets the start of the current (uninterrupted) recommendation period (UTC).</summary>
            public DateTime ShownSince { get; set; }

            /// <summary>Gets or sets the time of the last recommendation (UTC).</summary>
            public DateTime LastRecommended { get; set; }
        }

        /// <summary>
        /// A recommendation that was watched.
        /// </summary>
        public class HitEntry
        {
            /// <summary>Gets or sets the user ID.</summary>
            public Guid UserId { get; set; }

            /// <summary>Gets or sets the user name (only in reports).</summary>
            public string? UserName { get; set; }

            /// <summary>Gets or sets the section.</summary>
            public string Section { get; set; } = string.Empty;

            /// <summary>Gets or sets the title.</summary>
            public string Title { get; set; } = string.Empty;

            /// <summary>Gets or sets when it was first recommended (UTC).</summary>
            public DateTime RecommendedAt { get; set; }

            /// <summary>Gets or sets when it was watched (UTC).</summary>
            public DateTime WatchedAt { get; set; }
        }

        /// <summary>
        /// Evaluation of one section.
        /// </summary>
        public class SectionEvaluation
        {
            /// <summary>Gets or sets the section.</summary>
            public string Section { get; set; } = string.Empty;

            /// <summary>Gets or sets the number of recommended items.</summary>
            public int Recommended { get; set; }

            /// <summary>Gets or sets the number of recommended items that were watched.</summary>
            public int Watched { get; set; }

            /// <summary>Gets or sets the hit rate in percent.</summary>
            public double HitRate { get; set; }

            /// <summary>Gets or sets the average days between recommendation and watching.</summary>
            public double? AverageDaysToWatch { get; set; }
        }

        /// <summary>
        /// Evaluation report.
        /// </summary>
        public class EvaluationReport
        {
            /// <summary>Gets or sets the period in days.</summary>
            public int Days { get; set; }

            /// <summary>Gets or sets the start of the period (UTC).</summary>
            public DateTime Since { get; set; }

            /// <summary>Gets the evaluation per section.</summary>
            public List<SectionEvaluation> Sections { get; } = new List<SectionEvaluation>();

            /// <summary>Gets or sets the most recent hits.</summary>
            public List<HitEntry> RecentHits { get; set; } = new List<HitEntry>();
        }

        private sealed class FeedbackData
        {
            public Dictionary<string, RecommendedEntry> Recommended { get; set; } = new Dictionary<string, RecommendedEntry>();

            public List<HitEntry> Hits { get; set; } = new List<HitEntry>();

            public List<KeyValuePair<string, RecommendedEntry>> Where(Guid userId, string section, Func<RecommendedEntry, bool> predicate)
                => Recommended.Where(kv => kv.Value.UserId == userId && kv.Value.Section == section && predicate(kv.Value)).ToList();
        }
    }
}
