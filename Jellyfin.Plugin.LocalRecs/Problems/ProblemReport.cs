using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LocalRecs.Problems
{
    /// <summary>
    /// A problem report from an app.
    /// </summary>
    public class ProblemReport
    {
        /// <summary>Gets or sets the report ID.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets when the report was received (UTC).</summary>
        public DateTime Created { get; set; }

        /// <summary>Gets or sets the status (offen/erledigt).</summary>
        public string Status { get; set; } = ProblemReportStore.Open;

        /// <summary>Gets or sets when it was marked as handled (UTC).</summary>
        public DateTime? Handled { get; set; }

        /// <summary>Gets or sets a note of the administrator.</summary>
        public string? Note { get; set; }

        /// <summary>Gets or sets the user ID.</summary>
        public Guid UserId { get; set; }

        /// <summary>Gets or sets the user name.</summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>Gets or sets the client and its version (from the access token).</summary>
        public string Client { get; set; } = string.Empty;

        /// <summary>Gets or sets the device (from the app, e.g. „Amazon AFTKA, Android 11“).</summary>
        public string Device { get; set; } = string.Empty;

        /// <summary>Gets or sets the item ID.</summary>
        public Guid? ItemId { get; set; }

        /// <summary>Gets or sets the item name (from the server).</summary>
        public string ItemName { get; set; } = string.Empty;

        /// <summary>Gets or sets the reason, e.g. „2× nach wenigen Sekunden beendet“ or „Von Hand gemeldet“.</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>Gets or sets the playback method and media details (direct play/transcode, codecs, channels).</summary>
        public string Playback { get; set; } = string.Empty;

        /// <summary>Gets or sets the selected audio and subtitle tracks.</summary>
        public string Tracks { get; set; } = string.Empty;

        /// <summary>Gets or sets the detected audio output (e.g. „HDMI, Passthrough: AC3; max. 6 Kanäle“).</summary>
        public string Output { get; set; } = string.Empty;

        /// <summary>Gets or sets the last player error.</summary>
        public string Error { get; set; } = string.Empty;

        /// <summary>Gets or sets the last log lines of the app.</summary>
        public List<string> Log { get; set; } = new List<string>();
    }
}
