using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LocalRecs.Problems
{
    /// <summary>
    /// Problem report sent by an app.
    /// </summary>
    public class ProblemReportRequest
    {
        /// <summary>Gets or sets the item ID.</summary>
        public Guid? ItemId { get; set; }

        /// <summary>Gets or sets the device.</summary>
        public string? Device { get; set; }

        /// <summary>Gets or sets the reason.</summary>
        public string? Summary { get; set; }

        /// <summary>Gets or sets playback method and media details.</summary>
        public string? Playback { get; set; }

        /// <summary>Gets or sets audio and subtitle tracks.</summary>
        public string? Tracks { get; set; }

        /// <summary>Gets or sets the detected audio output.</summary>
        public string? Output { get; set; }

        /// <summary>Gets or sets the last player error.</summary>
        public string? Error { get; set; }

        /// <summary>Gets or sets the last log lines.</summary>
        public List<string>? Log { get; set; }
    }
}
