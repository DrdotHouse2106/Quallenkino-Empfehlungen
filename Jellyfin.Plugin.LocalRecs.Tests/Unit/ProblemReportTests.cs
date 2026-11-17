using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Jellyfin.Plugin.LocalRecs.Api;
using Jellyfin.Plugin.LocalRecs.Problems;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit
{
    /// <summary>
    /// Tests für die Problemberichte aus den Apps.
    /// </summary>
    public class ProblemReportTests : IDisposable
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
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
        public void Report_IsStored_HandledAndReloaded()
        {
            var store = new ProblemReportStore(NullLogger<ProblemReportStore>.Instance, _path);
            var report = store.Add(new ProblemReport { UserId = _user, UserName = "Beispiel", ItemName = "Testfilm", Summary = "2× abgebrochen" }, Now);

            report!.Status.Should().Be(ProblemReportStore.Open);
            store.Update(report.Id, ProblemReportStore.Done, "Ton-Ausgang geprüft", Now).Should().BeTrue();

            var reloaded = new ProblemReportStore(NullLogger<ProblemReportStore>.Instance, _path);
            reloaded.Get(ProblemReportStore.Open).Should().BeEmpty();
            var done = reloaded.Get(ProblemReportStore.Done).Single();
            done.Note.Should().Be("Ton-Ausgang geprüft");
            reloaded.Delete(done.Id).Should().BeTrue();
            reloaded.Get(null).Should().BeEmpty();
        }

        [Fact]
        public void TooManyReportsPerHour_AreRejected_DoneOnesExpire()
        {
            var store = new ProblemReportStore(NullLogger<ProblemReportStore>.Instance, _path);
            for (var i = 0; i < ProblemReportStore.MaxPerUserAndHour; i++)
            {
                store.Add(new ProblemReport { UserId = _user }, Now).Should().NotBeNull();
            }

            store.Add(new ProblemReport { UserId = _user }, Now).Should().BeNull();
            store.Add(new ProblemReport { UserId = Guid.NewGuid() }, Now).Should().NotBeNull("anderer Benutzer");

            var first = store.Get(null).Last();
            store.Update(first.Id, ProblemReportStore.Done, null, Now);
            store.Add(new ProblemReport { UserId = _user }, Now.AddDays(ProblemReportStore.KeepDoneDays + 1));
            store.Get(ProblemReportStore.Done).Should().BeEmpty("erledigte Berichte nach 90 Tagen gelöscht");
        }

        [Fact]
        public void Request_IsSanitized_AndShortened()
        {
            var request = new ProblemReportRequest
            {
                ItemId = Guid.NewGuid(),
                Device = "Testgerät",
                Error = "Fehler bei https://server/Videos/1/stream?api_key=geheim123",
                Log = Enumerable.Range(0, 150).Select(i => "Zeile " + i).Append("X-Emby-Token: geheim456").ToList()
            };

            var report = ProblemReportsController.ToReport(request, _user, "Beispiel", "Quallenkino 1.46.0", null);

            report.Error.Should().NotContain("geheim123");
            report.Log.Should().HaveCount(ProblemReportsController.MaxLogLines);
            report.Log.Last().Should().NotContain("geheim456");
            report.ItemId.Should().BeNull("unbekannter oder nicht sichtbarer Titel");
        }
    }
}
