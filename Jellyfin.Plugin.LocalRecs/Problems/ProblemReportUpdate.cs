namespace Jellyfin.Plugin.LocalRecs.Problems
{
    /// <summary>
    /// Change of a report by an administrator.
    /// </summary>
    public class ProblemReportUpdate
    {
        /// <summary>Gets or sets the new status.</summary>
        public string? Status { get; set; }

        /// <summary>Gets or sets the note.</summary>
        public string? Note { get; set; }
    }
}
