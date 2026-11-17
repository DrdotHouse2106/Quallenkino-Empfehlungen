using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LocalRecs.Problems;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LocalRecs.Api
{
    /// <summary>
    /// Problemberichte: Apps melden ein technisches Problem mit einem Titel, Administratoren arbeiten die Liste ab.
    /// </summary>
    [ApiController]
    [Route("LocalRecs/ProblemReports")]
    public class ProblemReportsController : ControllerBase
    {
        /// <summary>Maximum number of log lines per report.</summary>
        public const int MaxLogLines = 100;

        private const int MaxFieldLength = 1000;

        private readonly ProblemReportStore _store;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProblemReportsController"/> class.
        /// </summary>
        /// <param name="store">Report store.</param>
        /// <param name="userManager">User manager.</param>
        /// <param name="libraryManager">Library manager.</param>
        public ProblemReportsController(ProblemReportStore store, IUserManager userManager, ILibraryManager libraryManager)
        {
            _store = store;
            _userManager = userManager;
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Turns an app request into a report: access keys removed, fields and log shortened, title from the server.
        /// </summary>
        /// <param name="request">Request of the app.</param>
        /// <param name="userId">User ID.</param>
        /// <param name="userName">User name.</param>
        /// <param name="client">Client and version.</param>
        /// <param name="itemName">Item name from the server (null = unknown item).</param>
        /// <returns>The report.</returns>
        public static ProblemReport ToReport(ProblemReportRequest request, Guid userId, string userName, string client, string? itemName)
        {
            ArgumentNullException.ThrowIfNull(request);
            return new ProblemReport
            {
                UserId = userId,
                UserName = userName,
                Client = Field(client),
                Device = Field(request.Device),
                ItemId = itemName == null ? null : request.ItemId,
                ItemName = itemName ?? string.Empty,
                Summary = Field(request.Summary),
                Playback = Field(request.Playback),
                Tracks = Field(request.Tracks),
                Output = Field(request.Output),
                Error = Field(request.Error),
                Log = (request.Log ?? new List<string>()).TakeLast(MaxLogLines).Select(ClientLogController.Sanitize).ToList()
            };
        }

        /// <summary>
        /// Reports a technical problem (any signed-in user).
        /// </summary>
        /// <param name="request">The report.</param>
        /// <returns>The ID of the stored report.</returns>
        [HttpPost]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public ActionResult<ProblemReportCreated> Post([FromBody] ProblemReportRequest request)
        {
            if (request == null)
            {
                return BadRequest();
            }

            var userId = RecommendationsController.GetUserId(User);
            var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
            if (user == null)
            {
                return Unauthorized();
            }

            // Nur Titel, die der Benutzer sehen darf; der Name kommt vom Server, nicht aus der App
            var item = request.ItemId is Guid id ? _libraryManager.GetItemById(id) : null;
            var itemName = item != null && item.IsVisibleStandalone(user) ? item.Name ?? string.Empty : null;
            var client = Claim("Jellyfin-Client") + " " + Claim("Jellyfin-Version");
            var stored = _store.Add(ToReport(request, userId, user.Username, client, itemName), DateTime.UtcNow);
            return stored == null ? StatusCode(StatusCodes.Status429TooManyRequests) : Ok(new ProblemReportCreated { Id = stored.Id });
        }

        /// <summary>
        /// Gets the reports (administrators).
        /// </summary>
        /// <param name="status">„offen“, „erledigt“ or empty for all.</param>
        /// <returns>The reports, newest first.</returns>
        [HttpGet]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult<List<ProblemReport>> Get([FromQuery] string? status = null)
            => Ok(_store.Get(string.IsNullOrEmpty(status) ? null : status));

        /// <summary>
        /// Sets status and note of a report (administrators).
        /// </summary>
        /// <param name="id">Report ID.</param>
        /// <param name="update">New status and/or note.</param>
        /// <returns>No content.</returns>
        [HttpPost("{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult Update([FromRoute] string id, [FromBody] ProblemReportUpdate update)
            => _store.Update(id, update?.Status, update?.Note, DateTime.UtcNow) ? NoContent() : NotFound();

        /// <summary>
        /// Deletes a report (administrators).
        /// </summary>
        /// <param name="id">Report ID.</param>
        /// <returns>No content.</returns>
        [HttpDelete("{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult Delete([FromRoute] string id) => _store.Delete(id) ? NoContent() : NotFound();

        private static string Field(string? value)
        {
            var text = ClientLogController.Sanitize(value);
            return text.Length > MaxFieldLength ? text.Substring(0, MaxFieldLength) : text;
        }

        private string Claim(string type)
            => User.Claims.FirstOrDefault(c => string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase))?.Value ?? "?";
    }
}
