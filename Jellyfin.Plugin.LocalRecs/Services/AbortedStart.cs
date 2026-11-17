using System;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Filme, die gestartet, aber gleich wieder beendet wurden (Jellyfin speichert unter 5 % keine Position).
    /// </summary>
    public static class AbortedStart
    {
        /// <summary>Starts without progress from which a movie is held back regardless of when it was last started.</summary>
        public const int RepeatedStarts = 3;

        /// <summary>
        /// Whether a movie was started but stopped right away (play count without progress and not finished) within the
        /// last <paramref name="pauseDays"/> days – or at least <see cref="RepeatedStarts"/> times (then regardless of age:
        /// it does not play properly or is not wanted).
        /// </summary>
        /// <param name="playCount">Play count.</param>
        /// <param name="played">Whether it is marked as played.</param>
        /// <param name="positionTicks">Saved playback position.</param>
        /// <param name="lastPlayed">Last playback start.</param>
        /// <param name="now">Current time (UTC).</param>
        /// <param name="pauseDays">Pause in days (0 = off).</param>
        /// <returns>True if it should not be recommended for now.</returns>
        public static bool IsAborted(int playCount, bool played, long positionTicks, DateTime? lastPlayed, DateTime now, int pauseDays)
            => pauseDays > 0 && playCount >= 1 && !played && positionTicks <= 0
                && (playCount >= RepeatedStarts || (lastPlayed is DateTime last && now - last < TimeSpan.FromDays(pauseDays)));
    }
}
