using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Jellyfin.Plugin.LocalRecs.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit
{
    /// <summary>
    /// Tests für die Auswertung (empfohlen → geschaut).
    /// </summary>
    public class FeedbackStoreTests : IDisposable
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        private readonly string _path = Path.Combine(Path.GetTempPath(), "jellyfin-localrecs-tests", Guid.NewGuid() + ".json");
        private readonly Guid _user = Guid.NewGuid();

        public void Dispose()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }

        [Fact]
        public void ShownSince_CountsUninterruptedPeriod_AndRestartsAfterGap()
        {
            var store = new FeedbackStore(NullLogger<FeedbackStore>.Instance, _path);
            store.RecordRecommended(_user, FeedbackStore.Movies, new[] { ("a", "A"), ("b", "B") }, Now.AddDays(-30));
            for (var day = 29; day >= 0; day--)
            {
                store.RecordRecommended(_user, FeedbackStore.Movies, new[] { ("a", "A") }, Now.AddDays(-day));
            }

            // b war 30 Tage nicht mehr in der Zeile und kommt jetzt zurück
            store.RecordRecommended(_user, FeedbackStore.Movies, new[] { ("b", "B") }, Now);
            store.RecordWatched(_user, FeedbackStore.Movies, new[] { "c" }, Now);

            var shown = store.GetShownSince(_user, FeedbackStore.Movies, Now);
            shown["a"].Should().Be(Now.AddDays(-30));
            shown["b"].Should().Be(Now, "nach einer Lücke beginnt die Zählung neu");

            store.RecordWatched(_user, FeedbackStore.Movies, new[] { "a" }, Now);
            store.GetShownSince(_user, FeedbackStore.Movies, Now).Should().NotContainKey("a", "geschaute Titel werden nicht abgewertet");
        }

        [Fact]
        public void WatchedRecommendation_CountsAsHitOnce()
        {
            var store = new FeedbackStore(NullLogger<FeedbackStore>.Instance, _path);
            store.RecordRecommended(_user, FeedbackStore.Movies, new[] { ("a", "Film A"), ("b", "Film B"), ("c", "Film C"), ("d", "Film D") }, Now.AddDays(-3));

            store.RecordWatched(_user, FeedbackStore.Movies, new[] { "b" }, Now).Should().BeTrue();
            store.RecordWatched(_user, FeedbackStore.Movies, new[] { "b" }, Now.AddHours(1)).Should().BeFalse("ein zweites Anschauen zählt nicht doppelt");
            store.RecordWatched(_user, FeedbackStore.Movies, new[] { "x" }, Now).Should().BeFalse("nicht empfohlen");
            store.RecordWatched(Guid.NewGuid(), FeedbackStore.Movies, new[] { "a" }, Now).Should().BeFalse("anderer Benutzer");

            var movies = store.GetReport(30, Now).Sections.Single(s => s.Section == FeedbackStore.Movies);
            movies.Recommended.Should().Be(4);
            movies.Watched.Should().Be(1);
            movies.HitRate.Should().Be(25);
            movies.AverageDaysToWatch.Should().Be(3);
        }

        [Fact]
        public void RecommendedAgain_KeepsFirstDate_AndPersists()
        {
            var store = new FeedbackStore(NullLogger<FeedbackStore>.Instance, _path);
            store.RecordRecommended(_user, FeedbackStore.LiveTv, new[] { ("title:tatort", "Tatort") }, Now.AddDays(-2));
            store.RecordRecommended(_user, FeedbackStore.LiveTv, new[] { ("title:tatort", "Tatort") }, Now);
            store.RecordWatched(_user, FeedbackStore.LiveTv, new[] { "sky|123", "title:tatort" }, Now);
            store.SaveIfDirty();

            var reloaded = new FeedbackStore(NullLogger<FeedbackStore>.Instance, _path);
            var report = reloaded.GetReport(30, Now);
            report.Sections.Single(s => s.Section == FeedbackStore.LiveTv).AverageDaysToWatch.Should().Be(2);
            report.RecentHits.Single().Title.Should().Be("Tatort");
        }

        [Fact]
        public void Prune_RemovesOldEntries()
        {
            var store = new FeedbackStore(NullLogger<FeedbackStore>.Instance, _path);
            store.RecordRecommended(_user, FeedbackStore.Series, new[] { ("s", "Serie") }, Now.AddDays(-400));
            store.Prune(Now.AddDays(-365));

            store.GetReport(3650, Now).Sections.Sum(s => s.Recommended).Should().Be(0);
        }

        [Fact]
        public void Paused_AfterGraceDaysInTheRow_ForPauseDays()
        {
            var store = new FeedbackStore(NullLogger<FeedbackStore>.Instance, _path);
            for (var day = 0; day <= 14; day++)
            {
                store.RecordRecommended(_user, FeedbackStore.Movies, new[] { ("a", "A") }, Now.AddDays(day));
            }

            // b stand nur kurz in der Zeile
            store.RecordRecommended(_user, FeedbackStore.Movies, new[] { ("b", "B") }, Now);

            store.GetPaused(_user, FeedbackStore.Movies, Now.AddDays(13), 14, 35).Should().BeEmpty();
            store.GetPaused(_user, FeedbackStore.Movies, Now.AddDays(15), 14, 35).Should().BeEquivalentTo(new[] { "a" });
            store.GetPaused(_user, FeedbackStore.Movies, Now.AddDays(50), 14, 35).Should().BeEmpty("die Pause ist vorbei");
            store.GetPaused(_user, FeedbackStore.Movies, Now.AddDays(15), 14, 0).Should().BeEmpty("0 = aus");

            // Nach der Pause wieder empfohlen: neue Zählung
            store.RecordRecommended(_user, FeedbackStore.Movies, new[] { ("a", "A") }, Now.AddDays(50));
            store.GetShownSince(_user, FeedbackStore.Movies, Now.AddDays(50))["a"].Should().Be(Now.AddDays(50));
        }

        [Fact]
        public void NewItemId_KeepsHistory_ByStableReference()
        {
            var store = new FeedbackStore(NullLogger<FeedbackStore>.Instance, _path);
            var reference = FeedbackStore.StableRef("603", null, "Matrix", 1999);
            store.RecordRecommended(_user, FeedbackStore.Movies, new[] { ("alt", "Matrix", reference) }, Now.AddDays(-20));

            // Datei ausgetauscht: neue Item-ID, gleiche TMDB-ID
            store.RecordRecommended(_user, FeedbackStore.Movies, new[] { ("neu", "Matrix", reference) }, Now);
            store.GetReport(30, Now).Sections.Single(s => s.Section == FeedbackStore.Movies).Recommended.Should().Be(1, "kein zweiter Eintrag");
            store.GetShownSince(_user, FeedbackStore.Movies, Now).Should().ContainKey("neu").And.NotContainKey("alt");

            // Treffer unter der neuen ID zählt für die frühere Empfehlung
            store.RecordWatched(_user, FeedbackStore.Movies, new[] { "noch-neuer" }, Now, "Matrix", reference).Should().BeTrue();
            store.GetReport(30, Now).RecentHits.Single().RecommendedAt.Should().Be(Now.AddDays(-20));
        }

        [Fact]
        public void LegacyEntriesWithoutReference_AreMatchedByTitle()
        {
            var store = new FeedbackStore(NullLogger<FeedbackStore>.Instance, _path);
            store.RecordRecommended(_user, FeedbackStore.Movies, new[] { ("alt1", "Film"), ("alt2", "Film") }, Now.AddDays(-10));
            store.RecordRecommended(_user, FeedbackStore.Movies, new[] { ("neu", "Film", FeedbackStore.StableRef(null, "tt1", "Film", 2020)) }, Now);

            store.GetReport(30, Now).Sections.Single(s => s.Section == FeedbackStore.Movies).Recommended.Should().Be(1, "doppelte Einträge zusammengeführt");
        }

        [Theory]
        [InlineData("603", "tt0133093", "tmdb:603")]
        [InlineData(null, "TT0133093", "imdb:tt0133093")]
        [InlineData(null, null, "title:matrix|1999")]
        public void StableRef_PrefersTmdbThenImdbThenTitle(string? tmdb, string? imdb, string expected)
        {
            FeedbackStore.StableRef(tmdb, imdb, " Matrix ", 1999).Should().Be(expected);
        }

        [Fact]
        public void Consolidate_MergesOrphansAndDuplicates_FillsReferences()
        {
            // Stand vor 12.62: derselbe Film unter alter und neuer Item-ID, ohne Referenz
            string Entry(string key, string title, DateTime date) =>
                $"\"{_user:N}|movies|{key}\": {{ \"UserId\": \"{_user}\", \"Section\": \"movies\", \"Key\": \"{key}\", \"Title\": \"{title}\", \"Date\": \"{date:O}\", \"ShownSince\": \"{date:O}\", \"LastRecommended\": \"{date:O}\" }}";
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, "{ \"Recommended\": { " + string.Join(", ", Entry("alt", "Film", Now.AddDays(-20)), Entry("neu", "Film", Now), Entry("x", "Anderer", Now)) + " }, \"Hits\": [] }");
            var store = new FeedbackStore(NullLogger<FeedbackStore>.Instance, _path);
            store.GetReport(30, Now).Sections.Single(s => s.Section == FeedbackStore.Movies).Recommended.Should().Be(3);

            // „alt“ gibt es nicht mehr, „neu“ und „x“ schon
            var merged = store.Consolidate(key => key switch { "neu" => "tmdb:1", "x" => "tmdb:2", _ => null });

            merged.Should().Be(1);
            store.GetReport(30, Now).Sections.Single(s => s.Section == FeedbackStore.Movies).Recommended.Should().Be(2);
            store.RecordWatched(_user, FeedbackStore.Movies, new[] { "neu" }, Now).Should().BeTrue();
            store.GetReport(30, Now).RecentHits.Single().RecommendedAt.Should().Be(Now.AddDays(-20), "das früheste Empfehlungsdatum bleibt");
        }

        [Fact]
        public void Consolidate_KeepsRemakesWithSameNameApart()
        {
            var store = new FeedbackStore(NullLogger<FeedbackStore>.Instance, _path);
            store.RecordRecommended(_user, FeedbackStore.Movies, new[] { ("a", "Dune"), ("b", "Dune"), ("c", "Dune") }, Now);

            store.Consolidate(key => key switch { "a" => "tmdb:841", "b" => "tmdb:438631", _ => null }).Should().Be(0);
        }
    }
}
