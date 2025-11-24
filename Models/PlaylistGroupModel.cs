using System;
using System.Collections.Generic;

namespace ProjectionMapper.Models
{
    /// <summary>
    /// Represents a single group in a playlist. Each group can contain multiple
    /// source video IDs that play simultaneously. Groups play sequentially.
    /// </summary>
    public class PlaylistGroupModel
    {
        /// <summary>
        /// Unique identifier for this playlist group.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Display name for the group.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Order of this group in the playlist (0-based, sequential).
        /// </summary>
        public int Order { get; set; }

        /// <summary>
        /// List of source video IDs that belong to this group.
        /// These videos play simultaneously when the group is active.
        /// </summary>
        public List<string> SourceIds { get; set; } = new List<string>();
    }
}
