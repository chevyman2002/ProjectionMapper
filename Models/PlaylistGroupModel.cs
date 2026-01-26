using System;
using System.Collections.Generic;

namespace ProjectionMapper.Models
{
    /// <summary>
    /// Defines how videos within a playlist group are played.
    /// </summary>
    public enum GroupPlaybackMode
    {
        /// <summary>
        /// All videos in the group play at the same time (for projection mapping with multiple surfaces).
        /// Group advances to next when ALL videos complete.
        /// </summary>
        Simultaneous = 0,

        /// <summary>
        /// Videos in the group play one after another in sequence (traditional playlist behavior).
        /// Group advances to next when the LAST video in sequence completes.
        /// </summary>
        Sequential = 1
    }

    /// <summary>
    /// Represents a single group in a playlist. Each group can contain multiple
    /// source video IDs. Groups play sequentially, and videos within a group
    /// play according to the PlaybackMode setting.
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
        /// Playback behavior depends on the PlaybackMode setting.
        /// </summary>
        public List<string> SourceIds { get; set; } = new List<string>();

        /// <summary>
        /// Determines how videos within this group are played.
        /// Simultaneous: all videos play at once (projection mapping).
        /// Sequential: videos play one after another (traditional playlist).
        /// Default is Sequential for traditional playlist behavior.
        /// </summary>
        public GroupPlaybackMode PlaybackMode { get; set; } = GroupPlaybackMode.Sequential;
    }
}
